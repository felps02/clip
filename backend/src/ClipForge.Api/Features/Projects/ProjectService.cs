using ClipForge.Api.Features.Sources;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Features.Projects;

public sealed class ProjectService
{
    /// <summary>Clips (not deleted) that may be cut from one detected moment; bounds the render-job fan-out per candidate.</summary>
    public const int MaxClipsPerCandidate = 10;

    private readonly AppDbContext _db;
    private readonly QuotaService _quotas;
    private readonly SubscriptionService _subscriptions;
    private readonly SourceValidationService _sources;
    private readonly JobEvents _events;
    private readonly MediaUrls _urls;
    private readonly RenderService _renders;
    private readonly IClock _clock;
    private readonly JobsOptions _jobs;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(
        AppDbContext db,
        QuotaService quotas,
        SubscriptionService subscriptions,
        SourceValidationService sources,
        JobEvents events,
        MediaUrls urls,
        RenderService renders,
        IClock clock,
        IOptions<JobsOptions> jobs,
        ILogger<ProjectService> logger)
    {
        _db = db;
        _quotas = quotas;
        _subscriptions = subscriptions;
        _sources = sources;
        _events = events;
        _urls = urls;
        _renders = renders;
        _clock = clock;
        _jobs = jobs.Value;
        _logger = logger;
    }

    // ---------------------------------------------------------------- creation

    public async Task<(Project Project, ProcessingJob Job, bool Created)> CreateAsync(Guid userId, CreateProjectRequest request, string? idempotencyKey, CancellationToken ct)
    {
        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (idempotencyKey is { Length: > 128 })
        {
            throw AppException.Validation("Idempotency-Key deve ter no máximo 128 caracteres.");
        }

        if (idempotencyKey is not null)
        {
            var existing = await FindByClientRequestIdAsync(userId, idempotencyKey, ct);
            if (existing is not null) return (existing.Value.Project, existing.Value.Job, false);
        }

        // Source
        MediaAsset? asset = null;
        var sourceKind = request.Source.Kind switch
        {
            "upload" => SourceKind.Upload,
            "link" => SourceKind.Link,
            "live" => SourceKind.Live,
            _ => throw AppException.Validation("Tipo de origem inválido."),
        };
        var platform = SourcePlatform.Upload;
        string? sourceUrl = null;

        if (sourceKind == SourceKind.Upload)
        {
            asset = await _db.MediaAssets.FirstOrDefaultAsync(a => a.Id == request.Source.MediaAssetId && a.UserId == userId && a.DeletedAt == null, ct)
                ?? throw AppException.NotFound("Arquivo não encontrado.");
            if (!asset.IsUsable)
            {
                throw AppException.InvalidMedia("O upload ainda não foi concluído ou é inválido.");
            }
        }
        else if (sourceKind == SourceKind.Link)
        {
            var validation = await _sources.ValidateAsync(request.Source.Url!, ct);
            if (!validation.Supported)
            {
                throw new AppException(ErrorCodes.SourceNotSupported, 422, validation.Reason);
            }
            sourceUrl = request.Source.Url;
            platform = PlatformDetector.ToSourcePlatform(PlatformDetector.Detect(sourceUrl));
            throw new AppException(ErrorCodes.SourceNotSupported, 422, "Processamento direto de links ainda não está disponível.");
        }
        else
        {
            throw new AppException(ErrorCodes.SourceNotSupported, 422, "Sessões ao vivo são criadas pelo companion do OBS (fase 5).");
        }

        var sourceAsset = asset!;
        try
        {
            // Idempotency replay, asset reservation, quota checks and the insert run under the per-user lock, so
            // parallel requests cannot exceed the active-project / concurrent-job caps or reuse the same asset.
            return await _quotas.WithUserLockAsync(userId, async () =>
            {
                if (idempotencyKey is not null)
                {
                    var replay = await FindByClientRequestIdAsync(userId, idempotencyKey, ct);
                    if (replay is not null) return (replay.Value.Project, replay.Value.Job, false);
                }
                var inUse = await _db.Projects.AnyAsync(p => p.MediaAssetId == sourceAsset.Id && p.DeletedAt == null, ct);
                if (inUse) throw AppException.Conflict("Este arquivo já está sendo usado por outro projeto.");

                var usage = await _quotas.EnsureCanCreateProjectAsync(userId, ct);
                var limits = usage.Entitlement.Limits;

                var settings = request.Settings ?? await DefaultSettingsAsync(userId, ct);
                var capped = PlanLimitHelpers.CapSettings(settings, limits);

                var now = _clock.UtcNow;
                var title = string.IsNullOrWhiteSpace(request.Title)
                    ? Path.GetFileNameWithoutExtension(sourceAsset.OriginalFileName) is { Length: > 0 } name ? name : "Novo projeto"
                    : request.Title.Trim();

                var project = new Project
                {
                    UserId = userId,
                    Title = title.Length > 200 ? title[..200] : title,
                    SourceKind = sourceKind,
                    SourceUrl = sourceUrl,
                    SourcePlatform = platform,
                    MediaAssetId = sourceAsset.Id,
                    Status = ProjectStatus.Created,
                    Settings = capped,
                    ClientRequestId = idempotencyKey,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                var job = NewAnalyzeJob(project, capped, limits, now);
                _db.Projects.Add(project);
                _db.ProcessingJobs.Add(job);
                _events.Add(job, "Projeto criado; análise enfileirada", null, JobStatus.Queued);
                await _db.SaveChangesAsync(ct);
                return (project, job, true);
            }, ct);
        }
        catch (DbUpdateException ex) when (idempotencyKey is not null && IsUniqueViolation(ex))
        {
            // Lost a race on (user_id, client_request_id): the transaction rolled back, answer with the winner.
            _logger.LogInformation("Idempotent project creation race for user {UserId}", userId);
            _db.ChangeTracker.Clear();
            var existing = await FindByClientRequestIdAsync(userId, idempotencyKey, ct);
            if (existing is not null) return (existing.Value.Project, existing.Value.Job, false);
            throw;
        }
    }

    private ProcessingJob NewAnalyzeJob(Project project, AnalysisSettings capped, PlanLimits limits, DateTimeOffset now) => new()
    {
        UserId = project.UserId,
        ProjectId = project.Id,
        Kind = JobKind.AnalyzeSource,
        Status = JobStatus.Queued,
        Priority = limits.Priority,
        MaxAttempts = Math.Max(1, _jobs.MaxAttempts),
        Input = JsonDefaults.Serialize(capped),
        QueuedAt = now,
        CreatedAt = now,
        UpdatedAt = now,
        StatusMessage = "Na fila para análise",
    };

    /// <summary>
    /// Settings of a project created without explicit settings: the profile's editing preferences (format, subtitles,
    /// clip lengths, spoken language) over the AnalysisSettings defaults. The caller caps them by the plan.
    /// </summary>
    public async Task<AnalysisSettings> DefaultSettingsAsync(Guid userId, CancellationToken ct)
    {
        var stored = await _db.UserProfiles.Where(p => p.UserId == userId).Select(p => p.EditingPreferences).FirstOrDefaultAsync(ct);
        var prefs = (stored ?? new EditingPreferences()).Normalized();
        return new AnalysisSettings
        {
            MinClipSeconds = prefs.MinClipSeconds,
            MaxClipSeconds = prefs.MaxClipSeconds,
            TargetClipSeconds = prefs.TargetClipSeconds,
            AspectRatio = prefs.AspectRatio,
            Subtitles = new SubtitleSettings { Enabled = prefs.SubtitlesEnabled, Style = prefs.SubtitleStyle, Position = prefs.SubtitlePosition },
            Language = prefs.Language,
        };
    }

    private async Task<(Project Project, ProcessingJob Job)?> FindByClientRequestIdAsync(Guid userId, string key, CancellationToken ct)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.UserId == userId && p.ClientRequestId == key, ct);
        if (project is null) return null;
        var job = await _db.ProcessingJobs.Where(j => j.ProjectId == project.Id && j.Kind == JobKind.AnalyzeSource).OrderBy(j => j.CreatedAt).FirstAsync(ct);
        return (project, job);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    // ---------------------------------------------------------------- queries

    public async Task<Project> GetOwnedAsync(Guid userId, Guid projectId, CancellationToken ct, bool includeAsset = false)
    {
        IQueryable<Project> query = _db.Projects;
        if (includeAsset) query = query.Include(p => p.MediaAsset);
        var project = await query.FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId && p.DeletedAt == null, ct);
        return project ?? throw AppException.NotFound("Projeto não encontrado.");
    }

    public async Task<(IReadOnlyList<ProjectSummaryDto> Items, int Total)> ListAsync(Guid userId, string? status, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Projects.Where(p => p.UserId == userId && p.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(status) && EnumNames.TryParse<ProjectStatus>(status, out var parsed))
        {
            query = query.Where(p => p.Status == parsed);
        }
        var total = await query.CountAsync(ct);
        var projects = await query.OrderByDescending(p => p.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Include(p => p.MediaAsset)
            .ToListAsync(ct);
        var items = new List<ProjectSummaryDto>(projects.Count);
        foreach (var project in projects)
        {
            items.Add(await ToSummaryAsync(project, ct));
        }
        return (items, total);
    }

    public async Task<ProjectSummaryDto> ToSummaryAsync(Project project, CancellationToken ct)
    {
        var latestJob = await _db.ProcessingJobs.Where(j => j.ProjectId == project.Id).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        var candidateCount = await CandidateCountAsync(project.Id, ct);
        var clipCount = await _db.Clips.CountAsync(c => c.ProjectId == project.Id && c.DeletedAt == null, ct);
        var thumbnailKey = await _db.Clips.Where(c => c.ProjectId == project.Id && c.DeletedAt == null && c.ThumbnailStorageKey != null)
            .OrderByDescending(c => c.CreatedAt).Select(c => c.ThumbnailStorageKey).FirstOrDefaultAsync(ct);
        return new ProjectSummaryDto(project.Id, project.Title, project.SourceKind, project.SourcePlatform, project.Status, project.CreatedAt, project.UpdatedAt,
            project.MediaAsset?.DurationSeconds, latestJob?.ToSummary(project.Title), candidateCount, clipCount, await _urls.ThumbnailAsync(thumbnailKey, ct));
    }

    public async Task<ProjectDetailResponse> GetDetailAsync(Guid userId, Guid projectId, CancellationToken ct)
    {
        var project = await GetOwnedAsync(userId, projectId, ct, includeAsset: true);
        var jobs = await _db.ProcessingJobs.Where(j => j.ProjectId == project.Id).OrderByDescending(j => j.CreatedAt).ToListAsync(ct);
        var candidateCount = await CandidateCountAsync(project.Id, ct);
        var clipCount = await _db.Clips.CountAsync(c => c.ProjectId == project.Id && c.DeletedAt == null, ct);
        var asset = project.MediaAsset;
        var source = new ProjectSourceDto(asset?.ToDto(), asset?.IsPreviewable == true, asset?.IsPreviewable == true ? await _urls.PreviewAsync(asset, ct) : null);
        return new ProjectDetailResponse(project.ToDto(), jobs.Select(j => j.ToSummary(project.Title)).ToList(), candidateCount, clipCount, source);
    }

    private async Task<int> CandidateCountAsync(Guid projectId, CancellationToken ct)
    {
        var jobId = await LatestCandidateJobIdAsync(projectId, ct);
        return jobId is null ? 0 : await _db.ClipCandidates.CountAsync(c => c.JobId == jobId, ct);
    }

    private Task<Guid?> LatestCandidateJobIdAsync(Guid projectId, CancellationToken ct)
        => _db.ClipCandidates.Where(c => c.ProjectId == projectId).OrderByDescending(c => c.CreatedAt).Select(c => (Guid?)c.JobId).FirstOrDefaultAsync(ct);

    public async Task<Project> UpdateTitleAsync(Guid userId, Guid projectId, string title, CancellationToken ct)
    {
        var project = await GetOwnedAsync(userId, projectId, ct);
        project.Title = title.Trim();
        project.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return project;
    }

    public async Task DeleteAsync(Guid userId, Guid projectId, CancellationToken ct)
    {
        var project = await GetOwnedAsync(userId, projectId, ct);
        var now = _clock.UtcNow;
        project.DeletedAt = now;
        project.UpdatedAt = now;
        await _db.Clips.Where(c => c.ProjectId == project.Id && c.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, now).SetProperty(c => c.UpdatedAt, now), ct);

        var jobs = await _db.ProcessingJobs.Where(j => j.ProjectId == project.Id && JobStateMachine.ActiveStatuses.Contains(j.Status)).ToListAsync(ct);
        foreach (var job in jobs)
        {
            if (JobStateMachine.CanCancelImmediately(job.Status))
            {
                _events.Add(job, "Projeto excluído; job cancelado", job.Status, JobStatus.Cancelled);
                job.Status = JobStatus.Cancelled;
                job.FinishedAt = now;
                job.ReservedSeconds = 0;
            }
            else
            {
                job.CancelRequested = true;
                _events.Add(job, "Projeto excluído; cancelamento solicitado ao worker");
            }
            job.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- jobs

    public async Task<ProcessingJob> CreateAnalyzeJobAsync(Guid userId, Guid projectId, AnalysisSettings? settings, CancellationToken ct)
    {
        var project = await GetOwnedAsync(userId, projectId, ct, includeAsset: true);
        if (project.MediaAsset is not { IsUsable: true })
        {
            throw AppException.InvalidMedia("O projeto não possui um arquivo válido para analisar.");
        }
        return await _quotas.WithUserLockAsync(userId, async () =>
        {
            var active = await _db.ProcessingJobs.AnyAsync(j => j.ProjectId == project.Id && JobStateMachine.ActiveStatuses.Contains(j.Status), ct);
            if (active) throw AppException.Conflict("Já existe um processamento em andamento para este projeto.");

            var usage = await _quotas.EnsureCanStartJobAsync(userId, ct);
            if (!QuotaService.ActiveProjectStatuses.Contains(project.Status))
            {
                // A failed or cancelled project does not count as active; analysing it again makes it active, so the
                // active-projects cap applies as at creation (otherwise projects parked as cancelled right after
                // creation could all be revived past the cap).
                QuotaCalculator.CheckActiveProjects(usage.ActiveProjects, usage.Entitlement.Limits).ThrowIfDenied();
            }
            var capped = PlanLimitHelpers.CapSettings(settings ?? project.Settings, usage.Entitlement.Limits);
            var now = _clock.UtcNow;
            project.Settings = capped;
            project.Status = ProjectStatus.Created;
            project.UpdatedAt = now;
            var job = NewAnalyzeJob(project, capped, usage.Entitlement.Limits, now);
            _db.ProcessingJobs.Add(job);
            _events.Add(job, "Nova análise enfileirada", null, JobStatus.Queued);
            await _db.SaveChangesAsync(ct);
            return job;
        }, ct);
    }

    public async Task<ProcessingJob> GetJobAsync(Guid userId, Guid projectId, Guid jobId, CancellationToken ct)
    {
        var job = await _db.ProcessingJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId && j.UserId == userId && j.Project.DeletedAt == null, ct);
        return job ?? throw AppException.NotFound("Job não encontrado.");
    }

    public async Task<List<ProcessingJobEvent>> GetJobEventsAsync(Guid jobId, int? take, CancellationToken ct)
    {
        var query = _db.ProcessingJobEvents.Where(e => e.JobId == jobId).OrderByDescending(e => e.CreatedAt);
        var events = take is { } n ? await query.Take(n).ToListAsync(ct) : await query.ToListAsync(ct);
        events.Reverse();
        return events;
    }

    public async Task<ProcessingJob> CancelJobAsync(Guid userId, Guid projectId, Guid jobId, CancellationToken ct)
    {
        var job = await GetJobAsync(userId, projectId, jobId, ct);
        var now = _clock.UtcNow;
        if (JobStateMachine.CanCancelImmediately(job.Status))
        {
            _events.Add(job, "Cancelado pelo usuário", job.Status, JobStatus.Cancelled);
            job.Status = JobStatus.Cancelled;
            job.FinishedAt = now;
            job.ReservedSeconds = 0;
            job.StatusMessage = "Cancelado";
            job.UpdatedAt = now;
            await UpdateProjectStatusAfterJobAsync(job, ct);
        }
        else if (JobStateMachine.CanRequestCancel(job.Status))
        {
            if (!job.CancelRequested)
            {
                job.CancelRequested = true;
                job.UpdatedAt = now;
                _events.Add(job, "Cancelamento solicitado; aguardando o worker");
            }
        }
        else
        {
            throw new AppException(ErrorCodes.JobNotCancellable, 409, "Este job já foi finalizado e não pode ser cancelado.");
        }
        await _db.SaveChangesAsync(ct);
        return job;
    }

    /// <summary>Project status mirrors its latest analyze job (ready when any analysis completed before).</summary>
    public async Task UpdateProjectStatusAfterJobAsync(ProcessingJob job, CancellationToken ct)
    {
        if (job.Kind != JobKind.AnalyzeSource) return;
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == job.ProjectId, ct);
        if (project is null) return;
        var latestId = await _db.ProcessingJobs.Where(j => j.ProjectId == project.Id && j.Kind == JobKind.AnalyzeSource)
            .OrderByDescending(j => j.CreatedAt).Select(j => j.Id).FirstOrDefaultAsync(ct);
        if (latestId != job.Id) return;

        var hasCompleted = await _db.ProcessingJobs.AnyAsync(j => j.ProjectId == project.Id && j.Kind == JobKind.AnalyzeSource && j.Status == JobStatus.Completed && j.Id != job.Id, ct);
        project.Status = job.Status switch
        {
            JobStatus.Completed => ProjectStatus.Ready,
            JobStatus.Failed => hasCompleted ? ProjectStatus.Ready : ProjectStatus.Failed,
            JobStatus.Cancelled => hasCompleted ? ProjectStatus.Ready : ProjectStatus.Cancelled,
            JobStatus.Pending or JobStatus.Queued => project.Status == ProjectStatus.Ready ? ProjectStatus.Ready : ProjectStatus.Created,
            _ => ProjectStatus.Processing,
        };
        project.UpdatedAt = _clock.UtcNow;
    }

    // ---------------------------------------------------------------- candidates

    public async Task<CandidatesResponse> GetCandidatesAsync(Guid userId, Guid projectId, string? sort, string? category, CancellationToken ct)
    {
        var project = await GetOwnedAsync(userId, projectId, ct, includeAsset: true);
        var jobId = await LatestCandidateJobIdAsync(project.Id, ct);
        if (jobId is null)
        {
            return new CandidatesResponse([], new CandidatesSignalsDto(new(false), new(false), new(false)));
        }

        var query = _db.ClipCandidates.Where(c => c.JobId == jobId);
        if (!string.IsNullOrWhiteSpace(category) && EnumNames.TryParse<ClipCategory>(category, out var cat))
        {
            query = query.Where(c => c.Category == cat);
        }
        query = string.Equals(sort, "time", StringComparison.OrdinalIgnoreCase)
            ? query.OrderBy(c => c.StartSeconds)
            : query.OrderByDescending(c => c.Score).ThenBy(c => c.Rank);
        var candidates = await query.ToListAsync(ct);

        var candidateIds = candidates.Select(c => c.Id).ToList();
        var clipIds = await _db.Clips.Where(c => c.CandidateId != null && candidateIds.Contains(c.CandidateId.Value) && c.DeletedAt == null)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.CandidateId, c.Id })
            .ToListAsync(ct);
        var clipByCandidate = clipIds.GroupBy(x => x.CandidateId!.Value).ToDictionary(g => g.Key, g => g.Last().Id);

        var asset = project.MediaAsset;
        var previewable = asset?.IsPreviewable == true;
        var previewUrl = previewable ? await _urls.PreviewAsync(asset, ct) : null;

        var items = candidates.Select(c => new CandidateDto(c.Id, c.Rank, c.StartSeconds, c.EndSeconds, c.EndSeconds - c.StartSeconds, c.Category, c.Score,
            c.Reasoning, c.Transcript, JsonDefaults.ParseElement(c.Signals), c.Status,
            clipByCandidate.TryGetValue(c.Id, out var clipId) ? clipId : null, previewUrl, previewable)).ToList();

        var job = await _db.ProcessingJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        var signals = ReadSignals(job?.Result);
        return new CandidatesResponse(items, signals);
    }

    public static CandidatesSignalsDto ReadSignals(JobResult? result)
    {
        bool Available(string name)
        {
            if (result?.Signals is not { ValueKind: System.Text.Json.JsonValueKind.Object } signals) return false;
            if (!signals.TryGetProperty(name, out var section)) return false;
            if (section.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            return section.ValueKind == System.Text.Json.JsonValueKind.Object && section.TryGetProperty("available", out var available) && available.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        return new CandidatesSignalsDto(new(Available("transcript")), new(Available("audio")), new(Available("visual")));
    }

    public async Task<(Clip Clip, ProcessingJob Job)> CreateClipFromCandidateAsync(Guid userId, Guid projectId, Guid candidateId, CreateClipFromCandidateRequest request, CancellationToken ct)
    {
        var project = await GetOwnedAsync(userId, projectId, ct, includeAsset: true);
        if (project.MediaAsset is not { IsUsable: true })
        {
            throw AppException.InvalidMedia("O projeto não possui um arquivo válido para renderizar.");
        }
        var candidate = await _db.ClipCandidates.FirstOrDefaultAsync(c => c.Id == candidateId && c.ProjectId == project.Id && c.UserId == userId, ct)
            ?? throw AppException.NotFound("Momento não encontrado.");

        var entitlement = await _subscriptions.GetEntitlementAsync(userId, ct);
        var settings = project.Settings;
        var start = request.StartSeconds ?? candidate.StartSeconds;
        var end = request.EndSeconds ?? candidate.EndSeconds;
        if (end <= start) throw AppException.Validation("endSeconds deve ser maior que startSeconds.");
        if (project.MediaAsset.DurationSeconds is { } duration && end > duration + 1)
        {
            throw AppException.Validation("O trecho ultrapassa a duração do vídeo.");
        }

        // Clip cap, render quota (renders/month + concurrent jobs) and the inserts run under the per-user lock.
        return await _quotas.WithUserLockAsync(userId, async () =>
        {
            var clipsOfCandidate = await _db.Clips.CountAsync(c => c.CandidateId == candidate.Id && c.DeletedAt == null, ct);
            if (clipsOfCandidate >= MaxClipsPerCandidate)
            {
                throw AppException.Conflict($"Limite de {MaxClipsPerCandidate} clipes por momento atingido. Edite ou exclua um dos clipes existentes.");
            }

            var now = _clock.UtcNow;
            var clip = new Clip
            {
                UserId = userId,
                ProjectId = project.Id,
                CandidateId = candidate.Id,
                Title = string.IsNullOrWhiteSpace(request.Title) ? DefaultClipTitle(project, candidate) : request.Title.Trim(),
                StartSeconds = start,
                EndSeconds = end,
                AspectRatio = request.AspectRatio ?? settings.AspectRatio,
                Subtitles = PlanLimitHelpers.CapSubtitles(request.Subtitles ?? settings.Subtitles, entitlement.Limits),
                Framing = PlanLimitHelpers.CapFraming(request.Framing ?? settings.Framing, entitlement.Limits),
                Category = candidate.Category,
                Score = candidate.Score,
                Status = ClipStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Clips.Add(clip);
            if (candidate.Status == CandidateStatus.Detected) candidate.Status = CandidateStatus.Selected;

            var (job, _) = await _renders.RequestRenderAsync(clip, ct);
            await _db.SaveChangesAsync(ct);
            return (clip, job);
        }, ct);
    }

    public static string DefaultClipTitle(Project project, ClipCandidate candidate)
    {
        var label = candidate.Category switch
        {
            ClipCategory.Reaction => "Reação",
            ClipCategory.Funny => "Momento engraçado",
            ClipCategory.Hype => "Hype",
            ClipCategory.Tension => "Tensão",
            ClipCategory.Gameplay => "Jogada",
            ClipCategory.Quote => "Frase",
            _ => "Destaque",
        };
        var title = $"{label} #{candidate.Rank} - {project.Title}";
        return title.Length > 200 ? title[..200] : title;
    }
}
