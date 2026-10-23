using ClipForge.Api.Common;
using ClipForge.Api.Features.Auth;
using ClipForge.Api.Options;
using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Jobs;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Features.Account;

/// <summary>Partial editing preferences of <c>PUT /api/account/profile</c>: omitted (null) fields keep the stored value.</summary>
public sealed record EditingPreferencesPatch(
    string? AspectRatio,
    bool? SubtitlesEnabled,
    string? SubtitleStyle,
    string? SubtitlePosition,
    int? MinClipSeconds,
    int? MaxClipSeconds,
    int? TargetClipSeconds,
    string? Language);

/// <summary>Partial notification preferences: omitted (null) fields keep the stored value.</summary>
public sealed record NotificationPreferencesPatch(bool? EmailOnJobCompleted, bool? EmailOnJobFailed, bool? EmailOnQuotaWarning, bool? EmailOnBilling);

public sealed record UpdateProfileRequest(string? DisplayName, string? Locale, string? Timezone, EditingPreferencesPatch? EditingPreferences, NotificationPreferencesPatch? NotificationPreferences);

public sealed record DeleteAccountRequest(string Password);

public sealed record UsagePlanDto(string Code, string Name, PlanLimits Limits);
public sealed record UsagePeriodDto(DateTimeOffset Start, DateTimeOffset End);
public sealed record UsageNumbersDto(long ProcessingSeconds, int Renders, long StorageBytes, int ActiveProjects, int AssistantMessages);
public sealed record UsageRemainingDto(long? ProcessingSeconds, int? Renders, long? StorageBytes, int? ActiveProjects, int? AssistantMessages);
public sealed record UsageHistoryDto(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, long ProcessingSeconds, int Renders);
public sealed record UsageResponse(UsagePlanDto Plan, UsagePeriodDto Period, UsageNumbersDto Used, UsageRemainingDto Remaining, IReadOnlyList<UsageHistoryDto> History);

public sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.DisplayName).MinimumLength(2).MaximumLength(80).When(x => x.DisplayName is not null);
        RuleFor(x => x.Locale).MaximumLength(16).When(x => x.Locale is not null);
        RuleFor(x => x.Timezone).MaximumLength(64).When(x => x.Timezone is not null);
        RuleFor(x => x.EditingPreferences!).SetValidator(new EditingPreferencesPatchValidator()).When(x => x.EditingPreferences is not null);
    }
}

/// <summary>
/// Field rules of the editing preferences (docs/API.md → Account). The min ≤ target ≤ max relation is checked on the
/// merged document (<see cref="ProfilePreferences.Validate"/>) because omitted fields keep their stored values.
/// </summary>
public sealed class EditingPreferencesPatchValidator : AbstractValidator<EditingPreferencesPatch>
{
    public EditingPreferencesPatchValidator()
    {
        const int min = EditingPreferences.ClipSecondsMin;
        const int max = EditingPreferences.ClipSecondsMax;
        RuleFor(x => x.AspectRatio).Must(AspectRatios.IsValid).WithMessage("Formato inválido (9:16, 16:9 ou 1:1).").When(x => x.AspectRatio is not null);
        RuleFor(x => x.SubtitleStyle).Must(SubtitleStyles.IsStyle).WithMessage("Estilo de legenda inválido (bold, clean ou boxed).").When(x => x.SubtitleStyle is not null);
        RuleFor(x => x.SubtitlePosition).Must(SubtitleStyles.IsPosition).WithMessage("Posição de legenda inválida (bottom, center ou top).").When(x => x.SubtitlePosition is not null);
        RuleFor(x => x.MinClipSeconds).InclusiveBetween(min, max).WithMessage($"A duração mínima deve ficar entre {min} e {max} segundos.").When(x => x.MinClipSeconds.HasValue);
        RuleFor(x => x.MaxClipSeconds).InclusiveBetween(min, max).WithMessage($"A duração máxima deve ficar entre {min} e {max} segundos.").When(x => x.MaxClipSeconds.HasValue);
        RuleFor(x => x.TargetClipSeconds).InclusiveBetween(min, max).WithMessage($"A duração alvo deve ficar entre {min} e {max} segundos.").When(x => x.TargetClipSeconds.HasValue);
        RuleFor(x => x.Language).Must(l => EditingPreferences.IsLanguage(l!.Trim().ToLowerInvariant())).WithMessage("Idioma inválido (auto, pt, en ou es).").When(x => x.Language is not null);
    }
}

/// <summary>Field-level merge of the profile preferences (PUT /api/account/profile).</summary>
public static class ProfilePreferences
{
    public static EditingPreferences Merge(EditingPreferences? stored, EditingPreferencesPatch patch)
    {
        var merged = (stored ?? new EditingPreferences()).Normalized();
        if (patch.AspectRatio is not null) merged.AspectRatio = patch.AspectRatio;
        if (patch.SubtitlesEnabled is { } enabled) merged.SubtitlesEnabled = enabled;
        if (patch.SubtitleStyle is not null) merged.SubtitleStyle = patch.SubtitleStyle;
        if (patch.SubtitlePosition is not null) merged.SubtitlePosition = patch.SubtitlePosition;
        if (patch.MinClipSeconds is { } minSeconds) merged.MinClipSeconds = minSeconds;
        if (patch.MaxClipSeconds is { } maxSeconds) merged.MaxClipSeconds = maxSeconds;
        if (patch.TargetClipSeconds is { } targetSeconds) merged.TargetClipSeconds = targetSeconds;
        if (patch.Language is not null) merged.Language = patch.Language.Trim().ToLowerInvariant();
        return merged;
    }

    public static NotificationPreferences Merge(NotificationPreferences? stored, NotificationPreferencesPatch patch)
    {
        var merged = (stored ?? new NotificationPreferences()).Clone();
        if (patch.EmailOnJobCompleted is { } completed) merged.EmailOnJobCompleted = completed;
        if (patch.EmailOnJobFailed is { } failed) merged.EmailOnJobFailed = failed;
        if (patch.EmailOnQuotaWarning is { } quota) merged.EmailOnQuotaWarning = quota;
        if (patch.EmailOnBilling is { } billing) merged.EmailOnBilling = billing;
        return merged;
    }

    /// <summary>Cross-field rules on the merged document; the keys match the request fields (400 validation_failed).</summary>
    public static Dictionary<string, string[]> Validate(EditingPreferences merged)
    {
        var errors = new Dictionary<string, string[]>();
        if (merged.MaxClipSeconds < merged.MinClipSeconds)
        {
            errors["editingPreferences.maxClipSeconds"] = ["A duração máxima deve ser maior ou igual à duração mínima."];
        }
        if (merged.TargetClipSeconds < merged.MinClipSeconds || merged.TargetClipSeconds > merged.MaxClipSeconds)
        {
            errors["editingPreferences.targetClipSeconds"] = ["A duração alvo deve ficar entre a duração mínima e a máxima."];
        }
        return errors;
    }
}

public sealed class DeleteAccountRequestValidator : AbstractValidator<DeleteAccountRequest>
{
    public DeleteAccountRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().WithMessage("Informe a senha.");
    }
}

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account").WithTags("Account").WithValidation().RequireAuthorization();

        group.MapGet("/profile", async (HttpContext http, AppDbContext db, UserManager<AppUser> users, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.Unauthorized();
            var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct) ?? new UserProfile { UserId = userId };
            return Results.Ok(AuthService.ToProfileDto(user, profile));
        }).Produces<ProfileDto>();

        group.MapPut("/profile", async (UpdateProfileRequest request, HttpContext http, AppDbContext db, UserManager<AppUser> users, IClock clock, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.Unauthorized();
            var stored = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

            // Nested preference objects are merged field by field (omitted fields keep the stored value); the merged
            // document is validated before anything is written.
            var editing = request.EditingPreferences is { } editingPatch ? ProfilePreferences.Merge(stored?.EditingPreferences, editingPatch) : null;
            if (editing is not null && ProfilePreferences.Validate(editing) is { Count: > 0 } errors)
            {
                throw AppException.Validation("Dados inválidos.", errors);
            }
            var notifications = request.NotificationPreferences is { } notificationPatch ? ProfilePreferences.Merge(stored?.NotificationPreferences, notificationPatch) : null;

            var profile = stored;
            if (profile is null)
            {
                profile = new UserProfile { UserId = userId };
                db.UserProfiles.Add(profile);
            }
            if (request.DisplayName is not null)
            {
                user.DisplayName = request.DisplayName.Trim();
                var result = await users.UpdateAsync(user);
                if (!result.Succeeded) throw AppException.Validation("Não foi possível atualizar o nome.");
            }
            if (request.Locale is not null) profile.Locale = request.Locale;
            if (request.Timezone is not null) profile.Timezone = request.Timezone;
            if (editing is not null) profile.EditingPreferences = editing;
            if (notifications is not null) profile.NotificationPreferences = notifications;
            profile.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(AuthService.ToProfileDto(user, profile));
        }).Produces<ProfileDto>();

        group.MapGet("/usage", async (HttpContext http, QuotaService quotas, AppDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var usage = await quotas.GetUsageAsync(userId, ct);
            var limits = usage.Entitlement.Limits;
            var assistantMessages = await quotas.CountAssistantMessagesAsync(userId, usage.Entitlement, ct);

            var history = await db.UsageRecords
                .Where(u => u.UserId == userId && (u.Kind == UsageKind.ProcessingSeconds || u.Kind == UsageKind.Render))
                .GroupBy(u => new { u.PeriodStart, u.PeriodEnd })
                .Select(g => new
                {
                    g.Key.PeriodStart,
                    g.Key.PeriodEnd,
                    Processing = g.Where(u => u.Kind == UsageKind.ProcessingSeconds).Sum(u => (decimal?)u.Quantity) ?? 0m,
                    Renders = g.Where(u => u.Kind == UsageKind.Render).Sum(u => (decimal?)u.Quantity) ?? 0m,
                })
                .OrderByDescending(x => x.PeriodStart)
                .Take(12)
                .ToListAsync(ct);

            var response = new UsageResponse(
                new UsagePlanDto(usage.Entitlement.Plan.Code, usage.Entitlement.Plan.Name, limits),
                new UsagePeriodDto(usage.Entitlement.PeriodStart, usage.Entitlement.PeriodEnd),
                new UsageNumbersDto((long)Math.Round(usage.ProcessingSecondsUsed), usage.RendersUsed, usage.StorageBytesUsed, usage.ActiveProjects, assistantMessages),
                new UsageRemainingDto(
                    limits.MonthlyProcessingSeconds > 0 ? usage.RemainingProcessingSeconds : null,
                    PlanLimitHelpers.IsUnlimited(limits.MaxRendersPerMonth) ? null : Math.Max(0, limits.MaxRendersPerMonth - usage.RendersUsed),
                    limits.StorageQuotaBytes > 0 ? Math.Max(0, limits.StorageQuotaBytes - usage.StorageBytesUsed) : null,
                    PlanLimitHelpers.IsUnlimited(limits.MaxActiveProjects) ? null : Math.Max(0, limits.MaxActiveProjects - usage.ActiveProjects),
                    PlanLimitHelpers.IsUnlimited(limits.AssistantMessagesPerMonth) ? null : Math.Max(0, limits.AssistantMessagesPerMonth - assistantMessages)),
                history.Select(h => new UsageHistoryDto(h.PeriodStart, h.PeriodEnd, (long)Math.Round(h.Processing), (int)h.Renders)).ToList());
            return Results.Ok(response);
        }).Produces<UsageResponse>();

        // Re-authentication with the password: rate limited per IP like the credential endpoints, and wrong passwords
        // count toward the login lockout (423 locked_out).
        group.MapDelete("/", async ([FromBody] DeleteAccountRequest request, HttpContext http, AppDbContext db, UserManager<AppUser> users, AuthService auth, IClock clock, IOptions<AuthOptions> authOptions, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.Unauthorized();
            await auth.VerifyPasswordAsync(user, request.Password, "Senha incorreta.");

            var now = clock.UtcNow;
            user.DeletionRequestedAt = now;
            await users.UpdateAsync(user);

            // Soft-delete projects and clips, stop jobs; the purge service removes storage and rows later.
            await db.Projects.Where(p => p.UserId == userId && p.DeletedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, now).SetProperty(p => p.UpdatedAt, now), ct);
            await db.Clips.Where(c => c.UserId == userId && c.DeletedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, now).SetProperty(c => c.UpdatedAt, now), ct);
            await db.ProcessingJobs.Where(j => j.UserId == userId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Queued))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.FinishedAt, now).SetProperty(j => j.UpdatedAt, now), ct);
            await db.ProcessingJobs.Where(j => j.UserId == userId && JobStateMachine.RunningStatuses.Contains(j.Status))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.CancelRequested, true).SetProperty(j => j.UpdatedAt, now), ct);

            await auth.RevokeAllAsync(userId, ct);
            AuthCookies.Clear(http.Response, authOptions.Value);
            return Results.Accepted(value: new MessageResponse("Exclusão da conta agendada. Seus dados serão removidos em breve."));
        }).RequireRateLimiting(AuthEndpoints.AuthRateLimitPolicy);

        return app;
    }
}
