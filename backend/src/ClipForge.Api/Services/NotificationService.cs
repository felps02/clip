using System.Text.Json;
using ClipForge.Api.Options;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Email;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Services;

/// <summary>What a notification e-mail is about: each topic has its own switch in user_profiles.notification_preferences.</summary>
public enum EmailTopic { JobCompleted, JobFailed, QuotaWarning, Billing }

/// <summary>
/// In-app notifications (always created, they cannot be turned off) plus the e-mail copy the user opted into, sent only
/// to a confirmed address (docs/API.md → Notifications). E-mails and quota-threshold checks are collected while a
/// request works and only leave in <see cref="FlushAsync"/>, which the caller runs after its changes were committed:
/// rolled-back work never sends anything, and delivery itself happens in the background (<see cref="EmailOutbox"/>),
/// so a slow or failing SMTP server never fails or delays the request.
/// </summary>
public sealed class NotificationService
{
    /// <summary>Thresholds (% of the plan's monthly processing minutes) that raise a quota_warning, once per period each.</summary>
    public static readonly int[] QuotaThresholds = [80, 100];

    private readonly AppDbContext _db;
    private readonly QuotaService _quotas;
    private readonly EmailOutbox _outbox;
    private readonly IClock _clock;
    private readonly FrontendOptions _frontend;
    private readonly BrandingOptions _branding;
    private readonly ILogger<NotificationService> _logger;
    private readonly List<PendingEmail> _pendingEmails = new();
    private readonly HashSet<Guid> _quotaChecks = new();

    public NotificationService(
        AppDbContext db,
        QuotaService quotas,
        EmailOutbox outbox,
        IClock clock,
        IOptions<FrontendOptions> frontend,
        IOptions<BrandingOptions> branding,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _quotas = quotas;
        _outbox = outbox;
        _clock = clock;
        _frontend = frontend.Value;
        _branding = branding.Value;
        _logger = logger;
    }

    private sealed record PendingEmail(Guid UserId, EmailTopic Topic, string Subject, string Body);

    public string BrandName => _branding.Name;

    /// <summary>Absolute URL of a web page (Frontend:BaseUrl + path).</summary>
    public string AppUrl(string path) => $"{_frontend.BaseUrl.TrimEnd('/')}{path}";

    public string ProjectUrl(Guid projectId) => AppUrl($"/projects/{projectId:D}");

    /// <summary>Adds an in-app notification to the change tracker (the caller saves).</summary>
    public Notification Add(Guid userId, NotificationType type, string title, string? body = null, object? data = null)
    {
        var notification = new Notification
        {
            UserId = userId,
            Type = type,
            Title = title.Length > 200 ? title[..200] : title,
            Body = body is { Length: > 2000 } ? body[..2000] : body,
            Data = data is null ? null : JsonDefaults.Serialize(data),
            CreatedAt = _clock.UtcNow,
        };
        _db.Notifications.Add(notification);
        return notification;
    }

    /// <summary>
    /// Queues an e-mail about <paramref name="topic"/>. It goes out at <see cref="FlushAsync"/> when the user's
    /// preference for the topic is on, the address is confirmed and the account is not being deleted.
    /// <paramref name="body"/> is the message itself: the greeting and the footer with the preference that caused it
    /// are added when it is sent.
    /// </summary>
    public void QueueEmail(Guid userId, EmailTopic topic, string subject, string body)
        => _pendingEmails.Add(new PendingEmail(userId, topic, subject, body));

    /// <summary>Processing minutes were charged to the user: check the quota thresholds at the next <see cref="FlushAsync"/>.</summary>
    public void RequestQuotaCheck(Guid userId) => _quotaChecks.Add(userId);

    /// <summary>Drops what was queued: the work that produced it was rolled back.</summary>
    public void DiscardPending()
    {
        _pendingEmails.Clear();
        _quotaChecks.Clear();
    }

    /// <summary>
    /// Runs after the caller committed its changes: quota-threshold checks (which may add quota_warning notifications),
    /// then hands the queued e-mails the users opted into to the background outbox. Never throws: the request's work is
    /// already committed, so failures are only logged.
    /// </summary>
    public async Task FlushAsync()
    {
        // No request token: the committed work must not lose its follow-up because the caller disconnected.
        var token = CancellationToken.None;

        if (_quotaChecks.Count > 0)
        {
            var users = _quotaChecks.ToList();
            _quotaChecks.Clear();
            foreach (var userId in users)
            {
                try
                {
                    await CheckQuotaThresholdsAsync(userId, token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Quota threshold check failed for user {UserId}", userId);
                }
            }
        }

        if (_pendingEmails.Count == 0) return;
        var pending = _pendingEmails.ToList();
        _pendingEmails.Clear();
        try
        {
            await EnqueueEmailsAsync(pending, token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Count} notification e-mail(s) could not be queued", pending.Count);
        }
    }

    private async Task EnqueueEmailsAsync(List<PendingEmail> pending, CancellationToken ct)
    {
        var userIds = pending.Select(p => p.UserId).Distinct().ToList();
        // Only a confirmed address: registration does not require confirmation (Auth:RequireConfirmedEmail), and these
        // e-mails carry text the account chose (project titles, display name) — an unconfirmed address may belong to
        // someone else (a typo, or an address registered on purpose to send them mail from the product's domain). The
        // verification e-mail sent at registration is how the owner confirms it.
        var recipients = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.EmailConfirmed && u.DeletedAt == null && u.DeletionRequestedAt == null)
            .Select(u => new { u.Id, u.Email, u.DisplayName })
            .ToListAsync(ct);
        var preferences = await _db.UserProfiles.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .Select(p => new { p.UserId, p.NotificationPreferences })
            .ToListAsync(ct);

        foreach (var email in pending)
        {
            var recipient = recipients.FirstOrDefault(r => r.Id == email.UserId);
            if (recipient is null || string.IsNullOrWhiteSpace(recipient.Email)) continue;
            var prefs = preferences.FirstOrDefault(p => p.UserId == email.UserId)?.NotificationPreferences ?? new NotificationPreferences();
            if (!Wants(prefs, email.Topic)) continue;

            var text = $"Olá, {recipient.DisplayName}!\n\n{email.Body}\n\n—\n"
                + $"Você recebe este e-mail porque a opção \"{TopicLabel(email.Topic)}\" está ativada em Configurações → Notificações: {AppUrl("/settings")}\n"
                + _branding.Name;
            if (!_outbox.TryEnqueue(new EmailMessage(recipient.Email!, email.Subject, text)))
            {
                _logger.LogWarning("E-mail outbox is full; notification e-mail ({Topic}) for user {UserId} dropped", email.Topic, email.UserId);
            }
        }
    }

    public static bool Wants(NotificationPreferences prefs, EmailTopic topic) => topic switch
    {
        EmailTopic.JobCompleted => prefs.EmailOnJobCompleted,
        EmailTopic.JobFailed => prefs.EmailOnJobFailed,
        EmailTopic.QuotaWarning => prefs.EmailOnQuotaWarning,
        EmailTopic.Billing => prefs.EmailOnBilling,
        _ => false,
    };

    private static string TopicLabel(EmailTopic topic) => topic switch
    {
        EmailTopic.JobCompleted => "Análise concluída",
        EmailTopic.JobFailed => "Falha no processamento",
        EmailTopic.QuotaWarning => "Avisos de cota",
        _ => "Assinatura e pagamentos",
    };

    /// <summary>
    /// When the processing minutes used in the current period reach 80 % and then 100 % of the plan's monthly minutes,
    /// one quota_warning notification per threshold and period (the highest threshold reached; a lower one is never
    /// sent after a higher one), plus the e-mail when the user wants quota warnings. Serialised per user so two jobs
    /// finishing together cannot both raise the same threshold.
    /// </summary>
    private async Task CheckQuotaThresholdsAsync(Guid userId, CancellationToken ct)
    {
        await _quotas.WithUserLockAsync(userId, async () =>
        {
            var usage = await _quotas.GetUsageAsync(userId, ct);
            var limit = usage.ProcessingSecondsLimit;
            // Without a subscription row the period is not a real billing period (nothing to deduplicate against).
            if (limit <= 0 || usage.Entitlement.Subscription is null) return 0;

            var percent = usage.ProcessingSecondsUsed * 100m / limit;
            var threshold = QuotaThresholds.Where(t => percent >= t).DefaultIfEmpty(0).Max();
            if (threshold == 0) return 0;

            var periodStart = usage.Entitlement.PeriodStart;
            var sent = await _db.Notifications.AsNoTracking()
                .Where(n => n.UserId == userId && n.Type == NotificationType.QuotaWarning && n.CreatedAt >= periodStart)
                .Select(n => n.Data)
                .ToListAsync(ct);
            var highestSent = sent.Select(ReadThreshold).DefaultIfEmpty(0).Max();
            if (highestSent >= threshold) return 0;

            var limits = usage.Entitlement.Limits;
            var usedMinutes = (long)Math.Floor(usage.ProcessingSecondsUsed / 60m);
            var periodEnd = usage.Entitlement.PeriodEnd;
            var until = await FormatUserDateAsync(userId, periodEnd, ct);
            string title, body;
            if (threshold >= 100)
            {
                title = "Minutos de processamento esgotados";
                body = $"Você usou os {limits.MonthlyProcessingMinutes} minutos de processamento do seu plano neste período. Novas análises ficam bloqueadas até {until} ou até você fazer upgrade do plano.";
            }
            else
            {
                title = $"Você já usou {threshold}% dos minutos de processamento";
                body = $"Você usou {usedMinutes} de {limits.MonthlyProcessingMinutes} minutos de processamento do seu plano neste período (que termina em {until}). Faça upgrade do plano se precisar de mais.";
            }
            Add(userId, NotificationType.QuotaWarning, title, body, new
            {
                threshold,
                usedSeconds = (long)Math.Round(usage.ProcessingSecondsUsed),
                limitSeconds = limit,
                periodStart,
                periodEnd,
            });
            QueueEmail(userId, EmailTopic.QuotaWarning, $"{_branding.Name}: {title.ToLowerInvariant()}",
                $"{body}\n\nAcompanhe o uso do período: {AppUrl("/billing")}");
            await _db.SaveChangesAsync(ct);
            return 1;
        }, ct);
    }

    private static int ReadThreshold(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.TryGetProperty("threshold", out var value) && value.TryGetInt32(out var threshold) ? threshold : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>dd/MM/yyyy in the user's time zone (profile timezone, default America/Sao_Paulo; UTC when unknown).</summary>
    public async Task<string> FormatUserDateAsync(Guid userId, DateTimeOffset value, CancellationToken ct = default)
    {
        var timezone = await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.Timezone).FirstOrDefaultAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(timezone))
            {
                value = TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(timezone));
            }
        }
        catch (Exception)
        {
            // Unknown or invalid zone id (the profile accepts free text): keep UTC.
        }
        return value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }
}
