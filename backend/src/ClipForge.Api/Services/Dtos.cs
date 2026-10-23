using System.Text.Json;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Services;

// Shared DTOs used by several features (docs/API.md).

public sealed record UserDto(Guid Id, string Email, string DisplayName, bool EmailConfirmed, DateTimeOffset CreatedAt);

public sealed record AssetDto(
    Guid Id,
    MediaAssetKind Kind,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    MediaAssetStatus Status,
    decimal? DurationSeconds,
    int? Width,
    int? Height,
    double? Fps,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    bool? HasAudio,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UploadedAt,
    DateTimeOffset? ExpiresAt);

public sealed record JobSummaryDto(
    Guid Id,
    Guid ProjectId,
    string? ProjectTitle,
    JobKind Kind,
    JobStatus Status,
    int Progress,
    string? StatusMessage,
    DateTimeOffset UpdatedAt);

public sealed record JobEventDto(Guid Id, DateTimeOffset CreatedAt, JobEventLevel Level, JobStatus? FromStatus, JobStatus? ToStatus, string Message, JsonElement? Data);

public sealed record JobResultDto(int? CandidatesFound, int? ClipsRendered, decimal? ProcessedSeconds, JsonElement? Signals, WebcamRegion? WebcamRegion, string? Language);

public sealed record JobDto(
    Guid Id,
    Guid ProjectId,
    JobKind Kind,
    JobStatus Status,
    int Progress,
    string? StatusMessage,
    int Attempt,
    int MaxAttempts,
    string? ErrorCode,
    string? ErrorMessage,
    bool CancelRequested,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    JobResultDto? Result,
    IReadOnlyList<JobEventDto>? Events);

public sealed record RenderDto(
    Guid Id,
    int Version,
    string AspectRatio,
    int? Width,
    int? Height,
    long? SizeBytes,
    decimal? DurationSeconds,
    bool HasSubtitles,
    RenderStatus Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? StreamUrl,
    string? DownloadUrl);

public sealed record ClipSummaryDto(
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
    RenderDto? CurrentRender);

public static class DtoMapper
{
    public static UserDto ToDto(this AppUser user)
        => new(user.Id, user.Email ?? string.Empty, user.DisplayName, user.EmailConfirmed, user.CreatedAt);

    public static AssetDto ToDto(this MediaAsset a)
        => new(a.Id, a.Kind, a.OriginalFileName, a.ContentType, a.SizeBytes, a.Status, a.DurationSeconds, a.Width, a.Height, a.Fps,
            a.Container, a.VideoCodec, a.AudioCodec, a.HasAudio, a.CreatedAt, a.UploadedAt, a.ExpiresAt);

    public static JobSummaryDto ToSummary(this ProcessingJob j, string? projectTitle = null)
        => new(j.Id, j.ProjectId, projectTitle, j.Kind, j.Status, j.Progress, j.StatusMessage, j.UpdatedAt);

    public static JobEventDto ToDto(this ProcessingJobEvent e)
        => new(e.Id, e.CreatedAt, e.Level, e.FromStatus, e.ToStatus, e.Message, JsonDefaults.ParseElement(e.Data));

    public static JobResultDto? ToDto(this JobResult? r)
        => r is null ? null : new JobResultDto(r.CandidatesFound, r.ClipsRendered, r.ProcessedSeconds, r.Signals, r.WebcamRegion, r.Language);

    public static JobDto ToDto(this ProcessingJob j, IEnumerable<ProcessingJobEvent>? events = null)
        => new(j.Id, j.ProjectId, j.Kind, j.Status, j.Progress, j.StatusMessage, j.Attempt, j.MaxAttempts, j.ErrorCode, j.ErrorMessage,
            j.CancelRequested, j.QueuedAt, j.StartedAt, j.FinishedAt, j.CreatedAt, j.UpdatedAt, j.Result.ToDto(),
            events?.Select(e => e.ToDto()).ToList());

    public static RenderDto ToDto(this ClipRender r, string? streamUrl = null, string? downloadUrl = null)
        => new(r.Id, r.Version, r.AspectRatio, r.Width, r.Height, r.SizeBytes, r.DurationSeconds, r.HasSubtitles, r.Status, r.Error,
            r.CreatedAt, r.CompletedAt, streamUrl, downloadUrl);

    public static ClipSummaryDto ToSummary(this Clip c, string projectTitle, string? thumbnailUrl, ClipRender? currentRender)
        => new(c.Id, c.ProjectId, projectTitle, c.Title, c.Category, c.Score, c.Status, c.AspectRatio, c.EndSeconds - c.StartSeconds,
            c.StartSeconds, c.EndSeconds, thumbnailUrl, c.CreatedAt, c.UpdatedAt, currentRender?.ToDto());
}
