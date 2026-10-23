using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClipForge.Domain.Common;
using ClipForge.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Api.Tests.Infrastructure;

/// <summary>Base class for integration tests: shared factory, database reset before each test, helpers.</summary>
[Collection(ApiCollection.Name)]
public abstract class TestBase : IAsyncLifetime
{
    protected ApiFactory Factory { get; }

    protected TestBase(ApiFactory factory)
    {
        Factory = factory;
    }

    public static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    public virtual async Task InitializeAsync() => await Factory.ResetDatabaseAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------- clients

    protected HttpClient CreateClient() => Factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });

    protected HttpClient CreateInternalClient(string? workerId = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", ApiFactory.InternalApiKey);
        if (workerId is not null) client.DefaultRequestHeaders.Add("X-Worker-Id", workerId);
        return client;
    }

    private static int _userCounter;

    protected static string NextEmail(string prefix = "user") => $"{prefix}{Interlocked.Increment(ref _userCounter)}-{Guid.NewGuid():N}@example.com".ToLowerInvariant();

    /// <summary>
    /// Registers a user and returns an authenticated client (cookies) plus the user id. <paramref name="confirmEmail"/>
    /// also confirms the address (needed for notification e-mails, which only go to a confirmed address).
    /// </summary>
    protected async Task<(HttpClient Client, Guid UserId, string Email)> RegisterAsync(string? email = null, string password = "Password123", string displayName = "Test User", bool confirmEmail = false)
    {
        var client = CreateClient();
        email ??= NextEmail();
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password, displayName, acceptedTerms = true }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var userId = body.GetProperty("user").GetProperty("id").GetGuid();
        if (confirmEmail) await ConfirmEmailAsync(client, userId, email);
        return (client, userId, email);
    }

    /// <summary>Confirms the address the way its owner does: the token from the verification e-mail → POST /api/auth/verify-email.</summary>
    protected async Task ConfirmEmailAsync(HttpClient client, Guid userId, string email)
    {
        var message = Factory.Emails.Messages.LastOrDefault(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase) && m.TextBody.Contains("/verify-email?"));
        message.ShouldNotBeNull("no verification e-mail was sent to " + email);
        var match = System.Text.RegularExpressions.Regex.Match(message.TextBody, @"/verify-email\?userId=([^&\s]+)&token=([^\s]+)");
        match.Success.ShouldBeTrue(message.TextBody);
        var verify = await client.PostAsJsonAsync("/api/auth/verify-email", new { userId, token = Uri.UnescapeDataString(match.Groups[2].Value) }, Json);
        verify.StatusCode.ShouldBe(HttpStatusCode.NoContent, await verify.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- database

    protected async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = Factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(db);
    }

    protected async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    // ---------------------------------------------------------------- media / projects helpers

    public static readonly byte[] Mp4Header = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x02, 0x00, 0x69, 0x73, 0x6F, 0x6D, 0x69, 0x73, 0x6F, 0x32];

    public static byte[] FakeMp4(int size = 4096)
    {
        var bytes = new byte[Math.Max(size, Mp4Header.Length)];
        Mp4Header.CopyTo(bytes, 0);
        for (var i = Mp4Header.Length; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    /// <summary>POST /api/media/uploads → PUT bytes to the signed URL → complete. Returns the asset id.</summary>
    protected async Task<Guid> UploadAssetAsync(HttpClient client, byte[]? bytes = null, string fileName = "live.mp4", string contentType = "video/mp4", bool complete = true)
    {
        bytes ??= FakeMp4();
        var create = await client.PostAsJsonAsync("/api/media/uploads", new { fileName, sizeBytes = bytes.Length, contentType }, Json);
        create.StatusCode.ShouldBe(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var created = await create.Content.ReadFromJsonAsync<JsonElement>(Json);
        var assetId = created.GetProperty("asset").GetProperty("id").GetGuid();
        var url = created.GetProperty("upload").GetProperty("url").GetString()!;

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        var put = await client.PutAsync(url, content);
        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        if (complete)
        {
            var done = await client.PostAsync($"/api/media/uploads/{assetId}/complete", null);
            done.StatusCode.ShouldBe(HttpStatusCode.OK, await done.Content.ReadAsStringAsync());
        }
        return assetId;
    }

    /// <summary>Creates an upload project. Returns (projectId, jobId).</summary>
    protected async Task<(Guid ProjectId, Guid JobId)> CreateProjectAsync(HttpClient client, string? title = "Live de teste", object? settings = null, string? idempotencyKey = null)
    {
        var assetId = await UploadAssetAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects")
        {
            Content = JsonContent.Create(new { title, source = new { kind = "upload", mediaAssetId = assetId }, settings }, options: Json),
        };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (body.GetProperty("project").GetProperty("id").GetGuid(), body.GetProperty("job").GetProperty("id").GetGuid());
    }

    protected static readonly object ProbeBody = new
    {
        durationSeconds = 600.0,
        width = 1920,
        height = 1080,
        fps = 60.0,
        hasAudio = true,
        hasVideo = true,
        container = "mp4",
        videoCodec = "h264",
        audioCodec = "aac",
        sizeBytes = 4096,
    };

    /// <summary>Claims the next job with the internal API. Returns the payload (or null on 204).</summary>
    protected static async Task<JsonElement?> ClaimAsync(HttpClient internalClient, string workerId = "worker-1", params string[] kinds)
    {
        var response = await internalClient.PostAsJsonAsync($"/internal/workers/{workerId}/claim", new { kinds = kinds.Length == 0 ? new[] { "analyze_source", "render_clip" } : kinds }, Json);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /// <summary>Claim → probe → complete (minimal analyze run without candidates).</summary>
    protected async Task CompleteAnalyzeJobAsync(HttpClient internalClient, Guid jobId, double durationSeconds = 600)
    {
        var payload = await ClaimAsync(internalClient, "worker-1", "analyze_source");
        payload.ShouldNotBeNull();
        payload.Value.GetProperty("jobId").GetGuid().ShouldBe(jobId);
        var probe = await internalClient.PostAsJsonAsync($"/internal/jobs/{jobId}/probe", new
        {
            durationSeconds, width = 1920, height = 1080, fps = 30.0, hasAudio = true, hasVideo = true, container = "mp4", videoCodec = "h264", audioCodec = "aac", sizeBytes = 4096,
        }, Json);
        probe.StatusCode.ShouldBe(HttpStatusCode.OK, await probe.Content.ReadAsStringAsync());
        var complete = await internalClient.PostAsJsonAsync($"/internal/jobs/{jobId}/complete", new { processedSeconds = durationSeconds }, Json);
        complete.StatusCode.ShouldBe(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
    }

    /// <summary>Notification e-mails are delivered in the background: waits until the outbox is drained, then returns the e-mails sent to <paramref name="email"/>.</summary>
    protected async Task<IReadOnlyList<ClipForge.Infrastructure.Email.EmailMessage>> SentEmailsAsync(string email)
    {
        (await Factory.Outbox.WaitIdleAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue("the e-mail outbox did not drain");
        return Factory.Emails.Messages.Where(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>PUT /api/account/profile with the given body; asserts 200.</summary>
    protected static async Task<JsonElement> UpdateProfileAsync(HttpClient client, object body)
    {
        var response = await client.PutAsJsonAsync("/api/account/profile", body, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    protected static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    protected static async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, text);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("code").GetString().ShouldBe(code);
        doc.RootElement.GetProperty("status").GetInt32().ShouldBe((int)status);
    }
}
