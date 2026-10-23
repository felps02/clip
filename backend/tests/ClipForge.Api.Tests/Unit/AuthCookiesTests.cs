using ClipForge.Api.Features.Auth;
using ClipForge.Api.Options;
using ClipForge.Domain.Entities;
using Microsoft.AspNetCore.Http;

namespace ClipForge.Api.Tests.Unit;

public class AuthCookiesTests
{
    private static AuthSession Session() => new(new AppUser(), "access-token", DateTimeOffset.UtcNow.AddMinutes(15), "refresh-token", DateTimeOffset.UtcNow.AddDays(30));

    private static string[] SetCookies(HttpResponse response) => response.Headers.SetCookie.ToArray()!;

    [Fact]
    public void Without_a_cookie_domain_the_cookies_are_host_only()
    {
        var context = new DefaultHttpContext();
        AuthCookies.Write(context.Response, new AuthOptions(), Session());
        var cookies = SetCookies(context.Response);
        cookies.Length.ShouldBe(2);
        cookies.ShouldAllBe(c => !c.Contains("domain="));
        cookies.ShouldContain(c => c.StartsWith("cf_access=access-token") && c.Contains("path=/;") && c.Contains("httponly") && c.Contains("samesite=lax"));
        cookies.ShouldContain(c => c.StartsWith("cf_refresh=refresh-token") && c.Contains("path=/api/auth"));
    }

    [Fact]
    public void A_configured_cookie_domain_is_written_and_cleared_with_the_cookies()
    {
        var options = new AuthOptions { CookieDomain = " example.com ", CookieSecure = true };

        var written = new DefaultHttpContext();
        AuthCookies.Write(written.Response, options, Session());
        var cookies = SetCookies(written.Response);
        cookies.ShouldContain(c => c.StartsWith("cf_access=access-token") && c.Contains("domain=example.com") && c.Contains("path=/;") && c.Contains("secure"));
        cookies.ShouldContain(c => c.StartsWith("cf_refresh=refresh-token") && c.Contains("domain=example.com") && c.Contains("path=/api/auth"));
        // host-only copies left from before the setting are expired so the browser never holds two cf_access cookies
        cookies.ShouldContain(c => c.StartsWith("cf_access=;") && !c.Contains("domain=") && c.Contains("expires=Thu, 01 Jan 1970"));
        cookies.ShouldContain(c => c.StartsWith("cf_refresh=;") && !c.Contains("domain=") && c.Contains("path=/api/auth"));

        var cleared = new DefaultHttpContext();
        AuthCookies.Clear(cleared.Response, options);
        var deletions = SetCookies(cleared.Response);
        deletions.Length.ShouldBe(4);
        deletions.ShouldAllBe(c => c.Contains("expires=Thu, 01 Jan 1970"));
        deletions.ShouldContain(c => c.StartsWith("cf_access=;") && c.Contains("domain=example.com") && c.Contains("path=/;"));
        deletions.ShouldContain(c => c.StartsWith("cf_refresh=;") && c.Contains("domain=example.com") && c.Contains("path=/api/auth"));
    }
}
