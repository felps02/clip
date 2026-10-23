using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Jobs;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Media;

/// <summary>
/// State binding of the signed Local-storage upload (<c>PUT /api/storage/upload</c>). The HMAC signature proves the
/// API issued the URL, but it stays valid for its whole TTL, so the target must also still accept bytes: a source
/// upload (<c>users/{userId}/sources/{assetId}/…</c>) only while its media asset is pending — never after
/// <c>/complete</c> verified it, after a failed verification or after <c>DELETE /api/media/{id}</c> — and a job
/// artifact (<c>users/{userId}/jobs/{jobId}/…</c>) only while the job runs. The check runs before the body is read and
/// again when the uploaded file is moved into place; for sources that second check holds the per-user lock that
/// <c>/complete</c> and the asset deletion also take, so a verified object cannot be replaced and a deleted one cannot
/// come back as an untracked file outside the storage quota.
/// </summary>
public sealed class UploadTargetGuard
{
    private readonly AppDbContext _db;
    private readonly QuotaService _quotas;

    public UploadTargetGuard(AppDbContext db, QuotaService quotas)
    {
        _db = db;
        _quotas = quotas;
    }

    private sealed record Target(Guid UserId, Guid? AssetId, Guid? JobId);

    /// <summary>Throws 409 conflict when the target no longer accepts uploads (403 for keys that are not upload targets).</summary>
    public Task EnsureAcceptsAsync(string key, CancellationToken ct) => CheckAsync(Parse(key), key, ct);

    /// <summary>Re-checks the target and runs <paramref name="commit"/> (moves the uploaded file into place) while it still accepts the upload.</summary>
    public async Task CommitAsync(string key, Action commit, CancellationToken ct)
    {
        var target = Parse(key);
        if (target.AssetId is not null)
        {
            await _quotas.WithUserLockAsync(target.UserId, async () =>
            {
                await CheckAsync(target, key, ct);
                commit();
                return true;
            }, ct);
            return;
        }
        await CheckAsync(target, key, ct);
        commit();
    }

    private static Target Parse(string key)
    {
        var parts = key.Split('/');
        if (parts.Length >= 5 && parts[0] == "users" && Guid.TryParse(parts[1], out var userId) && Guid.TryParse(parts[3], out var id))
        {
            if (parts[2] == "sources" && parts.Length == 5) return new Target(userId, id, null);
            if (parts[2] == "jobs" && parts.Length == 6) return new Target(userId, null, id);
        }
        throw AppException.Forbidden("URL de upload inválida.");
    }

    private async Task CheckAsync(Target target, string key, CancellationToken ct)
    {
        if (target.AssetId is { } assetId)
        {
            var asset = await _db.MediaAssets.AsNoTracking()
                .Where(a => a.StorageKey == key)
                .Select(a => new { a.Id, a.UserId, a.Status, a.DeletedAt })
                .FirstOrDefaultAsync(ct);
            if (asset is null || asset.Id != assetId || asset.UserId != target.UserId)
            {
                throw AppException.Forbidden("URL de upload inválida.");
            }
            if (asset.Status != MediaAssetStatus.Pending || asset.DeletedAt is not null)
            {
                throw AppException.Conflict("Este upload já foi concluído ou cancelado. Crie um novo upload para enviar outro arquivo.");
            }
            return;
        }

        var status = await _db.ProcessingJobs.AsNoTracking()
            .Where(j => j.Id == target.JobId && j.UserId == target.UserId)
            .Select(j => (JobStatus?)j.Status)
            .FirstOrDefaultAsync(ct);
        if (status is null)
        {
            throw AppException.Forbidden("URL de upload inválida.");
        }
        if (!JobStateMachine.IsRunning(status.Value))
        {
            throw AppException.Conflict("O job não está mais em execução; ele não aceita novos arquivos.");
        }
    }
}
