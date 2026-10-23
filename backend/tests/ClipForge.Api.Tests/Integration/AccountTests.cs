using System.Net;
using System.Net.Http.Json;
using ClipForge.Api.HostedServices;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Api.Tests.Integration;

public class AccountTests : TestBase
{
    public AccountTests(ApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Profile_can_be_read_and_partially_updated()
    {
        var (client, _, _) = await RegisterAsync(displayName: "Original");
        var profile = await ReadJsonAsync(await client.GetAsync("/api/account/profile"));
        profile.GetProperty("displayName").GetString().ShouldBe("Original");
        profile.GetProperty("editingPreferences").GetProperty("aspectRatio").GetString().ShouldBe("9:16");

        var update = await client.PutAsJsonAsync("/api/account/profile", new { displayName = "Novo nome", timezone = "Europe/Lisbon", editingPreferences = new { aspectRatio = "16:9", subtitleStyle = "clean", subtitlesEnabled = false, targetClipSeconds = 45, language = "pt" } }, Json);
        update.StatusCode.ShouldBe(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        var updated = await ReadJsonAsync(update);
        updated.GetProperty("displayName").GetString().ShouldBe("Novo nome");
        updated.GetProperty("timezone").GetString().ShouldBe("Europe/Lisbon");
        updated.GetProperty("locale").GetString().ShouldBe("pt-BR");
        updated.GetProperty("editingPreferences").GetProperty("aspectRatio").GetString().ShouldBe("16:9");
        (await ReadJsonAsync(await client.GetAsync("/api/auth/me"))).GetProperty("user").GetProperty("displayName").GetString().ShouldBe("Novo nome");

        // preferences feed project defaults
        var (projectId, _) = await CreateProjectAsync(client);
        var project = await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}"));
        project.GetProperty("project").GetProperty("settings").GetProperty("aspectRatio").GetString().ShouldBe("16:9");
        project.GetProperty("project").GetProperty("settings").GetProperty("subtitles").GetProperty("enabled").GetBoolean().ShouldBeFalse();

        await ShouldBeProblemAsync(await client.PutAsJsonAsync("/api/account/profile", new { editingPreferences = new { aspectRatio = "4:3" } }, Json), HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Usage_reflects_completed_jobs_renders_storage_and_history()
    {
        var (client, userId, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (_, jobId) = await CreateProjectAsync(client);
        await CompleteAnalyzeJobAsync(worker, jobId, durationSeconds: 15 * 60);

        var usage = await ReadJsonAsync(await client.GetAsync("/api/account/usage"));
        usage.GetProperty("plan").GetProperty("code").GetString().ShouldBe("free");
        usage.GetProperty("period").GetProperty("start").GetDateTimeOffset().ShouldBeLessThan(DateTimeOffset.UtcNow);
        usage.GetProperty("period").GetProperty("end").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(27));
        var used = usage.GetProperty("used");
        used.GetProperty("processingSeconds").GetInt64().ShouldBe(900);
        used.GetProperty("renders").GetInt32().ShouldBe(0);
        used.GetProperty("storageBytes").GetInt64().ShouldBe(4096);
        used.GetProperty("activeProjects").GetInt32().ShouldBe(1);
        var remaining = usage.GetProperty("remaining");
        remaining.GetProperty("processingSeconds").GetInt64().ShouldBe(2700);
        remaining.GetProperty("renders").GetInt32().ShouldBe(10);
        remaining.GetProperty("activeProjects").GetInt32().ShouldBe(2);
        var history = usage.GetProperty("history").EnumerateArray().ToList();
        history.Count.ShouldBe(1);
        history[0].GetProperty("processingSeconds").GetInt64().ShouldBe(900);

        var records = await WithDbAsync(db => db.UsageRecords.Where(u => u.UserId == userId).ToListAsync());
        records.ShouldHaveSingleItem().Kind.ShouldBe(UsageKind.ProcessingSeconds);
        records[0].SubscriptionId.ShouldNotBeNull();

        var dashboard = await ReadJsonAsync(await client.GetAsync("/api/dashboard/summary"));
        dashboard.GetProperty("minutesProcessedThisPeriod").GetDecimal().ShouldBe(15m);
        dashboard.GetProperty("usage").GetProperty("processingSecondsLimit").GetInt64().ShouldBe(3600);
        dashboard.GetProperty("warnings").GetArrayLength().ShouldBe(0);

        // 80 % warning
        var (_, job2) = await CreateProjectAsync(client);
        await CompleteAnalyzeJobAsync(worker, job2, durationSeconds: 34 * 60);
        dashboard = await ReadJsonAsync(await client.GetAsync("/api/dashboard/summary"));
        dashboard.GetProperty("warnings").EnumerateArray().ShouldContain(w => w.GetProperty("code").GetString() == "processing_quota_warning");
    }

    [Fact]
    public async Task Notifications_are_listed_marked_read_and_counted()
    {
        var (client, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (_, jobId) = await CreateProjectAsync(client);
        await CompleteAnalyzeJobAsync(worker, jobId, 60);

        var list = await ReadJsonAsync(await client.GetAsync("/api/notifications"));
        list.GetProperty("total").GetInt32().ShouldBe(2); // welcome + job_completed
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items[0].GetProperty("type").GetString().ShouldBe("job_completed");
        items[0].GetProperty("data").GetProperty("jobId").GetGuid().ShouldBe(jobId);
        (await ReadJsonAsync(await client.GetAsync("/api/notifications/unread-count"))).GetProperty("count").GetInt32().ShouldBe(2);

        (await client.PostAsync($"/api/notifications/{items[0].GetProperty("id").GetGuid()}/read", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadJsonAsync(await client.GetAsync("/api/notifications/unread-count"))).GetProperty("count").GetInt32().ShouldBe(1);
        (await ReadJsonAsync(await client.GetAsync("/api/notifications?unreadOnly=true"))).GetProperty("total").GetInt32().ShouldBe(1);
        (await client.PostAsync("/api/notifications/read-all", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadJsonAsync(await client.GetAsync("/api/notifications/unread-count"))).GetProperty("count").GetInt32().ShouldBe(0);
        (await client.PostAsync($"/api/notifications/{Guid.NewGuid()}/read", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // other users cannot mark my notifications
        var (other, _, _) = await RegisterAsync();
        (await other.PostAsync($"/api/notifications/{items[0].GetProperty("id").GetGuid()}/read", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retention_cleanup_expires_pending_uploads_and_purges_deleted_projects()
    {
        var (client, userId, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var pending = await UploadAssetAsync(client, complete: false);
        var (projectId, jobId) = await CreateProjectAsync(client);
        await CompleteAnalyzeJobAsync(worker, jobId, 60);
        var sourceKey = await WithDbAsync(db => db.MediaAssets.Where(a => a.Id == pending).Select(a => a.StorageKey).FirstAsync());
        File.Exists(Path.Combine(Factory.StorageRoot, sourceKey.Replace('/', Path.DirectorySeparatorChar))).ShouldBeTrue();

        (await client.DeleteAsync($"/api/projects/{projectId}")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WithDbAsync(async db =>
        {
            await db.MediaAssets.Where(a => a.Id == pending).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)));
            await db.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow.AddDays(-8)));
        });

        await using (var scope = Factory.CreateAsyncScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            (await retention.ExpirePendingUploadsAsync(CancellationToken.None)).ShouldBe(1);
            (await retention.PurgeDeletedProjectsAsync(CancellationToken.None)).ShouldBe(1);
        }

        File.Exists(Path.Combine(Factory.StorageRoot, sourceKey.Replace('/', Path.DirectorySeparatorChar))).ShouldBeFalse();
        (await WithDbAsync(db => db.MediaAssets.Where(a => a.Id == pending).Select(a => a.Status).FirstAsync())).ShouldBe(MediaAssetStatus.Deleted);
        (await WithDbAsync(db => db.Projects.AnyAsync(p => p.Id == projectId))).ShouldBeFalse();
        (await WithDbAsync(db => db.ProcessingJobs.AnyAsync(j => j.ProjectId == projectId))).ShouldBeFalse();
        (await WithDbAsync(db => db.MediaAssets.CountAsync(a => a.UserId == userId && a.Status != MediaAssetStatus.Deleted))).ShouldBe(0);
        // usage records survive the purge (accounting)
        (await WithDbAsync(db => db.UsageRecords.CountAsync(u => u.UserId == userId))).ShouldBe(1);
    }

    [Fact]
    public async Task Retention_purge_keeps_a_source_asset_that_another_project_still_uses()
    {
        var (client, _, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var assetId = await UploadAssetAsync(client);
        var first = await ReadJsonAsync(await client.PostAsJsonAsync("/api/projects", new { title = "P1", source = new { kind = "upload", mediaAssetId = assetId } }, Json));
        var p1 = first.GetProperty("project").GetProperty("id").GetGuid();
        (await client.DeleteAsync($"/api/projects/{p1}")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // the asset is free again: a second project reuses it
        var again = await client.PostAsJsonAsync("/api/projects", new { title = "P2", source = new { kind = "upload", mediaAssetId = assetId } }, Json);
        again.StatusCode.ShouldBe(HttpStatusCode.Created, await again.Content.ReadAsStringAsync());
        var second = await ReadJsonAsync(again);
        var p2 = second.GetProperty("project").GetProperty("id").GetGuid();
        var p2Job = second.GetProperty("job").GetProperty("id").GetGuid();

        var key = await WithDbAsync(db => db.MediaAssets.Where(a => a.Id == assetId).Select(a => a.StorageKey).FirstAsync());
        var path = Path.Combine(Factory.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar));
        await WithDbAsync(db => db.Projects.Where(p => p.Id == p1).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow.AddDays(-8))));
        await using (var scope = Factory.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<RetentionService>().PurgeDeletedProjectsAsync(CancellationToken.None)).ShouldBe(1);
        }

        // P1 is gone, but the source of the live project survived
        (await WithDbAsync(db => db.Projects.AnyAsync(p => p.Id == p1))).ShouldBeFalse();
        (await WithDbAsync(db => db.Projects.Where(p => p.Id == p2).Select(p => p.MediaAssetId).FirstAsync())).ShouldBe(assetId);
        (await WithDbAsync(db => db.MediaAssets.AnyAsync(a => a.Id == assetId && a.DeletedAt == null))).ShouldBeTrue();
        File.Exists(path).ShouldBeTrue();
        await CompleteAnalyzeJobAsync(worker, p2Job, 60);
        (await ReadJsonAsync(await client.GetAsync($"/api/projects/{p2}"))).GetProperty("project").GetProperty("status").GetString().ShouldBe("ready");

        // the asset goes away with the last project that references it
        (await client.DeleteAsync($"/api/projects/{p2}")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WithDbAsync(db => db.Projects.Where(p => p.Id == p2).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow.AddDays(-8))));
        await using (var scope = Factory.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<RetentionService>().PurgeDeletedProjectsAsync(CancellationToken.None)).ShouldBe(1);
        }
        (await WithDbAsync(db => db.MediaAssets.AnyAsync(a => a.Id == assetId))).ShouldBeFalse();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task Account_purge_removes_user_rows_and_storage_but_keeps_anonymised_usage()
    {
        var (client, userId, _) = await RegisterAsync();
        var worker = CreateInternalClient("worker-1");
        var (_, jobId) = await CreateProjectAsync(client);
        await CompleteAnalyzeJobAsync(worker, jobId, 60);
        using (var request = new HttpRequestMessage(HttpMethod.Delete, "/api/account/") { Content = JsonContent.Create(new { password = "Password123" }, options: Json) })
        {
            (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }
        var userDir = Path.Combine(Factory.StorageRoot, "users", userId.ToString("D"));
        Directory.Exists(userDir).ShouldBeTrue();

        await using (var scope = Factory.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<RetentionService>().PurgeAccountsAsync(CancellationToken.None)).ShouldBe(1);
        }

        Directory.Exists(userDir).ShouldBeFalse();
        (await WithDbAsync(db => db.Users.AnyAsync(u => u.Id == userId))).ShouldBeFalse();
        (await WithDbAsync(db => db.Projects.AnyAsync(p => p.UserId == userId))).ShouldBeFalse();
        (await WithDbAsync(db => db.Subscriptions.AnyAsync(s => s.UserId == userId))).ShouldBeFalse();
        (await WithDbAsync(db => db.UsageRecords.CountAsync(u => u.UserId == null))).ShouldBe(1);
    }
}
