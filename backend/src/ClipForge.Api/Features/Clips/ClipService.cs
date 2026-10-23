using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Jobs;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Clips;

public sealed record UpdateClipRequest(string? Title, decimal? StartSeconds, decimal? EndSeconds, string? AspectRatio, SubtitleSettings? Subtitles, FramingSettings? Framing);

public sealed record ClipSourceDto(bool Previewable, string? PreviewUrl, decimal? DurationSeconds);

public sealed record ClipDetailDto(
    Guid Id,
    Guid ProjectId,
    string ProjectTitle,
    string Title,
    ClipCategory? Category,
    int? Score,
    ClipStatus Status,
    string AspectRatio,
    decimal DurationSeconds,
    decimal StartSeconds,
    decimal EndSeconds,
    string? ThumbnailUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    SubtitleSettings Subtitles,
    FramingSettings Framing,
    string? Reasoning,
    string? Transcript,
    RenderDto? CurrentRender,
    IReadOnlyList<RenderDto> Renders,
    ClipSourceDto Source,
    JobSummaryDto? ActiveJob);

public sealed record UpdateClipResponse(ClipDetailDto Clip, bool NeedsRender);

public sealed record RenderClipResponse(ClipDetailDto Clip, JobDto Job, RenderDto Render);

public sealed record DownloadResponse(string Url, DateTimeOffset ExpiresAt, string FileName);

public sealed class UpdateClipRequestValidator : AbstractValidator<UpdateClipRequest>
{
    public UpdateClipRequestValidator()
    {
        RuleFor(x => x.Title).MinimumLength(1).MaximumLength(200).When(x => x.Title is not null);
        RuleFor(x => x.StartSeconds).GreaterThanOrEqualTo(0).When(x => x.StartSeconds.HasValue);
        RuleFor(x => x.EndSeconds).GreaterThan(0).When(x => x.EndSeconds.HasValue);
        RuleFor(x => x.AspectRatio).Must(AspectRatios.IsValid).WithMessage("Formato inválido (9:16, 16:9 ou 1:1).").When(x => x.AspectRatio is not null);
        RuleFor(x => x.Subtitles!).SetValidator(new Projects.SubtitleSettingsValidator()).When(x => x.Subtitles is not null);
        RuleFor(x => x.Framing!).SetValidator(new Projects.FramingSettingsValidator()).When(x => x.Framing is not null);
    }
}

public sealed class ClipService
{
    private readonly AppDbContext _db;
    private readonly MediaUrls _urls;
    private readonly RenderService _renders;
    private readonly SubscriptionService _subscriptions;
    private readonly QuotaService _quotas;
    private readonly IClock _clock;
    private readonly Live.LiveJobSupport _live;

    public ClipService(AppDbContext db, MediaUrls urls, RenderService renders, SubscriptionService subscriptions, QuotaService quotas, IClock clock, Live.LiveJobSupport live)
    {
        _live = live;
        _db = db;
        _urls = urls;
        _renders = renders;
        _subscriptions = subscriptions;
        _quotas = quotas;
        _clock = clock;
    }

    public async Task<Clip> GetOwnedAsync(Guid userId, Guid clipId, CancellationToken ct)
    {
        var clip = await _db.Clips
            .Include(c => c.Project).ThenInclude(p => p.MediaAsset)
            .FirstOrDefaultAsync(c => c.Id == clipId && c.UserId == userId && c.DeletedAt == null && c.Project.DeletedAt == null, ct);
        return clip ?? throw AppException.NotFound("Clipe não encontrado.");
    }

    public async Task<(IReadOnlyList<ClipSummaryDto> Items, int Total)> ListAsync(Guid userId, Guid? projectId, string? category, string? status, string? q, string? sort, string? order, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Clips.Where(c => c.UserId == userId && c.DeletedAt == null && c.Project.DeletedAt == null);
        if (projectId is { } pid) query = query.Where(c => c.ProjectId == pid);
        if (!string.IsNullOrWhiteSpace(category) && EnumNames.TryParse<ClipCategory>(category, out var cat)) query = query.Where(c => c.Category == cat);
        if (!string.IsNullOrWhiteSpace(status) && EnumNames.TryParse<ClipStatus>(status, out var st)) query = query.Where(c => c.Status == st);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{EscapeLike(q.Trim())}%";
            query = query.Where(c => EF.Functions.ILike(c.Title, pattern, "\\"));
        }

        var desc = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
        query = (sort?.ToLowerInvariant()) switch
        {
            "score" => desc ? query.OrderByDescending(c => c.Score).ThenByDescending(c => c.CreatedAt) : query.OrderBy(c => c.Score).ThenBy(c => c.CreatedAt),
            "duration" => desc ? query.OrderByDescending(c => c.EndSeconds - c.StartSeconds) : query.OrderBy(c => c.EndSeconds - c.StartSeconds),
            _ => desc ? query.OrderByDescending(c => c.CreatedAt) : query.OrderBy(c => c.CreatedAt),
        };

        var total = await query.CountAsync(ct);
        var clips = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Include(c => c.Project)
            .ToListAsync(ct);

        var renders = await LoadCurrentRendersAsync(clips, ct);
        var items = new List<ClipSummaryDto>(clips.Count);
        foreach (var clip in clips)
        {
            items.Add(clip.ToSummary(clip.Project.Title, await _urls.ThumbnailAsync(clip.ThumbnailStorageKey, ct), renders.GetValueOrDefault(clip.Id)));
        }
        return (items, total);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Loads the current render of each clip in one query (keyed by clip id).</summary>
    public async Task<Dictionary<Guid, ClipRender>> LoadCurrentRendersAsync(IReadOnlyCollection<Clip> clips, CancellationToken ct)
    {
        var ids = clips.Where(c => c.CurrentRenderId is not null).Select(c => c.CurrentRenderId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, ClipRender>();
        var renders = await _db.ClipRenders.Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        return clips.Where(c => c.CurrentRenderId is not null)
            .Select(c => (c.Id, Render: renders.FirstOrDefault(r => r.Id == c.CurrentRenderId)))
            .Where(x => x.Render is not null)
            .ToDictionary(x => x.Id, x => x.Render!);
    }

    public async Task<ClipSummaryDto> ToSummaryAsync(Clip clip, CancellationToken ct)
    {
        var project = clip.Project ?? await _db.Projects.FirstAsync(p => p.Id == clip.ProjectId, ct);
        var render = clip.CurrentRenderId is { } rid ? await _db.ClipRenders.FirstOrDefaultAsync(r => r.Id == rid, ct) : null;
        return clip.ToSummary(project.Title, await _urls.ThumbnailAsync(clip.ThumbnailStorageKey, ct), render);
    }

    public async Task<ClipDetailDto> ToDetailAsync(Clip clip, CancellationToken ct)
    {
        var project = clip.Project ?? await _db.Projects.Include(p => p.MediaAsset).FirstAsync(p => p.Id == clip.ProjectId, ct);
        var asset = project.MediaAsset;
        var renders = await _db.ClipRenders.Where(r => r.ClipId == clip.Id).OrderByDescending(r => r.Version).ToListAsync(ct);
        var current = renders.FirstOrDefault(r => r.Id == clip.CurrentRenderId);
        var fileName = MediaUrls.DownloadFileName(clip);

        RenderDto? currentDto = null;
        if (current is not null)
        {
            currentDto = current.ToDto(await _urls.StreamAsync(current, ct), await _urls.DownloadAsync(current, fileName, ct));
        }

        var candidate = clip.CandidateId is { } cid ? await _db.ClipCandidates.FirstOrDefaultAsync(c => c.Id == cid, ct) : null;

        var activeJob = await _db.ProcessingJobs
            .Where(j => j.ProjectId == clip.ProjectId && j.Kind == JobKind.RenderClip && JobStateMachine.ActiveStatuses.Contains(j.Status))
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);
        var clipJob = activeJob.FirstOrDefault(j => j.Input != null && j.Input.Contains(clip.Id.ToString("D"), StringComparison.OrdinalIgnoreCase));

        var previewable = asset?.IsPreviewable == true;
        return new ClipDetailDto(clip.Id, clip.ProjectId, project.Title, clip.Title, clip.Category, clip.Score, clip.Status, clip.AspectRatio,
            clip.EndSeconds - clip.StartSeconds, clip.StartSeconds, clip.EndSeconds, await _urls.ThumbnailAsync(clip.ThumbnailStorageKey, ct),
            clip.CreatedAt, clip.UpdatedAt, clip.Subtitles, clip.Framing, candidate?.Reasoning, candidate?.Transcript, currentDto,
            renders.Select(r => r.ToDto()).ToList(),
            new ClipSourceDto(previewable, previewable ? await _urls.PreviewAsync(asset, ct) : null, asset?.DurationSeconds),
            clipJob?.ToSummary(project.Title));
    }

    public async Task<(Clip Clip, bool NeedsRender)> UpdateAsync(Guid userId, Guid clipId, UpdateClipRequest request, CancellationToken ct)
    {
        var clip = await GetOwnedAsync(userId, clipId, ct);
        var entitlement = await _subscriptions.GetEntitlementAsync(userId, ct);
        var needsRender = false;

        if (request.Title is not null && request.Title.Trim() != clip.Title)
        {
            clip.Title = request.Title.Trim();
        }

        var start = request.StartSeconds ?? clip.StartSeconds;
        var end = request.EndSeconds ?? clip.EndSeconds;
        if (end <= start) throw AppException.Validation("endSeconds deve ser maior que startSeconds.");
        if (clip.Project.MediaAsset?.DurationSeconds is { } duration && end > duration + 1)
        {
            throw AppException.Validation("O trecho ultrapassa a duração do vídeo.");
        }
        if (start != clip.StartSeconds || end != clip.EndSeconds)
        {
            clip.StartSeconds = start;
            clip.EndSeconds = end;
            needsRender = true;
        }

        if (request.AspectRatio is not null && request.AspectRatio != clip.AspectRatio)
        {
            clip.AspectRatio = request.AspectRatio;
            needsRender = true;
        }

        if (request.Subtitles is not null)
        {
            var capped = PlanLimitHelpers.CapSubtitles(request.Subtitles, entitlement.Limits);
            if (!capped.SameAs(clip.Subtitles))
            {
                clip.Subtitles = capped;
                needsRender = true;
            }
        }

        if (request.Framing is not null)
        {
            var capped = PlanLimitHelpers.CapFraming(request.Framing, entitlement.Limits);
            if (!capped.SameAs(clip.Framing))
            {
                clip.Framing = capped;
                needsRender = true;
            }
        }

        clip.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return (clip, needsRender);
    }

    public async Task<(Clip Clip, ProcessingJob Job, ClipRender Render)> RenderAsync(Guid userId, Guid clipId, CancellationToken ct)
    {
        var clip = await GetOwnedAsync(userId, clipId, ct);
        if (clip.Project.SourceKind == SourceKind.Live)
        {
            // A clip of a live is rendered from the session's segments that overlap it.
            if (!await _live.HasUsableSegmentsAsync(clip.ProjectId, ct))
            {
                throw AppException.InvalidMedia("Os segmentos desta live não estão mais disponíveis.");
            }
        }
        else if (clip.Project.MediaAsset is not { IsUsable: true })
        {
            throw AppException.InvalidMedia("O arquivo de origem não está mais disponível.");
        }
        // Render quota (renders/month + concurrent jobs) and the insert run under the per-user lock.
        return await _quotas.WithUserLockAsync(userId, async () =>
        {
            var (job, render) = await _renders.RequestRenderAsync(clip, ct);
            await _db.SaveChangesAsync(ct);
            return (clip, job, render);
        }, ct);
    }

    public async Task<DownloadResponse> GetDownloadAsync(Guid userId, Guid clipId, Guid? renderId, CancellationToken ct)
    {
        var clip = await GetOwnedAsync(userId, clipId, ct);
        var targetId = renderId ?? clip.CurrentRenderId;
        var render = targetId is { } id ? await _db.ClipRenders.FirstOrDefaultAsync(r => r.Id == id && r.ClipId == clip.Id, ct) : null;
        if (render is null) throw AppException.NotFound("Renderização não encontrada.");
        if (render.Status != RenderStatus.Ready || string.IsNullOrEmpty(render.StorageKey))
        {
            throw AppException.Conflict("A renderização ainda não está pronta.");
        }
        var fileName = MediaUrls.DownloadFileName(clip);
        var url = await _urls.DownloadAsync(render, fileName, ct) ?? throw AppException.NotFound("Renderização não encontrada.");
        return new DownloadResponse(url, _clock.UtcNow.Add(_urls.DownloadTtl), fileName);
    }

    public async Task DeleteAsync(Guid userId, Guid clipId, CancellationToken ct)
    {
        var clip = await GetOwnedAsync(userId, clipId, ct);
        var now = _clock.UtcNow;
        clip.DeletedAt = now;
        clip.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }
}
