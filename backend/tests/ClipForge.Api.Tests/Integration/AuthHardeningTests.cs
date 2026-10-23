using System.Net;
using System.Net.Http.Json;
using ClipForge.Api.Features.Auth;
using ClipForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Api.Tests.Integration;

/// <summary>Refresh-token rotation under concurrency, account-state disclosure, lockout of re-authentication, rate limits and the cookie domain.</summary>
public class AuthHardeningTests : TestBase
{
    public AuthHardeningTests(ApiFactory factory) : base(factory)
    {
    }

    private static string? CookieValue(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return null;
        var cookie = values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal) && !v.StartsWith(name + "=;", StringComparison.Ordinal));
        return cookie?.Split(';')[0][(name.Length + 1)..];
    }

    private HttpClient CreateCookielessClient(ApiFactory? factory = null)
        => (factory ?? Factory).CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("http://localhost") });

    private static HttpRequestMessage RefreshRequest(string? refreshToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        if (refreshToken is not null) request.Headers.Add("Cookie", $"cf_refresh={refreshToken}");
        return request;
    }

    private async Task<(Guid UserId, string RefreshToken)> RegisterWithoutCookiesAsync(HttpClient client, string? email = null)
    {
        email ??= NextEmail("auth");
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password = "Password123", displayName = "Test User", acceptedTerms = true }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var userId = (await ReadJsonAsync(response)).GetProperty("user").GetProperty("id").GetGuid();
        return (userId, CookieValue(response, "cf_refresh").ShouldNotBeNull());
    }

    [Fact]
    public async Task Concurrent_refreshes_of_one_token_never_fork_the_session()
    {
        var client = CreateCookielessClient();
        var (userId, token) = await RegisterWithoutCookiesAsync(client);

        // the same refresh token presented 8 times at once (e.g. a thief racing the victim)
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.SendAsync(RefreshRequest(token))));
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBeLessThanOrEqualTo(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.OK).ShouldAllBe(r => r.StatusCode == HttpStatusCode.Unauthorized);

        // one token is one rotation: the losers count as reuse, so no chain survives the race
        (await WithDbAsync(db => db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null))).ShouldBe(0);
        foreach (var winner in responses.Where(r => r.StatusCode == HttpStatusCode.OK))
        {
            var rotated = CookieValue(winner, "cf_refresh").ShouldNotBeNull();
            await ShouldBeProblemAsync(await client.SendAsync(RefreshRequest(rotated)), HttpStatusCode.Unauthorized, "unauthorized");
        }
    }

    [Fact]
    public async Task Sequential_rotation_still_works_and_reuse_revokes_the_family()
    {
        var client = CreateCookielessClient();
        var (userId, first) = await RegisterWithoutCookiesAsync(client);
        var second = CookieValue(await client.SendAsync(RefreshRequest(first)), "cf_refresh").ShouldNotBeNull();
        var third = CookieValue(await client.SendAsync(RefreshRequest(second)), "cf_refresh").ShouldNotBeNull();
        (await WithDbAsync(db => db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null))).ShouldBe(1);

        // the first token again: theft suspected, every session of the user ends
        await ShouldBeProblemAsync(await client.SendAsync(RefreshRequest(first)), HttpStatusCode.Unauthorized, "unauthorized");
        await ShouldBeProblemAsync(await client.SendAsync(RefreshRequest(third)), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Failed_refresh_expires_both_auth_cookies()
    {
        var response = await CreateCookielessClient().SendAsync(RefreshRequest("not-a-valid-token"));
        await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        cookies.ShouldContain(c => c.StartsWith("cf_access=;") && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase) && c.Contains("path=/"));
        cookies.ShouldContain(c => c.StartsWith("cf_refresh=;") && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase) && c.Contains("path=/api/auth"));
    }

    [Fact]
    public async Task Login_reveals_a_scheduled_deletion_only_after_the_right_password()
    {
        var (client, _, email) = await RegisterAsync();
        using (var request = new HttpRequestMessage(HttpMethod.Delete, "/api/account/") { Content = JsonContent.Create(new { password = "Password123" }, options: Json) })
        {
            (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var anonymous = CreateClient();
        await ShouldBeProblemAsync(await anonymous.PostAsJsonAsync("/api/auth/login", new { email, password = "Wrong-Pass1" }, Json), HttpStatusCode.Unauthorized, "invalid_credentials");
        await ShouldBeProblemAsync(await anonymous.PostAsJsonAsync("/api/auth/login", new { email, password = "Password123" }, Json), HttpStatusCode.Forbidden, "account_deletion_pending");
    }

    [Fact]
    public async Task Wrong_passwords_on_change_password_count_toward_the_lockout()
    {
        var (client, _, email) = await RegisterAsync();
        for (var i = 1; i <= 4; i++)
        {
            await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = $"Wrong-{i}x", newPassword = "Another123" }, Json), HttpStatusCode.Unauthorized, "invalid_credentials");
        }
        // the fifth failure (Auth:LockoutMaxFailedAttempts = 5) locks the account, also for the login with the right password
        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "Wrong-5x", newPassword = "Another123" }, Json), HttpStatusCode.Locked, "locked_out");
        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "Password123", newPassword = "Another123" }, Json), HttpStatusCode.Locked, "locked_out");
        await ShouldBeProblemAsync(await CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Password123" }, Json), HttpStatusCode.Locked, "locked_out");
    }

    [Fact]
    public async Task Wrong_passwords_on_account_deletion_count_toward_the_lockout()
    {
        var (client, userId, email) = await RegisterAsync();
        async Task<HttpResponseMessage> DeleteAsync(string password)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/account/") { Content = JsonContent.Create(new { password }, options: Json) };
            return await client.SendAsync(request);
        }

        for (var i = 1; i <= 4; i++)
        {
            await ShouldBeProblemAsync(await DeleteAsync($"Wrong-{i}x"), HttpStatusCode.Unauthorized, "invalid_credentials");
        }
        await ShouldBeProblemAsync(await DeleteAsync("Wrong-5x"), HttpStatusCode.Locked, "locked_out");
        await ShouldBeProblemAsync(await DeleteAsync("Password123"), HttpStatusCode.Locked, "locked_out");
        await ShouldBeProblemAsync(await CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Password123" }, Json), HttpStatusCode.Locked, "locked_out");
        (await WithDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.DeletionRequestedAt).FirstAsync())).ShouldBeNull();
    }

    [Fact]
    public void Rate_limit_policies_cover_credentials_refresh_and_reauthentication()
    {
        var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        string? PolicyOf(string method, string path)
        {
            var endpoint = endpoints.Single(e => e.RoutePattern.RawText!.TrimEnd('/') == path
                && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));
            return endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        }

        foreach (var path in new[] { "/api/auth/register", "/api/auth/login", "/api/auth/forgot-password", "/api/auth/reset-password", "/api/auth/verify-email", "/api/auth/resend-verification", "/api/auth/change-password" })
        {
            PolicyOf("POST", path).ShouldBe(AuthEndpoints.AuthRateLimitPolicy, path);
        }
        PolicyOf("DELETE", "/api/account").ShouldBe(AuthEndpoints.AuthRateLimitPolicy);
        PolicyOf("POST", "/api/auth/refresh").ShouldBe(AuthEndpoints.RefreshRateLimitPolicy);
        PolicyOf("GET", "/api/auth/me").ShouldBeNull();
        PolicyOf("POST", "/api/auth/logout").ShouldBeNull();
    }

    [Fact]
    public async Task Cookie_domain_is_applied_and_refresh_has_its_own_rate_limit_window()
    {
        await using var factory = new ApiFactory();
        factory.ExtraSettings["Auth:CookieDomain"] = "example.test";
        factory.ExtraSettings["RateLimiting:RefreshPerMinute"] = "3";
        try
        {
            var client = CreateCookielessClient(factory);
            var email = NextEmail("domain");
            var register = await client.PostAsJsonAsync("/api/auth/register", new { email, password = "Password123", displayName = "Domínio", acceptedTerms = true }, Json);
            register.StatusCode.ShouldBe(HttpStatusCode.Created, await register.Content.ReadAsStringAsync());
            var cookies = register.Headers.GetValues("Set-Cookie").ToList();
            cookies.ShouldContain(c => c.StartsWith("cf_access=ey") && c.Contains("domain=example.test") && c.Contains("path=/") && c.Contains("httponly"));
            cookies.ShouldContain(c => c.StartsWith("cf_refresh=") && !c.StartsWith("cf_refresh=;") && c.Contains("domain=example.test") && c.Contains("path=/api/auth"));
            // host-only copies written before the setting existed are expired in the same response
            cookies.ShouldContain(c => c.StartsWith("cf_access=;") && !c.Contains("domain="));
            var refreshToken = CookieValue(register, "cf_refresh").ShouldNotBeNull();

            // rotation keeps the domain; logout clears the domain cookies
            var refreshed = await client.SendAsync(RefreshRequest(refreshToken));
            refreshed.StatusCode.ShouldBe(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync());
            refreshed.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("cf_refresh=") && !c.StartsWith("cf_refresh=;") && c.Contains("domain=example.test"));
            using (var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout"))
            {
                logout.Headers.Add("Cookie", $"cf_refresh={CookieValue(refreshed, "cf_refresh")}");
                var loggedOut = await client.SendAsync(logout);
                loggedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
                var cleared = loggedOut.Headers.GetValues("Set-Cookie").ToList();
                cleared.ShouldContain(c => c.StartsWith("cf_access=;") && c.Contains("domain=example.test") && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
                cleared.ShouldContain(c => c.StartsWith("cf_refresh=;") && c.Contains("domain=example.test") && c.Contains("path=/api/auth"));
            }

            // RefreshPerMinute = 3: the 4th refresh of the minute from this address is refused; logout/me are not limited
            await ShouldBeProblemAsync(await client.SendAsync(RefreshRequest(null)), HttpStatusCode.Unauthorized, "unauthorized");
            await ShouldBeProblemAsync(await client.SendAsync(RefreshRequest(null)), HttpStatusCode.Unauthorized, "unauthorized");
            var limited = await client.SendAsync(RefreshRequest(null));
            await ShouldBeProblemAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited");
            (await client.PostAsync("/api/auth/logout", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            try
            {
                if (Directory.Exists(factory.StorageRoot)) Directory.Delete(factory.StorageRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
