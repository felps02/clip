using ClipForge.Api.Services;
using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Features.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName, bool AcceptedTerms, bool? MarketingConsent = null);

public sealed record LoginRequest(string Email, string Password);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record VerifyEmailRequest(Guid UserId, string Token);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record AuthResponse(UserDto User);

public sealed record MessageResponse(string Message);

public sealed record ProfileDto(string DisplayName, string Locale, string Timezone, EditingPreferences EditingPreferences, NotificationPreferences NotificationPreferences);

public sealed record SubscriptionSummaryDto(string PlanCode, string PlanName, SubscriptionStatus Status, DateTimeOffset CurrentPeriodEnd, DateTimeOffset CurrentPeriodStart, bool CancelAtPeriodEnd);

public sealed record MeResponse(UserDto User, ProfileDto Profile, SubscriptionSummaryDto Subscription, PlanLimits Limits);

/// <summary>Result of an authentication: user + tokens to be written as cookies.</summary>
public sealed record AuthSession(Domain.Entities.AppUser User, string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt);
