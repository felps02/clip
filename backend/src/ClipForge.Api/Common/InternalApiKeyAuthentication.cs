using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ClipForge.Api.Options;
using ClipForge.Domain.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Common;

/// <summary>Authentication scheme for the internal (worker) API: header X-Internal-Api-Key compared in constant time.</summary>
public sealed class InternalApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Internal";
    public const string PolicyName = "Internal";
    public const string HeaderName = "X-Internal-Api-Key";

    private readonly IOptionsMonitor<InternalOptions> _internal;

    public InternalApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<InternalOptions> internalOptions)
        : base(options, logger, encoder)
    {
        _internal = internalOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var provided) || string.IsNullOrEmpty(provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var expected = _internal.CurrentValue.ApiKey;
        if (string.IsNullOrWhiteSpace(expected) || !FixedTimeEquals(expected, provided.ToString()))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid internal API key."));
        }

        var identity = new ClaimsIdentity([new Claim("scope", "internal"), new Claim(ClaimTypes.Name, "internal-worker")], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => Problems.WriteAsync(Context, 401, ErrorCodes.Unauthorized, "Chave interna ausente ou inválida.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => Problems.WriteAsync(Context, 403, ErrorCodes.Forbidden, "Acesso negado.");

    public static bool FixedTimeEquals(string expected, string provided)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        // Length leak is acceptable; the comparison of the bytes is constant-time.
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
