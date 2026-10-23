using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClipForge.Api.Common;
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
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ClipForge.Api.Features.Live;

/// <summary>
/// Live capture (docs/LIVE_CAPTURE.md §2): companion devices and their tokens, sessions (each one owns a project), the
/// companion's heartbeats and segment uploads, and the reaper of silent sessions. Every query is scoped by the user of
/// the cookie/JWT (user endpoints) or of the device token (companion endpoints).
/// </summary>
public sealed class LiveService
{
    public const int MinSegmentSeconds = 20;
    public const int MaxSegmentSeconds = 120;
    public const int DefaultSegmentSeconds = 60;
    public const decimal MaxSegmentDurationSeconds = 300m;
    public const int DetailListLimit = 50;
    /// <summary>Room for the multipart fields and boundaries around the segment file.</summary>
    private const long MultipartOverheadBytes = 1024 * 1024;

    public static readonly TimeSpan DeviceOnlineWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SessionOnlineWindow = TimeSpan.FromSeconds(30);

    // pt-BR reasons shown on the Lives page ("Motivo: …").
    public const string QuotaExhaustedReason = "minutos de processamento do mês esgotados";
    public const string DeviceRevokedReason = "dispositivo revogado";
    public const string CompanionOfflineReason = "companion offline";
    public const string ProjectDeletedReason = "projeto excluído";
    public const string EndedByCompanionReason = "encerrada pelo companion";
    public const string StoppedByUserReason = "encerrada pelo usuário na página Lives";

    private static readonly string[] SegmentContainers = ["mkv", "webm", "mp4", "mov"];

    private readonly AppDbContext _db;
    private readonly QuotaService _quotas;
    private readonly SubscriptionService _subscriptions;
    private readonly ProjectService _projects;
    private readonly LiveJobSupport _jobs;
    private readonly JobEvents _events;
    private readonly MediaUrls _urls;
    private readonly IObjectStorage _storage;
    private readonly IClock _clock;
    private readonly LiveOptions _options;
    private readonly JobsOptions _jobOptions;
    private readonly ILogger<LiveService> _logger;

    public LiveService(
        AppDbContext db,
        QuotaService quotas,
        SubscriptionService subscriptions,
        ProjectService projects,
        LiveJobSupport jobs,
        JobEvents events,
        MediaUrls urls,
        IObjectStorage storage,
        IClock clock,
        IOptions<LiveOptions> options,
        IOptions<JobsOptions> jobOptions,
        ILogger<LiveService> logger)
    {
        _db = db;
        _quotas = quotas;
        _subscriptions = subscriptions;
        _projects = projects;
        _jobs = jobs;
        _events = events;
        _urls = urls;
        _storage = storage;
        _clock = clock;
        _options = options.Value;
        _jobOptions = jobOptions.Value;
        _logger = logger;
    }

    // ---------------------------------------------------------------- devices (user)

    public async Task<CreateDeviceTokenResponse> CreateDeviceAsync(Guid userId, string name, CancellationToken ct)
    {
        var token = DeviceTokens.Generate();
        var now = _clock.UtcNow;
        var device = new CompanionDevice
        {
            UserId = userId,
            Name = name.Trim(),
            TokenHash = DeviceTokens.Hash(token),
            TokenPrefix = DeviceTokens.DisplayPrefix(token),
            CreatedAt = now,
        };
        _db.CompanionDevices.Add(device);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Companion device {DeviceId} created for user {UserId}", device.Id, userId);
        return new CreateDeviceTokenResponse(device.ToDto(now), token);
    }

    /// <summary>Active devices first (newest first), revoked ones last.</summary>
    public async Task<IReadOnlyList<CompanionDeviceDto>> ListDevicesAsync(Guid userId, CancellationToken ct)
    {
        var devices = await _db.CompanionDevices.AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderBy(d => d.RevokedAt != null).ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        var now = _clock.UtcNow;
        return devices.Select(d => d.ToDto(now)).ToList();
    }

    /// <summary>Revokes the token (idempotent); the device's waiting/live/ending sessions go to error ("dispositivo revogado").</summary>
    public async Task RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken ct)
    {
        var device = await _db.CompanionDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, ct)
            ?? throw AppException.NotFound("Dispositivo não encontrado.");
        device.RevokedAt ??= _clock.UtcNow;
        var sessions = await _db.LiveSessions.Include(s => s.Project)
            .Where(s => s.DeviceId == device.Id && LiveSession.NonTerminalStatuses.Contains(s.Status))
            .ToListAsync(ct);
        foreach (var session in sessions)
        {
            await _jobs.SetSessionErrorAsync(session, DeviceRevokedReason, ct);
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Companion device {DeviceId} revoked ({Sessions} open sessions set to error)", device.Id, sessions.Count);
    }

    // ---------------------------------------------------------------- companion

    public async Task<CompanionMeResponse> MeAsync(Guid userId, Guid deviceId, CancellationToken ct)
    {
        var device = await _db.CompanionDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, ct)
            ?? throw AppException.Unauthorized("Token do dispositivo inválido.");
        var displayName = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        var entitlement = await _subscriptions.GetEntitlementAsync(userId, ct);
        return new CompanionMeResponse(new CompanionMeDevice(device.Id, device.Name), new CompanionMeUser(displayName ?? string.Empty),
            new CompanionMePlan(entitlement.Plan.Code, entitlement.Plan.Name));
    }

    /// <summary>
    /// Creates the session and its project (source_kind live, platform obs). 409 plan_limit when the device already has a
    /// waiting/live/ending session, or when the plan's active-project / concurrency caps are reached (checked under the
    /// per-user lock, like a project creation).
    /// </summary>
    public async Task<CreateLiveSessionResponse> CreateSessionAsync(Guid userId, Guid deviceId, CreateLiveSessionRequest request, CancellationToken ct)
    {
        var settings = MergeSettings(await _projects.DefaultSettingsAsync(userId, ct), request.Settings);
        var segmentSeconds = request.SegmentSeconds ?? DefaultSegmentSeconds;
        var now = _clock.UtcNow;
        var title = string.IsNullOrWhiteSpace(request.Title) ? await DefaultTitleAsync(userId, now, ct) : request.Title.Trim();
        try
        {
            var session = await _quotas.WithUserLockAsync(userId, async () =>
            {
                var device = await _db.CompanionDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId && d.RevokedAt == null, ct)
                    ?? throw AppException.Unauthorized("Este dispositivo foi revogado.");
                if (await _db.LiveSessions.AnyAsync(s => s.DeviceId == device.Id && LiveSession.NonTerminalStatuses.Contains(s.Status), ct))
                {
                    throw AppException.PlanLimit("Este dispositivo já tem uma sessão ao vivo em andamento (ou ainda em análise). Encerre-a antes de iniciar outra.");
                }
                var usage = await _quotas.EnsureCanCreateProjectAsync(userId, ct);
                var capped = PlanLimitHelpers.CapSettings(settings, usage.Entitlement.Limits);
                var project = new Project
                {
                    UserId = userId,
                    Title = title.Length > 200 ? title[..200] : title,
                    SourceKind = SourceKind.Live,
                    SourcePlatform = SourcePlatform.Obs,
                    Status = ProjectStatus.Processing,
                    Settings = capped,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                var created = new LiveSession
                {
                    UserId = userId,
                    DeviceId = device.Id,
                    ProjectId = project.Id,
                    Status = LiveSessionStatus.Waiting,
                    SegmentSeconds = segmentSeconds,
                    Settings = capped,
                    Obs = request.Obs is { } obs && (obs.Version is not null || obs.RecordingPath is not null)
                        ? new LiveObsInfo { Version = obs.Version?.Trim(), RecordingPath = obs.RecordingPath?.Trim() }
                        : null,
                    StartedAt = now,
                    UpdatedAt = now,
                    Device = device,
                    Project = project,
                };
                _db.Projects.Add(project);
                _db.LiveSessions.Add(created);
                await _db.SaveChangesAsync(ct);
                return created;
            }, ct);
            _logger.LogInformation("Live session {SessionId} created by device {DeviceId} (project {ProjectId})", session.Id, deviceId, session.ProjectId);
            return new CreateLiveSessionResponse((await ToDtosAsync([session], ct))[0], session.Settings);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the race on ix_live_sessions_device_active against a parallel creation for the same device.
            _db.ChangeTracker.Clear();
            throw AppException.PlanLimit("Este dispositivo já tem uma sessão ao vivo em andamento. Encerre-a antes de iniciar outra.");
        }
    }

    public async Task<LiveHeartbeatResponse> HeartbeatAsync(Guid userId, Guid sessionId, CompanionStateInput state, CancellationToken ct)
    {
        var session = await OwnedSessionAsync(userId, sessionId, ct);
        if (session.IsTerminal) throw TerminalConflict(session);
        var now = _clock.UtcNow;
        session.CompanionState = new LiveCompanionState
        {
            ObsConnected = state.ObsConnected,
            Streaming = state.Streaming,
            Recording = state.Recording,
            Uploading = state.Uploading,
            QueueLength = Math.Max(0, state.QueueLength),
            LastError = string.IsNullOrWhiteSpace(state.LastError) ? null : Truncate(state.LastError.Trim(), 500),
        };
        session.LastHeartbeatAt = now;
        session.UpdatedAt = now;
        if (state.Recording && session.Status == LiveSessionStatus.Waiting)
        {
            session.Status = LiveSessionStatus.Live;
        }
        await _db.SaveChangesAsync(ct);
        var last = await _db.LiveSegments.Where(s => s.SessionId == session.Id).MaxAsync(s => (int?)s.Sequence, ct);
        return new LiveHeartbeatResponse(session.Status, session.StopRequested, (last ?? -1) + 1);
    }

    /// <summary>
    /// waiting/live → ending; ending → ended at once when no segment job is pending (otherwise when the last one finishes).
    /// Repeating it is harmless: an ending or terminal session is returned as it is.
    /// </summary>
    public async Task<LiveSessionDto> EndAsync(Guid userId, Guid sessionId, int? lastSequence, CancellationToken ct)
    {
        var session = await OwnedSessionAsync(userId, sessionId, ct);
        var now = _clock.UtcNow;
        if (session.Status is LiveSessionStatus.Waiting or LiveSessionStatus.Live)
        {
            session.Status = LiveSessionStatus.Ending;
            session.EndReason ??= session.StopRequested ? StoppedByUserReason : EndedByCompanionReason;
            session.UpdatedAt = now;
            _logger.LogInformation("Live session {SessionId} ending (last sequence {LastSequence})", session.Id, lastSequence);
        }
        if (session.Status == LiveSessionStatus.Ending)
        {
            await _jobs.RefreshSessionAsync(session, null, null, ct);
        }
        await _db.SaveChangesAsync(ct);
        return (await ToDtosAsync([session], ct))[0];
    }

    /// <summary>
    /// <c>multipart/form-data</c> with the fields <c>sequence</c>, <c>startOffsetSeconds</c>, <c>durationSeconds</c>,
    /// <c>final</c> before the <c>file</c> part. The file is streamed to object storage (never buffered whole) after its
    /// magic bytes were checked; idempotent per (session, sequence): a repeated sequence answers the stored segment with
    /// <c>duplicate = true</c> and stores nothing.
    /// </summary>
    public async Task<SegmentUploadResponse> UploadSegmentAsync(Guid userId, Guid sessionId, HttpRequest request, CancellationToken ct)
    {
        var maxBytes = _options.MaxSegmentBytes;
        if (request.ContentLength is { } declared && declared > maxBytes + MultipartOverheadBytes)
        {
            throw TooLarge();
        }
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = maxBytes + MultipartOverheadBytes;
        }

        var session = await OwnedSessionAsync(userId, sessionId, ct);
        if (session.IsTerminal) throw TerminalConflict(session);

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
        {
            throw AppException.Validation("Envie o segmento como multipart/form-data.");
        }

        var reader = new MultipartReader(boundary, request.Body);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) continue;
            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? string.Empty;
            if (disposition.IsFileDisposition())
            {
                if (!string.Equals(name, "file", StringComparison.OrdinalIgnoreCase)) continue;
                var result = await StoreSegmentAsync(userId, session, fields, section.Body, ct);
                while (await reader.ReadNextSectionAsync(ct) is not null)
                {
                    // Parts after the file are ignored (drained by the reader).
                }
                return result;
            }
            if (disposition.IsFormDisposition() && name.Length > 0)
            {
                fields[name] = await ReadFieldAsync(name, section.Body, ct);
            }
        }
        throw AppException.Validation("Dados inválidos.", new Dictionary<string, string[]> { ["file"] = ["Envie o arquivo do segmento no campo file."] });
    }

    private async Task<SegmentUploadResponse> StoreSegmentAsync(Guid userId, LiveSession session, Dictionary<string, string> fields, Stream body, CancellationToken ct)
    {
        var (sequence, start, duration, final) = ParseSegmentFields(fields);

        var existing = await _db.LiveSegments.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == session.Id && s.Sequence == sequence, ct);
        if (existing is not null)
        {
            await DrainAsync(body, ct);
            return new SegmentUploadResponse(existing.ToDto(), true);
        }

        // Monthly minutes: nothing left → the session stops (the worker's /probe reserves each segment's exact duration).
        var usage = await _quotas.GetUsageAsync(userId, ct);
        if (usage.RemainingProcessingSeconds <= 0)
        {
            await _jobs.SetSessionErrorAsync(session, QuotaExhaustedReason, ct);
            await _db.SaveChangesAsync(ct);
            throw QuotaExhausted();
        }

        var header = new byte[MediaMagic.HeaderLength];
        var headerLength = 0;
        while (headerLength < header.Length)
        {
            var read = await body.ReadAsync(header.AsMemory(headerLength), ct);
            if (read == 0) break;
            headerLength += read;
        }
        var detected = MediaMagic.Detect(header.AsSpan(0, headerLength));
        if (detected is not { IsVideo: true } || Array.IndexOf(SegmentContainers, detected.Container) < 0)
        {
            throw AppException.InvalidMedia("O segmento não é um vídeo Matroska/WebM ou MP4 (assinatura do arquivo não reconhecida).");
        }

        var fileName = $"seg_{sequence:00000}.{detected.Container}";
        var key = $"{StorageKeys.UserPrefix(userId)}live/{session.Id:D}/{fileName}";
        long size;
        await using (var content = new SegmentUploadStream(header, headerLength, body, _options.MaxSegmentBytes, TooLarge))
        {
            await _storage.WriteAsync(key, content, detected.ContentType, ct);
            size = content.TotalBytes;
        }

        try
        {
            return await _quotas.WithUserLockAsync(userId, async () =>
            {
                var status = await _db.LiveSessions.Where(s => s.Id == session.Id).Select(s => s.Status).FirstAsync(ct);
                if (status is LiveSessionStatus.Ended or LiveSessionStatus.Error)
                {
                    session.Status = status;
                    throw TerminalConflict(session);
                }
                var now = _clock.UtcNow;
                var entitlement = usage.Entitlement;
                var asset = new MediaAsset
                {
                    UserId = userId,
                    Kind = MediaAssetKind.LiveSegment,
                    StorageKey = key,
                    OriginalFileName = fileName,
                    ContentType = detected.ContentType,
                    SizeBytes = size,
                    Status = MediaAssetStatus.Uploaded,
                    Container = detected.Container,
                    CreatedAt = now,
                    UploadedAt = now,
                };
                var job = new ProcessingJob
                {
                    UserId = userId,
                    ProjectId = session.ProjectId,
                    Kind = JobKind.LiveSegment,
                    Status = JobStatus.Queued,
                    Priority = entitlement.Limits.Priority,
                    MaxAttempts = Math.Max(1, _jobOptions.MaxAttempts),
                    Input = JsonDefaults.Serialize(session.Settings),
                    QueuedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                    StatusMessage = "Na fila para análise",
                };
                var segment = new LiveSegment
                {
                    SessionId = session.Id,
                    UserId = userId,
                    Sequence = sequence,
                    MediaAssetId = asset.Id,
                    JobId = job.Id,
                    StartOffsetSeconds = start,
                    DurationSeconds = duration,
                    IsFinal = final,
                    Status = LiveSegmentStatus.Queued,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.MediaAssets.Add(asset);
                _db.ProcessingJobs.Add(job);
                _db.LiveSegments.Add(segment);
                _events.Add(job, $"Segmento {sequence} da live recebido; análise enfileirada", null, JobStatus.Queued,
                    data: new { sessionId = session.Id, sequence, startOffsetSeconds = start, durationSeconds = duration, final, sizeBytes = size });
                if (session.Status == LiveSessionStatus.Waiting) session.Status = LiveSessionStatus.Live;
                session.LastSegmentAt = now;
                session.UpdatedAt = now;
                if (session.Project is { DeletedAt: null } project && project.Status != ProjectStatus.Processing)
                {
                    project.Status = ProjectStatus.Processing;
                    project.UpdatedAt = now;
                }
                await _db.SaveChangesAsync(ct);
                return new SegmentUploadResponse(segment.ToDto(), false);
            }, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The same sequence was stored by a parallel request (a retry while the first upload was still running):
            // both wrote the same storage key, the first row wins.
            _db.ChangeTracker.Clear();
            var winner = await _db.LiveSegments.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == session.Id && s.Sequence == sequence, ct);
            if (winner is not null) return new SegmentUploadResponse(winner.ToDto(), true);
            throw;
        }
    }

    private static (int Sequence, decimal Start, decimal Duration, bool Final) ParseSegmentFields(Dictionary<string, string> fields)
    {
        var errors = new Dictionary<string, string[]>();
        var sequence = 0;
        decimal start = 0, duration = 0;
        var final = false;
        if (!fields.TryGetValue("sequence", out var sequenceText)
            || !int.TryParse(sequenceText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence) || sequence is < 0 or > 1_000_000)
        {
            errors["sequence"] = ["sequence deve ser um inteiro ≥ 0 (enviado antes do arquivo)."];
        }
        if (!fields.TryGetValue("startOffsetSeconds", out var startText)
            || !decimal.TryParse(startText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out start) || start is < 0 or > 100_000_000m)
        {
            errors["startOffsetSeconds"] = ["startOffsetSeconds deve ser um número ≥ 0 (enviado antes do arquivo)."];
        }
        if (!fields.TryGetValue("durationSeconds", out var durationText)
            || !decimal.TryParse(durationText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration) || duration <= 0 || duration > MaxSegmentDurationSeconds)
        {
            errors["durationSeconds"] = [$"durationSeconds deve ser maior que 0 e no máximo {MaxSegmentDurationSeconds} (enviado antes do arquivo)."];
        }
        if (fields.TryGetValue("final", out var finalText) && !TryParseBool(finalText, out final))
        {
            errors["final"] = ["final deve ser true ou false."];
        }
        if (errors.Count > 0) throw AppException.Validation("Dados inválidos.", errors);
        return (sequence, Math.Round(start, 3), Math.Round(duration, 3), final);
    }

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "on":
                value = true;
                return true;
            case "false" or "0" or "off" or "":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static async Task<string> ReadFieldAsync(string name, Stream body, CancellationToken ct)
    {
        var buffer = new byte[1025];
        var length = 0;
        int read;
        while ((read = await body.ReadAsync(buffer.AsMemory(length), ct)) > 0)
        {
            length += read;
            if (length > 1024)
            {
                throw AppException.Validation("Dados inválidos.", new Dictionary<string, string[]> { [name] = ["Campo muito longo."] });
            }
        }
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    /// <summary>Reads what is left of a request body, so the client always gets the answer (best effort).</summary>
    public static async Task DrainAsync(Stream body, CancellationToken ct)
    {
        try
        {
            await body.CopyToAsync(Stream.Null, ct);
        }
        catch (Exception ex) when (ex is IOException or BadHttpRequestException or InvalidOperationException or InvalidDataException)
        {
        }
    }

    private AppException TooLarge()
        => new("payload_too_large", 413, $"O segmento excede o tamanho máximo de {_options.MaxSegmentMb} MB.");

    private static AppException QuotaExhausted()
        => AppException.QuotaExceeded("Os minutos de processamento do mês acabaram: a sessão ao vivo foi encerrada. Faça upgrade do plano para continuar.");

    private static AppException TerminalConflict(LiveSession session)
        => session.Status == LiveSessionStatus.Error && session.EndReason == QuotaExhaustedReason
            ? QuotaExhausted()
            : AppException.Conflict(session.EndReason is { Length: > 0 } reason
                ? $"A sessão já foi encerrada ({reason})."
                : "A sessão já foi encerrada.");

    // ---------------------------------------------------------------- sessions (user)

    public async Task<PagedResult<LiveSessionDto>> ListSessionsAsync(Guid userId, string? status, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.LiveSessions.AsNoTracking().Where(s => s.UserId == userId && s.Project!.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(status) && EnumNames.TryParse<LiveSessionStatus>(status, out var parsed))
        {
            query = query.Where(s => s.Status == parsed);
        }
        var total = await query.CountAsync(ct);
        var sessions = await query.Include(s => s.Device).Include(s => s.Project)
            .OrderByDescending(s => s.StartedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<LiveSessionDto>(await ToDtosAsync(sessions, ct), page, pageSize, total);
    }

    public async Task<LiveSessionDetailResponse> GetDetailAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var session = await _db.LiveSessions.AsNoTracking().Include(s => s.Device).Include(s => s.Project)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.Project!.DeletedAt == null, ct)
            ?? throw AppException.NotFound("Sessão não encontrada.");
        var dto = (await ToDtosAsync([session], ct))[0];
        var segments = await _db.LiveSegments.AsNoTracking().Where(s => s.SessionId == session.Id)
            .OrderByDescending(s => s.Sequence).Take(DetailListLimit).ToListAsync(ct);
        var clips = await _db.Clips.AsNoTracking().Where(c => c.ProjectId == session.ProjectId && c.DeletedAt == null)
            .OrderByDescending(c => c.CreatedAt).Take(DetailListLimit).ToListAsync(ct);
        var renderIds = clips.Where(c => c.CurrentRenderId != null).Select(c => c.CurrentRenderId!.Value).ToList();
        var renders = await _db.ClipRenders.AsNoTracking().Where(r => renderIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var highlights = new List<ClipSummaryDto>(clips.Count);
        foreach (var clip in clips)
        {
            var render = clip.CurrentRenderId is { } renderId && renders.TryGetValue(renderId, out var found) ? found : null;
            highlights.Add(clip.ToSummary(session.Project!.Title, await _urls.ThumbnailAsync(clip.ThumbnailStorageKey, ct), render));
        }
        var candidateCount = await _db.ClipCandidates.CountAsync(c => c.ProjectId == session.ProjectId, ct);
        return new LiveSessionDetailResponse(dto, session.Settings, segments.Select(s => s.ToDto()).ToList(), highlights, candidateCount);
    }

    /// <summary>"Encerrar sessão": the companion sees <c>stopRequested</c> in its next heartbeat and ends the session.</summary>
    public async Task<LiveSessionDto> StopAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var session = await OwnedSessionAsync(userId, sessionId, ct);
        if (session.IsTerminal || session.Project?.DeletedAt is not null) throw TerminalConflict(session);
        if (!session.StopRequested)
        {
            session.StopRequested = true;
            session.UpdatedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return (await ToDtosAsync([session], ct))[0];
    }

    // ---------------------------------------------------------------- reaper

    /// <summary>
    /// Waiting/live sessions without a heartbeat (nor a segment) for <c>Live:SessionTimeoutMinutes</c> → error
    /// ("companion offline"; their pending segments are still analysed). Also ends "ending" sessions whose segment jobs
    /// are all terminal (safety net for jobs that left the queue without passing through the job hooks).
    /// </summary>
    public async Task<int> ReapAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var threshold = now - TimeSpan.FromMinutes(Math.Max(1, _options.SessionTimeoutMinutes));
        var silent = await _db.LiveSessions.Include(s => s.Project)
            .Where(s => (s.Status == LiveSessionStatus.Waiting || s.Status == LiveSessionStatus.Live)
                && (s.LastHeartbeatAt ?? s.StartedAt) < threshold
                && (s.LastSegmentAt == null || s.LastSegmentAt < threshold))
            .OrderBy(s => s.StartedAt).Take(200)
            .ToListAsync(ct);
        foreach (var session in silent)
        {
            await _jobs.SetSessionErrorAsync(session, CompanionOfflineReason, ct);
            _logger.LogInformation("Live session {SessionId}: no heartbeat since {LastHeartbeat}; set to error (companion offline)", session.Id, session.LastHeartbeatAt);
        }
        var finished = await _db.LiveSessions.Include(s => s.Project)
            .Where(s => s.Status == LiveSessionStatus.Ending
                && !_db.ProcessingJobs.Any(j => j.ProjectId == s.ProjectId && j.Kind == JobKind.LiveSegment && JobStateMachine.ActiveStatuses.Contains(j.Status)))
            .Take(200)
            .ToListAsync(ct);
        foreach (var session in finished)
        {
            await _jobs.RefreshSessionAsync(session, null, null, ct);
        }
        if (silent.Count + finished.Count > 0) await _db.SaveChangesAsync(ct);
        return silent.Count + finished.Count;
    }

    // ---------------------------------------------------------------- helpers

    private async Task<LiveSession> OwnedSessionAsync(Guid userId, Guid sessionId, CancellationToken ct)
        => await _db.LiveSessions.Include(s => s.Project).Include(s => s.Device)
               .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct)
           ?? throw AppException.NotFound("Sessão não encontrada.");

    /// <summary>Session DTOs with their counters (one grouped query for the segments, one for the clips).</summary>
    private async Task<List<LiveSessionDto>> ToDtosAsync(IReadOnlyList<LiveSession> sessions, CancellationToken ct)
    {
        var ids = sessions.Select(s => s.Id).ToList();
        var projectIds = sessions.Select(s => s.ProjectId).ToList();
        var segmentStats = await _db.LiveSegments.AsNoTracking()
            .Where(s => ids.Contains(s.SessionId))
            .GroupBy(s => new { s.SessionId, s.Status })
            .Select(g => new { g.Key.SessionId, g.Key.Status, Count = g.Count(), Seconds = g.Sum(x => x.DurationSeconds) })
            .ToListAsync(ct);
        var clipCounts = await _db.Clips.AsNoTracking()
            .Where(c => projectIds.Contains(c.ProjectId) && c.DeletedAt == null)
            .GroupBy(c => c.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var now = _clock.UtcNow;
        var result = new List<LiveSessionDto>(sessions.Count);
        foreach (var session in sessions)
        {
            var stats = segmentStats.Where(x => x.SessionId == session.Id).ToList();
            var dto = new LiveSessionStatsDto(
                stats.Sum(x => x.Count),
                stats.Where(x => x.Status == LiveSegmentStatus.Analyzed).Sum(x => x.Count),
                stats.Where(x => x.Status == LiveSegmentStatus.Failed).Sum(x => x.Count),
                clipCounts.FirstOrDefault(x => x.ProjectId == session.ProjectId)?.Count ?? 0,
                stats.Sum(x => x.Seconds));
            result.Add(new LiveSessionDto(session.Id, session.ProjectId, session.Project?.Title ?? string.Empty, session.Status, session.DeviceId,
                session.Device?.Name, session.SegmentSeconds, session.StartedAt, session.EndedAt, session.LastSegmentAt, session.LastHeartbeatAt,
                session.LastHeartbeatAt is { } heartbeat && now - heartbeat <= SessionOnlineWindow,
                session.StopRequested, session.EndReason, session.CompanionState.ToDto(), dto));
        }
        return result;
    }

    /// <summary>
    /// The partial settings of the companion over the profile's editing preferences: top-level fields replace, the
    /// <c>subtitles</c> and <c>framing</c> objects merge field by field. Validated like project settings (400).
    /// </summary>
    private static AnalysisSettings MergeSettings(AnalysisSettings baseline, JsonElement? partial)
    {
        if (partial is not { ValueKind: JsonValueKind.Object } patch) return baseline;
        var node = JsonSerializer.SerializeToNode(baseline, JsonDefaults.Options)!.AsObject();
        foreach (var property in patch.EnumerateObject())
        {
            var key = node.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, property.Name, StringComparison.OrdinalIgnoreCase)) ?? property.Name;
            var value = JsonNode.Parse(property.Value.GetRawText());
            if (value is JsonObject incoming && node[key] is JsonObject current)
            {
                foreach (var (childName, childValue) in incoming.ToList())
                {
                    incoming.Remove(childName);
                    var childKey = current.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, childName, StringComparison.OrdinalIgnoreCase)) ?? childName;
                    current[childKey] = childValue;
                }
            }
            else
            {
                node[key] = value;
            }
        }
        AnalysisSettings merged;
        try
        {
            merged = node.Deserialize<AnalysisSettings>(JsonDefaults.Options) ?? baseline;
        }
        catch (JsonException)
        {
            throw AppException.Validation("Dados inválidos.", new Dictionary<string, string[]> { ["settings"] = ["Configurações de análise inválidas."] });
        }
        var validation = new AnalysisSettingsValidator().Validate(merged);
        if (!validation.IsValid)
        {
            var errors = validation.Errors
                .GroupBy(e => "settings." + (e.PropertyName.Length > 0 ? char.ToLowerInvariant(e.PropertyName[0]) + e.PropertyName[1..] : e.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            throw AppException.Validation("Dados inválidos.", errors);
        }
        return merged;
    }

    /// <summary>"Live de dd/MM HH:mm" in the user's time zone (America/Sao_Paulo by default, UTC−3 when it cannot be resolved).</summary>
    private async Task<string> DefaultTitleAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var timezone = await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.Timezone).FirstOrDefaultAsync(ct);
        DateTimeOffset local;
        try
        {
            local = string.IsNullOrWhiteSpace(timezone) ? now.ToOffset(TimeSpan.FromHours(-3)) : TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timezone));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            local = now.ToOffset(TimeSpan.FromHours(-3));
        }
        return "Live de " + local.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>
/// The segment file as it is streamed to storage: the magic-byte header already read, then the rest of the multipart
/// section, counting bytes and failing with 413 past the limit (so a body larger than declared never fills the disk).
/// </summary>
internal sealed class SegmentUploadStream : Stream
{
    private readonly byte[] _head;
    private readonly int _headLength;
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private readonly Func<Exception> _tooLarge;
    private int _headOffset;

    public SegmentUploadStream(byte[] head, int headLength, Stream inner, long maxBytes, Func<Exception> tooLarge)
    {
        _head = head;
        _headLength = headLength;
        _inner = inner;
        _maxBytes = maxBytes;
        _tooLarge = tooLarge;
    }

    public long TotalBytes { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => TotalBytes;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read;
        if (_headOffset < _headLength)
        {
            read = Math.Min(buffer.Length, _headLength - _headOffset);
            _head.AsSpan(_headOffset, read).CopyTo(buffer.Span);
            _headOffset += read;
        }
        else
        {
            read = await _inner.ReadAsync(buffer, cancellationToken);
        }
        return Count(read);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read;
        if (_headOffset < _headLength)
        {
            read = Math.Min(count, _headLength - _headOffset);
            Array.Copy(_head, _headOffset, buffer, offset, read);
            _headOffset += read;
        }
        else
        {
            read = _inner.Read(buffer, offset, count);
        }
        return Count(read);
    }

    private int Count(int read)
    {
        TotalBytes += read;
        if (TotalBytes > _maxBytes) throw _tooLarge();
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
