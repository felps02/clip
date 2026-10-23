using ClipForge.Api.Features.Internal;
using ClipForge.Api.Features.Media;
using ClipForge.Api.Options;
using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Jobs;
using ClipForge.Domain.Media;
using ClipForge.Infrastructure.Persistence;
using ClipForge.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.HostedServices;

/// <summary>Base class for periodic maintenance services. The actual work lives in scoped services so tests can call it.</summary>
public abstract class PeriodicService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    protected PeriodicService(IServiceProvider services, ILogger logger)
    {
        _services = services;
        _logger = logger;
    }

    protected abstract TimeSpan Interval { get; }

    protected abstract Task RunOnceAsync(IServiceProvider scope, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so startup (migrations, seeding) finishes first.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Service} iteration failed", GetType().Name);
            }
        } while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Moves jobs with expired leases back to the queue (or fails them when attempts are exhausted).</summary>
public sealed class JobReaper : PeriodicService
{
    private readonly JobsOptions _options;

    public JobReaper(IServiceProvider services, IOptions<JobsOptions> options, ILogger<JobReaper> logger) : base(services, logger)
    {
        _options = options.Value;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(5, _options.ReaperIntervalSeconds));

    protected override Task RunOnceAsync(IServiceProvider scope, CancellationToken ct)
        => scope.GetRequiredService<InternalJobService>().ReapExpiredLeasesAsync(ct);
}

/// <summary>Expired pending uploads and purge of soft-deleted projects/clips after the plan retention window.</summary>
public sealed class RetentionCleanup : PeriodicService
{
    private readonly RetentionOptions _options;

    public RetentionCleanup(IServiceProvider services, IOptions<RetentionOptions> options, ILogger<RetentionCleanup> logger) : base(services, logger)
    {
        _options = options.Value;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes));

    protected override Task RunOnceAsync(IServiceProvider scope, CancellationToken ct)
        => scope.GetRequiredService<RetentionService>().RunOnceAsync(ct);
}

/// <summary>Purges accounts whose deletion was requested (LGPD).</summary>
public sealed class AccountPurge : PeriodicService
{
    private readonly RetentionOptions _options;

    public AccountPurge(IServiceProvider services, IOptions<RetentionOptions> options, ILogger<AccountPurge> logger) : base(services, logger)
    {
        _options = options.Value;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes));

    protected override Task RunOnceAsync(IServiceProvider scope, CancellationToken ct)
        => scope.GetRequiredService<RetentionService>().PurgeAccountsAsync(ct);
}

/// <summary>Scoped implementation of the retention and purge rules (docs/DATA_MODEL.md "Deletion strategy").</summary>
public sealed class RetentionService
{
    private readonly AppDbContext _db;
    private readonly IObjectStorage _storage;
    private readonly QuotaService _quotas;
    private readonly IClock _clock;
    private readonly RetentionOptions _options;
    private readonly MediaOptions _media;
    private readonly UserManager<AppUser> _users;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(AppDbContext db, IObjectStorage storage, QuotaService quotas, IClock clock, IOptions<RetentionOptions> options,
        IOptions<MediaOptions> media, UserManager<AppUser> users, ILogger<RetentionService> logger)
    {
        _db = db;
        _storage = storage;
        _quotas = quotas;
        _clock = clock;
        _options = options.Value;
        _media = media.Value;
        _users = users;
        _logger = logger;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await ExpirePendingUploadsAsync(ct);
        await SweepDeletedUploadsAsync(ct);
        await PurgeDeletedProjectsAsync(ct);
        await PurgeDeletedClipsAsync(ct);
    }

    public async Task<int> ExpirePendingUploadsAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var expired = await _db.MediaAssets
            .Where(a => a.Status == MediaAssetStatus.Pending && a.ExpiresAt != null && a.ExpiresAt < now)
            .OrderBy(a => a.ExpiresAt)
            .Take(200)
            .ToListAsync(ct);
        foreach (var asset in expired)
        {
            // Status first, under the lock a signed upload takes to move its file into place, then the object: an
            // upload finishing concurrently either landed before (and is deleted here) or is refused.
            await _quotas.WithUserLockAsync(asset.UserId, async () =>
            {
                asset.Status = MediaAssetStatus.Deleted;
                asset.DeletedAt = now;
                asset.ExpiresAt = MediaEndpoints.DeletedObjectSweepAt(asset, _media, now);
                await _db.SaveChangesAsync(ct);
                return true;
            }, ct);
            await SafeDeleteAsync(asset.StorageKey, ct);
        }
        if (expired.Count > 0)
        {
            _logger.LogInformation("Expired {Count} pending uploads", expired.Count);
        }
        return expired.Count;
    }

    /// <summary>
    /// Deletes once more the storage objects of assets deleted while their signed upload URL was still valid, after it
    /// expired (media_assets.expires_at of a deleted asset): an object re-uploaded with that URL in between would
    /// otherwise stay on storage without a row counting it in the storage quota.
    /// </summary>
    public async Task<int> SweepDeletedUploadsAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var assets = await _db.MediaAssets
            .Where(a => a.Status == MediaAssetStatus.Deleted && a.ExpiresAt != null && a.ExpiresAt < now)
            .OrderBy(a => a.ExpiresAt)
            .Take(200)
            .ToListAsync(ct);
        foreach (var asset in assets)
        {
            await SafeDeleteAsync(asset.StorageKey, ct);
            asset.ExpiresAt = null;
        }
        if (assets.Count > 0) await _db.SaveChangesAsync(ct);
        return assets.Count;
    }

    private async Task<Dictionary<Guid, int>> RetentionDaysByUserAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        var subs = await _db.Subscriptions.Include(s => s.Plan)
            .Where(s => ids.Contains(s.UserId) && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.PastDue))
            .ToListAsync(ct);
        var free = await _db.Plans.FirstOrDefaultAsync(p => p.Code == "free", ct);
        var fallback = free?.Limits.RetentionDays ?? 7;
        return ids.ToDictionary(id => id, id => subs.Where(s => s.UserId == id).OrderByDescending(s => s.CreatedAt).FirstOrDefault()?.Plan.Limits.RetentionDays ?? fallback);
    }

    public async Task<int> PurgeDeletedProjectsAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var candidates = await _db.Projects.Include(p => p.MediaAsset)
            .Where(p => p.DeletedAt != null)
            .OrderBy(p => p.DeletedAt)
            .Take(100)
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;
        var retention = await RetentionDaysByUserAsync(candidates.Select(p => p.UserId), ct);
        var purged = 0;
        foreach (var project in candidates)
        {
            var days = retention.TryGetValue(project.UserId, out var d) ? d : 7;
            if (project.DeletedAt!.Value.AddDays(days) > now) continue;
            if (await _db.ProcessingJobs.AnyAsync(j => j.ProjectId == project.Id && JobStateMachine.RunningStatuses.Contains(j.Status), ct)) continue;
            await PurgeProjectAsync(project, ct);
            purged++;
        }
        if (purged > 0) _logger.LogInformation("Purged {Count} deleted projects", purged);
        return purged;
    }

    public async Task PurgeProjectAsync(Project project, CancellationToken ct)
    {
        // Storage first (source, job artifacts, renders, thumbnails), then rows.
        var jobIds = await _db.ProcessingJobs.Where(j => j.ProjectId == project.Id).Select(j => j.Id).ToListAsync(ct);
        foreach (var jobId in jobIds)
        {
            await SafeDeletePrefixAsync(StorageKeys.JobArtifactPrefix(project.UserId, jobId), ct);
        }
        var renderKeys = await _db.ClipRenders.Where(r => r.Clip.ProjectId == project.Id && r.StorageKey != null).Select(r => r.StorageKey!).ToListAsync(ct);
        foreach (var key in renderKeys) await SafeDeleteAsync(key, ct);
        var thumbKeys = await _db.Clips.Where(c => c.ProjectId == project.Id && c.ThumbnailStorageKey != null).Select(c => c.ThumbnailStorageKey!).ToListAsync(ct);
        foreach (var key in thumbKeys) await SafeDeleteAsync(key, ct);

        // The source asset may have been reused by another project after this one was soft-deleted (the API allows
        // it): it is only purged together with the last project that references it, otherwise the live project would
        // silently lose its source (projects.media_asset_id is ON DELETE SET NULL).
        var asset = project.MediaAsset;
        var assetStillUsed = asset is not null && await _db.Projects.AnyAsync(p => p.MediaAssetId == asset.Id && p.Id != project.Id, ct);
        if (asset is not null && !assetStillUsed)
        {
            await SafeDeleteAsync(asset.StorageKey, ct);
        }

        await _db.Clips.Where(c => c.ProjectId == project.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CurrentRenderId, (Guid?)null), ct);
        await _db.ClipRenders.Where(r => r.Clip.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await _db.Clips.Where(c => c.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await _db.ClipCandidates.Where(c => c.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await _db.ProcessingJobs.Where(j => j.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await _db.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync(ct);
        if (asset is not null && !assetStillUsed)
        {
            await _db.MediaAssets.Where(m => m.Id == asset.Id).ExecuteDeleteAsync(ct);
        }
        else if (asset is not null)
        {
            _logger.LogInformation("Kept media asset {AssetId} of purged project {ProjectId}: still referenced by another project", asset.Id, project.Id);
        }
    }

    public async Task<int> PurgeDeletedClipsAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var clips = await _db.Clips.Where(c => c.DeletedAt != null && c.Project.DeletedAt == null).OrderBy(c => c.DeletedAt).Take(200).ToListAsync(ct);
        if (clips.Count == 0) return 0;
        var retention = await RetentionDaysByUserAsync(clips.Select(c => c.UserId), ct);
        var purged = 0;
        foreach (var clip in clips)
        {
            var days = retention.TryGetValue(clip.UserId, out var d) ? d : 7;
            if (clip.DeletedAt!.Value.AddDays(days) > now) continue;
            if (await _db.ClipRenders.AnyAsync(r => r.ClipId == clip.Id && (r.Status == RenderStatus.Pending || r.Status == RenderStatus.Rendering), ct)) continue;

            var keys = await _db.ClipRenders.Where(r => r.ClipId == clip.Id && r.StorageKey != null).Select(r => r.StorageKey!).ToListAsync(ct);
            foreach (var key in keys) await SafeDeleteAsync(key, ct);
            if (clip.ThumbnailStorageKey is not null) await SafeDeleteAsync(clip.ThumbnailStorageKey, ct);

            await _db.Clips.Where(c => c.Id == clip.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CurrentRenderId, (Guid?)null), ct);
            await _db.ClipRenders.Where(r => r.ClipId == clip.Id).ExecuteDeleteAsync(ct);
            await _db.Clips.Where(c => c.Id == clip.Id).ExecuteDeleteAsync(ct);
            purged++;
        }
        if (purged > 0) _logger.LogInformation("Purged {Count} deleted clips", purged);
        return purged;
    }

    /// <summary>Hard-deletes users whose deletion request is older than Retention:AccountPurgeDelayHours.</summary>
    public async Task<int> PurgeAccountsAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var threshold = now.AddHours(-Math.Max(0, _options.AccountPurgeDelayHours));
        var users = await _db.Users.Where(u => u.DeletionRequestedAt != null && u.DeletionRequestedAt <= threshold).OrderBy(u => u.DeletionRequestedAt).Take(20).ToListAsync(ct);
        var purged = 0;
        foreach (var user in users)
        {
            if (await _db.ProcessingJobs.AnyAsync(j => j.UserId == user.Id && JobStateMachine.RunningStatuses.Contains(j.Status), ct))
            {
                continue; // wait for the worker to abort
            }
            await PurgeUserAsync(user, ct);
            purged++;
        }
        if (purged > 0) _logger.LogInformation("Purged {Count} accounts", purged);
        return purged;
    }

    public async Task PurgeUserAsync(AppUser user, CancellationToken ct)
    {
        var userId = user.Id;
        var now = _clock.UtcNow;

        await _db.RefreshTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.ProcessingJobs.Where(j => j.UserId == userId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Queued))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.FinishedAt, now), ct);

        // Whole user storage space.
        await SafeDeletePrefixAsync(StorageKeys.UserPrefix(userId), ct);

        // Accounting rows are kept without PII.
        await _db.UsageRecords.Where(u => u.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.UserId, (Guid?)null), ct);
        await _db.PaymentEvents.Where(p => p.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(p => p.UserId, (Guid?)null), ct);

        await _db.Clips.Where(c => c.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(c => c.CurrentRenderId, (Guid?)null), ct);
        await _db.ClipRenders.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.Clips.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.ClipCandidates.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.ProcessingJobs.Where(j => j.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.Projects.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.MediaAssets.Where(m => m.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.Notifications.Where(n => n.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.IntegrationConnections.Where(i => i.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.Subscriptions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.UserProfiles.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);

        var result = await _users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            _logger.LogError("Could not delete user {UserId}: {Errors}", userId, string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    private async Task SafeDeleteAsync(string key, CancellationToken ct)
    {
        try
        {
            await _storage.DeleteAsync(key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not delete storage object {Key}", key);
        }
    }

    private async Task SafeDeletePrefixAsync(string prefix, CancellationToken ct)
    {
        try
        {
            await _storage.DeleteByPrefixAsync(prefix, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not delete storage prefix {Prefix}", prefix);
        }
    }
}
