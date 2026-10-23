using System.Data;
using ClipForge.Api.Features.Projects;
using ClipForge.Api.Options;
using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Jobs;
using ClipForge.Domain.Media;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Persistence;
using ClipForge.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ClipForge.Api.Features.Internal;

/// <summary>Worker-facing job operations: claim, heartbeat, probe, reports, completion and failure handling.</summary>
public sealed class InternalJobService
{
    private readonly AppDbContext _db;
    private readonly QuotaService _quotas;
    private readonly SubscriptionService _subscriptions;
    private readonly ProjectService _projects;
    private readonly NotificationService _notifications;
    private readonly JobEvents _events;
    private readonly MediaUrls _urls;
    private readonly IObjectStorage _storage;
    private readonly IClock _clock;
    private readonly JobsOptions _jobs;
    private readonly ILogger<InternalJobService> _logger;

    public InternalJobService(
        AppDbContext db,
        QuotaService quotas,
        SubscriptionService subscriptions,
        ProjectService projects,
        NotificationService notifications,
        JobEvents events,
        MediaUrls urls,
        IObjectStorage storage,
        IClock clock,
        IOptions<JobsOptions> jobs,
        ILogger<InternalJobService> logger)
    {
        _db = db;
        _quotas = quotas;
        _subscriptions = subscriptions;
        _projects = projects;
        _notifications = notifications;
        _events = events;
        _urls = urls;
        _storage = storage;
        _clock = clock;
        _jobs = jobs.Value;
        _logger = logger;
    }

    // ---------------------------------------------------------------- claim

    /// <summary>
    /// Claims the queued job with the highest priority then the oldest queued_at among the requested kinds using a
    /// single UPDATE ... WHERE id = (SELECT ... FOR UPDATE SKIP LOCKED) RETURNING statement.
    /// </summary>
    public async Task<object?> ClaimAsync(string workerId, string[]? kinds, CancellationToken ct)
    {
        workerId = workerId.Trim();
        if (workerId.Length is 0 or > 120) throw AppException.Validation("workerId inválido.");

        var kindNames = (kinds is { Length: > 0 } ? kinds : ["analyze_source", "render_clip"])
            .Select(k => EnumNames.TryParse<JobKind>(k, out var parsed) ? EnumNames.ToSnake(parsed) : throw AppException.Validation($"Tipo de job inválido: {k}"))
            .Distinct()
            .ToArray();

        var now = _clock.UtcNow;
        var lease = now.AddSeconds(Math.Max(10, _jobs.LeaseSeconds));

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();

        Guid? jobId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            command.CommandText = """
                UPDATE processing_jobs
                SET status = 'preparing_media',
                    worker_id = @worker,
                    lease_expires_at = @lease,
                    started_at = @now,
                    updated_at = @now,
                    attempt = attempt + 1,
                    progress = 0,
                    status_message = 'Preparando mídia'
                WHERE id = (
                    SELECT id FROM processing_jobs
                    WHERE status = 'queued' AND kind = ANY(@kinds)
                    ORDER BY priority DESC, queued_at ASC NULLS LAST, created_at ASC
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id;
                """;
            command.Parameters.AddWithValue("worker", workerId);
            command.Parameters.AddWithValue("lease", lease);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("kinds", kindNames);
            var result = await command.ExecuteScalarAsync(ct);
            jobId = result is Guid id ? id : null;
        }

        if (jobId is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        var job = await _db.ProcessingJobs.Include(j => j.Project).ThenInclude(p => p.MediaAsset).FirstAsync(j => j.Id == jobId.Value, ct);
        _events.Add(job, $"Job reivindicado pelo worker {workerId} (tentativa {job.Attempt})", JobStatus.Queued, JobStatus.PreparingMedia, data: new { workerId });

        if (job.Kind == JobKind.AnalyzeSource && job.Project.Status is ProjectStatus.Created or ProjectStatus.Failed or ProjectStatus.Cancelled)
        {
            job.Project.Status = ProjectStatus.Processing;
            job.Project.UpdatedAt = now;
        }

        object payload;
        try
        {
            payload = await BuildPayloadAsync(job, ct);
        }
        catch (AppException ex)
        {
            // The job cannot run (e.g. missing source): fail it so the worker does not spin on it.
            _logger.LogWarning("Job {JobId} cannot be dispatched: {Message}", job.Id, ex.Message);
            await MarkFailedAsync(job, ex.Code, ex.Message, ct);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await _notifications.FlushAsync();
            return null;
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return payload;
    }

    private async Task<object> BuildPayloadAsync(ProcessingJob job, CancellationToken ct)
    {
        var project = job.Project;
        var asset = project.MediaAsset;
        if (asset is not { IsUsable: true })
        {
            throw AppException.InvalidMedia("O arquivo de origem do projeto não está disponível.");
        }

        var usage = await _quotas.GetUsageAsync(job.UserId, ct);
        var limits = usage.Entitlement.Limits;
        var source = new JobPayloadSource(asset.Id, await _urls.WorkerDownloadAsync(asset.StorageKey, ct), asset.OriginalFileName, asset.ContentType, asset.SizeBytes, asset.DurationSeconds);
        var leaseSeconds = Math.Max(10, _jobs.LeaseSeconds);

        if (job.Kind == JobKind.RenderClip)
        {
            var input = JsonDefaults.Deserialize<RenderJobInput>(job.Input) ?? throw AppException.Conflict("Job de renderização sem entrada válida.");
            var clip = await _db.Clips.FirstOrDefaultAsync(c => c.Id == input.ClipId && c.UserId == job.UserId, ct)
                ?? throw AppException.NotFound("Clipe do job não encontrado.");
            var render = await _db.ClipRenders.FirstOrDefaultAsync(r => r.Id == input.RenderId && r.ClipId == clip.Id, ct)
                ?? throw AppException.NotFound("Renderização do job não encontrada.");
            if (clip.DeletedAt is not null)
            {
                throw AppException.Conflict("O clipe foi excluído.");
            }

            render.Status = RenderStatus.Rendering;

            var analyzeJob = await _db.ProcessingJobs
                .Where(j => j.ProjectId == project.Id && j.Kind == JobKind.AnalyzeSource && j.Status == JobStatus.Completed)
                .OrderByDescending(j => j.FinishedAt).ThenByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);
            string? transcriptUrl = null;
            if (analyzeJob?.TranscriptStorageKey is { Length: > 0 } transcriptKey)
            {
                transcriptUrl = await _urls.WorkerDownloadAsync(transcriptKey, ct);
            }

            var framing = clip.Framing.Clone();
            framing.FocusX ??= 0.5;
            framing.FocusY ??= 0.5;

            return new RenderJobPayload(job.Id, job.Kind, job.Attempt, job.UserId, project.Id, leaseSeconds, source,
                new RenderClipInfo(clip.Id, render.Id, render.Version, clip.Title, clip.StartSeconds, clip.EndSeconds, clip.AspectRatio, clip.Subtitles, framing),
                new RenderAnalysisInfo(transcriptUrl, analyzeJob?.Result?.WebcamRegion),
                new RenderPayloadLimits(limits.MaxRenderHeight, limits.AllowWebcamLayout));
        }

        var settings = JsonDefaults.Deserialize<AnalysisSettings>(job.Input) ?? project.Settings;
        settings = PlanLimitHelpers.CapSettings(settings, limits);
        return new AnalyzeJobPayload(job.Id, job.Kind, job.Attempt, job.UserId, project.Id, leaseSeconds, source, settings,
            new AnalyzePayloadLimits(limits.MaxRenderHeight, limits.MaxSourceDurationSeconds, usage.RemainingProcessingSeconds, limits.TranscriptionModel, limits.AllowWebcamLayout));
    }

    // ---------------------------------------------------------------- helpers

    public async Task<ProcessingJob> GetJobAsync(Guid jobId, CancellationToken ct)
        => await _db.ProcessingJobs.Include(j => j.Project).FirstOrDefaultAsync(j => j.Id == jobId, ct) ?? throw AppException.NotFound("Job não encontrado.");

    /// <summary>
    /// Every report must come from the worker holding the lease: the worker id is mandatory (header X-Worker-Id or
    /// body workerId, 400 when absent) and must equal the job's worker_id (409 otherwise, including jobs that are not
    /// leased at all, e.g. requeued by the reaper and not yet re-claimed). A stale worker whose lease expired and was
    /// re-claimed elsewhere therefore cannot interleave with, fail or finish the new run.
    /// </summary>
    private static void EnsureWorker(ProcessingJob job, string? workerId)
    {
        workerId = workerId?.Trim();
        if (string.IsNullOrEmpty(workerId))
        {
            throw AppException.Validation("Informe o worker responsável pelo job (header X-Worker-Id ou campo workerId).");
        }
        if (string.IsNullOrEmpty(job.WorkerId))
        {
            throw AppException.Conflict($"O job não está reservado para nenhum worker (status atual: {EnumNames.ToSnake(job.Status)}).");
        }
        if (!string.Equals(job.WorkerId, workerId, StringComparison.Ordinal))
        {
            throw AppException.Conflict($"O job está reservado para outro worker ({job.WorkerId}).");
        }
    }

    private static void EnsureRunning(ProcessingJob job)
    {
        if (!JobStateMachine.IsRunning(job.Status))
        {
            throw AppException.Conflict($"O job não está em execução (status atual: {EnumNames.ToSnake(job.Status)}).");
        }
    }

    private DateTimeOffset ExtendLease(ProcessingJob job, DateTimeOffset now)
    {
        job.LeaseExpiresAt = now.AddSeconds(Math.Max(10, _jobs.LeaseSeconds));
        job.UpdatedAt = now;
        return job.LeaseExpiresAt.Value;
    }

    // ---------------------------------------------------------------- heartbeat / status / events

    public async Task<HeartbeatResponse> HeartbeatAsync(Guid jobId, HeartbeatRequest request, string? workerId, CancellationToken ct)
    {
        var job = await GetJobAsync(jobId, ct);
        EnsureWorker(job, workerId ?? request.WorkerId);
        EnsureRunning(job);
        var now = _clock.UtcNow;
        ApplyProgress(job, request.Progress, request.Message);
        var lease = ExtendLease(job, now);
        await _db.SaveChangesAsync(ct);
        return new HeartbeatResponse(job.CancelRequested, lease);
    }

    public async Task<ProcessingJob> SetStatusAsync(Guid jobId, StatusRequest request, string? workerId, CancellationToken ct)
    {
        var job = await GetJobAsync(jobId, ct);
        EnsureWorker(job, workerId ?? request.WorkerId);
        var target = EnumNames.Parse<JobStatus>(request.Status);
        var now = _clock.UtcNow;

        if (JobStateMachine.IsProgressUpdate(job.Status, target))
        {
            // The status the job already has (e.g. preparing_media right after the claim, or a throttled progress
            // post): an idempotent progress/message update that extends the lease, never a conflict.
            ApplyProgress(job, request.Progress, request.Message);
            ExtendLease(job, now);
            await _db.SaveChangesAsync(ct);
            return job;
        }

        JobStateMachine.EnsureTransition(job.Kind, job.Status, target);

        switch (target)
        {
            case JobStatus.Completed:
                await CompleteAsync(job, null, null, ct);
                break;
            case JobStatus.Failed:
                await MarkFailedAsync(job, "failed", request.Message ?? "Falha reportada pelo worker.", null, ct);
                break;
            case JobStatus.Cancelled:
                await CancelAsync(job, request.Message ?? "Cancelado pelo worker", ct);
                break;
            default:
                _events.Add(job, request.Message ?? $"Status: {EnumNames.ToSnake(target)}", job.Status, target);
                job.Status = target;
                ApplyProgress(job, request.Progress, request.Message);
                ExtendLease(job, now);
                if (job.Kind == JobKind.AnalyzeSource && job.Project.Status != ProjectStatus.Processing)
                {
                    job.Project.Status = ProjectStatus.Processing;
                    job.Project.UpdatedAt = now;
                }
                break;
        }

        await _db.SaveChangesAsync(ct);
        await _notifications.FlushAsync();
        return job;
    }

    private static void ApplyProgress(ProcessingJob job, int? progress, string? message)
    {
        if (progress is { } value) job.Progress = (short)Math.Clamp(value, 0, 100);
        if (!string.IsNullOrWhiteSpace(message)) job.StatusMessage = message.Trim();
    }

    /// <summary>
    /// Worker-reported cancellation. Once the analysis started (status past preparing_media) the reserved minutes are
    /// consumed: the expensive work already ran and its candidates stay usable, so a cancel is never a free analysis.
    /// A cancel while still preparing the media releases the reservation.
    /// </summary>
    private async Task CancelAsync(ProcessingJob job, string message, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var charged = job.Kind == JobKind.AnalyzeSource && JobStateMachine.HasStartedProcessing(job.Status) ? job.ReservedSeconds : 0;
        _events.Add(job, message, job.Status, JobStatus.Cancelled, data: new { chargedSeconds = charged });
        await ChargeProcessingAsync(job, charged, "cancelled", null, ct);
        job.Status = JobStatus.Cancelled;
        job.StatusMessage = "Cancelado";
        job.FinishedAt = now;
        job.ReservedSeconds = 0;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = now;
        await _projects.UpdateProjectStatusAfterJobAsync(job, ct);
        await FailPendingRenderAsync(job, "Renderização cancelada.", ct);
    }

    /// <summary>Appends the processing_seconds usage of an analyze_source job (append-only, audited with the reason).</summary>
    private async Task ChargeProcessingAsync(ProcessingJob job, decimal seconds, string reason, decimal? processedSeconds, CancellationToken ct)
    {
        if (job.Kind != JobKind.AnalyzeSource || seconds <= 0) return;
        var entitlement = await _subscriptions.GetEntitlementAsync(job.UserId, ct);
        _quotas.RecordUsage(entitlement, job.UserId, UsageKind.ProcessingSeconds, seconds, job.Id, job.ProjectId, null,
            JsonDefaults.Serialize(new { reason, reservedSeconds = job.ReservedSeconds, processedSeconds }));
        // 80 % / 100 % of the monthly minutes: checked once the charge is committed.
        _notifications.RequestQuotaCheck(job.UserId);
    }

    public async Task AddEventAsync(Guid jobId, EventRequest request, string? workerId, CancellationToken ct)
    {
        var job = await GetJobAsync(jobId, ct);
        EnsureWorker(job, workerId);
        EnsureRunning(job);
        var level = request.Level is not null && EnumNames.TryParse<JobEventLevel>(request.Level, out var parsed) ? parsed : JobEventLevel.Info;
        _events.Add(job, request.Message, level: level, data: request.Data);
        await _db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- probe

    public async Task<ProbeResponse> ProbeAsync(Guid jobId, ProbeRequest request, string? workerId, CancellationToken ct)
    {
        var job = await _db.ProcessingJobs.Include(j => j.Project).ThenInclude(p => p.MediaAsset).FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw AppException.NotFound("Job não encontrado.");
        EnsureWorker(job, workerId ?? request.WorkerId);
        EnsureRunning(job);

        if (!request.HasVideo)
        {
            await RejectProbeAsync(job, ErrorCodes.InvalidMedia, "O arquivo não contém vídeo (arquivo sem vídeo).", ct);
        }
        if (request.DurationSeconds <= 0)
        {
            await RejectProbeAsync(job, ErrorCodes.InvalidMedia, "Não foi possível determinar a duração do arquivo (duração inválida).", ct);
        }

        var asset = job.Project.MediaAsset;
        if (asset is not null)
        {
            asset.DurationSeconds = request.DurationSeconds;
            asset.Width = request.Width;
            asset.Height = request.Height;
            asset.Fps = request.Fps;
            asset.HasAudio = request.HasAudio;
            if (!string.IsNullOrWhiteSpace(request.Container)) asset.Container = request.Container.Trim().ToLowerInvariant();
            asset.VideoCodec = request.VideoCodec;
            asset.AudioCodec = request.AudioCodec;
            if (asset.Status == MediaAssetStatus.Uploaded) asset.Status = MediaAssetStatus.Verified;
        }

        if (job.Kind != JobKind.AnalyzeSource)
        {
            ExtendLease(job, _clock.UtcNow);
            await _db.SaveChangesAsync(ct);
            var current = await _quotas.GetUsageAsync(job.UserId, ct);
            return new ProbeResponse(true, current.RemainingProcessingSeconds);
        }

        var (decision, usage) = await _quotas.CheckProbeAsync(job, request.DurationSeconds, ct);
        if (!decision.Allowed)
        {
            await RejectProbeAsync(job, decision.Code!, decision.Message!, ct);
        }

        // usage.ProcessingSecondsReserved already holds this job's previous reservation when the probe is re-sent or
        // the job was requeued after an earlier probe (Requeue keeps it): it is replaced, never counted twice.
        var previousReservation = job.ReservedSeconds;
        job.ReservedSeconds = (int)Math.Ceiling(request.DurationSeconds);
        _events.Add(job, $"Mídia verificada: {Math.Round(request.DurationSeconds / 60m, 1)} min, {request.Width}x{request.Height}", data: new
        {
            request.DurationSeconds, request.Width, request.Height, request.Fps, request.Container, request.VideoCodec, request.AudioCodec, request.HasAudio,
        });
        ExtendLease(job, _clock.UtcNow);
        await _db.SaveChangesAsync(ct);

        var remaining = QuotaCalculator.RemainingProcessingSeconds(usage.ProcessingSecondsUsed,
            usage.ProcessingSecondsReserved - previousReservation + job.ReservedSeconds, usage.Entitlement.Limits);
        return new ProbeResponse(true, remaining);
    }

    /// <summary>A probe the API refuses (no video, unknown duration, plan/quota): the job fails server-side and the worker gets 409 with the code.</summary>
    private async Task RejectProbeAsync(ProcessingJob job, string code, string message, CancellationToken ct)
    {
        await MarkFailedAsync(job, code, message, ct);
        await _db.SaveChangesAsync(ct);
        await _notifications.FlushAsync();
        throw new AppException(code, 409, message);
    }

    // ---------------------------------------------------------------- uploads / analysis / candidates / clips

    public async Task<UploadUrlsResponse> UploadUrlsAsync(Guid jobId, UploadUrlsRequest request, string? workerId, CancellationToken ct)
    {
        var job = await GetJobAsync(jobId, ct);
        EnsureWorker(job, workerId);
        EnsureRunning(job);
        var files = new List<UploadUrlDto>(request.Files.Count);
        foreach (var file in request.Files)
        {
            var key = StorageKeys.JobArtifactKey(job.UserId, job.Id, file.Kind, file.FileName);
            var max = file.SizeBytes is > 0 ? file.SizeBytes.Value : 20L * 1024 * 1024 * 1024;
            var target = await _storage.GetSignedUploadUrlAsync(key, _urls.WorkerTtl, max, file.ContentType, UrlAudience.Worker, ct);
            files.Add(new UploadUrlDto(file.FileName, key, target.Url, target.Method, target.Headers));
        }
        return new UploadUrlsResponse(files);
    }

    public async Task StoreAnalysisAsync(Guid jobId, AnalysisRequest request, string? workerId, CancellationToken ct)
    {
        var job = await GetJobAsync(jobId, ct);
        EnsureWorker(job, workerId);
        EnsureRunning(job);
        if (request.TranscriptKey is not null)
        {
            StorageKeys.EnsureOwned(request.TranscriptKey, job.UserId);
            job.TranscriptStorageKey = request.TranscriptKey;
        }
        if (request.SignalsKey is not null)
        {
            StorageKeys.EnsureOwned(request.SignalsKey, job.UserId);
            job.SignalsStorageKey = request.SignalsKey;
        }
        MergeSummary(job, request.Summary);
        job.UpdatedAt = _clock.UtcNow;
        _events.Add(job, "Artefatos de análise registrados", data: new { request.TranscriptKey, request.SignalsKey });
        await _db.SaveChangesAsync(ct);
    }

    private static void MergeSummary(ProcessingJob job, AnalysisSummaryInput? summary)
    {
        if (summary is null) return;
        var result = job.Result ?? new JobResult();
        if (summary.Signals is { ValueKind: not System.Text.Json.JsonValueKind.Undefined } signals) result.Signals = signals;
        if (summary.WebcamRegion is not null) result.WebcamRegion = summary.WebcamRegion.IsValid() ? summary.WebcamRegion : null;
        if (summary.Language is not null) result.Language = summary.Language;
        if (summary.ProcessedSeconds is not null) result.ProcessedSeconds = summary.ProcessedSeconds;
        if (summary.CandidatesFound is not null) result.CandidatesFound = summary.CandidatesFound;
        if (summary.ClipsRendered is not null) result.ClipsRendered = summary.ClipsRendered;
        job.Result = result;
    }

    public async Task<CandidatesResponse> ReplaceCandidatesAsync(Guid jobId, CandidatesRequest request, string? workerId, CancellationToken ct)
    {
        var job = await GetJobAsync(jobId, ct);
        EnsureWorker(job, workerId);
        EnsureRunning(job);
        var now = _clock.UtcNow;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var staleObjects = new List<string>();
        if (job.Attempt > 1 && job.StartedAt is { } attemptStart)
        {
            staleObjects = await RemoveClipsOfPreviousAttemptsAsync(job, attemptStart, ct);
        }

        // Replaces every candidate of the job: those of a re-sent request and those a previous attempt detected.
        await _db.ClipCandidates.Where(c => c.JobId == job.Id).ExecuteDeleteAsync(ct);

        var ids = new List<Guid>(request.Candidates.Count);
        var rank = 0;
        foreach (var input in request.Candidates)
        {
            rank++;
            var candidate = new ClipCandidate
            {
                JobId = job.Id,
                ProjectId = job.ProjectId,
                UserId = job.UserId,
                Rank = input.Rank > 0 ? input.Rank : rank,
                StartSeconds = input.StartSeconds,
                EndSeconds = input.EndSeconds,
                Category = EnumNames.Parse<ClipCategory>(input.Category),
                Score = (short)Math.Clamp(input.Score, 0, 100),
                Reasoning = input.Reasoning,
                Transcript = input.Transcript,
                Signals = input.Signals is { ValueKind: not System.Text.Json.JsonValueKind.Undefined } s ? s.GetRawText() : null,
                Status = CandidateStatus.Detected,
                CreatedAt = now,
            };
            _db.ClipCandidates.Add(candidate);
            ids.Add(candidate.Id);
        }

        var result = job.Result ?? new JobResult();
        result.CandidatesFound = ids.Count;
        job.Result = result;
        job.UpdatedAt = now;
        if (job.Kind == JobKind.AnalyzeSource)
        {
            job.Project.Status = ProjectStatus.Processing;
            job.Project.UpdatedAt = now;
        }
        _events.Add(job, $"{ids.Count} momentos detectados");
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Storage objects of the removed clips go once their rows are gone (best effort).
        foreach (var key in staleObjects)
        {
            await SafeDeleteObjectAsync(key);
        }
        return new CandidatesResponse(ids);
    }

    /// <summary>
    /// A retried analysis (attempt &gt; 1) starts its output over: the clips the previous attempts auto-rendered for
    /// this job (render rows of this job created before the current claim) are removed with their renders, so the new
    /// attempt's /clips does not duplicate the same moments. Clips the user re-rendered through a render job are kept
    /// (user work; they only lose the candidate link). Returns the storage keys to delete after the commit.
    /// </summary>
    private async Task<List<string>> RemoveClipsOfPreviousAttemptsAsync(ProcessingJob job, DateTimeOffset attemptStart, CancellationToken ct)
    {
        var result = job.Result ?? new JobResult();
        result.ClipsRendered = null; // the summary describes the attempt that completes
        job.Result = result;

        var clipIds = await _db.ClipRenders
            .Where(r => r.JobId == job.Id && r.CreatedAt < attemptStart)
            .Select(r => r.ClipId)
            .Distinct()
            .Where(clipId => !_db.ClipRenders.Any(other => other.ClipId == clipId && other.JobId != job.Id))
            .ToListAsync(ct);
        if (clipIds.Count == 0) return [];

        var renderKeys = await _db.ClipRenders.Where(r => clipIds.Contains(r.ClipId) && r.StorageKey != null).Select(r => r.StorageKey!).ToListAsync(ct);
        var thumbnailKeys = await _db.Clips.Where(c => clipIds.Contains(c.Id) && c.ThumbnailStorageKey != null).Select(c => c.ThumbnailStorageKey!).ToListAsync(ct);
        await _db.Clips.Where(c => clipIds.Contains(c.Id)).ExecuteUpdateAsync(s => s.SetProperty(c => c.CurrentRenderId, (Guid?)null), ct);
        await _db.ClipRenders.Where(r => clipIds.Contains(r.ClipId)).ExecuteDeleteAsync(ct);
        await _db.Clips.Where(c => clipIds.Contains(c.Id)).ExecuteDeleteAsync(ct);
        _events.Add(job, $"{clipIds.Count} clipe(s) gerado(s) pela tentativa anterior removido(s); esta tentativa gera os clipes de novo.",
            level: JobEventLevel.Warning, data: new { clipIds });
        _logger.LogInformation("Job {JobId} attempt {Attempt}: removed {Count} clips auto-rendered by a previous attempt", job.Id, job.Attempt, clipIds.Count);
        return renderKeys.Concat(thumbnailKeys).Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task SafeDeleteObjectAsync(string key)
    {
        try
        {
            await _storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete storage object {Key}", key);
        }
    }

    public async Task<ClipsResponse> CreateClipsAsync(Guid jobId, ClipsRequest request, string? workerId, CancellationToken ct)
    {
        var job = await GetJobAsync(jobId, ct);
        EnsureWorker(job, workerId);
        EnsureRunning(job);
        var project = job.Project;
        var entitlement = await _subscriptions.GetEntitlementAsync(job.UserId, ct);
        var now = _clock.UtcNow;
        var created = new List<ClipCreatedDto>(request.Clips.Count);

        foreach (var input in request.Clips)
        {
            var candidate = await _db.ClipCandidates.FirstOrDefaultAsync(c => c.Id == input.CandidateId && c.ProjectId == project.Id && c.UserId == job.UserId, ct)
                ?? throw AppException.NotFound($"Momento {input.CandidateId} não encontrado.");
            StorageKeys.EnsureOwned(input.Render.StorageKey, job.UserId);
            if (input.ThumbnailKey is not null) StorageKeys.EnsureOwned(input.ThumbnailKey, job.UserId);

            var start = input.StartSeconds ?? candidate.StartSeconds;
            var end = input.EndSeconds ?? candidate.EndSeconds;
            if (end <= start) throw AppException.Validation("endSeconds deve ser maior que startSeconds.");

            var clip = new Clip
            {
                UserId = job.UserId,
                ProjectId = project.Id,
                CandidateId = candidate.Id,
                Title = string.IsNullOrWhiteSpace(input.Title) ? ProjectService.DefaultClipTitle(project, candidate) : input.Title.Trim(),
                StartSeconds = start,
                EndSeconds = end,
                AspectRatio = input.AspectRatio ?? project.Settings.AspectRatio,
                Subtitles = PlanLimitHelpers.CapSubtitles(input.Subtitles ?? project.Settings.Subtitles, entitlement.Limits),
                Framing = PlanLimitHelpers.CapFraming(input.Framing ?? project.Settings.Framing, entitlement.Limits),
                Category = candidate.Category,
                Score = candidate.Score,
                Status = ClipStatus.Ready,
                ThumbnailStorageKey = input.ThumbnailKey,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var render = new ClipRender
            {
                ClipId = clip.Id,
                UserId = job.UserId,
                JobId = job.Id,
                Version = 1,
                AspectRatio = clip.AspectRatio,
                Width = input.Render.Width,
                Height = input.Render.Height,
                StorageKey = input.Render.StorageKey,
                SizeBytes = input.Render.SizeBytes,
                DurationSeconds = input.Render.DurationSeconds ?? end - start,
                HasSubtitles = input.Render.HasSubtitles,
                Status = RenderStatus.Ready,
                CreatedAt = now,
                CompletedAt = now,
            };
            _db.Clips.Add(clip);
            _db.ClipRenders.Add(render);
            candidate.Status = CandidateStatus.Rendered;
            created.Add(new ClipCreatedDto(candidate.Id, clip.Id, render.Id));
        }

        await _db.SaveChangesAsync(ct);

        // Second step: point each clip to its render (avoids the circular insert).
        foreach (var item in created)
        {
            var clip = await _db.Clips.FirstAsync(c => c.Id == item.ClipId, ct);
            clip.CurrentRenderId = item.RenderId;
        }
        var result = job.Result ?? new JobResult();
        result.ClipsRendered = (result.ClipsRendered ?? 0) + created.Count;
        job.Result = result;
        job.UpdatedAt = now;
        _events.Add(job, $"{created.Count} clipes renderizados");
        await _db.SaveChangesAsync(ct);
        return new ClipsResponse(created);
    }

    public async Task RenderResultAsync(Guid jobId, RenderResultRequest request, string? workerId, CancellationToken ct)
    {
        var job = await GetJobAsync(jobId, ct);
        EnsureWorker(job, workerId ?? request.WorkerId);
        EnsureRunning(job);
        if (job.Kind != JobKind.RenderClip) throw AppException.Conflict("Este job não é uma renderização.");
        var input = JsonDefaults.Deserialize<RenderJobInput>(job.Input) ?? throw AppException.Conflict("Job sem entrada válida.");
        if (input.RenderId != request.RenderId) throw AppException.Conflict("renderId não corresponde ao job.");

        var render = await _db.ClipRenders.Include(r => r.Clip).FirstOrDefaultAsync(r => r.Id == request.RenderId && r.UserId == job.UserId, ct)
            ?? throw AppException.NotFound("Renderização não encontrada.");
        var clip = render.Clip;
        var now = _clock.UtcNow;

        if (request.Status == "ready")
        {
            StorageKeys.EnsureOwned(request.StorageKey, job.UserId);
            if (request.ThumbnailKey is not null) StorageKeys.EnsureOwned(request.ThumbnailKey, job.UserId);
            render.Status = RenderStatus.Ready;
            render.StorageKey = request.StorageKey;
            render.Width = request.Width;
            render.Height = request.Height;
            render.DurationSeconds = request.DurationSeconds ?? clip.EndSeconds - clip.StartSeconds;
            render.SizeBytes = request.SizeBytes;
            render.HasSubtitles = request.HasSubtitles ?? render.HasSubtitles;
            render.Error = null;
            render.CompletedAt = now;
            clip.Status = ClipStatus.Ready;
            clip.CurrentRenderId = render.Id;
            if (request.ThumbnailKey is not null) clip.ThumbnailStorageKey = request.ThumbnailKey;
            _events.Add(job, $"Renderização v{render.Version} concluída", data: new { render.Id, render.StorageKey, render.Width, render.Height });
        }
        else
        {
            render.Status = RenderStatus.Failed;
            render.Error = request.Error ?? "Falha na renderização.";
            render.CompletedAt = now;
            var hasReady = await _db.ClipRenders.AnyAsync(r => r.ClipId == clip.Id && r.Status == RenderStatus.Ready, ct);
            clip.Status = hasReady ? ClipStatus.Ready : ClipStatus.Failed;
            _events.Add(job, $"Renderização v{render.Version} falhou: {render.Error}", level: JobEventLevel.Error);
        }
        clip.UpdatedAt = now;
        ExtendLease(job, now);
        await _db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- complete / fail

    public async Task<ProcessingJob> CompleteFromWorkerAsync(Guid jobId, CompleteRequest request, string? workerId, CancellationToken ct)
    {
        var job = await GetJobAsync(jobId, ct);
        EnsureWorker(job, workerId ?? request.WorkerId);
        EnsureRunning(job);
        await CompleteAsync(job, request.ProcessedSeconds, request.Summary, ct);
        await _db.SaveChangesAsync(ct);
        await _notifications.FlushAsync();
        return job;
    }

    private async Task CompleteAsync(ProcessingJob job, decimal? processedSeconds, AnalysisSummaryInput? summary, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        MergeSummary(job, summary);
        var result = job.Result ?? new JobResult();
        if (processedSeconds is not null) result.ProcessedSeconds = processedSeconds;

        if (job.Kind == JobKind.AnalyzeSource)
        {
            var seconds = job.ReservedSeconds > 0 ? job.ReservedSeconds : (processedSeconds ?? 0m);
            await ChargeProcessingAsync(job, seconds, "completed", processedSeconds, ct);
            result.ProcessedSeconds ??= seconds;
            result.CandidatesFound ??= await _db.ClipCandidates.CountAsync(c => c.JobId == job.Id, ct);
            result.ClipsRendered ??= await _db.ClipRenders.CountAsync(r => r.JobId == job.Id && r.Status == RenderStatus.Ready, ct);
        }
        else if (job.Kind == JobKind.RenderClip)
        {
            await FailPendingRenderAsync(job, "O worker concluiu o job sem reportar o resultado da renderização.", ct);
        }

        _events.Add(job, "Processamento concluído", job.Status, JobStatus.Completed);
        job.Result = result;
        job.Status = JobStatus.Completed;
        job.Progress = 100;
        job.StatusMessage = "Concluído";
        job.FinishedAt = now;
        job.ReservedSeconds = 0;
        job.LeaseExpiresAt = null;
        job.CancelRequested = false;
        job.UpdatedAt = now;

        await _projects.UpdateProjectStatusAfterJobAsync(job, ct);

        if (job.Kind == JobKind.AnalyzeSource)
        {
            var clips = result.ClipsRendered ?? 0;
            var outcome = clips > 0 ? $"{result.CandidatesFound ?? 0} momentos encontrados e {clips} clipes gerados." : $"{result.CandidatesFound ?? 0} momentos encontrados.";
            _notifications.Add(job.UserId, NotificationType.JobCompleted,
                $"Análise concluída: {job.Project.Title}",
                outcome,
                new { jobId = job.Id, projectId = job.ProjectId, candidatesFound = result.CandidatesFound, clipsRendered = clips });
            if (job.Project.DeletedAt is null)
            {
                var title = job.Project.Title;
                _notifications.QueueEmail(job.UserId, EmailTopic.JobCompleted,
                    $"{_notifications.BrandName}: análise concluída – {Shorten(title, 80)}",
                    $"A análise de \"{title}\" foi concluída: {outcome}\n\nVeja os momentos e os clipes: {_notifications.ProjectUrl(job.ProjectId)}");
            }
        }
        else if (job.Kind == JobKind.RenderClip)
        {
            var input = JsonDefaults.Deserialize<RenderJobInput>(job.Input);
            _notifications.Add(job.UserId, NotificationType.ClipReady, "Clipe pronto", $"A nova versão do clipe em \"{job.Project.Title}\" está disponível.",
                new { jobId = job.Id, projectId = job.ProjectId, clipId = input?.ClipId, renderId = input?.RenderId });
        }
    }

    public async Task<ProcessingJob> FailFromWorkerAsync(Guid jobId, FailRequest request, string? workerId, CancellationToken ct)
    {
        var job = await GetJobAsync(jobId, ct);
        EnsureWorker(job, workerId ?? request.WorkerId);
        EnsureRunning(job);
        var code = string.IsNullOrWhiteSpace(request.Code) ? "worker_error" : request.Code.Trim();
        var message = string.IsNullOrWhiteSpace(request.Message) ? "Falha no processamento." : request.Message.Trim();

        if (request.Retryable && job.Attempt < job.MaxAttempts)
        {
            Requeue(job, code, message);
        }
        else
        {
            await MarkFailedAsync(job, code, message, request.ProcessedSeconds, ct);
        }
        await _db.SaveChangesAsync(ct);
        await _notifications.FlushAsync();
        return job;
    }

    /// <summary>Puts a job back in the queue (retry). Attempt is incremented again at claim time.</summary>
    public void Requeue(ProcessingJob job, string code, string message)
    {
        var now = _clock.UtcNow;
        _events.Add(job, $"Tentativa {job.Attempt} falhou ({code}): {message}. Job devolvido à fila.", job.Status, JobStatus.Queued, JobEventLevel.Warning, new { code, message });
        job.Status = JobStatus.Queued;
        job.WorkerId = null;
        job.LeaseExpiresAt = null;
        job.StartedAt = null;
        job.Progress = 0;
        job.StatusMessage = "Na fila (nova tentativa)";
        job.ErrorCode = code;
        job.ErrorMessage = message;
        job.QueuedAt = now;
        job.UpdatedAt = now;
    }

    /// <summary>Terminal failure without worker-reported processed seconds (probe rejections, dispatch errors, reaper).</summary>
    public Task MarkFailedAsync(ProcessingJob job, string code, string message, CancellationToken ct)
        => MarkFailedAsync(job, code, message, null, ct);

    /// <summary>
    /// Terminal failure: charges the seconds the worker reports as processed (capped by the reservation, e.g. a render
    /// failure after a full analysis), releases the reservation, updates the project and notifies the user.
    /// </summary>
    public async Task MarkFailedAsync(ProcessingJob job, string code, string message, decimal? processedSeconds, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var charged = job.Kind == JobKind.AnalyzeSource && job.ReservedSeconds > 0 && processedSeconds is > 0
            ? Math.Min(processedSeconds.Value, job.ReservedSeconds)
            : 0m;
        _events.Add(job, $"Falha ({code}): {message}", job.Status, JobStatus.Failed, JobEventLevel.Error, new { code, message, chargedSeconds = charged });
        await ChargeProcessingAsync(job, charged, "failed", processedSeconds, ct);
        job.Status = JobStatus.Failed;
        job.ErrorCode = code;
        job.ErrorMessage = message.Length > 2000 ? message[..2000] : message;
        job.StatusMessage = "Falhou";
        job.FinishedAt = now;
        job.ReservedSeconds = 0;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = now;

        if (job.Project is null)
        {
            await _db.Entry(job).Reference(j => j.Project).LoadAsync(ct);
        }
        await _projects.UpdateProjectStatusAfterJobAsync(job, ct);
        await FailPendingRenderAsync(job, message, ct);

        _notifications.Add(job.UserId, NotificationType.JobFailed,
            job.Kind == JobKind.RenderClip ? "Falha na renderização" : $"Falha na análise: {job.Project?.Title}",
            message,
            new { jobId = job.Id, projectId = job.ProjectId, code });
        await QueueFailureEmailAsync(job, message, ct);
    }

    /// <summary>E-mail copy of a terminal failure (when the user keeps emailOnJobFailed on); nothing for deleted projects or clips.</summary>
    private async Task QueueFailureEmailAsync(ProcessingJob job, string message, CancellationToken ct)
    {
        if (job.Project is not { DeletedAt: null } project) return;
        var reason = Shorten(message, 500);
        if (job.Kind == JobKind.AnalyzeSource)
        {
            _notifications.QueueEmail(job.UserId, EmailTopic.JobFailed,
                $"{_notifications.BrandName}: falha na análise – {Shorten(project.Title, 80)}",
                $"A análise de \"{project.Title}\" não pôde ser concluída: {reason}\n\nAbra o projeto para tentar novamente: {_notifications.ProjectUrl(project.Id)}");
        }
        else if (job.Kind == JobKind.RenderClip && JsonDefaults.Deserialize<RenderJobInput>(job.Input) is { } input)
        {
            var clip = await _db.Clips.AsNoTracking().Where(c => c.Id == input.ClipId && c.DeletedAt == null).Select(c => new { c.Id, c.Title }).FirstOrDefaultAsync(ct);
            if (clip is null) return;
            _notifications.QueueEmail(job.UserId, EmailTopic.JobFailed,
                $"{_notifications.BrandName}: falha na renderização – {Shorten(clip.Title, 80)}",
                $"A renderização do clipe \"{clip.Title}\" (projeto \"{project.Title}\") falhou: {reason}\n\nAbra o clipe para tentar de novo: {_notifications.AppUrl($"/clips/{clip.Id:D}")}\nProjeto: {_notifications.ProjectUrl(project.Id)}");
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private async Task FailPendingRenderAsync(ProcessingJob job, string error, CancellationToken ct)
    {
        if (job.Kind != JobKind.RenderClip) return;
        var input = JsonDefaults.Deserialize<RenderJobInput>(job.Input);
        if (input is null) return;
        var render = await _db.ClipRenders.Include(r => r.Clip).FirstOrDefaultAsync(r => r.Id == input.RenderId, ct);
        if (render is null || render.Status is RenderStatus.Ready or RenderStatus.Failed) return;
        var now = _clock.UtcNow;
        render.Status = RenderStatus.Failed;
        render.Error = error.Length > 2000 ? error[..2000] : error;
        render.CompletedAt = now;
        var hasReady = await _db.ClipRenders.AnyAsync(r => r.ClipId == render.ClipId && r.Status == RenderStatus.Ready, ct);
        render.Clip.Status = hasReady ? ClipStatus.Ready : ClipStatus.Failed;
        render.Clip.UpdatedAt = now;
    }

    // ---------------------------------------------------------------- reaper

    /// <summary>Requeues (or fails) running jobs whose lease expired. Returns the number of jobs touched.</summary>
    public async Task<int> ReapExpiredLeasesAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var expired = await _db.ProcessingJobs.Include(j => j.Project)
            .Where(j => JobStateMachine.RunningStatuses.Contains(j.Status) && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now)
            .ToListAsync(ct);
        foreach (var job in expired)
        {
            if (job.Attempt < job.MaxAttempts)
            {
                Requeue(job, "lease_expired", $"O worker {job.WorkerId} parou de responder");
                _logger.LogWarning("Job {JobId} lease expired; requeued (attempt {Attempt}/{Max})", job.Id, job.Attempt, job.MaxAttempts);
            }
            else
            {
                await MarkFailedAsync(job, "lease_expired", "O processamento foi interrompido repetidamente (worker sem resposta). Tente novamente mais tarde.", ct);
                _logger.LogWarning("Job {JobId} lease expired; failed after {Attempt} attempts", job.Id, job.Attempt);
            }
        }
        if (expired.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            await _notifications.FlushAsync();
        }
        return expired.Count;
    }
}
