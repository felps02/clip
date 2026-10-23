using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Jobs;
using ClipForge.Domain.Plans;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Services;

public sealed record UsageSnapshot(
    Entitlement Entitlement,
    decimal ProcessingSecondsUsed,
    decimal ProcessingSecondsReserved,
    int RendersUsed,
    long StorageBytesUsed,
    int ActiveProjects,
    int ActiveJobs)
{
    public long ProcessingSecondsLimit => Entitlement.Limits.MonthlyProcessingSeconds;
    public long RemainingProcessingSeconds => QuotaCalculator.RemainingProcessingSeconds(ProcessingSecondsUsed, ProcessingSecondsReserved, Entitlement.Limits);
}

/// <summary>Server-side quota enforcement (docs/PLANS_AND_BILLING.md). Pure rules live in <see cref="QuotaCalculator"/>.</summary>
public sealed class QuotaService
{
    private readonly AppDbContext _db;
    private readonly SubscriptionService _subscriptions;
    private readonly IClock _clock;

    public QuotaService(AppDbContext db, SubscriptionService subscriptions, IClock clock)
    {
        _db = db;
        _subscriptions = subscriptions;
        _clock = clock;
    }

    public static readonly ProjectStatus[] ActiveProjectStatuses = [ProjectStatus.Created, ProjectStatus.Processing, ProjectStatus.Ready];

    public async Task<UsageSnapshot> GetUsageAsync(Guid userId, CancellationToken ct = default)
    {
        var entitlement = await _subscriptions.GetEntitlementAsync(userId, ct);
        var (start, end) = (entitlement.PeriodStart, entitlement.PeriodEnd);

        var processing = await _db.UsageRecords
            .Where(u => u.UserId == userId && u.Kind == UsageKind.ProcessingSeconds && u.PeriodStart >= start && u.PeriodStart < end)
            .SumAsync(u => (decimal?)u.Quantity, ct) ?? 0m;

        var renders = await _db.UsageRecords
            .Where(u => u.UserId == userId && u.Kind == UsageKind.Render && u.PeriodStart >= start && u.PeriodStart < end)
            .SumAsync(u => (decimal?)u.Quantity, ct) ?? 0m;

        var reserved = await _db.ProcessingJobs
            .Where(j => j.UserId == userId && JobStateMachine.ActiveStatuses.Contains(j.Status))
            .SumAsync(j => (int?)j.ReservedSeconds, ct) ?? 0;

        var storage = await _db.MediaAssets
            .Where(m => m.UserId == userId && m.DeletedAt == null && m.Status != MediaAssetStatus.Deleted && m.Status != MediaAssetStatus.Failed)
            .SumAsync(m => (long?)m.SizeBytes, ct) ?? 0L;

        var activeProjects = await _db.Projects
            .CountAsync(p => p.UserId == userId && p.DeletedAt == null && ActiveProjectStatuses.Contains(p.Status), ct);

        var activeJobs = await _db.ProcessingJobs
            .CountAsync(j => j.UserId == userId && JobStateMachine.ActiveStatuses.Contains(j.Status), ct);

        return new UsageSnapshot(entitlement, processing, reserved, (int)renders, storage, activeProjects, activeJobs);
    }

    /// <summary>Project creation: active projects and concurrent jobs (duration unknown yet).</summary>
    public async Task<UsageSnapshot> EnsureCanCreateProjectAsync(Guid userId, CancellationToken ct = default)
    {
        var usage = await GetUsageAsync(userId, ct);
        QuotaCalculator.CheckActiveProjects(usage.ActiveProjects, usage.Entitlement.Limits).ThrowIfDenied();
        QuotaCalculator.CheckConcurrentJobs(usage.ActiveJobs, usage.Entitlement.Limits).ThrowIfDenied();
        return usage;
    }

    /// <summary>Starting another analysis job for an existing project.</summary>
    public async Task<UsageSnapshot> EnsureCanStartJobAsync(Guid userId, CancellationToken ct = default)
    {
        var usage = await GetUsageAsync(userId, ct);
        QuotaCalculator.CheckConcurrentJobs(usage.ActiveJobs, usage.Entitlement.Limits).ThrowIfDenied();
        return usage;
    }

    /// <summary>At probe time: source duration and monthly minutes (used + reserved by other jobs + requested).</summary>
    public async Task<(QuotaDecision Decision, UsageSnapshot Usage)> CheckProbeAsync(ProcessingJob job, decimal durationSeconds, CancellationToken ct = default)
    {
        var usage = await GetUsageAsync(job.UserId, ct);
        var limits = usage.Entitlement.Limits;

        var duration = QuotaCalculator.CheckSourceDuration(durationSeconds, limits);
        if (!duration.Allowed) return (duration, usage);

        var reservedByOthers = usage.ProcessingSecondsReserved - job.ReservedSeconds;
        var minutes = QuotaCalculator.CheckProcessingSeconds(usage.ProcessingSecondsUsed, reservedByOthers, durationSeconds, limits);
        return (minutes, usage);
    }

    /// <summary>Requesting a render: renders in the period and, like any other job, the concurrent-jobs cap.</summary>
    public async Task<UsageSnapshot> EnsureCanRenderAsync(Guid userId, CancellationToken ct = default)
    {
        var usage = await GetUsageAsync(userId, ct);
        QuotaCalculator.CheckRenders(usage.RendersUsed, usage.Entitlement.Limits).ThrowIfDenied();
        QuotaCalculator.CheckConcurrentJobs(usage.ActiveJobs, usage.Entitlement.Limits).ThrowIfDenied();
        return usage;
    }

    public async Task<UsageSnapshot> EnsureCanUploadAsync(Guid userId, long sizeBytes, CancellationToken ct = default)
    {
        var usage = await GetUsageAsync(userId, ct);
        QuotaCalculator.CheckUpload(sizeBytes, usage.StorageBytesUsed, usage.Entitlement.Limits).ThrowIfDenied();
        return usage;
    }

    /// <summary>
    /// Serialises a check-then-act sequence of one user (quota check + insert): takes a per-user PostgreSQL advisory
    /// lock inside a transaction that spans <paramref name="action"/>, whose <c>SaveChangesAsync</c> therefore commits
    /// before the lock is released. Concurrent requests of the same user queue behind the lock and see each other's
    /// rows, so plan caps (active projects, concurrent jobs, renders, storage) cannot be exceeded by firing requests in
    /// parallel. Nested calls reuse the current transaction.
    /// </summary>
    public async Task<T> WithUserLockAsync<T>(Guid userId, Func<Task<T>> action, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            await LockUserAsync(userId, ct);
            return await action();
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await LockUserAsync(userId, ct);
        var result = await action();
        await transaction.CommitAsync(ct);
        return result;
    }

    private Task LockUserAsync(Guid userId, CancellationToken ct)
        => _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey(userId)})", ct);

    /// <summary>Stable 64-bit advisory lock key derived from the user id (a collision only adds serialisation).</summary>
    public static long AdvisoryLockKey(Guid userId)
    {
        Span<byte> bytes = stackalloc byte[16];
        userId.TryWriteBytes(bytes);
        return BitConverter.ToInt64(bytes) ^ BitConverter.ToInt64(bytes[8..]);
    }

    /// <summary>Assistant chat messages counted in the entitlement's current period (usage kind assistant_message).</summary>
    public async Task<int> CountAssistantMessagesAsync(Guid userId, Entitlement entitlement, CancellationToken ct = default)
    {
        var (start, end) = (entitlement.PeriodStart, entitlement.PeriodEnd);
        var used = await _db.UsageRecords
            .Where(u => u.UserId == userId && u.Kind == UsageKind.AssistantMessage && u.PeriodStart >= start && u.PeriodStart < end)
            .SumAsync(u => (decimal?)u.Quantity, ct) ?? 0m;
        return (int)used;
    }

    /// <summary>Appends an auditable usage record for the current period.</summary>
    public UsageRecord RecordUsage(Entitlement entitlement, Guid userId, UsageKind kind, decimal quantity, Guid? jobId = null, Guid? projectId = null, Guid? clipId = null, string? metadataJson = null)
    {
        var record = new UsageRecord
        {
            UserId = userId,
            SubscriptionId = entitlement.SubscriptionId,
            Kind = kind,
            Quantity = quantity,
            PeriodStart = entitlement.PeriodStart,
            PeriodEnd = entitlement.PeriodEnd,
            JobId = jobId,
            ProjectId = projectId,
            ClipId = clipId,
            Metadata = metadataJson,
            CreatedAt = _clock.UtcNow,
        };
        _db.UsageRecords.Add(record);
        return record;
    }
}
