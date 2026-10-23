using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ClipForge.Api.Common;
using ClipForge.Domain.Common;
using ClipForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Features.Live;

/// <summary>Device tokens of the OBS companion: <c>cfd_</c> + 40 base62 characters, stored as SHA-256 hex.</summary>
public static class DeviceTokens
{
    public const string Prefix = "cfd_";
    public const int RandomLength = 40;
    public const int DisplayPrefixLength = 8;
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>A new token (uniformly random base62 characters).</summary>
    public static string Generate() => Prefix + new string(RandomNumberGenerator.GetItems<char>(Alphabet, RandomLength));

    /// <summary>Lower-case SHA-256 hex of the whole token.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>The characters kept in clear for display (first 8 after the prefix).</summary>
    public static string DisplayPrefix(string token) => token.Substring(Prefix.Length, DisplayPrefixLength);

    public static bool IsWellFormed(string? token)
        => token is { Length: 44 } && token.StartsWith(Prefix, StringComparison.Ordinal) && token.AsSpan(Prefix.Length).IndexOfAnyExcept(Alphabet) < 0;

    /// <summary>The bearer value of an Authorization header when it is a device token (any well-formed or not <c>cfd_</c> value), else null.</summary>
    public static string? FromAuthorizationHeader(string? header)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var value = header["Bearer ".Length..].Trim();
        return value.StartsWith(Prefix, StringComparison.Ordinal) ? value : null;
    }
}

/// <summary>
/// Authentication scheme "Device" (docs/LIVE_CAPTURE.md §2): <c>Authorization: Bearer cfd_…</c> looked up by its SHA-256
/// hash. Unknown or revoked tokens (or an account being deleted) → 401. The principal carries <c>sub</c> (the device's
/// user) and <c>device_id</c>; only the companion endpoints accept this scheme, and they accept nothing else.
/// <c>last_seen_at</c> is written at most every 30 s.
/// </summary>
public sealed class DeviceTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Device";
    public const string PolicyName = "Device";
    public const string DeviceIdClaim = "device_id";
    public static readonly TimeSpan LastSeenResolution = TimeSpan.FromSeconds(30);
    private const string FailureItem = "clipforge.device_auth_failure";

    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public DeviceTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AppDbContext db,
        IClock clock)
        : base(options, logger, encoder)
    {
        _db = db;
        _clock = clock;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = DeviceTokens.FromAuthorizationHeader(Request.Headers.Authorization.ToString());
        if (token is null) return AuthenticateResult.NoResult();
        if (!DeviceTokens.IsWellFormed(token)) return Fail("Token do dispositivo inválido.");

        var hash = DeviceTokens.Hash(token);
        var device = await _db.CompanionDevices.AsNoTracking()
            .Where(d => d.TokenHash == hash)
            .Select(d => new { d.Id, d.UserId, d.Name, d.RevokedAt, d.LastSeenAt })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (device is null) return Fail("Token do dispositivo inválido.");
        if (device.RevokedAt is not null) return Fail("Este dispositivo foi revogado. Gere um novo token na página Lives.");

        var userActive = await _db.Users.AnyAsync(u => u.Id == device.UserId && u.DeletionRequestedAt == null && u.DeletedAt == null, Context.RequestAborted);
        if (!userActive) return Fail("Token do dispositivo inválido.");

        var now = _clock.UtcNow;
        if (device.LastSeenAt is null || now - device.LastSeenAt.Value >= LastSeenResolution)
        {
            var threshold = now - LastSeenResolution;
            await _db.CompanionDevices
                .Where(d => d.Id == device.Id && (d.LastSeenAt == null || d.LastSeenAt <= threshold))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now), Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim("sub", device.UserId.ToString("D")),
            new Claim(DeviceIdClaim, device.Id.ToString("D")),
            new Claim("name", device.Name),
        ], SchemeName, "name", "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private AuthenticateResult Fail(string message)
    {
        Context.Items[FailureItem] = message;
        return AuthenticateResult.Fail(message);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => Problems.WriteAsync(Context, 401, ErrorCodes.Unauthorized,
            Context.Items[FailureItem] as string ?? "Token do dispositivo ausente. Envie Authorization: Bearer cfd_….");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => Problems.WriteAsync(Context, 403, ErrorCodes.Forbidden, "Acesso negado.");
}

public static class DeviceClaims
{
    /// <summary>Device id of a principal authenticated by the Device scheme (401 otherwise).</summary>
    public static Guid GetDeviceId(this ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue(DeviceTokenAuthenticationHandler.DeviceIdClaim), out var id) ? id : throw AppException.Unauthorized();
}
