using System.Text.Json;
using ClipForge.Api.Features.Internal;
using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Jobs;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Live;

/// <summary>
/// The live side of the job pipeline (docs/LIVE_CAPTURE.md §2–3): live_segment payloads (previous segment + context),
/// render_clip segments of live projects, segment status mirroring, the ending → ended transition and the status of a
/// session's project. Used by <see cref="InternalJobService"/>, <see cref="LiveService"/> and the project/clip services.
/// </summary>
public sealed class LiveJobSupport
{
    private readonly AppDbContext _db;
    private readonly MediaUrls _urls;
    private readonly IClock _clock;

    public LiveJobSupport(AppDbContext db, MediaUrls urls, IClock clock)
    {
        _db = db;
        _urls = urls;
        _clock = clock;
    }

    /// <summary>The segment (with its session and asset) of a live_segment job, tracked.</summary>
    public Task<LiveSegment?> SegmentOfJobAsync(Guid jobId, CancellationToken ct)
        => _db.LiveSegments.Include(s => s.Session).Include(s => s.MediaAsset).FirstOrDefaultAsync(s => s.JobId == jobId, ct);

    // ---------------------------------------------------------------- payloads

    public async Task<LiveSegmentJobPayload> BuildSegmentPayloadAsync(ProcessingJob job, UsageSnapshot usage, int leaseSeconds, CancellationToken ct)
    {
        var segment = await SegmentOfJobAsync(job.Id, ct) ?? throw AppException.Conflict("O segmento da live deste job não existe mais.");
        var session = segment.Session!;
        var asset = segment.MediaAsset;
        if (asset is not { IsUsable: true })
        {
            throw AppException.InvalidMedia("O arquivo do segmento da live não está disponível.");
        }
        var limits = usage.Entitlement.Limits;
        var settings = PlanLimitHelpers.CapSettings(JsonDefaults.Deserialize<AnalysisSettings>(job.Input) ?? session.Settings, limits);
        var source = new JobPayloadSource(asset.Id, await _urls.WorkerDownloadAsync(asset.StorageKey, ct), asset.OriginalFileName, asset.ContentType,
            asset.SizeBytes, asset.DurationSeconds ?? segment.DurationSeconds);

        LivePreviousInfo? previous = null;
        if (segment.Sequence > 0)
        {
            var before = await _db.LiveSegments.Include(s => s.MediaAsset).AsNoTracking()
                .FirstOrDefaultAsync(s => s.SessionId == segment.SessionId && s.Sequence == segment.Sequence - 1, ct);
            if (before is { Status: not LiveSegmentStatus.Failed, MediaAsset: { IsUsable: true } previousAsset })
            {
                previous = new LivePreviousInfo(before.Id, before.Sequence, await _urls.WorkerDownloadAsync(previousAsset.StorageKey, ct),
                    before.StartOffsetSeconds, before.DurationSeconds, previousAsset.OriginalFileName, previousAsset.ContentType, previousAsset.SizeBytes);
            }
        }

        // The liveContext of the closest earlier segment that reported one (normally sequence − 1): a failed segment does
        // not reset the session's score sample and language. The worker trusts the context's committedUntil only when its
        // sequence is this one − 1.
        var contextJson = await _db.LiveSegments.AsNoTracking()
            .Where(s => s.SessionId == segment.SessionId && s.Sequence < segment.Sequence && s.LiveContext != null)
            .OrderByDescending(s => s.Sequence)
            .Select(s => s.LiveContext)
            .FirstOrDefaultAsync(ct);

        return new LiveSegmentJobPayload(job.Id, job.Kind, job.Attempt, job.UserId, job.ProjectId, leaseSeconds, source, settings,
            new AnalyzePayloadLimits(limits.MaxRenderHeight, limits.MaxSourceDurationSeconds, usage.RemainingProcessingSeconds, limits.TranscriptionModel, limits.AllowWebcamLayout),
            new LivePayloadInfo(session.Id, segment.Id, segment.Sequence, segment.StartOffsetSeconds, segment.DurationSeconds, segment.IsFinal, session.SegmentSeconds),
            previous, JsonDefaults.ParseElement(contextJson));
    }

    /// <summary>Every stored segment of the project's session overlapping [start, end) (session times), in order.</summary>
    public async Task<IReadOnlyList<LiveSegmentSourceDto>> RenderSegmentsAsync(Guid projectId, decimal start, decimal end, CancellationToken ct)
    {
        var segments = await _db.LiveSegments.Include(s => s.MediaAsset).AsNoTracking()
            .Where(s => s.Session!.ProjectId == projectId && s.StartOffsetSeconds < end && s.StartOffsetSeconds + s.DurationSeconds > start)
            .OrderBy(s => s.Sequence)
            .ToListAsync(ct);
        var result = new List<LiveSegmentSourceDto>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment.MediaAsset is not { IsUsable: true } asset) continue;
            result.Add(new LiveSegmentSourceDto(await _urls.WorkerDownloadAsync(asset.StorageKey, ct), segment.StartOffsetSeconds, segment.DurationSeconds,
                asset.OriginalFileName, asset.ContentType, asset.SizeBytes));
        }
        if (result.Count == 0)
        {
            throw AppException.InvalidMedia("Os segmentos da live que cobrem este clipe não estão mais disponíveis.");
        }
        return result;
    }

    /// <summary>Whether a live project still has stored segments to cut clips from.</summary>
    public Task<bool> HasUsableSegmentsAsync(Guid projectId, CancellationToken ct)
        => _db.LiveSegments.AnyAsync(s => s.Session!.ProjectId == projectId && s.MediaAsset != null
            && (s.MediaAsset.Status == MediaAssetStatus.Uploaded || s.MediaAsset.Status == MediaAssetStatus.Verified) && s.MediaAsset.DeletedAt == null, ct);

    /// <summary>
    /// Transcript and webcam region for rendering a clip of a live project: those of the segment job that detected its
    /// moment, else of the analysed segment holding the clip's middle (live transcripts are in session times).
    /// </summary>
    public async Task<(string? TranscriptKey, WebcamRegion? WebcamRegion)> RenderAnalysisAsync(Clip clip, CancellationToken ct)
    {
        ProcessingJob? job = null;
        if (clip.CandidateId is { } candidateId)
        {
            job = await _db.ClipCandidates.AsNoTracking().Where(c => c.Id == candidateId).Select(c => c.Job).FirstOrDefaultAsync(ct);
        }
        if (job is not { Status: JobStatus.Completed })
        {
            var middle = (clip.StartSeconds + clip.EndSeconds) / 2;
            job = await (from s in _db.LiveSegments
                         join j in _db.ProcessingJobs on s.JobId equals j.Id
                         where s.Session!.ProjectId == clip.ProjectId && s.StartOffsetSeconds <= middle && s.StartOffsetSeconds + s.DurationSeconds > middle
                               && j.Status == JobStatus.Completed
                         orderby s.Sequence
                         select j).AsNoTracking().FirstOrDefaultAsync(ct);
        }
        return (job?.TranscriptStorageKey, job?.Result?.WebcamRegion);
    }

    // ---------------------------------------------------------------- job → segment / session

    /// <summary>Stores the <c>liveContext</c> a segment job reported (/analysis or /complete summary).</summary>
    public async Task StoreLiveContextAsync(ProcessingJob job, JsonElement? liveContext, CancellationToken ct)
    {
        if (job.Kind != JobKind.LiveSegment || liveContext is not { ValueKind: JsonValueKind.Object } context) return;
        var segment = await SegmentOfJobAsync(job.Id, ct);
        if (segment is null) return;
        segment.LiveContext = context.GetRawText();
        segment.UpdatedAt = _clock.UtcNow;
    }

    /// <summary>
    /// Mirrors a live_segment job's status on its segment (queued → analyzing → analyzed | failed). Once the job is
    /// terminal, an <c>ending</c> session whose segment jobs are all terminal becomes <c>ended</c>, and the project status
    /// follows. The caller saves.
    /// </summary>
    public async Task OnJobStatusChangedAsync(ProcessingJob job, CancellationToken ct)
    {
        if (job.Kind != JobKind.LiveSegment) return;
        var segment = await SegmentOfJobAsync(job.Id, ct);
        if (segment is null) return;
        var now = _clock.UtcNow;
        segment.Status = job.Status switch
        {
            JobStatus.Completed => LiveSegmentStatus.Analyzed,
            JobStatus.Failed or JobStatus.Cancelled => LiveSegmentStatus.Failed,
            JobStatus.Pending or JobStatus.Queued => LiveSegmentStatus.Queued,
            _ => LiveSegmentStatus.Analyzing,
        };
        segment.Error = segment.Status == LiveSegmentStatus.Failed ? Truncate(job.ErrorMessage ?? job.StatusMessage ?? "Falha no processamento.", 2000) : null;
        segment.UpdatedAt = now;
        if (JobStateMachine.IsTerminal(job.Status))
        {
            await RefreshSessionAsync(segment.Session!, job.Id, segment, ct);
        }
    }

    /// <summary>
    /// <c>ending</c> → <c>ended</c> when no segment job of the session is active any more, then the project status.
    /// <paramref name="terminalJobId"/> is a job whose terminal status is not saved yet (it no longer counts as active).
    /// </summary>
    public async Task RefreshSessionAsync(LiveSession session, Guid? terminalJobId, LiveSegment? changedSegment, CancellationToken ct)
    {
        var active = await _db.ProcessingJobs.AnyAsync(j => j.ProjectId == session.ProjectId && j.Kind == JobKind.LiveSegment
            && JobStateMachine.ActiveStatuses.Contains(j.Status) && (terminalJobId == null || j.Id != terminalJobId), ct);
        var now = _clock.UtcNow;
        if (!active && session.Status == LiveSessionStatus.Ending)
        {
            session.Status = LiveSessionStatus.Ended;
            session.EndedAt ??= now;
            session.UpdatedAt = now;
        }
        await UpdateProjectStatusAsync(session, active, changedSegment, ct);
    }

    /// <summary>A non-terminal session goes to <c>error</c> with a pt-BR reason (its pending segments are still processed).</summary>
    public async Task SetSessionErrorAsync(LiveSession session, string reason, CancellationToken ct)
    {
        if (session.IsTerminal) return;
        var now = _clock.UtcNow;
        session.Status = LiveSessionStatus.Error;
        session.EndReason = Truncate(reason, 500);
        session.EndedAt ??= now;
        session.UpdatedAt = now;
        await RefreshSessionAsync(session, null, null, ct);
    }

    /// <summary>
    /// A live project is <c>processing</c> while its session is open or segments are being analysed; afterwards
    /// <c>ready</c> when a segment was analysed (or an ended session had nothing to analyse), else <c>failed</c>.
    /// </summary>
    private async Task UpdateProjectStatusAsync(LiveSession session, bool hasActiveJobs, LiveSegment? changedSegment, CancellationToken ct)
    {
        var project = session.Project ?? await _db.Projects.FirstOrDefaultAsync(p => p.Id == session.ProjectId, ct);
        if (project is null || project.DeletedAt is not null) return;
        ProjectStatus status;
        if (!session.IsTerminal || hasActiveJobs)
        {
            status = ProjectStatus.Processing;
        }
        else
        {
            var analyzed = changedSegment?.Status == LiveSegmentStatus.Analyzed
                || await _db.LiveSegments.AnyAsync(s => s.SessionId == session.Id && s.Status == LiveSegmentStatus.Analyzed
                    && (changedSegment == null || s.Id != changedSegment.Id), ct);
            var anySegment = changedSegment is not null || await _db.LiveSegments.AnyAsync(s => s.SessionId == session.Id, ct);
            status = analyzed || (session.Status == LiveSessionStatus.Ended && !anySegment) ? ProjectStatus.Ready : ProjectStatus.Failed;
        }
        if (project.Status != status)
        {
            project.Status = status;
            project.UpdatedAt = _clock.UtcNow;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
