using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClipForge.Api.Features.Internal;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Api.Tests.Integration;

public class InternalJobTests : TestBase
{
    public InternalJobTests(ApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Claim_returns_the_job_once_with_the_documented_payload()
    {
        var (client, userId, _) = await RegisterAsync();
        var (projectId, jobId) = await CreateProjectAsync(client);
        var internalClient = CreateInternalClient("worker-1");

        var payload = (await ClaimAsync(internalClient, "worker-1", "analyze_source")).ShouldNotBeNull();
        payload.GetProperty("jobId").GetGuid().ShouldBe(jobId);
        payload.GetProperty("kind").GetString().ShouldBe("analyze_source");
        payload.GetProperty("attempt").GetInt32().ShouldBe(1);
        payload.GetProperty("userId").GetGuid().ShouldBe(userId);
        payload.GetProperty("projectId").GetGuid().ShouldBe(projectId);
        payload.GetProperty("leaseSeconds").GetInt32().ShouldBe(180);
        var source = payload.GetProperty("source");
        source.GetProperty("fileName").GetString().ShouldBe("live.mp4");
        source.GetProperty("contentType").GetString().ShouldBe("video/mp4");
        source.GetProperty("sizeBytes").GetInt64().ShouldBe(4096);
        source.GetProperty("durationSeconds").ValueKind.ShouldBe(JsonValueKind.Null);
        var downloadUrl = source.GetProperty("downloadUrl").GetString()!;
        downloadUrl.ShouldContain("/api/storage/download?key=");
        var exp = long.Parse(System.Web.HttpUtility.ParseQueryString(new Uri(downloadUrl).Query)["exp"]!);
        DateTimeOffset.FromUnixTimeSeconds(exp).ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(6).AddMinutes(-1));
        // the worker can actually download the source through the signed URL
        var bytes = await internalClient.GetByteArrayAsync(downloadUrl);
        bytes.ShouldBe(FakeMp4());

        var settings = payload.GetProperty("settings");
        settings.GetProperty("maxCandidates").GetInt32().ShouldBe(15);
        settings.GetProperty("subtitles").GetProperty("style").GetString().ShouldBe("bold");
        settings.GetProperty("framing").GetProperty("mode").GetString().ShouldBe("auto");
        settings.GetProperty("framing").GetProperty("focusX").GetDouble().ShouldBe(0.5); // explicit numbers: the worker model rejects null
        settings.GetProperty("framing").GetProperty("focusY").GetDouble().ShouldBe(0.5);
        settings.GetProperty("language").GetString().ShouldBe("auto");
        var limits = payload.GetProperty("limits");
        limits.GetProperty("maxRenderHeight").GetInt32().ShouldBe(720);
        limits.GetProperty("maxSourceDurationSeconds").GetInt64().ShouldBe(7200);
        limits.GetProperty("remainingProcessingSeconds").GetInt64().ShouldBe(3600);
        limits.GetProperty("transcriptionModel").GetString().ShouldBe("base"); // free plan (docs/PLANS_AND_BILLING.md)
        limits.GetProperty("allowWebcamLayout").GetBoolean().ShouldBeFalse();

        var job = await WithDbAsync(db => db.ProcessingJobs.FirstAsync(j => j.Id == jobId));
        job.Status.ShouldBe(JobStatus.PreparingMedia);
        job.WorkerId.ShouldBe("worker-1");
        job.LeaseExpiresAt.ShouldNotBeNull();
        job.StartedAt.ShouldNotBeNull();
        job.Attempt.ShouldBe(1);
        (await WithDbAsync(db => db.ProcessingJobEvents.CountAsync(e => e.JobId == jobId))).ShouldBeGreaterThanOrEqualTo(2);

        // second claim gets nothing
        (await ClaimAsync(internalClient, "worker-2", "analyze_source")).ShouldBeNull();
        (await ClaimAsync(internalClient, "worker-2", "render_clip")).ShouldBeNull();
    }

    [Fact]
    public async Task Claim_respects_priority_then_queue_order()
    {
        var (a, _, _) = await RegisterAsync();
        var (b, bId, _) = await RegisterAsync();
        var (_, jobA) = await CreateProjectAsync(a);
        var (_, jobB) = await CreateProjectAsync(b);
        // user B's job gets a higher priority (as a paid plan would)
        await WithDbAsync(async db =>
        {
            await db.ProcessingJobs.Where(j => j.Id == jobB).ExecuteUpdateAsync(s => s.SetProperty(j => j.Priority, 2));
        });
        var internalClient = CreateInternalClient("worker-1");
        (await ClaimAsync(internalClient)).ShouldNotBeNull().GetProperty("jobId").GetGuid().ShouldBe(jobB);
        (await ClaimAsync(internalClient)).ShouldNotBeNull().GetProperty("jobId").GetGuid().ShouldBe(jobA);
        (await ClaimAsync(internalClient)).ShouldBeNull();
        _ = bId;
    }

    [Fact]
    public async Task Heartbeat_from_another_worker_is_rejected()
    {
        var (client, _, _) = await RegisterAsync();
        var (_, jobId) = await CreateProjectAsync(client);
        var worker1 = CreateInternalClient("worker-1");
        var worker2 = CreateInternalClient("worker-2");
        (await ClaimAsync(worker1, "worker-1", "analyze_source")).ShouldNotBeNull();

        var ok = await worker1.PostAsJsonAsync($"/internal/jobs/{jobId}/heartbeat", new { progress = 5, message = "Baixando" }, Json);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(ok);
        body.GetProperty("cancelRequested").GetBoolean().ShouldBeFalse();
        body.GetProperty("leaseExpiresAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(100));

        await ShouldBeProblemAsync(await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/heartbeat", new { progress = 6 }, Json), HttpStatusCode.Conflict, "conflict");
        // the worker id can also travel in the body
        var anonymousWorker = CreateInternalClient();
        await ShouldBeProblemAsync(await anonymousWorker.PostAsJsonAsync($"/internal/jobs/{jobId}/heartbeat", new { progress = 6, workerId = "worker-3" }, Json), HttpStatusCode.Conflict, "conflict");
        await ShouldBeProblemAsync(await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "analyzing" }, Json), HttpStatusCode.Conflict, "conflict");

        // unknown job
        (await worker1.PostAsJsonAsync($"/internal/jobs/{Guid.NewGuid()}/heartbeat", new { }, Json)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Probe_over_plan_duration_fails_the_job_with_plan_limit()
    {
        var (client, _, _) = await RegisterAsync();
        var (projectId, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();

        var probe = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", new
        {
            durationSeconds = 121 * 60.0, width = 1920, height = 1080, fps = 30.0, hasAudio = true, hasVideo = true, container = "mp4", videoCodec = "h264", audioCodec = "aac", sizeBytes = 4096,
        }, Json);
        await ShouldBeProblemAsync(probe, HttpStatusCode.Conflict, "plan_limit");
        (await ReadJsonAsync(probe)).GetProperty("detail").GetString()!.ShouldContain("120 min");

        var job = await WithDbAsync(db => db.ProcessingJobs.FirstAsync(j => j.Id == jobId));
        job.Status.ShouldBe(JobStatus.Failed);
        job.ErrorCode.ShouldBe("plan_limit");
        job.ReservedSeconds.ShouldBe(0);
        (await WithDbAsync(db => db.Projects.Where(p => p.Id == projectId).Select(p => p.Status).FirstAsync())).ShouldBe(ProjectStatus.Failed);
        (await WithDbAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationType.JobFailed))).ShouldBe(1);

        // audio-only sources are rejected too
        var (_, job2) = await CreateProjectAsync(client);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        var noVideo = await worker.PostAsJsonAsync($"/internal/jobs/{job2}/probe", new { durationSeconds = 60.0, hasAudio = true, hasVideo = false }, Json);
        await ShouldBeProblemAsync(noVideo, HttpStatusCode.Conflict, "invalid_media");
    }

    [Fact]
    public async Task Probe_over_remaining_minutes_fails_with_quota_exceeded_and_reserves_otherwise()
    {
        var (client, userId, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");

        // first job consumes 50 minutes
        var (_, job1) = await CreateProjectAsync(client);
        await CompleteAnalyzeJobAsync(worker, job1, durationSeconds: 50 * 60);
        var usage = await ReadJsonAsync(await client.GetAsync("/api/account/usage"));
        usage.GetProperty("used").GetProperty("processingSeconds").GetInt64().ShouldBe(3000);
        usage.GetProperty("remaining").GetProperty("processingSeconds").GetInt64().ShouldBe(600);

        // second job asks for 11 minutes → over the 60 minutes of the free plan
        var (_, job2) = await CreateProjectAsync(client);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        var probe = await worker.PostAsJsonAsync($"/internal/jobs/{job2}/probe", new
        {
            durationSeconds = 11 * 60.0, width = 1280, height = 720, fps = 30.0, hasAudio = true, hasVideo = true, container = "mp4", sizeBytes = 4096,
        }, Json);
        await ShouldBeProblemAsync(probe, HttpStatusCode.Conflict, "quota_exceeded");
        (await ReadJsonAsync(probe)).GetProperty("detail").GetString()!.ShouldContain("10 min");
        (await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == job2).Select(j => j.Status).FirstAsync())).ShouldBe(JobStatus.Failed);

        // third job within the remaining minutes reserves seconds and reports the remaining budget
        var (_, job3) = await CreateProjectAsync(client);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        var ok = await worker.PostAsJsonAsync($"/internal/jobs/{job3}/probe", new
        {
            durationSeconds = 9 * 60.0, width = 1280, height = 720, fps = 30.0, hasAudio = true, hasVideo = true, container = "mp4", videoCodec = "h264", audioCodec = "aac", sizeBytes = 4096,
        }, Json);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(ok);
        body.GetProperty("ok").GetBoolean().ShouldBeTrue();
        body.GetProperty("remainingProcessingSeconds").GetInt64().ShouldBe(60);
        var job = await WithDbAsync(db => db.ProcessingJobs.Include(j => j.Project).ThenInclude(p => p.MediaAsset).FirstAsync(j => j.Id == job3));
        job.ReservedSeconds.ShouldBe(540);
        job.Project.MediaAsset!.DurationSeconds.ShouldBe(540m);
        job.Project.MediaAsset.Width.ShouldBe(1280);
        job.Project.MediaAsset.VideoCodec.ShouldBe("h264");
        _ = userId;
    }

    [Fact]
    public async Task Full_happy_path_ends_with_a_ready_project_and_a_downloadable_clip()
    {
        var (client, userId, _) = await RegisterAsync();
        var (projectId, jobId) = await CreateProjectAsync(client, "Live do campeonato");
        var worker = CreateInternalClient("worker-1");

        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var (status, progress, message) in new[] { ("analyzing", 10, "Analisando áudio"), ("detecting_moments", 60, "Detectando momentos"), ("generating_clips", 65, "Gerando clipes") })
        {
            var response = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status, progress, message }, Json);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var reported = await ReadJsonAsync(response);
            reported.GetProperty("status").GetString().ShouldBe(status);
            reported.GetProperty("progress").GetInt32().ShouldBe(progress);
            reported.GetProperty("statusMessage").GetString().ShouldBe(message);
        }

        // the user sees the progress
        var userJob = await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}/jobs/{jobId}"));
        userJob.GetProperty("status").GetString().ShouldBe("generating_clips");
        userJob.GetProperty("events").GetArrayLength().ShouldBeGreaterThan(3);
        (await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}"))).GetProperty("project").GetProperty("status").GetString().ShouldBe("processing");

        var candidates = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", new
        {
            candidates = new object[]
            {
                new { rank = 1, startSeconds = 100.5, endSeconds = 130.0, score = 91, category = "reaction", reasoning = "Pico de energia de áudio", transcript = "que clutch!", signals = new { audio = new { available = true, energyZ = 3.1 }, transcript = new { available = true }, visual = new { available = false } } },
                new { rank = 2, startSeconds = 300.0, endSeconds = 340.0, score = 75, category = "funny", reasoning = "Risadas", transcript = "kkkk", signals = new { audio = new { available = true } } },
            },
        }, Json);
        candidates.StatusCode.ShouldBe(HttpStatusCode.OK, await candidates.Content.ReadAsStringAsync());
        var ids = (await ReadJsonAsync(candidates)).GetProperty("ids").EnumerateArray().Select(e => e.GetGuid()).ToList();
        ids.Count.ShouldBe(2);

        // replacing candidates keeps only the new set
        var replaced = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", new
        {
            candidates = new object[]
            {
                new { rank = 1, startSeconds = 100.5, endSeconds = 130.0, score = 91, category = "reaction", reasoning = "Pico de energia de áudio", transcript = "que clutch!", signals = new { audio = new { available = true, energyZ = 3.1 }, transcript = new { available = true }, visual = new { available = false } } },
                new { rank = 2, startSeconds = 300.0, endSeconds = 340.0, score = 75, category = "funny", reasoning = "Risadas", transcript = "kkkk", signals = new { audio = new { available = true } } },
                new { rank = 3, startSeconds = 500.0, endSeconds = 520.0, score = 40, category = "highlight", reasoning = "x", transcript = "", signals = new { } },
            },
        }, Json);
        ids = (await ReadJsonAsync(replaced)).GetProperty("ids").EnumerateArray().Select(e => e.GetGuid()).ToList();
        ids.Count.ShouldBe(3);
        (await WithDbAsync(db => db.ClipCandidates.CountAsync(c => c.ProjectId == projectId))).ShouldBe(3);

        // upload urls for a render + thumbnail + analysis artifacts
        var urls = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/upload-urls", new
        {
            files = new[]
            {
                new { kind = "clip_render", fileName = "clip-1.mp4", contentType = "video/mp4", sizeBytes = (long?)4096 },
                new { kind = "thumbnail", fileName = "clip-1.jpg", contentType = "image/jpeg", sizeBytes = (long?)null },
                new { kind = "analysis", fileName = "transcript.json", contentType = "application/json", sizeBytes = (long?)null },
            },
        }, Json);
        urls.StatusCode.ShouldBe(HttpStatusCode.OK, await urls.Content.ReadAsStringAsync());
        var files = (await ReadJsonAsync(urls)).GetProperty("files").EnumerateArray().ToList();
        files.Count.ShouldBe(3);
        files[0].GetProperty("key").GetString().ShouldBe($"users/{userId}/jobs/{jobId}/clip_render/clip-1.mp4");
        files[0].GetProperty("method").GetString().ShouldBe("PUT");
        var renderBytes = FakeMp4();
        (await worker.PutAsync(files[0].GetProperty("url").GetString(), new ByteArrayContent(renderBytes))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await worker.PutAsync(files[1].GetProperty("url").GetString(), new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await worker.PutAsync(files[2].GetProperty("url").GetString(), new StringContent("{\"segments\":[]}"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var clips = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/clips", new
        {
            clips = new[]
            {
                new
                {
                    candidateId = ids[0], title = "Que clutch!", aspectRatio = "9:16", startSeconds = 100.5, endSeconds = 130.0,
                    subtitles = new { enabled = true, style = "bold", position = "bottom" }, framing = new { mode = "center" },
                    render = new { storageKey = files[0].GetProperty("key").GetString(), width = 1080, height = 1920, durationSeconds = 29.5, sizeBytes = renderBytes.Length, hasSubtitles = true },
                    thumbnailKey = files[1].GetProperty("key").GetString(),
                },
            },
        }, Json);
        clips.StatusCode.ShouldBe(HttpStatusCode.OK, await clips.Content.ReadAsStringAsync());
        var created = (await ReadJsonAsync(clips)).GetProperty("clips")[0];
        created.GetProperty("candidateId").GetGuid().ShouldBe(ids[0]);
        var clipId = created.GetProperty("clipId").GetGuid();

        var analysis = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/analysis", new
        {
            transcriptKey = files[2].GetProperty("key").GetString(),
            summary = new { signals = new { audio = new { available = true }, transcript = new { available = true, model = "faster-whisper/tiny" }, visual = new { available = false } }, language = "pt", processedSeconds = 600, webcamRegion = new { x = 0.75, y = 0.7, w = 0.25, h = 0.3 } },
        }, Json);
        analysis.StatusCode.ShouldBe(HttpStatusCode.OK, await analysis.Content.ReadAsStringAsync());

        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "finalizing", progress = 95 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var complete = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = 600, summary = new { candidatesFound = 3, clipsRendered = 1 } }, Json);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
        var completed = await ReadJsonAsync(complete);
        completed.GetProperty("status").GetString().ShouldBe("completed");
        completed.GetProperty("progress").GetInt32().ShouldBe(100);
        completed.GetProperty("result").GetProperty("candidatesFound").GetInt32().ShouldBe(3);
        completed.GetProperty("result").GetProperty("clipsRendered").GetInt32().ShouldBe(1);
        completed.GetProperty("result").GetProperty("webcamRegion").GetProperty("x").GetDouble().ShouldBe(0.75);

        // project ready, usage recorded, notification created
        var project = await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}"));
        project.GetProperty("project").GetProperty("status").GetString().ShouldBe("ready");
        project.GetProperty("candidateCount").GetInt32().ShouldBe(3);
        project.GetProperty("clipCount").GetInt32().ShouldBe(1);
        var usageRecords = await WithDbAsync(db => db.UsageRecords.Where(u => u.UserId == userId).ToListAsync());
        usageRecords.ShouldContain(u => u.Kind == UsageKind.ProcessingSeconds && u.Quantity == 600 && u.JobId == jobId);
        var job = await WithDbAsync(db => db.ProcessingJobs.FirstAsync(j => j.Id == jobId));
        job.ReservedSeconds.ShouldBe(0);
        job.FinishedAt.ShouldNotBeNull();
        job.TranscriptStorageKey.ShouldBe(files[2].GetProperty("key").GetString());
        var notifications = await ReadJsonAsync(await client.GetAsync("/api/notifications?unreadOnly=true"));
        notifications.GetProperty("items").EnumerateArray().ShouldContain(n => n.GetProperty("type").GetString() == "job_completed");

        // candidates for the user, with preview URL
        var userCandidates = await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}/candidates?sort=score"));
        var items = userCandidates.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(3);
        items[0].GetProperty("score").GetInt32().ShouldBe(91);
        items[0].GetProperty("clipId").GetGuid().ShouldBe(clipId);
        items[0].GetProperty("status").GetString().ShouldBe("rendered");
        items[0].GetProperty("previewable").GetBoolean().ShouldBeTrue();
        items[0].GetProperty("previewUrl").GetString()!.ShouldContain("/api/storage/download?");
        items[0].GetProperty("durationSeconds").GetDecimal().ShouldBe(29.5m);
        userCandidates.GetProperty("signals").GetProperty("transcript").GetProperty("available").GetBoolean().ShouldBeTrue();
        userCandidates.GetProperty("signals").GetProperty("visual").GetProperty("available").GetBoolean().ShouldBeFalse();
        (await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}/candidates?category=funny"))).GetProperty("items").GetArrayLength().ShouldBe(1);

        // clip listed with a working download URL
        var clipList = await ReadJsonAsync(await client.GetAsync("/api/clips"));
        clipList.GetProperty("total").GetInt32().ShouldBe(1);
        var summary = clipList.GetProperty("items")[0];
        summary.GetProperty("id").GetGuid().ShouldBe(clipId);
        summary.GetProperty("status").GetString().ShouldBe("ready");
        summary.GetProperty("category").GetString().ShouldBe("reaction");
        summary.GetProperty("score").GetInt32().ShouldBe(91);
        summary.GetProperty("projectTitle").GetString().ShouldBe("Live do campeonato");
        summary.GetProperty("thumbnailUrl").GetString().ShouldNotBeNullOrEmpty();
        summary.GetProperty("currentRender").GetProperty("version").GetInt32().ShouldBe(1);

        var detail = await ReadJsonAsync(await client.GetAsync($"/api/clips/{clipId}"));
        detail.GetProperty("currentRender").GetProperty("streamUrl").GetString().ShouldNotBeNullOrEmpty();
        detail.GetProperty("renders").GetArrayLength().ShouldBe(1);
        detail.GetProperty("reasoning").GetString().ShouldBe("Pico de energia de áudio");
        detail.GetProperty("source").GetProperty("durationSeconds").GetDecimal().ShouldBe(600m);

        var download = await ReadJsonAsync(await client.GetAsync($"/api/clips/{clipId}/download"));
        download.GetProperty("fileName").GetString().ShouldBe("que-clutch.mp4");
        download.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(10));
        var url = download.GetProperty("url").GetString()!;
        var downloaded = await client.GetAsync(url);
        downloaded.StatusCode.ShouldBe(HttpStatusCode.OK);
        downloaded.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("que-clutch.mp4");
        (await downloaded.Content.ReadAsByteArrayAsync()).ShouldBe(renderBytes);

        // range requests are honoured
        using var range = new HttpRequestMessage(HttpMethod.Get, url);
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);
        var partial = await client.SendAsync(range);
        partial.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsByteArrayAsync()).Length.ShouldBe(10);

        // dashboard reflects the work
        var dashboard = await ReadJsonAsync(await client.GetAsync("/api/dashboard/summary"));
        dashboard.GetProperty("clipsTotal").GetInt32().ShouldBe(1);
        dashboard.GetProperty("minutesProcessedThisPeriod").GetDecimal().ShouldBe(10m);
        dashboard.GetProperty("usage").GetProperty("processingSecondsUsed").GetInt64().ShouldBe(600);
        dashboard.GetProperty("recentClips").GetArrayLength().ShouldBe(1);
        dashboard.GetProperty("activeJobs").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Invalid_status_transition_returns_409()
    {
        var (client, _, _) = await RegisterAsync();
        var (_, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "analyzing" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "preparing_media" }, Json), HttpStatusCode.Conflict, "conflict");
        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "queued" }, Json), HttpStatusCode.Conflict, "conflict");
        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "bogus" }, Json), HttpStatusCode.BadRequest, "validation_failed");

        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "cancelled" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "analyzing" }, Json), HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task Cancel_queued_job_is_immediate_and_running_job_is_requested_through_heartbeat()
    {
        var (client, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");

        // queued → cancelled immediately
        var (projectId, jobId) = await CreateProjectAsync(client);
        var cancel = await client.PostAsync($"/api/projects/{projectId}/jobs/{jobId}/cancel", null);
        cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted, await cancel.Content.ReadAsStringAsync());
        (await ReadJsonAsync(cancel)).GetProperty("job").GetProperty("status").GetString().ShouldBe("cancelled");
        (await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}"))).GetProperty("project").GetProperty("status").GetString().ShouldBe("cancelled");
        await ShouldBeProblemAsync(await client.PostAsync($"/api/projects/{projectId}/jobs/{jobId}/cancel", null), HttpStatusCode.Conflict, "job_not_cancellable");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldBeNull();

        // running → cancel requested → heartbeat → worker reports cancelled
        var (project2, job2) = await CreateProjectAsync(client);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{job2}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var requested = await client.PostAsync($"/api/projects/{project2}/jobs/{job2}/cancel", null);
        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var requestedJob = (await ReadJsonAsync(requested)).GetProperty("job");
        requestedJob.GetProperty("status").GetString().ShouldBe("preparing_media");
        requestedJob.GetProperty("cancelRequested").GetBoolean().ShouldBeTrue();

        var heartbeat = await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{job2}/heartbeat", new { progress = 20 }, Json));
        heartbeat.GetProperty("cancelRequested").GetBoolean().ShouldBeTrue();

        var cancelled = await worker.PostAsJsonAsync($"/internal/jobs/{job2}/status", new { status = "cancelled", message = "Cancelado pelo usuário" }, Json);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());
        var job = await WithDbAsync(db => db.ProcessingJobs.FirstAsync(j => j.Id == job2));
        job.Status.ShouldBe(JobStatus.Cancelled);
        job.ReservedSeconds.ShouldBe(0);
        job.FinishedAt.ShouldNotBeNull();
        (await WithDbAsync(db => db.Projects.Where(p => p.Id == project2).Select(p => p.Status).FirstAsync())).ShouldBe(ProjectStatus.Cancelled);
        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{job2}/heartbeat", new { }, Json), HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task Retryable_failure_requeues_until_attempts_are_exhausted()
    {
        var (client, _, _) = await RegisterAsync();
        var (projectId, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");

        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull().GetProperty("attempt").GetInt32().ShouldBe(1);
        var fail1 = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/fail", new { code = "worker_error", message = "boom", retryable = true }, Json);
        fail1.StatusCode.ShouldBe(HttpStatusCode.OK, await fail1.Content.ReadAsStringAsync());
        var requeued = await ReadJsonAsync(fail1);
        requeued.GetProperty("status").GetString().ShouldBe("queued");
        requeued.GetProperty("attempt").GetInt32().ShouldBe(1);
        requeued.GetProperty("errorCode").GetString().ShouldBe("worker_error");

        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull().GetProperty("attempt").GetInt32().ShouldBe(2);
        (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/fail", new { code = "worker_error", message = "boom", retryable = true }, Json))).GetProperty("status").GetString().ShouldBe("queued");

        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull().GetProperty("attempt").GetInt32().ShouldBe(3);
        var fail3 = await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/fail", new { code = "worker_error", message = "boom again", retryable = true }, Json));
        fail3.GetProperty("status").GetString().ShouldBe("failed");
        fail3.GetProperty("attempt").GetInt32().ShouldBe(3);
        fail3.GetProperty("errorMessage").GetString().ShouldBe("boom again");

        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldBeNull();
        (await WithDbAsync(db => db.Projects.Where(p => p.Id == projectId).Select(p => p.Status).FirstAsync())).ShouldBe(ProjectStatus.Failed);
        (await WithDbAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationType.JobFailed))).ShouldBe(1);
        var events = await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}/jobs/{jobId}/events"));
        events.GetProperty("items").EnumerateArray().Count(e => e.GetProperty("level").GetString() == "warning").ShouldBe(2);

        // non-retryable failure is terminal immediately
        var (_, job2) = await CreateProjectAsync(client);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{job2}/fail", new { code = "media_error", message = "corrompido", retryable = false }, Json))).GetProperty("status").GetString().ShouldBe("failed");
        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{job2}/fail", new { code = "x", message = "y", retryable = false }, Json), HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task Same_status_post_is_an_idempotent_progress_update()
    {
        var (client, _, _) = await RegisterAsync();
        var (projectId, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();

        // the claim already moved the job to preparing_media: posting it again is not a conflict
        var again = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "preparing_media", progress = 3, message = "Baixando arquivo" }, Json);
        again.StatusCode.ShouldBe(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(again);
        body.GetProperty("status").GetString().ShouldBe("preparing_media");
        body.GetProperty("progress").GetInt32().ShouldBe(3);
        body.GetProperty("statusMessage").GetString().ShouldBe("Baixando arquivo");

        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "analyzing", progress = 10, message = "Analisando áudio" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var leaseBefore = await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).Select(j => j.LeaseExpiresAt).FirstAsync());
        var eventsBefore = await WithDbAsync(db => db.ProcessingJobEvents.CountAsync(e => e.JobId == jobId));

        // a throttled progress post with the current status updates progress/message and extends the lease, without a transition event
        var progress = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "analyzing", progress = 25, message = "Transcrevendo áudio" }, Json);
        progress.StatusCode.ShouldBe(HttpStatusCode.OK, await progress.Content.ReadAsStringAsync());
        var job = await WithDbAsync(db => db.ProcessingJobs.FirstAsync(j => j.Id == jobId));
        job.Status.ShouldBe(JobStatus.Analyzing);
        job.Progress.ShouldBe((short)25);
        job.StatusMessage.ShouldBe("Transcrevendo áudio");
        job.LeaseExpiresAt.ShouldNotBeNull();
        job.LeaseExpiresAt.Value.ShouldBeGreaterThanOrEqualTo(leaseBefore!.Value);
        (await WithDbAsync(db => db.ProcessingJobEvents.CountAsync(e => e.JobId == jobId))).ShouldBe(eventsBefore);
        (await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}/jobs/{jobId}"))).GetProperty("progress").GetInt32().ShouldBe(25);

        // going backwards is still a conflict
        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "preparing_media" }, Json), HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task Worker_id_is_mandatory_and_stale_workers_are_rejected_after_a_reclaim()
    {
        var (client, _, _) = await RegisterAsync();
        var (_, jobId) = await CreateProjectAsync(client);
        var worker1 = CreateInternalClient("worker-1");
        var worker2 = CreateInternalClient("worker-2");
        var anonymous = CreateInternalClient();
        (await ClaimAsync(worker1, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker1.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var candidates = new { candidates = new[] { new { rank = 1, startSeconds = 1.0, endSeconds = 20.0, score = 50, category = "highlight", reasoning = "r", transcript = "t", signals = new { } } } };
        var uploadUrls = new { files = new[] { new { kind = "thumbnail", fileName = "x.jpg", contentType = "image/jpeg" } } };
        var clips = new { clips = new[] { new { candidateId = Guid.NewGuid(), render = new { storageKey = "users/x/jobs/y/clip_render/a.mp4", hasSubtitles = false } } } };
        var reports = new (string Path, object Body)[]
        {
            ("heartbeat", new { progress = 55 }),
            ("status", new { status = "detecting_moments", progress = 60 }),
            ("events", new { level = "warning", message = "stale" }),
            ("upload-urls", uploadUrls),
            ("analysis", new { summary = new { language = "en" } }),
            ("candidates", candidates),
            ("clips", clips),
            ("complete", new { processedSeconds = 600 }),
            ("fail", new { code = "worker_error", message = "stale", retryable = false }),
        };

        // without a worker id every report is refused, even with the API key
        foreach (var (path, body) in reports)
        {
            await ShouldBeProblemAsync(await anonymous.PostAsJsonAsync($"/internal/jobs/{jobId}/{path}", body, Json), HttpStatusCode.BadRequest, "validation_failed");
        }
        (await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).Select(j => j.Status).FirstAsync())).ShouldBe(JobStatus.PreparingMedia);

        // the lease expires and the reaper requeues the job: the old worker no longer owns it
        await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await using (var scope = Factory.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<InternalJobService>().ReapExpiredLeasesAsync(CancellationToken.None)).ShouldBe(1);
        }
        foreach (var (path, body) in reports)
        {
            await ShouldBeProblemAsync(await worker1.PostAsJsonAsync($"/internal/jobs/{jobId}/{path}", body, Json), HttpStatusCode.Conflict, "conflict");
        }
        (await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).Select(j => j.Status).FirstAsync())).ShouldBe(JobStatus.Queued);

        // worker-2 re-claims and runs the job; worker-1 cannot interleave with, fail or finish it
        (await ClaimAsync(worker2, "worker-2", "analyze_source")).ShouldNotBeNull().GetProperty("attempt").GetInt32().ShouldBe(2);
        (await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "analyzing", progress = 10 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var (path, body) in reports)
        {
            await ShouldBeProblemAsync(await worker1.PostAsJsonAsync($"/internal/jobs/{jobId}/{path}", body, Json), HttpStatusCode.Conflict, "conflict");
        }
        var job = await WithDbAsync(db => db.ProcessingJobs.FirstAsync(j => j.Id == jobId));
        job.Status.ShouldBe(JobStatus.Analyzing);
        job.WorkerId.ShouldBe("worker-2");
        job.Progress.ShouldBe((short)10);
        (await WithDbAsync(db => db.ClipCandidates.CountAsync(c => c.JobId == jobId))).ShouldBe(0);

        var heartbeat = await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/heartbeat", new { progress = 40 }, Json);
        heartbeat.StatusCode.ShouldBe(HttpStatusCode.OK, await heartbeat.Content.ReadAsStringAsync());
        (await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = 600 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).Select(j => j.Status).FirstAsync())).ShouldBe(JobStatus.Completed);
        // nothing is accepted after completion, not even from the owner
        await ShouldBeProblemAsync(await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/events", new { level = "info", message = "late" }, Json), HttpStatusCode.Conflict, "conflict");
        await ShouldBeProblemAsync(await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", candidates, Json), HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task Cancel_after_the_analysis_started_consumes_the_reserved_minutes_and_keeps_the_candidates()
    {
        var (client, userId, _) = await RegisterAsync();
        var (projectId, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        // the whole monthly budget of the free plan
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", new { durationSeconds = 3600.0, width = 1920, height = 1080, fps = 30.0, hasAudio = true, hasVideo = true, container = "mp4", sizeBytes = 4096 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "analyzing", progress = 10 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "detecting_moments", progress = 60 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var candidates = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", new
        {
            candidates = new[] { new { rank = 1, startSeconds = 10.0, endSeconds = 40.0, score = 80, category = "highlight", reasoning = "r", transcript = "t", signals = new { } } },
        }, Json);
        candidates.StatusCode.ShouldBe(HttpStatusCode.OK, await candidates.Content.ReadAsStringAsync());
        var candidateId = (await ReadJsonAsync(candidates)).GetProperty("ids")[0].GetGuid();

        // the user cancels at ~65 %: the worker aborts at its next heartbeat and reports cancelled
        (await client.PostAsync($"/api/projects/{projectId}/jobs/{jobId}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/heartbeat", new { progress = 65 }, Json))).GetProperty("cancelRequested").GetBoolean().ShouldBeTrue();
        var cancelled = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "cancelled", message = "Cancelado pelo usuário" }, Json);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());

        // the analysis ran: the reserved minutes are consumed
        var usage = await ReadJsonAsync(await client.GetAsync("/api/account/usage"));
        usage.GetProperty("used").GetProperty("processingSeconds").GetInt64().ShouldBe(3600);
        usage.GetProperty("remaining").GetProperty("processingSeconds").GetInt64().ShouldBe(0);
        var record = await WithDbAsync(db => db.UsageRecords.SingleAsync(u => u.UserId == userId && u.Kind == UsageKind.ProcessingSeconds));
        record.JobId.ShouldBe(jobId);
        record.Quantity.ShouldBe(3600m);
        using (var metadata = JsonDocument.Parse(record.Metadata.ShouldNotBeNull()))
        {
            metadata.RootElement.GetProperty("reason").GetString().ShouldBe("cancelled");
            metadata.RootElement.GetProperty("reservedSeconds").GetInt32().ShouldBe(3600);
        }
        var job = await WithDbAsync(db => db.ProcessingJobs.FirstAsync(j => j.Id == jobId));
        job.Status.ShouldBe(JobStatus.Cancelled);
        job.ReservedSeconds.ShouldBe(0);

        // a second full analysis in the same period is refused at probe time
        var (_, job2) = await CreateProjectAsync(client);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{job2}/probe", new { durationSeconds = 60.0, hasAudio = true, hasVideo = true, container = "mp4" }, Json), HttpStatusCode.Conflict, "quota_exceeded");

        // the candidates of the cancelled job stay usable (they were paid for)
        (await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}/candidates"))).GetProperty("items").GetArrayLength().ShouldBe(1);
        var clip = await client.PostAsJsonAsync($"/api/projects/{projectId}/candidates/{candidateId}/clips", new { title = "Clip" }, Json);
        clip.StatusCode.ShouldBe(HttpStatusCode.Accepted, await clip.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cancel_while_preparing_the_media_releases_the_reservation()
    {
        var (client, userId, _) = await RegisterAsync();
        var (projectId, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync($"/api/projects/{projectId}/jobs/{jobId}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "cancelled" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await WithDbAsync(db => db.UsageRecords.CountAsync(u => u.UserId == userId))).ShouldBe(0);
        (await ReadJsonAsync(await client.GetAsync("/api/account/usage"))).GetProperty("remaining").GetProperty("processingSeconds").GetInt64().ShouldBe(3600);
    }

    [Fact]
    public async Task Terminal_failure_charges_the_processed_seconds_reported_by_the_worker()
    {
        var (client, userId, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");

        // a render failure after a full analysis: the worker reports the whole duration
        var (_, job1) = await CreateProjectAsync(client);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{job1}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await worker.PostAsJsonAsync($"/internal/jobs/{job1}/status", new { status = "generating_clips", progress = 70 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{job1}/fail", new { code = "render_failed", message = "ffmpeg", retryable = false, processedSeconds = 600 }, Json))).GetProperty("status").GetString().ShouldBe("failed");
        (await ReadJsonAsync(await client.GetAsync("/api/account/usage"))).GetProperty("used").GetProperty("processingSeconds").GetInt64().ShouldBe(600);

        // a retryable failure is not charged (the retry re-processes the file) ...
        var (_, job2) = await CreateProjectAsync(client);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{job2}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{job2}/fail", new { code = "worker_error", message = "boom", retryable = true, processedSeconds = 600 }, Json))).GetProperty("status").GetString().ShouldBe("queued");
        (await ReadJsonAsync(await client.GetAsync("/api/account/usage"))).GetProperty("used").GetProperty("processingSeconds").GetInt64().ShouldBe(600);
        // ... and a reported value above the reservation is capped by it
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{job2}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{job2}/fail", new { code = "invalid_media", message = "x", retryable = false, processedSeconds = 99999 }, Json))).GetProperty("status").GetString().ShouldBe("failed");
        (await ReadJsonAsync(await client.GetAsync("/api/account/usage"))).GetProperty("used").GetProperty("processingSeconds").GetInt64().ShouldBe(1200);

        // failures without a report (nothing processed, lost lease) charge nothing
        var (_, job3) = await CreateProjectAsync(client);
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{job3}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{job3}/fail", new { code = "media_error", message = "x", retryable = false }, Json))).GetProperty("status").GetString().ShouldBe("failed");
        (await ReadJsonAsync(await client.GetAsync("/api/account/usage"))).GetProperty("used").GetProperty("processingSeconds").GetInt64().ShouldBe(1200);
        (await WithDbAsync(db => db.UsageRecords.CountAsync(u => u.UserId == userId && u.Kind == UsageKind.ProcessingSeconds))).ShouldBe(2);
    }

    [Fact]
    public async Task Reaper_requeues_expired_leases_and_fails_exhausted_jobs()
    {
        var (client, _, _) = await RegisterAsync();
        var (_, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();

        async Task<int> ReapAsync()
        {
            await using var scope = Factory.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<InternalJobService>().ReapExpiredLeasesAsync(CancellationToken.None);
        }

        (await ReapAsync()).ShouldBe(0); // lease still valid

        await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        (await ReapAsync()).ShouldBe(1);
        var job = await WithDbAsync(db => db.ProcessingJobs.FirstAsync(j => j.Id == jobId));
        job.Status.ShouldBe(JobStatus.Queued);
        job.WorkerId.ShouldBeNull();
        job.LeaseExpiresAt.ShouldBeNull();
        job.ErrorCode.ShouldBe("lease_expired");
        (await WithDbAsync(db => db.ProcessingJobEvents.AnyAsync(e => e.JobId == jobId && e.ToStatus == JobStatus.Queued && e.Level == JobEventLevel.Warning))).ShouldBeTrue();

        // last attempt expiring → failed
        await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Attempt, 2)));
        (await ClaimAsync(worker, "worker-2", "analyze_source")).ShouldNotBeNull().GetProperty("attempt").GetInt32().ShouldBe(3);
        await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        (await ReapAsync()).ShouldBe(1);
        (await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).Select(j => j.Status).FirstAsync())).ShouldBe(JobStatus.Failed);
    }

    [Fact]
    public async Task Worker_cannot_store_keys_outside_the_user_space()
    {
        var (client, _, _) = await RegisterAsync();
        var (_, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        var response = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/analysis", new { transcriptKey = $"users/{Guid.NewGuid()}/jobs/x/analysis/transcript.json" }, Json);
        await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        var traversal = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/upload-urls", new { files = new[] { new { kind = "analysis", fileName = "../../../etc/passwd", contentType = "text/plain" } } }, Json);
        traversal.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(traversal)).GetProperty("files")[0].GetProperty("key").GetString()!.ShouldEndWith("/analysis/passwd");
    }
}
