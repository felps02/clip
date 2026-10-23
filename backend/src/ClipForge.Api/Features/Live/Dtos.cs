using System.Text.Json;
using ClipForge.Api.Services;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;
using FluentValidation;

namespace ClipForge.Api.Features.Live;

// ---------------------------------------------------------------- requests

public sealed record CreateDeviceTokenRequest(string Name);

public sealed record ObsInfoInput(string? Version, string? RecordingPath);

/// <summary><c>settings</c> is a partial AnalysisSettings: the given fields over the profile's editing preferences.</summary>
public sealed record CreateLiveSessionRequest(string? Title, int? SegmentSeconds, JsonElement? Settings, ObsInfoInput? Obs);

public sealed record CompanionStateInput(bool ObsConnected, bool Streaming, bool Recording, bool Uploading, int QueueLength, string? LastError);

public sealed record LiveHeartbeatRequest(CompanionStateInput? State);

public sealed record EndLiveSessionRequest(int? LastSequence);

// ---------------------------------------------------------------- responses

public sealed record CompanionDeviceDto(Guid Id, string Name, string TokenPrefix, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, DateTimeOffset? RevokedAt, bool Online);

public sealed record CreateDeviceTokenResponse(CompanionDeviceDto Device, string Token);

public sealed record CompanionDevicesResponse(IReadOnlyList<CompanionDeviceDto> Items);

public sealed record CompanionMeDevice(Guid Id, string Name);

public sealed record CompanionMeUser(string DisplayName);

public sealed record CompanionMePlan(string Code, string Name);

public sealed record CompanionMeResponse(CompanionMeDevice Device, CompanionMeUser User, CompanionMePlan Plan);

public sealed record LiveCompanionStateDto(bool ObsConnected, bool Streaming, bool Recording, bool Uploading, int QueueLength, string? LastError);

public sealed record LiveSessionStatsDto(int SegmentsReceived, int SegmentsAnalyzed, int SegmentsFailed, int Highlights, decimal RecordedSeconds);

public sealed record LiveSessionDto(
    Guid Id,
    Guid ProjectId,
    string Title,
    LiveSessionStatus Status,
    Guid DeviceId,
    string? DeviceName,
    int SegmentSeconds,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset? LastSegmentAt,
    DateTimeOffset? LastHeartbeatAt,
    bool Online,
    bool StopRequested,
    string? EndReason,
    LiveCompanionStateDto CompanionState,
    LiveSessionStatsDto Stats);

public sealed record LiveSegmentDto(Guid Id, int Sequence, decimal StartOffsetSeconds, decimal DurationSeconds, LiveSegmentStatus Status, Guid? JobId, string? Error, DateTimeOffset CreatedAt);

public sealed record LiveSessionResponse(LiveSessionDto Session);

public sealed record CreateLiveSessionResponse(LiveSessionDto Session, AnalysisSettings Settings);

public sealed record LiveSessionDetailResponse(LiveSessionDto Session, AnalysisSettings Settings, IReadOnlyList<LiveSegmentDto> Segments, IReadOnlyList<ClipSummaryDto> Highlights, int CandidateCount);

public sealed record LiveHeartbeatResponse(LiveSessionStatus Status, bool StopRequested, int NextSequence);

public sealed record SegmentUploadResponse(LiveSegmentDto Segment, bool Duplicate);

public static class LiveMapping
{
    public static CompanionDeviceDto ToDto(this CompanionDevice d, DateTimeOffset now)
        => new(d.Id, d.Name, d.TokenPrefix, d.CreatedAt, d.LastSeenAt, d.RevokedAt,
            d.RevokedAt is null && d.LastSeenAt is { } seen && now - seen <= LiveService.DeviceOnlineWindow);

    public static LiveSegmentDto ToDto(this LiveSegment s)
        => new(s.Id, s.Sequence, s.StartOffsetSeconds, s.DurationSeconds, s.Status, s.JobId, s.Error, s.CreatedAt);

    public static LiveCompanionStateDto ToDto(this LiveCompanionState? s)
        => s is null ? new LiveCompanionStateDto(false, false, false, false, 0, null)
            : new LiveCompanionStateDto(s.ObsConnected, s.Streaming, s.Recording, s.Uploading, s.QueueLength, s.LastError);
}

// ---------------------------------------------------------------- validators

public sealed class CreateDeviceTokenRequestValidator : AbstractValidator<CreateDeviceTokenRequest>
{
    public CreateDeviceTokenRequestValidator()
    {
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 1 and <= 60)
            .WithMessage("Informe um nome de 1 a 60 caracteres para o dispositivo.");
    }
}

public sealed class CreateLiveSessionRequestValidator : AbstractValidator<CreateLiveSessionRequest>
{
    public CreateLiveSessionRequestValidator()
    {
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.SegmentSeconds).InclusiveBetween(LiveService.MinSegmentSeconds, LiveService.MaxSegmentSeconds)
            .When(x => x.SegmentSeconds.HasValue)
            .WithMessage($"segmentSeconds deve estar entre {LiveService.MinSegmentSeconds} e {LiveService.MaxSegmentSeconds}.");
        RuleFor(x => x.Settings).Must(s => s is null || s.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null)
            .WithMessage("settings deve ser um objeto.");
        RuleFor(x => x.Obs!.Version).MaximumLength(100).When(x => x.Obs is not null);
        RuleFor(x => x.Obs!.RecordingPath).MaximumLength(1000).When(x => x.Obs is not null);
    }
}

public sealed class LiveHeartbeatRequestValidator : AbstractValidator<LiveHeartbeatRequest>
{
    public LiveHeartbeatRequestValidator()
    {
        RuleFor(x => x.State).NotNull().WithMessage("Informe o estado do companion (state).");
        RuleFor(x => x.State!.QueueLength).GreaterThanOrEqualTo(0).When(x => x.State is not null);
    }
}

public sealed class EndLiveSessionRequestValidator : AbstractValidator<EndLiveSessionRequest>
{
    public EndLiveSessionRequestValidator()
    {
        RuleFor(x => x.LastSequence).GreaterThanOrEqualTo(0).When(x => x.LastSequence.HasValue);
    }
}
