using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClipForge.Api.Features.Media;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Enums;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Api.Tests.Integration;

public class MediaTests : TestBase
{
    public MediaTests(ApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Upload_create_put_and_complete_validates_magic_bytes()
    {
        var (client, userId, _) = await RegisterAsync();

        // a text file is rejected and the object deleted
        var text = Encoding.ASCII.GetBytes("this is definitely not a video file, just plain text content for the test");
        var create = await client.PostAsJsonAsync("/api/media/uploads", new { fileName = "notes.mp4", sizeBytes = text.Length, contentType = "video/mp4" }, Json);
        create.StatusCode.ShouldBe(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var created = await ReadJsonAsync(create);
        created.GetProperty("asset").GetProperty("status").GetString().ShouldBe("pending");
        created.GetProperty("upload").GetProperty("method").GetString().ShouldBe("PUT");
        var url = created.GetProperty("upload").GetProperty("url").GetString()!;
        url.ShouldContain("/api/storage/upload?key=users%2F" + userId);
        var assetId = created.GetProperty("asset").GetProperty("id").GetGuid();

        (await client.PutAsync(url, new ByteArrayContent(text))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var complete = await client.PostAsync($"/api/media/uploads/{assetId}/complete", null);
        await ShouldBeProblemAsync(complete, HttpStatusCode.UnprocessableEntity, "invalid_media");
        var key = await WithDbAsync(db => db.MediaAssets.Where(a => a.Id == assetId).Select(a => a.StorageKey).FirstAsync());
        File.Exists(Path.Combine(Factory.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar))).ShouldBeFalse();

        // a file starting with 00 00 00 18 66 74 79 70 is accepted
        var mp4 = FakeMp4(2048);
        var okId = await UploadAssetAsync(client, mp4, complete: false);
        var done = await client.PostAsync($"/api/media/uploads/{okId}/complete", null);
        done.StatusCode.ShouldBe(HttpStatusCode.OK, await done.Content.ReadAsStringAsync());
        var asset = (await ReadJsonAsync(done)).GetProperty("asset");
        asset.GetProperty("status").GetString().ShouldBe("uploaded");
        asset.GetProperty("container").GetString().ShouldBe("mp4");
        asset.GetProperty("sizeBytes").GetInt64().ShouldBe(mp4.Length);

        // completing twice is idempotent
        (await client.PostAsync($"/api/media/uploads/{okId}/complete", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // GET asset
        var get = await ReadJsonAsync(await client.GetAsync($"/api/media/{okId}"));
        get.GetProperty("asset").GetProperty("originalFileName").GetString().ShouldBe("live.mp4");
    }

    [Fact]
    public async Task Complete_fails_when_object_missing_or_size_mismatch()
    {
        var (client, _, _) = await RegisterAsync();
        var create = await client.PostAsJsonAsync("/api/media/uploads", new { fileName = "a.mp4", sizeBytes = 5000, contentType = "video/mp4" }, Json);
        var created = await ReadJsonAsync(create);
        var assetId = created.GetProperty("asset").GetProperty("id").GetGuid();
        var url = created.GetProperty("upload").GetProperty("url").GetString()!;

        await ShouldBeProblemAsync(await client.PostAsync($"/api/media/uploads/{assetId}/complete", null), HttpStatusCode.UnprocessableEntity, "invalid_media");

        // body larger than the declared size is refused by the signed upload (max)
        (await client.PutAsync(url, new ByteArrayContent(FakeMp4(6000)))).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

        // smaller than declared → size mismatch
        (await client.PutAsync(url, new ByteArrayContent(FakeMp4(1000)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var mismatch = await client.PostAsync($"/api/media/uploads/{assetId}/complete", null);
        await ShouldBeProblemAsync(mismatch, HttpStatusCode.UnprocessableEntity, "invalid_media");
    }

    [Fact]
    public async Task Upload_rejects_disallowed_content_types_and_plan_file_size()
    {
        var (client, _, _) = await RegisterAsync();
        var bad = await client.PostAsJsonAsync("/api/media/uploads", new { fileName = "a.txt", sizeBytes = 10, contentType = "text/plain" }, Json);
        await ShouldBeProblemAsync(bad, HttpStatusCode.BadRequest, "validation_failed");

        var huge = await client.PostAsJsonAsync("/api/media/uploads", new { fileName = "a.mp4", sizeBytes = 3L * 1024 * 1024 * 1024, contentType = "video/mp4" }, Json);
        await ShouldBeProblemAsync(huge, HttpStatusCode.RequestEntityTooLarge, "plan_limit");
    }

    [Fact]
    public async Task Signed_upload_and_download_urls_reject_tampering()
    {
        var (client, _, _) = await RegisterAsync();
        var create = await client.PostAsJsonAsync("/api/media/uploads", new { fileName = "a.mp4", sizeBytes = 100, contentType = "video/mp4" }, Json);
        var url = (await ReadJsonAsync(create)).GetProperty("upload").GetProperty("url").GetString()!;

        var tamperedMax = url.Replace("&max=100", "&max=999999");
        (await client.PutAsync(tamperedMax, new ByteArrayContent(new byte[10]))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var tamperedKey = url.Replace("original.mp4", "other.mp4");
        (await client.PutAsync(tamperedKey, new ByteArrayContent(new byte[10]))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var noSig = url[..url.IndexOf("&sig=", StringComparison.Ordinal)];
        (await client.PutAsync(noSig, new ByteArrayContent(new byte[10]))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var download = await client.GetAsync("/api/storage/download?key=users%2Fx%2Fy.mp4&exp=9999999999&sig=abc");
        await ShouldBeProblemAsync(download, HttpStatusCode.Forbidden, "forbidden");
        var traversal = await client.GetAsync("/api/storage/download?key=..%2F..%2Fappsettings.json&exp=9999999999&sig=abc");
        traversal.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void Signed_transfer_endpoint_has_its_own_rate_limit_policy()
    {
        var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        static string Describe(RouteEndpoint e) => $"{string.Join('/', e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText} ({e.DisplayName})";
        var transfer = endpoints.Single(e => e.RoutePattern.RawText!.EndsWith("/api/storage/upload", StringComparison.Ordinal));
        transfer.Metadata.GetMetadata<EnableRateLimitingAttribute>().ShouldNotBeNull(Describe(transfer)).PolicyName.ShouldBe(StorageEndpoints.TransfersRateLimitPolicy);
        var uploads = endpoints.Where(e => e.RoutePattern.RawText!.StartsWith("/api/media/uploads", StringComparison.Ordinal)).ToList();
        uploads.Count.ShouldBe(2, string.Join("; ", uploads.Select(Describe)));
        foreach (var upload in uploads)
        {
            upload.Metadata.GetMetadata<EnableRateLimitingAttribute>().ShouldNotBeNull(Describe(upload)).PolicyName.ShouldBe(MediaEndpoints.UploadsRateLimitPolicy);
        }
    }

    [Fact]
    public async Task Delete_asset_is_refused_while_referenced_by_a_project()
    {
        var (client, _, _) = await RegisterAsync();
        var (projectId, _) = await CreateProjectAsync(client);
        var assetId = await WithDbAsync(db => db.Projects.Where(p => p.Id == projectId).Select(p => p.MediaAssetId!.Value).FirstAsync());
        await ShouldBeProblemAsync(await client.DeleteAsync($"/api/media/{assetId}"), HttpStatusCode.Conflict, "conflict");

        var loose = await UploadAssetAsync(client);
        (await client.DeleteAsync($"/api/media/{loose}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/media/{loose}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var status = await WithDbAsync(db => db.MediaAssets.Where(a => a.Id == loose).Select(a => a.Status).FirstAsync());
        status.ShouldBe(MediaAssetStatus.Deleted);
    }
}
