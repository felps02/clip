using System.Diagnostics;
using ClipForge.Api.Common;
using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Assistant;

/// <summary>
/// <c>POST /api/assistant/chat</c>: provider availability → monthly message quota (reserved under the per-user lock, given
/// back when no answer is produced) → model (structured output) + rule layer → validation and plan caps → composed reply.
/// </summary>
public sealed class AssistantService
{
    private readonly AppDbContext _db;
    private readonly QuotaService _quotas;
    private readonly SubscriptionService _subscriptions;
    private readonly IAssistantModel _model;
    private readonly AssistantStatusCache _status;
    private readonly ILogger<AssistantService> _logger;

    public AssistantService(AppDbContext db, QuotaService quotas, SubscriptionService subscriptions, IAssistantModel model, AssistantStatusCache status, ILogger<AssistantService> logger)
    {
        _db = db;
        _quotas = quotas;
        _subscriptions = subscriptions;
        _model = model;
        _status = status;
        _logger = logger;
    }

    public async Task<AssistantChatResponse> ChatAsync(Guid userId, AssistantChatRequest request, CancellationToken ct)
    {
        var availability = await _status.GetAsync(ct);
        if (!availability.Available) throw Unavailable(availability.Reason ?? DisabledAssistantModel.Reason);

        var (reservationId, limits) = await ReserveMessageAsync(userId, ct);
        var answered = false;
        try
        {
            var conversation = request.Messages!;
            var message = (conversation[^1].Content ?? string.Empty).Trim();
            var current = PlanLimitHelpers.CapSettings(request.CurrentSettings ?? new AnalysisSettings(), limits);
            var duration = request.Context?.DurationSeconds;

            var rules = AssistantRules.Analyze(message, current);
            var started = Stopwatch.GetTimestamp();
            var reply = await _model.CompleteAsync(AssistantPrompt.Build(conversation, current, limits, duration), ct);
            var requested = AssistantMerge.Merge(rules, reply.Output, message);
            var outcome = AssistantPatchBuilder.Apply(current, requested, limits, duration);
            var text = AssistantReplyComposer.Compose(outcome, conversation.Count);
            answered = true;

            _logger.LogInformation("Assistant answered in {ElapsedMs} ms via {Provider}: {Changes} change(s), {Unsupported} unsupported, {Notes} note(s)",
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, _model.Provider, outcome.Changes.Count, outcome.Unsupported.Count, outcome.Notes.Count);
            return new AssistantChatResponse(text, outcome.Patch, outcome.Changes.Select(c => c.ToDto()).ToList(), _model.Provider, reply.Model ?? availability.Model);
        }
        catch (AssistantUnavailableException ex)
        {
            _status.Invalidate();
            throw Unavailable(ex.Message);
        }
        finally
        {
            // Only answered messages count: a provider failure or an aborted request gives the reservation back.
            if (!answered) await ReleaseAsync(reservationId);
        }
    }

    private static AppException Unavailable(string message) => new(ErrorCodes.AssistantUnavailable, StatusCodes.Status503ServiceUnavailable, message);

    /// <summary>
    /// Checks <c>assistantMessagesPerMonth</c> and records the message (usage kind assistant_message) under the per-user
    /// lock, so parallel requests cannot exceed the plan; the model call happens after the lock is released.
    /// </summary>
    private Task<(Guid ReservationId, PlanLimits Limits)> ReserveMessageAsync(Guid userId, CancellationToken ct)
        => _quotas.WithUserLockAsync(userId, async () =>
        {
            var entitlement = await _subscriptions.GetEntitlementAsync(userId, ct);
            var limit = entitlement.Limits.AssistantMessagesPerMonth;
            if (!PlanLimitHelpers.IsUnlimited(limit))
            {
                var used = await _quotas.CountAssistantMessagesAsync(userId, entitlement, ct);
                if (used >= limit)
                {
                    throw AppException.PlanLimit($"Limite de mensagens do assistente do seu plano atingido no período ({used} de {limit}).");
                }
            }
            var record = _quotas.RecordUsage(entitlement, userId, UsageKind.AssistantMessage, 1,
                metadataJson: JsonDefaults.Serialize(new { provider = _model.Provider }));
            await _db.SaveChangesAsync(ct);
            return (record.Id, entitlement.Limits);
        }, ct);

    private async Task ReleaseAsync(Guid reservationId)
    {
        try
        {
            await _db.UsageRecords.Where(u => u.Id == reservationId).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not release the assistant message reservation {ReservationId}", reservationId);
        }
    }
}

public static class AssistantEndpoints
{
    public const string RateLimitPolicy = "assistant";

    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assistant").WithTags("Assistant").WithValidation().RequireAuthorization();

        group.MapGet("/status", async (AssistantStatusCache status, CancellationToken ct) => Results.Ok(await status.GetAsync(ct)))
            .Produces<AssistantStatusDto>();

        group.MapPost("/chat", async (AssistantChatRequest request, HttpContext http, AssistantService service, CancellationToken ct)
                => Results.Ok(await service.ChatAsync(http.User.GetUserId(), request, ct)))
            .RequireRateLimiting(RateLimitPolicy)
            .Produces<AssistantChatResponse>();

        return app;
    }
}
