using ClipForge.Api.Common;
using ClipForge.Api.Options;
using ClipForge.Api.Services;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Features.Auth;

public static class AuthEndpoints
{
    /// <summary>Strict per-IP window of the credential endpoints (RateLimiting:AuthPerMinute).</summary>
    public const string AuthRateLimitPolicy = "auth";

    /// <summary>Per-IP window of POST /api/auth/refresh (RateLimiting:RefreshPerMinute); /me and /logout are not limited.</summary>
    public const string RefreshRateLimitPolicy = "auth-refresh";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth").WithValidation();

        // Credential endpoints are rate limited per IP.
        var limited = group.MapGroup(string.Empty).RequireRateLimiting(AuthRateLimitPolicy);

        limited.MapPost("/register", async (RegisterRequest request, HttpContext http, AuthService auth, IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            var (user, session) = await auth.RegisterAsync(request, ClientIp(http), ct);
            if (session is null)
            {
                return Results.Accepted(value: new MessageResponse("Conta criada. Confirme seu e-mail para entrar."));
            }
            AuthCookies.Write(http.Response, options.Value, session);
            return Results.Created("/api/auth/me", new AuthResponse(user.ToDto()));
        }).AllowAnonymous().Produces<AuthResponse>(201).Produces<MessageResponse>(202);

        limited.MapPost("/login", async (LoginRequest request, HttpContext http, AuthService auth, IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            var session = await auth.LoginAsync(request, ClientIp(http), ct);
            AuthCookies.Write(http.Response, options.Value, session);
            return Results.Ok(new AuthResponse(session.User.ToDto()));
        }).AllowAnonymous().Produces<AuthResponse>();

        group.MapPost("/logout", async (HttpContext http, AuthService auth, IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            await auth.LogoutAsync(http.Request.Cookies[AuthOptions.RefreshCookieName], ct);
            AuthCookies.Clear(http.Response, options.Value);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapPost("/refresh", async (HttpContext http, AuthService auth, IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            try
            {
                var session = await auth.RefreshAsync(http.Request.Cookies[AuthOptions.RefreshCookieName], ClientIp(http), ct);
                AuthCookies.Write(http.Response, options.Value, session);
                return Results.Ok(new AuthResponse(session.User.ToDto()));
            }
            catch (Domain.Common.AppException ex) when (ex.Status == 401)
            {
                // Answered here instead of rethrowing: the global exception handler resets the response headers,
                // which would drop the Set-Cookie headers that expire the stale cookies.
                AuthCookies.Clear(http.Response, options.Value);
                return Problems.Result(ex.Status, ex.Code, ex.Message);
            }
        }).AllowAnonymous().RequireRateLimiting(RefreshRateLimitPolicy).Produces<AuthResponse>();

        group.MapGet("/me", async (HttpContext http, AuthService auth, CancellationToken ct)
            => Results.Ok(await auth.GetMeAsync(http.User.GetUserId(), ct)))
            .RequireAuthorization().Produces<MeResponse>();

        limited.MapPost("/forgot-password", async (ForgotPasswordRequest request, AuthService auth, CancellationToken ct) =>
        {
            await auth.ForgotPasswordAsync(request.Email, ct);
            return Results.Accepted(value: new MessageResponse("Se o e-mail estiver cadastrado, você receberá as instruções em instantes."));
        }).AllowAnonymous();

        limited.MapPost("/reset-password", async (ResetPasswordRequest request, AuthService auth, CancellationToken ct) =>
        {
            await auth.ResetPasswordAsync(request, ct);
            return Results.NoContent();
        }).AllowAnonymous();

        limited.MapPost("/verify-email", async (VerifyEmailRequest request, AuthService auth, CancellationToken ct) =>
        {
            await auth.VerifyEmailAsync(request, ct);
            return Results.NoContent();
        }).AllowAnonymous();

        limited.MapPost("/resend-verification", async (HttpContext http, AuthService auth, CancellationToken ct) =>
        {
            await auth.ResendVerificationAsync(http.User.GetUserId(), ct);
            return Results.Accepted(value: new MessageResponse("E-mail de verificação enviado."));
        }).RequireAuthorization();

        limited.MapPost("/change-password", async (ChangePasswordRequest request, HttpContext http, AuthService auth, IOptions<AuthOptions> options, CancellationToken ct) =>
        {
            var session = await auth.ChangePasswordAsync(http.User.GetUserId(), request, ClientIp(http), ct);
            AuthCookies.Write(http.Response, options.Value, session);
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    public static string? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();
}
