using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Api.Tests.Integration;

/// <summary>
/// Client address resolution behind a reverse proxy (ReverseProxy section): the per-IP rate-limit buckets, the
/// lockout and the IP stored with refresh tokens must see the real client, and only when the proxy is trusted.
/// </summary>
public class ReverseProxyTests : TestBase
{
    public ReverseProxyTests(ApiFactory factory) : base(factory)
    {
    }

    /// <summary>Registers a user sending X-Forwarded-For and returns the IP the API stored with the refresh token.</summary>
    private static async Task<string?> RegisterThroughProxyAsync(ApiFactory factory, string forwardedFor)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        var email = NextEmail("proxy");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new { email, password = "Password123", displayName = "Proxy User", acceptedTerms = true }, options: Json),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var userId = (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("user").GetProperty("id").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RefreshTokens.Where(t => t.UserId == userId).Select(t => t.CreatedByIp).FirstAsync();
    }

    [Fact]
    public async Task Forwarded_headers_are_honoured_only_from_a_configured_proxy()
    {
        // default host: no ReverseProxy section → the connection address is used and the header is ignored
        (await RegisterThroughProxyAsync(Factory, "203.0.113.9")).ShouldNotBe("203.0.113.9");

        // a host with a trusted proxy: the client address comes from X-Forwarded-For
        await using var proxied = new ApiFactory();
        proxied.ExtraSettings["ReverseProxy:Enabled"] = "true";
        proxied.ExtraSettings["ReverseProxy:KnownProxies:0"] = "127.0.0.1";
        proxied.ExtraSettings["ReverseProxy:ForwardLimit"] = "1";
        (await RegisterThroughProxyAsync(proxied, "203.0.113.9")).ShouldBe("203.0.113.9");
        // only the hop added by the trusted proxy counts (ForwardLimit 1): the right-most address, never one a client prepends
        (await RegisterThroughProxyAsync(proxied, "198.51.100.7, 203.0.113.10")).ShouldBe("203.0.113.10");
    }
}
