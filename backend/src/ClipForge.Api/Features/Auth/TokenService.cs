using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ClipForge.Api.Options;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Features.Auth;

/// <summary>Issues JWT access tokens and random refresh tokens (stored hashed with SHA-256).</summary>
public sealed class TokenService
{
    private readonly AuthOptions _options;
    private readonly IClock _clock;
    private readonly SigningCredentials _credentials;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(IOptions<AuthOptions> options, IClock clock)
    {
        _options = options.Value;
        _clock = clock;
        _credentials = new SigningCredentials(CreateKey(_options.JwtSigningKey), SecurityAlgorithms.HmacSha256);
    }

    public static SymmetricSecurityKey CreateKey(string signingKey)
    {
        if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32)
        {
            throw new InvalidOperationException("Auth:JwtSigningKey must be configured with at least 32 characters.");
        }
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(AppUser user)
    {
        var now = _clock.UtcNow;
        var expires = now.AddMinutes(Math.Max(1, _options.AccessTokenMinutes));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime.AddSeconds(-5),
            Expires = expires.UtcDateTime,
            SigningCredentials = _credentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString("D"),
                [JwtRegisteredClaimNames.Email] = user.Email ?? string.Empty,
                [JwtRegisteredClaimNames.Name] = user.DisplayName,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            },
        };
        return (_handler.CreateToken(descriptor), expires);
    }

    public (string Raw, string Hash, DateTimeOffset ExpiresAt) CreateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        var raw = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (raw, HashToken(raw), _clock.UtcNow.AddDays(Math.Max(1, _options.RefreshTokenDays)));
    }

    public static string HashToken(string raw)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}

/// <summary>
/// Writes/clears the HttpOnly auth cookies (cf_access on "/", cf_refresh on "/api/auth"). With Auth:CookieDomain set
/// (e.g. "example.com") both cookies carry that Domain, so a web app on app.example.com sees the cookies the API on
/// api.example.com sets (the web's server-side route guard reads cf_access). Host-only copies left from before the
/// setting was enabled are expired in the same responses, so a browser never holds two cookies of the same name.
/// </summary>
public static class AuthCookies
{
    public static void Write(HttpResponse response, AuthOptions options, AuthSession session)
    {
        var domain = Domain(options);
        if (domain is not null) ExpireHostOnly(response, options);
        response.Cookies.Append(AuthOptions.AccessCookieName, session.AccessToken, CookieOptionsFor(options, "/", domain, session.AccessExpiresAt));
        response.Cookies.Append(AuthOptions.RefreshCookieName, session.RefreshToken, CookieOptionsFor(options, AuthOptions.RefreshCookiePath, domain, session.RefreshExpiresAt));
    }

    public static void Clear(HttpResponse response, AuthOptions options)
    {
        var domain = Domain(options);
        if (domain is not null) ExpireHostOnly(response, options);
        response.Cookies.Delete(AuthOptions.AccessCookieName, CookieOptionsFor(options, "/", domain, null));
        response.Cookies.Delete(AuthOptions.RefreshCookieName, CookieOptionsFor(options, AuthOptions.RefreshCookiePath, domain, null));
    }

    private static string? Domain(AuthOptions options)
        => string.IsNullOrWhiteSpace(options.CookieDomain) ? null : options.CookieDomain.Trim();

    // Must run before the Domain cookies are appended/deleted: ResponseCookies.Delete without a Domain removes
    // pending Set-Cookie values of the same name and path.
    private static void ExpireHostOnly(HttpResponse response, AuthOptions options)
    {
        response.Cookies.Delete(AuthOptions.AccessCookieName, CookieOptionsFor(options, "/", null, null));
        response.Cookies.Delete(AuthOptions.RefreshCookieName, CookieOptionsFor(options, AuthOptions.RefreshCookiePath, null, null));
    }

    private static CookieOptions CookieOptionsFor(AuthOptions options, string path, string? domain, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = options.CookieSecure,
        SameSite = SameSiteMode.Lax,
        Path = path,
        Domain = domain,
        Expires = expires,
        IsEssential = true,
    };
}
