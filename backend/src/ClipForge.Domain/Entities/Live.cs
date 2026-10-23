using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Domain.Entities;

/// <summary>
/// A computer running the OBS companion (docs/LIVE_CAPTURE.md). Authenticates with a device token
/// (<c>cfd_</c> + 40 base62 characters) of which only the SHA-256 hex and the first 8 characters after the prefix are kept.
/// </summary>
public class CompanionDevice
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>SHA-256 hex (lower case) of the whole token. Unique.</summary>
    public string TokenHash { get; set; } = string.Empty;
    /// <summary>First 8 characters after <c>cfd_</c>, for display.</summary>
    public string TokenPrefix { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Last authenticated request (written at most every 30 s).</summary>
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>One live capture of a device. Owns one project (source_kind live) whose candidates and clips are the highlights.</summary>
public class LiveSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid ProjectId { get; set; }
    public LiveSessionStatus Status { get; set; } = LiveSessionStatus.Waiting;
    public int SegmentSeconds { get; set; } = 60;
    /// <summary>Analysis settings of the session (capped by the plan at creation and again when each job is claimed).</summary>
    public AnalysisSettings Settings { get; set; } = new();
    /// <summary>What the companion reported in its last heartbeat.</summary>
    public LiveCompanionState? CompanionState { get; set; }
    /// <summary>"Encerrar sessão" on the Lives page: the companion ends the session when its heartbeat sees it.</summary>
    public bool StopRequested { get; set; }
    /// <summary>pt-BR reason shown on the Lives page (e.g. "dispositivo revogado", "companion offline").</summary>
    public string? EndReason { get; set; }
    public LiveObsInfo? Obs { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? LastSegmentAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public CompanionDevice? Device { get; set; }
    public Project? Project { get; set; }

    public static readonly LiveSessionStatus[] NonTerminalStatuses = [LiveSessionStatus.Waiting, LiveSessionStatus.Live, LiveSessionStatus.Ending];

    public bool IsTerminal => Status is LiveSessionStatus.Ended or LiveSessionStatus.Error;
}

/// <summary>One uploaded segment of a session: its media asset and its live_segment job.</summary>
public class LiveSegment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public int Sequence { get; set; }
    public Guid? MediaAssetId { get; set; }
    public Guid? JobId { get; set; }
    /// <summary>Seconds from the start of the session's recording.</summary>
    public decimal StartOffsetSeconds { get; set; }
    public decimal DurationSeconds { get; set; }
    public bool IsFinal { get; set; }
    public LiveSegmentStatus Status { get; set; } = LiveSegmentStatus.Received;
    public string? Error { get; set; }
    /// <summary>Raw JSON: the <c>liveContext</c> the segment's job reported in its /analysis (or /complete) summary.</summary>
    public string? LiveContext { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public LiveSession? Session { get; set; }
    public MediaAsset? MediaAsset { get; set; }
}

/// <summary>live_sessions.companion_state (jsonb): the companion's last heartbeat.</summary>
public class LiveCompanionState
{
    public bool ObsConnected { get; set; }
    public bool Streaming { get; set; }
    public bool Recording { get; set; }
    public bool Uploading { get; set; }
    public int QueueLength { get; set; }
    public string? LastError { get; set; }
}

/// <summary>live_sessions.obs (jsonb): what the companion knew about OBS when the session was created.</summary>
public class LiveObsInfo
{
    public string? Version { get; set; }
    public string? RecordingPath { get; set; }
}
