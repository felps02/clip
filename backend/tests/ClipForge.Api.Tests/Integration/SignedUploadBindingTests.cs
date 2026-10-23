using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClipForge.Api.HostedServices;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Api.Tests.Integration;

/// <summary>
/// The signed Local upload URL is bound to the state of its target: once an upload is completed or deleted (or a job
/// stopped running) the still-valid URL cannot replace or re-create the object.
/// </summary>
public class SignedUploadBindingTests : TestBase
{
    public SignedUploadBindingTests(ApiFactory factory) : base(factory)
    {
    }

    private string PathOf(string key) => Path.Combine(Factory.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Upload_url_cannot_replace_a_verified_object_nor_recreate_a_deleted_one()
    {
        var (client, _, _) = await RegisterAsync();
        var original = FakeMp4(4096);
        var create = await ReadJsonAsync(await client.PostAsJsonAsync("/api/media/uploads", new { fileName = "live.mp4", sizeBytes = original.Length, contentType = "video/mp4" }, Json));
        var assetId = create.GetProperty("asset").GetProperty("id").GetGuid();
        var url = create.GetProperty("upload").GetProperty("url").GetString()!;

        // while pending the URL can be used (and re-used, e.g. a retried transfer)
        (await client.PutAsync(url, new ByteArrayContent(original))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PutAsync(url, new ByteArrayContent(original))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync($"/api/media/uploads/{assetId}/complete", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var key = await WithDbAsync(db => db.MediaAssets.Where(a => a.Id == assetId).Select(a => a.StorageKey).FirstAsync());

        // after /complete verified the magic bytes the object can no longer change
        var junk = Encoding.ASCII.GetBytes(new string('x', 4096));
        await ShouldBeProblemAsync(await client.PutAsync(url, new ByteArrayContent(junk)), HttpStatusCode.Conflict, "conflict");
        (await File.ReadAllBytesAsync(PathOf(key))).ShouldBe(original);
        Directory.GetFiles(Path.GetDirectoryName(PathOf(key))!).ShouldBe([PathOf(key)]); // no temporary leftovers

        // deleted: the URL cannot bring the object back outside the storage quota
        (await client.DeleteAsync($"/api/media/{assetId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(PathOf(key)).ShouldBeFalse();
        await ShouldBeProblemAsync(await client.PutAsync(url, new ByteArrayContent(original)), HttpStatusCode.Conflict, "conflict");
        File.Exists(PathOf(key)).ShouldBeFalse();
        (await ReadJsonAsync(await client.GetAsync("/api/account/usage"))).GetProperty("used").GetProperty("storageBytes").GetInt64().ShouldBe(0);

        // a key the API never issued for an upload target is refused even with a valid signature
        await using (var scope = Factory.CreateAsyncScope())
        {
            var signer = scope.ServiceProvider.GetRequiredService<ClipForge.Infrastructure.Storage.ISignedUrlService>();
            var forged = signer.BuildUploadUrl($"users/{Guid.NewGuid()}/other/file.mp4", TimeSpan.FromMinutes(5), 100, ClipForge.Infrastructure.Storage.UrlAudience.Public, out _);
            await ShouldBeProblemAsync(await client.PutAsync(forged.Replace("http://localhost", string.Empty), new ByteArrayContent([1, 2, 3])), HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Deleted_uploads_are_swept_once_more_after_their_upload_url_expired()
    {
        var (client, _, _) = await RegisterAsync();
        var assetId = await UploadAssetAsync(client, complete: false);
        (await client.DeleteAsync($"/api/media/{assetId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var asset = await WithDbAsync(db => db.MediaAssets.AsNoTracking().FirstAsync(a => a.Id == assetId));
        asset.Status.ShouldBe(MediaAssetStatus.Deleted);
        asset.ExpiresAt.ShouldNotBeNull(); // the upload URL (Media:UploadUrlMinutes) was still valid
        asset.ExpiresAt.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // a provider whose presigned URLs cannot be refused (S3) may get the object back before the URL expires
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(asset.StorageKey))!);
        await File.WriteAllBytesAsync(PathOf(asset.StorageKey), FakeMp4());
        await WithDbAsync(db => db.MediaAssets.Where(a => a.Id == assetId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));

        await using (var scope = Factory.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<RetentionService>().SweepDeletedUploadsAsync(CancellationToken.None)).ShouldBe(1);
            (await scope.ServiceProvider.GetRequiredService<RetentionService>().SweepDeletedUploadsAsync(CancellationToken.None)).ShouldBe(0);
        }
        File.Exists(PathOf(asset.StorageKey)).ShouldBeFalse();
        (await WithDbAsync(db => db.MediaAssets.Where(a => a.Id == assetId).Select(a => a.ExpiresAt).FirstAsync())).ShouldBeNull();
    }

    [Fact]
    public async Task Expired_pending_upload_refuses_its_url()
    {
        var (client, _, _) = await RegisterAsync();
        var bytes = FakeMp4();
        var create = await ReadJsonAsync(await client.PostAsJsonAsync("/api/media/uploads", new { fileName = "live.mp4", sizeBytes = bytes.Length, contentType = "video/mp4" }, Json));
        var assetId = create.GetProperty("asset").GetProperty("id").GetGuid();
        var url = create.GetProperty("upload").GetProperty("url").GetString()!;
        await WithDbAsync(db => db.MediaAssets.Where(a => a.Id == assetId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresAt, DateTimeOffset.UtcNow.AddHours(-1))));
        await using (var scope = Factory.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<RetentionService>().ExpirePendingUploadsAsync(CancellationToken.None)).ShouldBe(1);
        }
        await ShouldBeProblemAsync(await client.PutAsync(url, new ByteArrayContent(bytes)), HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task Job_artifact_url_is_refused_once_the_job_finished()
    {
        var (client, _, _) = await RegisterAsync();
        var (_, jobId) = await CreateProjectAsync(client);
        var worker = CreateInternalClient("worker-1");
        (await ClaimAsync(worker, "worker-1", "analyze_source")).ShouldNotBeNull();
        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", ProbeBody, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var urls = await ReadJsonAsync(await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/upload-urls", new { files = new[] { new { kind = "analysis", fileName = "signals.json", contentType = "application/json" } } }, Json));
        var url = urls.GetProperty("files")[0].GetProperty("url").GetString()!;
        (await worker.PutAsync(url, new StringContent("{}"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await worker.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = 600 }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldBeProblemAsync(await worker.PutAsync(url, new StringContent("{\"tampered\":true}")), HttpStatusCode.Conflict, "conflict");
    }
}
