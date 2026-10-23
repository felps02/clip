using ClipForge.Api.Options;
using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Email;
using ClipForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Features.Auth;

public sealed class AuthService
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly TokenService _tokens;
    private readonly SubscriptionService _subscriptions;
    private readonly NotificationService _notifications;
    private readonly IEmailSender _email;
    private readonly IClock _clock;
    private readonly AuthOptions _auth;
    private readonly FrontendOptions _frontend;
    private readonly BrandingOptions _branding;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext db,
        UserManager<AppUser> users,
        TokenService tokens,
        SubscriptionService subscriptions,
        NotificationService notifications,
        IEmailSender email,
        IClock clock,
        IOptions<AuthOptions> auth,
        IOptions<FrontendOptions> frontend,
        IOptions<BrandingOptions> branding,
        ILogger<AuthService> logger)
    {
        _db = db;
        _users = users;
        _tokens = tokens;
        _subscriptions = subscriptions;
        _notifications = notifications;
        _email = email;
        _clock = clock;
        _auth = auth.Value;
        _frontend = frontend.Value;
        _branding = branding.Value;
        _logger = logger;
    }

    public async Task<(AppUser User, AuthSession? Session)> RegisterAsync(RegisterRequest request, string? ip, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var email = request.Email.Trim();
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            CreatedAt = now,
        };

        var result = await _users.CreateAsync(user, request.Password);
        if (!result.Succeeded) throw MapIdentityErrors(result);

        _db.UserProfiles.Add(new UserProfile
        {
            UserId = user.Id,
            AcceptedTermsAt = now,
            MarketingConsent = request.MarketingConsent ?? false,
            UpdatedAt = now,
        });
        await _subscriptions.CreateFreeSubscriptionAsync(user.Id, ct);
        _notifications.Add(user.Id, NotificationType.System,
            $"Bem-vindo(a) ao {_branding.Name}!",
            "Envie uma gravação para gerar seus primeiros clipes automaticamente.");
        await _db.SaveChangesAsync(ct);

        await SendVerificationEmailAsync(user, ct);

        if (_auth.RequireConfirmedEmail)
        {
            return (user, null);
        }

        var session = await CreateSessionAsync(user, ip, ct);
        return (user, session);
    }

    public async Task<AuthSession> LoginAsync(LoginRequest request, string? ip, CancellationToken ct)
    {
        var user = await _users.FindByEmailAsync(request.Email.Trim());
        if (user is null || user.DeletedAt is not null)
        {
            throw new AppException(ErrorCodes.InvalidCredentials, 401, "E-mail ou senha incorretos.");
        }
        if (await _users.IsLockedOutAsync(user))
        {
            throw LockedOut();
        }

        if (!await _users.CheckPasswordAsync(user, request.Password))
        {
            await _users.AccessFailedAsync(user);
            if (await _users.IsLockedOutAsync(user)) throw LockedOut();
            throw new AppException(ErrorCodes.InvalidCredentials, 401, "E-mail ou senha incorretos.");
        }

        // Account states are only revealed to whoever proved the password: a wrong password on an account scheduled
        // for deletion is the same 401 invalid_credentials as any other wrong password.
        if (user.DeletionRequestedAt is not null)
        {
            throw new AppException(ErrorCodes.AccountDeletionPending, 403, "A exclusão desta conta foi solicitada.");
        }

        if (_auth.RequireConfirmedEmail && !user.EmailConfirmed)
        {
            throw new AppException(ErrorCodes.EmailNotConfirmed, 403, "Confirme seu e-mail antes de entrar.");
        }

        await _users.ResetAccessFailedCountAsync(user);
        return await CreateSessionAsync(user, ip, ct);
    }

    public async Task<AuthSession> RefreshAsync(string? rawRefreshToken, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            throw new AppException(ErrorCodes.Unauthorized, 401, "Sessão expirada. Entre novamente.");
        }
        var hash = TokenService.HashToken(rawRefreshToken);
        var now = _clock.UtcNow;
        var token = await _db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
        {
            throw new AppException(ErrorCodes.Unauthorized, 401, "Sessão inválida. Entre novamente.");
        }
        if (token.RevokedAt is not null)
        {
            if (token.ReplacedByTokenHash is not null)
            {
                // Reuse of a rotated token: revoke the whole family of the user (possible theft).
                await RevokeFamilyAfterReuseAsync(token.UserId, ct);
            }
            throw new AppException(ErrorCodes.Unauthorized, 401, "Sessão inválida. Entre novamente.");
        }
        if (token.ExpiresAt <= now)
        {
            throw new AppException(ErrorCodes.Unauthorized, 401, "Sessão expirada. Entre novamente.");
        }

        var user = await _users.FindByIdAsync(token.UserId.ToString());
        if (user is null || user.DeletedAt is not null || user.DeletionRequestedAt is not null)
        {
            throw new AppException(ErrorCodes.Unauthorized, 401, "Sessão inválida. Entre novamente.");
        }

        // Atomic rotation: the token is revoked with a conditional UPDATE (… WHERE id = @id AND revoked_at IS NULL),
        // so of two requests presenting the same token only one rotates it (the second blocks on the row lock and
        // then matches nothing). The loser is a reuse of a rotated token — one token can never fork into two live
        // session chains — and revokes the family like any other reuse.
        var (raw, newHash, expires) = _tokens.CreateRefreshToken();
        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            var rotated = await _db.RefreshTokens
                .Where(t => t.Id == token.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.ReplacedByTokenHash, newHash), ct);
            if (rotated == 0)
            {
                await transaction.RollbackAsync(ct);
                await RevokeFamilyAfterReuseAsync(token.UserId, ct);
                throw new AppException(ErrorCodes.Unauthorized, 401, "Sessão inválida. Entre novamente.");
            }
            _db.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = newHash,
                ExpiresAt = expires,
                CreatedAt = now,
                CreatedByIp = ip,
            });
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var (access, accessExpires) = _tokens.CreateAccessToken(user);
        return new AuthSession(user, access, accessExpires, raw, expires);
    }

    private async Task RevokeFamilyAfterReuseAsync(Guid userId, CancellationToken ct)
    {
        _logger.LogWarning("Refresh token reuse detected for user {UserId}; all sessions revoked", userId);
        await RevokeAllAsync(userId, ct);
    }

    public async Task LogoutAsync(string? rawRefreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken)) return;
        var hash = TokenService.HashToken(rawRefreshToken);
        var now = _clock.UtcNow;
        await _db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public async Task<MeResponse> GetMeAsync(Guid userId, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(userId.ToString()) ?? throw AppException.Unauthorized();
        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct) ?? new UserProfile { UserId = userId };
        var entitlement = await _subscriptions.GetEntitlementAsync(userId, ct);
        var sub = entitlement.Subscription;
        return new MeResponse(
            user.ToDto(),
            ToProfileDto(user, profile),
            new SubscriptionSummaryDto(entitlement.Plan.Code, entitlement.Plan.Name, sub?.Status ?? SubscriptionStatus.Active,
                entitlement.PeriodEnd, entitlement.PeriodStart, sub?.CancelAtPeriodEnd ?? false),
            entitlement.Limits);
    }

    /// <summary>Profile as the API shows it: preferences normalised to the contract (older documents get the defaults of missing fields).</summary>
    public static ProfileDto ToProfileDto(AppUser user, UserProfile profile)
        => new(user.DisplayName, profile.Locale, profile.Timezone,
            (profile.EditingPreferences ?? new EditingPreferences()).Normalized(),
            (profile.NotificationPreferences ?? new NotificationPreferences()).Clone());

    public async Task ForgotPasswordAsync(string email, CancellationToken ct)
    {
        var user = await _users.FindByEmailAsync(email.Trim());
        if (user is null || user.DeletedAt is not null) return; // never reveal whether the e-mail exists
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var link = $"{_frontend.BaseUrl.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(user.Email!)}&token={Uri.EscapeDataString(token)}";
        await _email.SendAsync(new EmailMessage(
            user.Email!,
            $"{_branding.Name}: redefinição de senha",
            $"Olá, {user.DisplayName}!\n\nPara redefinir sua senha, acesse o link abaixo (válido por tempo limitado):\n{link}\n\nSe você não pediu a redefinição, ignore este e-mail."), ct);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await _users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            throw new AppException(ErrorCodes.InvalidToken, 400, "Token inválido ou expirado.");
        }
        var result = await _users.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "InvalidToken"))
            {
                throw new AppException(ErrorCodes.InvalidToken, 400, "Token inválido ou expirado.");
            }
            throw MapIdentityErrors(result);
        }
        await _users.ResetAccessFailedCountAsync(user);
        await _users.SetLockoutEndDateAsync(user, null);
        await RevokeAllAsync(user.Id, ct);
    }

    public async Task VerifyEmailAsync(VerifyEmailRequest request, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(request.UserId.ToString());
        if (user is null) throw new AppException(ErrorCodes.InvalidToken, 400, "Token inválido ou expirado.");
        if (user.EmailConfirmed) return;
        var result = await _users.ConfirmEmailAsync(user, request.Token);
        if (!result.Succeeded) throw new AppException(ErrorCodes.InvalidToken, 400, "Token inválido ou expirado.");
    }

    public async Task ResendVerificationAsync(Guid userId, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(userId.ToString()) ?? throw AppException.Unauthorized();
        if (user.EmailConfirmed) return;
        await SendVerificationEmailAsync(user, ct);
    }

    public async Task<AuthSession> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, string? ip, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(userId.ToString()) ?? throw AppException.Unauthorized();
        await VerifyPasswordAsync(user, request.CurrentPassword, "Senha atual incorreta.");
        var result = await _users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "PasswordMismatch"))
            {
                throw new AppException(ErrorCodes.InvalidCredentials, 401, "Senha atual incorreta.");
            }
            throw MapIdentityErrors(result);
        }
        await _users.UpdateSecurityStampAsync(user);
        await RevokeAllAsync(user.Id, ct);
        return await CreateSessionAsync(user, ip, ct);
    }

    /// <summary>
    /// Re-authentication for sensitive actions of a signed-in user (change password, delete account): a locked-out
    /// account answers 423, and a wrong password counts toward the same lockout as a failed login (401, then 423).
    /// </summary>
    public async Task VerifyPasswordAsync(AppUser user, string password, string wrongPasswordMessage)
    {
        if (await _users.IsLockedOutAsync(user)) throw LockedOut();
        if (!await _users.CheckPasswordAsync(user, password))
        {
            await _users.AccessFailedAsync(user);
            if (await _users.IsLockedOutAsync(user)) throw LockedOut();
            throw new AppException(ErrorCodes.InvalidCredentials, 401, wrongPasswordMessage);
        }
        await _users.ResetAccessFailedCountAsync(user);
    }

    public async Task RevokeAllAsync(Guid userId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private async Task<AuthSession> CreateSessionAsync(AppUser user, string? ip, CancellationToken ct)
    {
        var (raw, hash, expires) = _tokens.CreateRefreshToken();
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = hash,
            ExpiresAt = expires,
            CreatedAt = _clock.UtcNow,
            CreatedByIp = ip,
        });
        await _db.SaveChangesAsync(ct);
        var (access, accessExpires) = _tokens.CreateAccessToken(user);
        return new AuthSession(user, access, accessExpires, raw, expires);
    }

    private async Task SendVerificationEmailAsync(AppUser user, CancellationToken ct)
    {
        var token = await _users.GenerateEmailConfirmationTokenAsync(user);
        var link = $"{_frontend.BaseUrl.TrimEnd('/')}/verify-email?userId={user.Id:D}&token={Uri.EscapeDataString(token)}";
        await _email.SendAsync(new EmailMessage(
            user.Email!,
            $"{_branding.Name}: confirme seu e-mail",
            $"Olá, {user.DisplayName}!\n\nConfirme seu e-mail acessando o link abaixo:\n{link}\n\nSe você não criou uma conta, ignore este e-mail."), ct);
    }

    private AppException LockedOut()
        => new(ErrorCodes.LockedOut, 423, $"Conta temporariamente bloqueada por excesso de tentativas. Tente novamente em {_auth.LockoutMinutes} minutos.");

    private static AppException MapIdentityErrors(IdentityResult result)
    {
        var errors = result.Errors.ToList();
        if (errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
        {
            return new AppException(ErrorCodes.EmailTaken, 409, "Já existe uma conta com este e-mail.");
        }
        var dict = new Dictionary<string, string[]>();
        var passwordErrors = errors.Where(e => e.Code.StartsWith("Password", StringComparison.Ordinal)).Select(e => e.Description).ToArray();
        if (passwordErrors.Length > 0) dict["password"] = passwordErrors;
        var others = errors.Where(e => !e.Code.StartsWith("Password", StringComparison.Ordinal)).Select(e => e.Description).ToArray();
        if (others.Length > 0) dict["request"] = others;
        return new AppException(ErrorCodes.ValidationFailed, 400, "Dados inválidos.", dict);
    }
}
