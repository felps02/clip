using System.Net;
using System.Net.Http.Json;
using ClipForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Tests.Integration;

/// <summary>Every user-owned resource must be invisible (404) to other users.</summary>
public class IsolationTests : TestBase
{
    public IsolationTests(ApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Other_users_cannot_see_projects_jobs_candidates_clips_assets_or_downloads()
    {
        var (owner, _, _) = await RegisterAsync();
        var (intruder, _, _) = await RegisterAsync();
        var internalClient = CreateInternalClient("worker-1");

        var (projectId, jobId) = await CreateProjectAsync(owner);
        var assetId = await WithDbAsync(db => db.Projects.Where(p => p.Id == projectId).Select(p => p.MediaAssetId!.Value).FirstAsync());

        // run the pipeline so there are candidates and a clip
        var payload = await ClaimAsync(internalClient, "worker-1", "analyze_source");
        payload.ShouldNotBeNull();
        (await internalClient.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var candidates = await internalClient.PostAsJsonAsync($"/internal/jobs/{jobId}/candidates", new
        {
            candidates = new[] { new { rank = 1, startSeconds = 10.0, endSeconds = 40.0, score = 80, category = "highlight", reasoning = "r", transcript = "t", signals = new { audio = new { available = true } } } },
        }, Json);
        candidates.StatusCode.ShouldBe(HttpStatusCode.OK, await candidates.Content.ReadAsStringAsync());
        var candidateId = (await ReadJsonAsync(candidates)).GetProperty("ids")[0].GetGuid();
        var urls = await ReadJsonAsync(await internalClient.PostAsJsonAsync($"/internal/jobs/{jobId}/upload-urls", new { files = new[] { new { kind = "clip_render", fileName = "clip-1.mp4", contentType = "video/mp4", sizeBytes = 4096 } } }, Json));
        var file = urls.GetProperty("files")[0];
        (await internalClient.PutAsync(file.GetProperty("url").GetString(), new ByteArrayContent(FakeMp4()))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var clips = await internalClient.PostAsJsonAsync($"/internal/jobs/{jobId}/clips", new
        {
            clips = new[] { new { candidateId, title = "Clip", aspectRatio = "9:16", startSeconds = 10.0, endSeconds = 40.0, render = new { storageKey = file.GetProperty("key").GetString(), width = 1080, height = 1920, durationSeconds = 30.0, sizeBytes = 4096, hasSubtitles = true } } },
        }, Json);
        clips.StatusCode.ShouldBe(HttpStatusCode.OK, await clips.Content.ReadAsStringAsync());
        var clipId = (await ReadJsonAsync(clips)).GetProperty("clips")[0].GetProperty("clipId").GetGuid();
        (await internalClient.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = 600 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // owner sees everything
        (await owner.GetAsync($"/api/projects/{projectId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetAsync($"/api/projects/{projectId}/jobs/{jobId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetAsync($"/api/projects/{projectId}/candidates")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetAsync($"/api/clips/{clipId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetAsync($"/api/clips/{clipId}/download")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetAsync($"/api/media/{assetId}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // intruder gets 404 everywhere
        await ShouldBeProblemAsync(await intruder.GetAsync($"/api/projects/{projectId}"), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.GetAsync($"/api/projects/{projectId}/jobs/{jobId}"), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.GetAsync($"/api/projects/{projectId}/jobs/{jobId}/events"), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.PostAsync($"/api/projects/{projectId}/jobs/{jobId}/cancel", null), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.GetAsync($"/api/projects/{projectId}/candidates"), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.PostAsJsonAsync($"/api/projects/{projectId}/candidates/{candidateId}/clips", new { }, Json), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.GetAsync($"/api/clips/{clipId}"), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.GetAsync($"/api/clips/{clipId}/download"), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.PatchAsJsonAsync($"/api/clips/{clipId}", new { title = "hacked" }, Json), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.DeleteAsync($"/api/clips/{clipId}"), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.GetAsync($"/api/media/{assetId}"), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.DeleteAsync($"/api/media/{assetId}"), HttpStatusCode.NotFound, "not_found");
        await ShouldBeProblemAsync(await intruder.DeleteAsync($"/api/projects/{projectId}"), HttpStatusCode.NotFound, "not_found");

        // lists are scoped
        var intruderProjects = await ReadJsonAsync(await intruder.GetAsync("/api/projects"));
        intruderProjects.GetProperty("total").GetInt32().ShouldBe(0);
        var intruderClips = await ReadJsonAsync(await intruder.GetAsync("/api/clips"));
        intruderClips.GetProperty("total").GetInt32().ShouldBe(0);
        var intruderJobs = await ReadJsonAsync(await intruder.GetAsync("/api/jobs?status=all"));
        intruderJobs.GetProperty("total").GetInt32().ShouldBe(0);

        // a project cannot be created with someone else's asset
        var steal = await intruder.PostAsJsonAsync("/api/projects", new { source = new { kind = "upload", mediaAssetId = assetId } }, Json);
        await ShouldBeProblemAsync(steal, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Internal_endpoints_require_the_api_key()
    {
        var anonymous = CreateClient();
        await ShouldBeProblemAsync(await anonymous.PostAsJsonAsync("/internal/workers/w/claim", new { kinds = new[] { "analyze_source" } }, Json), HttpStatusCode.Unauthorized, "unauthorized");

        var wrongKey = CreateClient();
        wrongKey.DefaultRequestHeaders.Add("X-Internal-Api-Key", "wrong");
        await ShouldBeProblemAsync(await wrongKey.PostAsJsonAsync("/internal/workers/w/claim", new { kinds = new[] { "analyze_source" } }, Json), HttpStatusCode.Unauthorized, "unauthorized");

        // a user session is not enough either
        var (user, _, _) = await RegisterAsync();
        (await user.PostAsJsonAsync("/internal/workers/w/claim", new { kinds = new[] { "analyze_source" } }, Json)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var ok = CreateInternalClient();
        (await ok.PostAsJsonAsync("/internal/workers/w/claim", new { kinds = new[] { "analyze_source" } }, Json)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
