using ClipForge.Domain.Common;
using ClipForge.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Tests.Unit;

public class SignedUrlTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private static (HmacSignedUrlService Service, FixedClock Clock) Create(string key = "0123456789abcdef0123456789abcdef-signing")
    {
        var clock = new FixedClock();
        var storage = Microsoft.Extensions.Options.Options.Create(new StorageOptions { SigningKey = key });
        var urls = Microsoft.Extensions.Options.Options.Create(new ApiUrlOptions { PublicBaseUrl = "http://public:5080/", WorkerBaseUrl = "http://api:8080" });
        return (new HmacSignedUrlService(storage, urls, clock), clock);
    }

    [Fact]
    public void Download_signature_verifies_and_rejects_tampering()
    {
        var (service, clock) = Create();
        var exp = clock.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
        var sig = service.SignDownload("users/a/sources/x/original.mp4", exp);

        service.VerifyDownload("users/a/sources/x/original.mp4", exp, sig).ShouldBeTrue();
        service.VerifyDownload("users/b/sources/x/original.mp4", exp, sig).ShouldBeFalse();
        service.VerifyDownload("users/a/sources/x/original.mp4", exp + 1, sig).ShouldBeFalse();
        service.VerifyDownload("users/a/sources/x/original.mp4", exp, sig[..^2] + "AA").ShouldBeFalse();
        service.VerifyDownload("users/a/sources/x/original.mp4", exp, "not-base64!!").ShouldBeFalse();
        service.VerifyDownload("users/a/sources/x/original.mp4", exp, null).ShouldBeFalse();
    }

    [Fact]
    public void Upload_signature_covers_method_key_expiry_and_max()
    {
        var (service, clock) = Create();
        var exp = clock.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
        var sig = service.SignUpload("PUT", "users/a/k", exp, 1000);
        service.VerifyUpload("PUT", "users/a/k", exp, 1000, sig).ShouldBeTrue();
        service.VerifyUpload("put", "users/a/k", exp, 1000, sig).ShouldBeTrue(); // method is normalised
        service.VerifyUpload("PUT", "users/a/k", exp, 1001, sig).ShouldBeFalse();
        service.VerifyUpload("POST", "users/a/k", exp, 1000, sig).ShouldBeFalse();
    }

    [Fact]
    public void Expired_signatures_are_rejected()
    {
        var (service, clock) = Create();
        var exp = clock.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        var sig = service.SignDownload("users/a/k", exp);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        service.VerifyDownload("users/a/k", exp, sig).ShouldBeFalse();
    }

    [Fact]
    public void Different_keys_produce_different_signatures()
    {
        var (a, clock) = Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var (b, _) = Create("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var exp = clock.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        var sig = a.SignDownload("users/a/k", exp);
        b.VerifyDownload("users/a/k", exp, sig).ShouldBeFalse();
    }

    [Fact]
    public void Urls_use_the_audience_base_url()
    {
        var (service, _) = Create();
        var download = service.BuildDownloadUrl("users/a/b c.mp4", TimeSpan.FromMinutes(5), "my clip.mp4", UrlAudience.Public);
        download.ShouldStartWith("http://public:5080/api/storage/download?key=users%2Fa%2Fb%20c.mp4&exp=");
        download.ShouldContain("&sig=");
        download.ShouldEndWith("&name=my%20clip.mp4");

        var upload = service.BuildUploadUrl("users/a/k", TimeSpan.FromMinutes(5), 123, UrlAudience.Worker, out var expiresAt);
        upload.ShouldStartWith("http://api:8080/api/storage/upload?key=users%2Fa%2Fk&exp=");
        upload.ShouldContain("&max=123&sig=");
        expiresAt.ShouldBe(new DateTimeOffset(2026, 1, 1, 12, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Short_signing_key_is_rejected()
    {
        Should.Throw<InvalidOperationException>(() => Create("short"));
    }
}
