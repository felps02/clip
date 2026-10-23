using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Domain.Entities;

/// <summary>One analysed source ("MediaSource") with its settings, jobs, candidates and clips.</summary>
public class Project
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public SourceKind SourceKind { get; set; } = SourceKind.Upload;
    public string? SourceUrl { get; set; }
    public SourcePlatform SourcePlatform { get; set; } = SourcePlatform.Upload;
    public Guid? MediaAssetId { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Created;
    public AnalysisSettings Settings { get; set; } = new();
    public string? ClientRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }

    public MediaAsset? MediaAsset { get; set; }
    public List<ProcessingJob> Jobs { get; set; } = new();
    public List<Clip> Clips { get; set; } = new();
    public List<ClipCandidate> Candidates { get; set; } = new();
}

public class ProcessingJob
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
    public JobKind Kind { get; set; } = JobKind.AnalyzeSource;
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public short Progress { get; set; }
    public string? StatusMessage { get; set; }
    public int Priority { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public string? WorkerId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public bool CancelRequested { get; set; }
    /// <summary>Kind-specific input document (raw JSON): capped AnalysisSettings or { clipId, renderId }.</summary>
    public string? Input { get; set; }
    public JobResult? Result { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int ReservedSeconds { get; set; }
    public string? TranscriptStorageKey { get; set; }
    public string? SignalsStorageKey { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Project Project { get; set; } = null!;
    public List<ProcessingJobEvent> Events { get; set; } = new();
    public List<ClipCandidate> Candidates { get; set; } = new();
}

public class ProcessingJobEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid JobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public JobEventLevel Level { get; set; } = JobEventLevel.Info;
    public JobStatus? FromStatus { get; set; }
    public JobStatus? ToStatus { get; set; }
    public string Message { get; set; } = string.Empty;
    /// <summary>Raw JSON document.</summary>
    public string? Data { get; set; }
}

public class ClipCandidate
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid JobId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public int Rank { get; set; }
    public decimal StartSeconds { get; set; }
    public decimal EndSeconds { get; set; }
    /// <summary>Stored generated column: end_seconds - start_seconds.</summary>
    public decimal DurationSeconds { get; private set; }
    public ClipCategory Category { get; set; } = ClipCategory.Highlight;
    public short Score { get; set; }
    public string? Reasoning { get; set; }
    public string? Transcript { get; set; }
    /// <summary>Raw JSON document (signals used for the detection).</summary>
    public string? Signals { get; set; }
    public CandidateStatus Status { get; set; } = CandidateStatus.Detected;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ProcessingJob Job { get; set; } = null!;
    public Project Project { get; set; } = null!;
}

public class Clip
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? CandidateId { get; set; }
    public string Title { get; set; } = string.Empty;
    public decimal StartSeconds { get; set; }
    public decimal EndSeconds { get; set; }
    public string AspectRatio { get; set; } = AspectRatios.Vertical;
    public SubtitleSettings Subtitles { get; set; } = new();
    public FramingSettings Framing { get; set; } = new();
    public ClipCategory? Category { get; set; }
    public short? Score { get; set; }
    public ClipStatus Status { get; set; } = ClipStatus.Pending;
    /// <summary>Id of the render shown to the user (FK to clip_renders, ON DELETE SET NULL). Set after the render row exists.</summary>
    public Guid? CurrentRenderId { get; set; }
    public string? ThumbnailStorageKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }

    public Project Project { get; set; } = null!;
    public ClipCandidate? Candidate { get; set; }
    public List<ClipRender> Renders { get; set; } = new();

    public decimal DurationSeconds => EndSeconds - StartSeconds;
}

public class ClipRender
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ClipId { get; set; }
    public Guid UserId { get; set; }
    public Guid? JobId { get; set; }
    public int Version { get; set; } = 1;
    public string AspectRatio { get; set; } = AspectRatios.Vertical;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? StorageKey { get; set; }
    public long? SizeBytes { get; set; }
    public decimal? DurationSeconds { get; set; }
    public bool HasSubtitles { get; set; }
    public RenderStatus Status { get; set; } = RenderStatus.Pending;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public Clip Clip { get; set; } = null!;
}

public class IntegrationConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public IntegrationProvider Provider { get; set; }
    public IntegrationStatus Status { get; set; } = IntegrationStatus.Connected;
    public string? ExternalAccountId { get; set; }
    public string? DisplayName { get; set; }
    public string[] Scopes { get; set; } = [];
    public string? AccessTokenEncrypted { get; set; }
    public string? RefreshTokenEncrypted { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    /// <summary>Raw JSON document.</summary>
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Notification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; } = NotificationType.System;
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    /// <summary>Raw JSON document.</summary>
    public string? Data { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
