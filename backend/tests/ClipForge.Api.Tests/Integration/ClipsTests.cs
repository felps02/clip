using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClipForge.Api.Features.Projects;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Tests.Integration;

public class ClipsTests : TestBase
{
    public ClipsTests(ApiFactory factory) : base(factory)
    {
    }

    /// <summary>Runs an analysis that produces one rendered clip. Returns (projectId, jobId, clipId, candidateIds).</summary>
    private async Task<(Guid ProjectId, Guid JobId, Guid ClipId, List<Guid> CandidateIds)> AnalyzeWithClipAsync(HttpClient client, HttpClient worker)
    {
        var (projectId, jobId) = await CreateProjectAsync(client, "Live");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var candidates = await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", new
        {
            candidates = new object[]
            {
                new { rank = 1, startSeconds = 10.0, endSeconds = 40.0, score = 90, category = "hype", reasoning = "a", transcript = "b", signals = new { } },
                new { rank = 2, startSeconds = 100.0, endSeconds = 120.0, score = 70, category = "quote", reasoning = "c", transcript = "d", signals = new { } },
            },
        }, Json));
        var ids = candidates.GetProperty("ids").EnumerateArray().Select(e => e.GetGuid()).ToList();
        var files = (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/upload-urls", new
        {
            files = new[] { new { kind = "clip_render", fileName = "clip-1.mp4", contentType = "video/mp4", sizeBytes = 4096 } },
        }, Json))).GetProperty("files");
        (await worker.PutAsync(files[0].GetProperty("url").GetString(), new ByteArrayContent(FakeMp4()))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var clips = await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/clips", new
        {
            clips = new[] { new { candidateId = ids[0], title = "Hype!", aspectRatio = "9:16", startSeconds = 10.0, endSeconds = 40.0, render = new { storageKey = files[0].GetProperty("key").GetString(), width = 720, height = 1280, durationSeconds = 30.0, sizeBytes = 4096, hasSubtitles = true } } },
        }, Json));
        var clipId = clips.GetProperty("clips")[0].GetProperty("clipId").GetGuid();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = 600 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return (projectId, jobId, clipId, ids);
    }

    [Fact]
    public async Task Render_flow_creates_a_new_version_with_a_new_download_url()
    {
        var (client, userId, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (projectId, _, clipId, _) = await AnalyzeWithClipAsync(client, worker);
        var firstDownload = (await ReadJsonAsync(await client.GetAsync($"/api/clips/{clipId}/download"))).GetProperty("url").GetString();

        // title only → no render needed
        var titleOnly = await ReadJsonAsync(await client.PatchAsJsonAsync($"/api/clips/{clipId}", new { title = "Hype renomeado" }, Json));
        titleOnly.GetProperty("needsRender").GetBoolean().ShouldBeFalse();
        titleOnly.GetProperty("clip").GetProperty("title").GetString().ShouldBe("Hype renomeado");

        // trim + aspect → needs render
        var patched = await ReadJsonAsync(await client.PatchAsJsonAsync($"/api/clips/{clipId}", new { startSeconds = 12.0, endSeconds = 42.0, aspectRatio = "1:1", subtitles = new { enabled = false, style = "bold", position = "bottom" } }, Json));
        patched.GetProperty("needsRender").GetBoolean().ShouldBeTrue();
        patched.GetProperty("clip").GetProperty("aspectRatio").GetString().ShouldBe("1:1");
        patched.GetProperty("clip").GetProperty("startSeconds").GetDecimal().ShouldBe(12m);
        // invalid trim
        await ShouldBeProblemAsync(await client.PatchAsJsonAsync($"/api/clips/{clipId}", new { startSeconds = 50.0, endSeconds = 40.0 }, Json), HttpStatusCode.BadRequest, "validation_failed");

        var render = await client.PostAsync($"/api/clips/{clipId}/render", null);
        render.StatusCode.ShouldBe(HttpStatusCode.Accepted, await render.Content.ReadAsStringAsync());
        var rendered = await ReadJsonAsync(render);
        rendered.GetProperty("clip").GetProperty("status").GetString().ShouldBe("rendering");
        rendered.GetProperty("job").GetProperty("kind").GetString().ShouldBe("render_clip");
        rendered.GetProperty("render").GetProperty("version").GetInt32().ShouldBe(2);
        rendered.GetProperty("render").GetProperty("status").GetString().ShouldBe("pending");
        var renderId = rendered.GetProperty("render").GetProperty("id").GetGuid();
        var renderJobId = rendered.GetProperty("job").GetProperty("id").GetGuid();
        (await WithDbAsync(db => db.UsageRecords.CountAsync(u => u.UserId == userId && u.Kind == UsageKind.Render))).ShouldBe(1);

        // a second render while one is pending is refused
        await ShouldBeProblemAsync(await client.PostAsync($"/api/clips/{clipId}/render", null), HttpStatusCode.Conflict, "conflict");

        // worker claims the render job
        var payload = (await ClaimAsync(worker, "worker-1", "render_clip")).ShouldNotBeNull();
        payload.GetProperty("jobId").GetGuid().ShouldBe(renderJobId);
        payload.GetProperty("kind").GetString().ShouldBe("render_clip");
        var clipInfo = payload.GetProperty("clip");
        clipInfo.GetProperty("clipId").GetGuid().ShouldBe(clipId);
        clipInfo.GetProperty("renderId").GetGuid().ShouldBe(renderId);
        clipInfo.GetProperty("version").GetInt32().ShouldBe(2);
        clipInfo.GetProperty("aspectRatio").GetString().ShouldBe("1:1");
        clipInfo.GetProperty("startSeconds").GetDecimal().ShouldBe(12m);
        clipInfo.GetProperty("subtitles").GetProperty("enabled").GetBoolean().ShouldBeFalse();
        clipInfo.GetProperty("framing").GetProperty("focusX").GetDouble().ShouldBe(0.5);
        payload.GetProperty("analysis").GetProperty("transcriptUrl").ValueKind.ShouldBe(JsonValueKind.Null);
        payload.GetProperty("limits").GetProperty("maxRenderHeight").GetInt32().ShouldBe(720);
        payload.GetProperty("source").GetProperty("durationSeconds").GetDecimal().ShouldBe(600m);

        (await worker.PostAsJsonAsync($"/internal/jobs/{renderJobId}/status", new { status = "generating_clips", progress = 50 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var files = (await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{renderJobId}/upload-urls", new
        {
            files = new object[] { new { kind = "clip_render", fileName = "v2.mp4", contentType = "video/mp4", sizeBytes = 8192 }, new { kind = "thumbnail", fileName = "v2.jpg", contentType = "image/jpeg" } },
        }, Json))).GetProperty("files");
        var v2Bytes = FakeMp4(8192);
        (await worker.PutAsync(files[0].GetProperty("url").GetString(), new ByteArrayContent(v2Bytes))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await worker.PutAsync(files[1].GetProperty("url").GetString(), new ByteArrayContent([0xFF, 0xD8]))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await worker.PostAsJsonAsync($"/internal/jobs/{renderJobId}/render-result", new
        {
            renderId, status = "ready", storageKey = files[0].GetProperty("key").GetString(), width = 720, height = 720, durationSeconds = 30.0, sizeBytes = v2Bytes.Length, hasSubtitles = false, thumbnailKey = files[1].GetProperty("key").GetString(),
        }, Json);
        result.StatusCode.ShouldBe(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
        var complete = await worker.PostAsJsonAsync($"/internal/jobs/{renderJobId}/complete", new { processedSeconds = 30 }, Json);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());

        var detail = await ReadJsonAsync(await client.GetAsync($"/api/clips/{clipId}"));
        detail.GetProperty("status").GetString().ShouldBe("ready");
        detail.GetProperty("currentRender").GetProperty("version").GetInt32().ShouldBe(2);
        detail.GetProperty("currentRender").GetProperty("width").GetInt32().ShouldBe(720);
        detail.GetProperty("renders").GetArrayLength().ShouldBe(2);
        detail.GetProperty("activeJob").ValueKind.ShouldBe(JsonValueKind.Null);

        var download = await ReadJsonAsync(await client.GetAsync($"/api/clips/{clipId}/download"));
        var url = download.GetProperty("url").GetString()!;
        url.ShouldNotBe(firstDownload);
        download.GetProperty("fileName").GetString().ShouldBe("hype-renomeado.mp4");
        (await client.GetByteArrayAsync(url)).ShouldBe(v2Bytes);

        // an explicit renderId downloads the older version
        var renders = await WithDbAsync(db => db.ClipRenders.Where(r => r.ClipId == clipId).OrderBy(r => r.Version).ToListAsync());
        var v1 = await ReadJsonAsync(await client.GetAsync($"/api/clips/{clipId}/download?renderId={renders[0].Id}"));
        (await client.GetByteArrayAsync(v1.GetProperty("url").GetString()!)).ShouldBe(FakeMp4());

        // no extra processing_seconds usage for render jobs; one render usage record
        var usage = await WithDbAsync(db => db.UsageRecords.Where(u => u.UserId == userId).ToListAsync());
        usage.Count(u => u.Kind == UsageKind.ProcessingSeconds).ShouldBe(1);
        usage.Count(u => u.Kind == UsageKind.Render).ShouldBe(1);
        (await WithDbAsync(db => db.Notifications.CountAsync(n => n.UserId == userId && n.Type == NotificationType.ClipReady))).ShouldBe(1);
        _ = projectId;
    }

    [Fact]
    public async Task Failed_render_keeps_the_previous_version()
    {
        var (client, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (_, _, clipId, _) = await AnalyzeWithClipAsync(client, worker);
        var rendered = await ReadJsonAsync(await client.PostAsync($"/api/clips/{clipId}/render", null));
        var renderId = rendered.GetProperty("render").GetProperty("id").GetGuid();
        var jobId = rendered.GetProperty("job").GetProperty("id").GetGuid();
        (await ClaimAsync(worker, "worker-1", "render_clip")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/render-result", new { renderId, status = "failed", error = "ffmpeg exploded" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/fail", new { code = "render_error", message = "ffmpeg exploded", retryable = false }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await ReadJsonAsync(await client.GetAsync($"/api/clips/{clipId}"));
        detail.GetProperty("status").GetString().ShouldBe("ready");
        detail.GetProperty("currentRender").GetProperty("version").GetInt32().ShouldBe(1);
        detail.GetProperty("renders").EnumerateArray().ShouldContain(r => r.GetProperty("version").GetInt32() == 2 && r.GetProperty("status").GetString() == "failed");
    }

    [Fact]
    public async Task Renders_per_month_limit_returns_409_plan_limit()
    {
        var (client, userId, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (projectId, _, clipId, _) = await AnalyzeWithClipAsync(client, worker);

        await WithDbAsync(async db =>
        {
            var sub = await db.Subscriptions.FirstAsync(s => s.UserId == userId);
            for (var i = 0; i < 10; i++)
            {
                db.UsageRecords.Add(new UsageRecord
                {
                    UserId = userId, SubscriptionId = sub.Id, Kind = UsageKind.Render, Quantity = 1,
                    PeriodStart = sub.CurrentPeriodStart, PeriodEnd = sub.CurrentPeriodEnd, ProjectId = projectId, ClipId = clipId,
                });
            }
            await db.SaveChangesAsync();
        });

        var render = await client.PostAsync($"/api/clips/{clipId}/render", null);
        await ShouldBeProblemAsync(render, HttpStatusCode.Conflict, "plan_limit");
        (await ReadJsonAsync(render)).GetProperty("detail").GetString()!.ShouldContain("10");
        var dashboard = await ReadJsonAsync(await client.GetAsync("/api/dashboard/summary"));
        dashboard.GetProperty("warnings").EnumerateArray().ShouldContain(w => w.GetProperty("code").GetString() == "render_quota_exhausted");
    }

    [Fact]
    public async Task Clip_from_candidate_creates_a_clip_and_a_render_job()
    {
        var (client, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (projectId, _, _, ids) = await AnalyzeWithClipAsync(client, worker);

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/candidates/{ids[1]}/clips", new { title = "Frase marcante", aspectRatio = "16:9", startSeconds = 98.0, endSeconds = 125.0 }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(response);
        var clip = body.GetProperty("clip");
        clip.GetProperty("title").GetString().ShouldBe("Frase marcante");
        clip.GetProperty("aspectRatio").GetString().ShouldBe("16:9");
        clip.GetProperty("category").GetString().ShouldBe("quote");
        clip.GetProperty("score").GetInt32().ShouldBe(70);
        clip.GetProperty("status").GetString().ShouldBe("rendering");
        clip.GetProperty("startSeconds").GetDecimal().ShouldBe(98m);
        body.GetProperty("job").GetProperty("kind").GetString().ShouldBe("render_clip");

        var payload = (await ClaimAsync(worker, "worker-1", "render_clip")).ShouldNotBeNull();
        payload.GetProperty("clip").GetProperty("version").GetInt32().ShouldBe(1);
        payload.GetProperty("clip").GetProperty("title").GetString().ShouldBe("Frase marcante");

        var candidates = await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}/candidates"));
        candidates.GetProperty("items").EnumerateArray().First(c => c.GetProperty("id").GetGuid() == ids[1]).GetProperty("clipId").GetGuid().ShouldBe(clip.GetProperty("id").GetGuid());
        (await client.PostAsJsonAsync($"/api/projects/{projectId}/candidates/{Guid.NewGuid()}/clips", new { }, Json)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Render_jobs_count_against_the_concurrent_jobs_cap_even_when_requested_in_parallel()
    {
        var (client, userId, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (projectId, _, clipId, ids) = await AnalyzeWithClipAsync(client, worker);

        // free plan: one concurrent job → of five parallel clip creations exactly one render job is queued
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            client.PostAsJsonAsync($"/api/projects/{projectId}/candidates/{ids[i % 2]}/clips", new { title = $"Clip {i}" }, Json)));
        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Accepted))
        {
            await ShouldBeProblemAsync(refused, HttpStatusCode.Conflict, "plan_limit");
        }
        (await WithDbAsync(db => db.ProcessingJobs.CountAsync(j => j.UserId == userId && j.Kind == JobKind.RenderClip))).ShouldBe(1);
        (await WithDbAsync(db => db.Clips.CountAsync(c => c.UserId == userId))).ShouldBe(2); // the auto-rendered one + the accepted one

        // while that render job is active the editor's re-render and a new analysis are refused as well
        await ShouldBeProblemAsync(await client.PostAsync($"/api/clips/{clipId}/render", null), HttpStatusCode.Conflict, "plan_limit");
        var assetId = await UploadAssetAsync(client);
        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/projects", new { source = new { kind = "upload", mediaAssetId = assetId } }, Json), HttpStatusCode.Conflict, "plan_limit");

        // once the worker finishes the render job the next request goes through
        var renderJobId = await WithDbAsync(db => db.ProcessingJobs.Where(j => j.UserId == userId && j.Kind == JobKind.RenderClip).Select(j => j.Id).FirstAsync());
        (await ClaimAsync(worker, "worker-1", "render_clip")).ShouldNotBeNull().GetProperty("jobId").GetGuid().ShouldBe(renderJobId);
        (await worker.PostAsJsonAsync($"/internal/jobs/{renderJobId}/fail", new { code = "render_failed", message = "x", retryable = false }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var render = await client.PostAsync($"/api/clips/{clipId}/render", null);
        render.StatusCode.ShouldBe(HttpStatusCode.Accepted, await render.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Clips_per_candidate_are_capped()
    {
        var (client, userId, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (projectId, _, _, ids) = await AnalyzeWithClipAsync(client, worker);
        await WithDbAsync(async db =>
        {
            for (var i = 0; i < ProjectService.MaxClipsPerCandidate; i++)
            {
                db.Clips.Add(new Clip { UserId = userId, ProjectId = projectId, CandidateId = ids[1], Title = $"Seed {i}", StartSeconds = 100, EndSeconds = 120, Status = ClipStatus.Ready });
            }
            await db.SaveChangesAsync();
        });

        var refused = await client.PostAsJsonAsync($"/api/projects/{projectId}/candidates/{ids[1]}/clips", new { title = "Mais um" }, Json);
        await ShouldBeProblemAsync(refused, HttpStatusCode.Conflict, "conflict");
        (await ReadJsonAsync(refused)).GetProperty("detail").GetString()!.ShouldContain(ProjectService.MaxClipsPerCandidate.ToString());

        // deleting one frees a slot
        var victim = await WithDbAsync(db => db.Clips.Where(c => c.CandidateId == ids[1]).Select(c => c.Id).FirstAsync());
        (await client.DeleteAsync($"/api/clips/{victim}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var accepted = await client.PostAsJsonAsync($"/api/projects/{projectId}/candidates/{ids[1]}/clips", new { title = "Mais um" }, Json);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task List_supports_filters_search_and_sorting_and_delete_is_soft()
    {
        var (client, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (projectId, _, clipId, ids) = await AnalyzeWithClipAsync(client, worker);
        await client.PostAsJsonAsync($"/api/projects/{projectId}/candidates/{ids[1]}/clips", new { title = "Outra frase" }, Json);

        (await ReadJsonAsync(await client.GetAsync("/api/clips"))).GetProperty("total").GetInt32().ShouldBe(2);
        (await ReadJsonAsync(await client.GetAsync("/api/clips?q=OUTRA"))).GetProperty("total").GetInt32().ShouldBe(1);
        (await ReadJsonAsync(await client.GetAsync("/api/clips?q=%25"))).GetProperty("total").GetInt32().ShouldBe(0);
        (await ReadJsonAsync(await client.GetAsync("/api/clips?category=hype"))).GetProperty("total").GetInt32().ShouldBe(1);
        (await ReadJsonAsync(await client.GetAsync("/api/clips?status=ready"))).GetProperty("total").GetInt32().ShouldBe(1);
        (await ReadJsonAsync(await client.GetAsync($"/api/clips?projectId={projectId}"))).GetProperty("total").GetInt32().ShouldBe(2);
        (await ReadJsonAsync(await client.GetAsync($"/api/clips?projectId={Guid.NewGuid()}"))).GetProperty("total").GetInt32().ShouldBe(0);
        var byScore = await ReadJsonAsync(await client.GetAsync("/api/clips?sort=score&order=desc"));
        byScore.GetProperty("items")[0].GetProperty("score").GetInt32().ShouldBe(90);
        var byDuration = await ReadJsonAsync(await client.GetAsync("/api/clips?sort=duration&order=asc"));
        byDuration.GetProperty("items")[0].GetProperty("durationSeconds").GetDecimal().ShouldBe(20m);
        var paged = await ReadJsonAsync(await client.GetAsync("/api/clips?page=2&pageSize=1"));
        paged.GetProperty("items").GetArrayLength().ShouldBe(1);
        paged.GetProperty("page").GetInt32().ShouldBe(2);

        (await client.DeleteAsync($"/api/clips/{clipId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/clips/{clipId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(await client.GetAsync("/api/clips"))).GetProperty("total").GetInt32().ShouldBe(1);
        (await WithDbAsync(db => db.Clips.Where(c => c.Id == clipId).Select(c => c.DeletedAt).FirstAsync())).ShouldNotBeNull();
    }

    [Fact]
    public async Task Tampered_download_url_is_rejected_with_403()
    {
        var (client, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (_, _, clipId, _) = await AnalyzeWithClipAsync(client, worker);
        var url = (await ReadJsonAsync(await client.GetAsync($"/api/clips/{clipId}/download"))).GetProperty("url").GetString()!;
        (await client.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        var exp = long.Parse(query["exp"]!);
        var tamperedExp = url.Replace($"exp={exp}", $"exp={exp + 86400}");
        await ShouldBeProblemAsync(await client.GetAsync(tamperedExp), HttpStatusCode.Forbidden, "forbidden");
        var tamperedKey = url.Replace("clip-1.mp4", "clip-2.mp4");
        await ShouldBeProblemAsync(await client.GetAsync(tamperedKey), HttpStatusCode.Forbidden, "forbidden");
        var tamperedSig = url.Replace("&sig=", "&sig=x");
        await ShouldBeProblemAsync(await client.GetAsync(tamperedSig), HttpStatusCode.Forbidden, "forbidden");
        var expired = url.Replace($"exp={exp}", "exp=1000");
        await ShouldBeProblemAsync(await client.GetAsync(expired), HttpStatusCode.Forbidden, "forbidden");
    }
}
