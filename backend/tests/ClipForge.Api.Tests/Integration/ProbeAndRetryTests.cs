using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClipForge.Api.Features.Internal;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Api.Tests.Integration;

/// <summary>/probe edge cases and retried analyses (lease lost after part of the output was reported).</summary>
public class ProbeAndRetryTests : TestBase
{
    public ProbeAndRetryTests(ApiFactory factory) : base(factory)
    {
    }

    private async Task ExpireLeaseAndReapAsync(Guid jobId)
    {
        await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await using var scope = Factory.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<InternalJobService>().ReapExpiredLeasesAsync(CancellationToken.None)).ShouldBe(1);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public async Task Probe_without_a_positive_duration_fails_the_job_with_invalid_media(double duration)
    {
        var (client, _, _) = await RegisterAsync();
        var (projectId, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();

        var probe = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", new { durationSeconds = duration, width = 1920, height = 1080, hasAudio = true, hasVideo = true, container = "mp4" }, Json);
        await ShouldBeProblemAsync(probe, HttpStatusCode.Conflict, "invalid_media");

        var job = await WithDbAsync(db => db.ProcessingJobs.FirstAsync(j => j.Id == jobId));
        job.Status.ShouldBe(JobStatus.Failed);
        job.ErrorCode.ShouldBe("invalid_media");
        job.ReservedSeconds.ShouldBe(0);
        (await WithDbAsync(db => db.Projects.Where(p => p.Id == projectId).Select(p => p.Status).FirstAsync())).ShouldBe(ProjectStatus.Failed);
        // the job is already failed server-side: the worker's later reports are refused
        await ShouldBeProblemAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/fail", new { code = "invalid_media", message = "x", retryable = false }, Json), HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task Probe_after_a_requeue_counts_the_jobs_reservation_once()
    {
        var (client, _, _) = await RegisterAsync();
        var (_, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        var first = await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json));
        first.GetProperty("remainingProcessingSeconds").GetInt64().ShouldBe(3000); // 3600 − 600

        // the lease expires: the job goes back to the queue still holding its 600 s reservation
        await ExpireLeaseAndReapAsync(jobId);
        (await WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).Select(j => j.ReservedSeconds).FirstAsync())).ShouldBe(600);

        var worker2 = CreateInternalClient("worker-2");
        (await ClaimAsync(worker2, "worker-2", "analyze_source")).ShouldNotBeNull().GetProperty("attempt").GetInt32().ShouldBe(2);
        var second = await ReadJsonAsync(await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json));
        second.GetProperty("remainingProcessingSeconds").GetInt64().ShouldBe(3000); // not 2400: the old reservation is replaced
        // a re-sent probe of the same attempt is idempotent too
        var resent = await ReadJsonAsync(await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json));
        resent.GetProperty("remainingProcessingSeconds").GetInt64().ShouldBe(3000);
    }

    [Fact]
    public async Task Retried_analysis_replaces_the_clips_the_previous_attempt_rendered()
    {
        var (client, userId, _) = await RegisterAsync();
        var (projectId, jobId) = await CreateProjectAsync(client);

        async Task<(List<Guid> Ids, Guid ClipId, string RenderKey, string ThumbKey)> RunUntilClipsAsync(HttpClient worker, string workerId)
        {
            (await ClaimAsync(worker, workerId, "analyze_source")).ShouldNotBeNull();
            (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "detecting_moments", progress = 60 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
            var posted = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", new
            {
                candidates = new object[]
                {
                    new { rank = 1, startSeconds = 100.0, endSeconds = 130.0, score = 90, category = "reaction", reasoning = "pico", transcript = "uau", signals = new { } },
                    new { rank = 2, startSeconds = 300.0, endSeconds = 330.0, score = 70, category = "funny", reasoning = "risadas", transcript = "kkk", signals = new { } },
                },
            }, Json);
            posted.StatusCode.ShouldBe(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync());
            var ids = (await ReadJsonAsync(posted)).GetProperty("ids").EnumerateArray().Select(e => e.GetGuid()).ToList();

            (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "generating_clips", progress = 65 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
            var urls = await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/upload-urls", new
            {
                files = new[]
                {
                    new { kind = "clip_render", fileName = "clip-1.mp4", contentType = "video/mp4" },
                    new { kind = "thumbnail", fileName = "clip-1.jpg", contentType = "image/jpeg" },
                },
            }, Json));
            var files = urls.GetProperty("files").EnumerateArray().ToList();
            (await worker.PutAsync(files[0].GetProperty("url").GetString(), new ByteArrayContent(FakeMp4()))).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await worker.PutAsync(files[1].GetProperty("url").GetString(), new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0]))).StatusCode.ShouldBe(HttpStatusCode.OK);
            var renderKey = files[0].GetProperty("key").GetString()!;
            var thumbKey = files[1].GetProperty("key").GetString()!;
            var clips = await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/clips", new
            {
                clips = new[]
                {
                    new
                    {
                        candidateId = ids[0], title = "Reação", aspectRatio = "9:16", startSeconds = 100.0, endSeconds = 130.0,
                        subtitles = new { enabled = true, style = "bold", position = "bottom" }, framing = new { mode = "auto" },
                        render = new { storageKey = renderKey, width = 720, height = 1280, durationSeconds = 30.0, sizeBytes = 4096, hasSubtitles = true },
                        thumbnailKey = thumbKey,
                    },
                },
            }, Json);
            clips.StatusCode.ShouldBe(HttpStatusCode.OK, await clips.Content.ReadAsStringAsync());
            var clipId = (await ReadJsonAsync(clips)).GetProperty("clips")[0].GetProperty("clipId").GetGuid();
            return (ids, clipId, renderKey, thumbKey);
        }

        string PathOf(string key) => Path.Combine(Factory.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar));

        // attempt 1 reports candidates and an auto-rendered clip, then its worker disappears
        var first = await RunUntilClipsAsync(CreateInternalClient("worker-1"), "worker-1");
        File.Exists(PathOf(first.RenderKey)).ShouldBeTrue();
        await ExpireLeaseAndReapAsync(jobId);

        // attempt 2 reports its candidates: the first attempt's clip, render and files are gone
        var worker2 = CreateInternalClient("worker-2");
        (await ClaimAsync(worker2, "worker-2", "analyze_source")).ShouldNotBeNull().GetProperty("attempt").GetInt32().ShouldBe(2);
        (await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var replaced = await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", new
        {
            candidates = new[] { new { rank = 1, startSeconds = 100.0, endSeconds = 131.0, score = 91, category = "reaction", reasoning = "pico", transcript = "uau", signals = new { } } },
        }, Json);
        replaced.StatusCode.ShouldBe(HttpStatusCode.OK, await replaced.Content.ReadAsStringAsync());
        var secondIds = (await ReadJsonAsync(replaced)).GetProperty("ids").EnumerateArray().Select(e => e.GetGuid()).ToList();

        (await WithDbAsync(db => db.Clips.AnyAsync(c => c.Id == first.ClipId))).ShouldBeFalse();
        (await WithDbAsync(db => db.ClipRenders.AnyAsync(r => r.ClipId == first.ClipId))).ShouldBeFalse();
        File.Exists(PathOf(first.RenderKey)).ShouldBeFalse();
        File.Exists(PathOf(first.ThumbKey)).ShouldBeFalse();
        (await WithDbAsync(db => db.ClipCandidates.Where(c => c.JobId == jobId).Select(c => c.Id).ToListAsync())).ShouldBe(secondIds);
        (await ReadJsonAsync(await client.GetAsync("/api/clips"))).GetProperty("total").GetInt32().ShouldBe(0);
        (await WithDbAsync(db => db.ProcessingJobEvents.AnyAsync(e => e.JobId == jobId && e.Level == JobEventLevel.Warning && e.Message.Contains("tentativa anterior")))).ShouldBeTrue();

        // attempt 2 renders again and completes: one clip per moment, counted once
        (await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/status", new { status = "generating_clips", progress = 65 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var urls = await ReadJsonAsync(await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/upload-urls", new { files = new[] { new { kind = "clip_render", fileName = "clip-1.mp4", contentType = "video/mp4" } } }, Json));
        var file = urls.GetProperty("files")[0];
        (await worker2.PutAsync(file.GetProperty("url").GetString(), new ByteArrayContent(FakeMp4()))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var clips = await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/clips", new
        {
            clips = new[]
            {
                new
                {
                    candidateId = secondIds[0], title = "Reação", aspectRatio = "9:16", startSeconds = 100.0, endSeconds = 131.0,
                    subtitles = new { enabled = true, style = "bold", position = "bottom" }, framing = new { mode = "auto" },
                    render = new { storageKey = file.GetProperty("key").GetString(), width = 720, height = 1280, durationSeconds = 31.0, sizeBytes = 4096, hasSubtitles = true },
                    thumbnailKey = (string?)null,
                },
            },
        }, Json);
        clips.StatusCode.ShouldBe(HttpStatusCode.OK, await clips.Content.ReadAsStringAsync());
        var completed = await ReadJsonAsync(await worker2.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = 600 }, Json));
        completed.GetProperty("result").GetProperty("clipsRendered").GetInt32().ShouldBe(1);
        completed.GetProperty("result").GetProperty("candidatesFound").GetInt32().ShouldBe(1);

        var project = await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}"));
        project.GetProperty("clipCount").GetInt32().ShouldBe(1);
        var candidates = (await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}/candidates"))).GetProperty("items").EnumerateArray().ToList();
        candidates.ShouldHaveSingleItem().GetProperty("clipId").ValueKind.ShouldBe(JsonValueKind.String);
        File.Exists(PathOf(file.GetProperty("key").GetString()!)).ShouldBeTrue();
        _ = userId;
    }
}
