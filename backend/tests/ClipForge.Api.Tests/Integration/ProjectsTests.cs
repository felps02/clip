using System.Net;
using System.Net.Http.Json;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Tests.Integration;

public class ProjectsTests : TestBase
{
    public ProjectsTests(ApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Create_project_queues_an_analyze_job_with_capped_settings()
    {
        var (client, userId, _) = await RegisterAsync();
        var assetId = await UploadAssetAsync(client);
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            title = "Minha live",
            source = new { kind = "upload", mediaAssetId = assetId },
            settings = new { maxCandidates = 99, autoRenderTop = 50, aspectRatio = "16:9", framing = new { mode = "webcam-split" }, subtitles = new { enabled = true, style = "boxed", position = "top" } },
        }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(response);
        var project = body.GetProperty("project");
        project.GetProperty("title").GetString().ShouldBe("Minha live");
        project.GetProperty("status").GetString().ShouldBe("created");
        project.GetProperty("sourceKind").GetString().ShouldBe("upload");
        var settings = project.GetProperty("settings");
        settings.GetProperty("maxCandidates").GetInt32().ShouldBe(15); // free plan cap
        settings.GetProperty("autoRenderTop").GetInt32().ShouldBe(15); // free plan cap (maxAutoRenderPerJob 15)
        settings.GetProperty("aspectRatio").GetString().ShouldBe("16:9");
        settings.GetProperty("framing").GetProperty("mode").GetString().ShouldBe("auto"); // webcam layout not allowed on free
        settings.GetProperty("subtitles").GetProperty("style").GetString().ShouldBe("bold"); // custom styles not allowed on free

        var job = body.GetProperty("job");
        job.GetProperty("kind").GetString().ShouldBe("analyze_source");
        job.GetProperty("status").GetString().ShouldBe("queued");
        job.GetProperty("attempt").GetInt32().ShouldBe(0);
        job.GetProperty("maxAttempts").GetInt32().ShouldBe(3);

        var stored = await WithDbAsync(db => db.ProcessingJobs.FirstAsync(j => j.ProjectId == project.GetProperty("id").GetGuid()));
        stored.UserId.ShouldBe(userId);
        stored.Priority.ShouldBe(0);
        using (var input = System.Text.Json.JsonDocument.Parse(stored.Input.ShouldNotBeNull()))
        {
            input.RootElement.GetProperty("maxCandidates").GetInt32().ShouldBe(15);
            input.RootElement.GetProperty("autoRenderTop").GetInt32().ShouldBe(15);
        }
        stored.QueuedAt.ShouldNotBeNull();

        // detail + list
        var detail = await ReadJsonAsync(await client.GetAsync($"/api/projects/{project.GetProperty("id").GetGuid()}"));
        detail.GetProperty("jobs").GetArrayLength().ShouldBe(1);
        detail.GetProperty("source").GetProperty("previewable").GetBoolean().ShouldBeTrue();
        detail.GetProperty("source").GetProperty("previewUrl").GetString().ShouldNotBeNullOrEmpty();
        var list = await ReadJsonAsync(await client.GetAsync("/api/projects"));
        list.GetProperty("total").GetInt32().ShouldBe(1);
        list.GetProperty("items")[0].GetProperty("latestJob").GetProperty("status").GetString().ShouldBe("queued");
    }

    [Fact]
    public async Task Idempotency_key_returns_the_original_project()
    {
        var (client, _, _) = await RegisterAsync();
        var assetId = await UploadAssetAsync(client);
        var key = "idem-" + Guid.NewGuid().ToString("N");

        async Task<HttpResponseMessage> SendAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects")
            {
                Content = JsonContent.Create(new { title = "Idem", source = new { kind = "upload", mediaAssetId = assetId } }, options: Json),
            };
            request.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(request);
        }

        var first = await SendAsync();
        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        var second = await SendAsync();
        second.StatusCode.ShouldBe(HttpStatusCode.Created, await second.Content.ReadAsStringAsync());

        var a = await ReadJsonAsync(first);
        var b = await ReadJsonAsync(second);
        b.GetProperty("project").GetProperty("id").GetGuid().ShouldBe(a.GetProperty("project").GetProperty("id").GetGuid());
        b.GetProperty("job").GetProperty("id").GetGuid().ShouldBe(a.GetProperty("job").GetProperty("id").GetGuid());
        (await WithDbAsync(db => db.Projects.CountAsync())).ShouldBe(1);
        (await WithDbAsync(db => db.Projects.Select(p => p.ClientRequestId).FirstAsync())).ShouldBe(key);
    }

    [Fact]
    public async Task Concurrent_jobs_limit_blocks_a_second_project_while_the_first_is_queued()
    {
        var (client, _, _) = await RegisterAsync();
        await CreateProjectAsync(client);
        var assetId = await UploadAssetAsync(client);
        var response = await client.PostAsJsonAsync("/api/projects", new { source = new { kind = "upload", mediaAssetId = assetId } }, Json);
        await ShouldBeProblemAsync(response, HttpStatusCode.Conflict, "plan_limit");
        var body = await ReadJsonAsync(response);
        body.GetProperty("detail").GetString()!.ShouldContain("simult");
    }

    [Fact]
    public async Task Active_projects_limit_returns_409_plan_limit()
    {
        var (client, _, _) = await RegisterAsync();
        var internalClient = CreateInternalClient("worker-1");

        for (var i = 0; i < 3; i++)
        {
            var (_, jobId) = await CreateProjectAsync(client, $"Projeto {i}");
            await CompleteAnalyzeJobAsync(internalClient, jobId, durationSeconds: 60);
        }

        var assetId = await UploadAssetAsync(client);
        var fourth = await client.PostAsJsonAsync("/api/projects", new { source = new { kind = "upload", mediaAssetId = assetId } }, Json);
        await ShouldBeProblemAsync(fourth, HttpStatusCode.Conflict, "plan_limit");
        (await ReadJsonAsync(fourth)).GetProperty("detail").GetString()!.ShouldContain("projetos ativos");

        // deleting a project frees a slot
        var projects = await ReadJsonAsync(await client.GetAsync("/api/projects"));
        var firstId = projects.GetProperty("items")[0].GetProperty("id").GetGuid();
        (await client.DeleteAsync($"/api/projects/{firstId}")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var again = await client.PostAsJsonAsync("/api/projects", new { source = new { kind = "upload", mediaAssetId = assetId } }, Json);
        again.StatusCode.ShouldBe(HttpStatusCode.Created, await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concurrent_project_creation_cannot_exceed_the_plan_caps()
    {
        var (client, userId, _) = await RegisterAsync();
        var assets = new List<Guid>();
        for (var i = 0; i < 6; i++) assets.Add(await UploadAssetAsync(client));

        // free plan: 3 active projects and 1 concurrent job → exactly one of six parallel requests may pass
        var responses = await Task.WhenAll(assets.Select(assetId =>
            client.PostAsJsonAsync("/api/projects", new { title = "Paralelo", source = new { kind = "upload", mediaAssetId = assetId } }, Json)));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await ShouldBeProblemAsync(refused, HttpStatusCode.Conflict, "plan_limit");
        }
        (await WithDbAsync(db => db.Projects.CountAsync(p => p.UserId == userId))).ShouldBe(1);
        (await WithDbAsync(db => db.ProcessingJobs.CountAsync(j => j.UserId == userId))).ShouldBe(1);

        // the same asset cannot back two projects even when requested in parallel
        var created = await ReadJsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Created));
        await CompleteAnalyzeJobAsync(CreateInternalClient("worker-1"), created.GetProperty("job").GetProperty("id").GetGuid(), 60);
        var shared = await UploadAssetAsync(client);
        var sameAsset = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PostAsJsonAsync("/api/projects", new { source = new { kind = "upload", mediaAssetId = shared } }, Json)));
        sameAsset.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        foreach (var refused in sameAsset.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await ShouldBeProblemAsync(refused, HttpStatusCode.Conflict, "conflict");
        }
        (await WithDbAsync(db => db.Projects.CountAsync(p => p.UserId == userId))).ShouldBe(2);
    }

    [Fact]
    public async Task Link_sources_are_identified_but_not_supported()
    {
        var (client, _, _) = await RegisterAsync();
        var validate = await client.PostAsJsonAsync("/api/sources/validate", new { url = "https://www.twitch.tv/videos/123" }, Json);
        validate.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(validate);
        body.GetProperty("platform").GetString().ShouldBe("twitch");
        body.GetProperty("supported").GetBoolean().ShouldBeFalse();
        body.GetProperty("reason").GetString().ShouldNotBeNullOrEmpty();
        var alternatives = body.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ToList();
        alternatives.ShouldBe(["upload", "obs_companion"]);

        (await ReadJsonAsync(await client.PostAsJsonAsync("/api/sources/validate", new { url = "https://youtu.be/abc" }, Json))).GetProperty("platform").GetString().ShouldBe("youtube");
        (await ReadJsonAsync(await client.PostAsJsonAsync("/api/sources/validate", new { url = "https://kick.com/x" }, Json))).GetProperty("platform").GetString().ShouldBe("kick");
        (await ReadJsonAsync(await client.PostAsJsonAsync("/api/sources/validate", new { url = "https://example.com/x" }, Json))).GetProperty("platform").GetString().ShouldBe("unknown");

        var create = await client.PostAsJsonAsync("/api/projects", new { source = new { kind = "link", url = "https://www.youtube.com/watch?v=abc" } }, Json);
        await ShouldBeProblemAsync(create, HttpStatusCode.UnprocessableEntity, "source_not_supported");
    }

    [Fact]
    public async Task Project_requires_a_completed_upload_and_an_asset_can_only_back_one_project()
    {
        var (client, _, _) = await RegisterAsync();
        var pending = await UploadAssetAsync(client, complete: false);
        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/projects", new { source = new { kind = "upload", mediaAssetId = pending } }, Json), HttpStatusCode.UnprocessableEntity, "invalid_media");

        var (projectId, jobId) = await CreateProjectAsync(client);
        var assetId = await WithDbAsync(db => db.Projects.Where(p => p.Id == projectId).Select(p => p.MediaAssetId!.Value).FirstAsync());
        // finish the job so concurrency is not the blocker
        await CompleteAnalyzeJobAsync(CreateInternalClient("worker-1"), jobId, 60);
        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/projects", new { source = new { kind = "upload", mediaAssetId = assetId } }, Json), HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task Patch_title_delete_and_reanalyse()
    {
        var (client, _, _) = await RegisterAsync();
        var internalClient = CreateInternalClient("worker-1");
        var (projectId, jobId) = await CreateProjectAsync(client);

        var patched = await ReadJsonAsync(await client.PatchAsJsonAsync($"/api/projects/{projectId}", new { title = "Novo título" }, Json));
        patched.GetProperty("project").GetProperty("title").GetString().ShouldBe("Novo título");

        // re-analyse is refused while a job is active
        await ShouldBeProblemAsync(await client.PostAsJsonAsync($"/api/projects/{projectId}/jobs", new { }, Json), HttpStatusCode.Conflict, "conflict");
        await CompleteAnalyzeJobAsync(internalClient, jobId, 60);
        var status = await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}"));
        status.GetProperty("project").GetProperty("status").GetString().ShouldBe("ready");

        var rerun = await client.PostAsJsonAsync($"/api/projects/{projectId}/jobs", new { settings = new { sensitivity = 80 } }, Json);
        rerun.StatusCode.ShouldBe(HttpStatusCode.Created, await rerun.Content.ReadAsStringAsync());
        (await ReadJsonAsync(rerun)).GetProperty("job").GetProperty("status").GetString().ShouldBe("queued");

        // delete: soft delete + queued job cancelled
        (await client.DeleteAsync($"/api/projects/{projectId}")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.GetAsync($"/api/projects/{projectId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var project = await WithDbAsync(db => db.Projects.FirstAsync(p => p.Id == projectId));
        project.DeletedAt.ShouldNotBeNull();
        var jobs = await WithDbAsync(db => db.ProcessingJobs.Where(j => j.ProjectId == projectId).ToListAsync());
        jobs.ShouldContain(j => j.Status == JobStatus.Cancelled);
        (await ClaimAsync(internalClient, "worker-1", "analyze_source")).ShouldBeNull();
    }

    [Fact]
    public async Task Reanalysing_a_cancelled_or_failed_project_respects_the_active_projects_cap()
    {
        var (client, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");

        // parked right after creation: a cancelled project does not count as active (free plan: 3 active projects)
        var (parked, parkedJob) = await CreateProjectAsync(client, "Estacionado");
        (await client.PostAsync($"/api/projects/{parked}/jobs/{parkedJob}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var active = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (projectId, jobId) = await CreateProjectAsync(client, $"Ativo {i}");
            await CompleteAnalyzeJobAsync(worker, jobId, 60);
            active.Add(projectId);
        }

        // analysing it again would make it the 4th active project
        var refused = await client.PostAsJsonAsync($"/api/projects/{parked}/jobs", new { }, Json);
        await ShouldBeProblemAsync(refused, HttpStatusCode.Conflict, "plan_limit");
        (await ReadJsonAsync(refused)).GetProperty("detail").GetString()!.ShouldContain("projetos ativos");
        (await ReadJsonAsync(await client.GetAsync("/api/account/usage"))).GetProperty("used").GetProperty("activeProjects").GetInt32().ShouldBe(3);

        // a ready project can always be analysed again (it is already counted)
        (await client.PostAsJsonAsync($"/api/projects/{active[0]}/jobs", new { }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var rerunJob = (await WithDbAsync(db => db.ProcessingJobs.Where(j => j.ProjectId == active[0]).OrderByDescending(j => j.CreatedAt).Select(j => j.Id).FirstAsync()));
        await CompleteAnalyzeJobAsync(worker, rerunJob, 60);

        // freeing a slot lets the parked project come back
        (await client.DeleteAsync($"/api/projects/{active[1]}")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.PostAsJsonAsync($"/api/projects/{parked}/jobs", new { }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReadJsonAsync(await client.GetAsync("/api/account/usage"))).GetProperty("used").GetProperty("activeProjects").GetInt32().ShouldBe(3);
    }
}
