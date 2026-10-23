using System.Text.Json;
using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;
using FluentValidation;

namespace ClipForge.Api.Features.Internal;

// ---------------------------------------------------------------- requests

public sealed record ClaimRequest(string[]? Kinds, JsonElement? Capabilities);

public sealed record HeartbeatRequest(int? Progress, string? Message, string? WorkerId);

public sealed record ProbeRequest(
    decimal DurationSeconds,
    int? Width,
    int? Height,
    double? Fps,
    bool HasAudio,
    bool HasVideo,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    long? SizeBytes,
    string? WorkerId);

public sealed record StatusRequest(string Status, int? Progress, string? Message, string? WorkerId);

public sealed record EventRequest(string? Level, string Message, JsonElement? Data);

public sealed record UploadFileRequest(string Kind, string FileName, string? ContentType, long? SizeBytes);

public sealed record UploadUrlsRequest(List<UploadFileRequest> Files);

public sealed record AnalysisSummaryInput(JsonElement? Signals, WebcamRegion? WebcamRegion, string? Language, decimal? ProcessedSeconds, int? CandidatesFound, int? ClipsRendered);

public sealed record AnalysisRequest(string? TranscriptKey, string? SignalsKey, AnalysisSummaryInput? Summary);

public sealed record CandidateInput(int Rank, decimal StartSeconds, decimal EndSeconds, int Score, string Category, string? Reasoning, string? Transcript, JsonElement? Signals);

public sealed record CandidatesRequest(List<CandidateInput> Candidates);

public sealed record RenderInput(string StorageKey, int? Width, int? Height, decimal? DurationSeconds, long? SizeBytes, bool HasSubtitles);

public sealed record ClipInput(
    Guid CandidateId,
    string? Title,
    string? AspectRatio,
    decimal? StartSeconds,
    decimal? EndSeconds,
    SubtitleSettings? Subtitles,
    FramingSettings? Framing,
    RenderInput Render,
    string? ThumbnailKey);

public sealed record ClipsRequest(List<ClipInput> Clips);

public sealed record RenderResultRequest(
    Guid RenderId,
    string Status,
    string? StorageKey,
    int? Width,
    int? Height,
    decimal? DurationSeconds,
    long? SizeBytes,
    bool? HasSubtitles,
    string? ThumbnailKey,
    string? Error,
    string? WorkerId);

public sealed record CompleteRequest(decimal? ProcessedSeconds, AnalysisSummaryInput? Summary, string? WorkerId);

public sealed record FailRequest(string? Code, string? Message, bool Retryable, decimal? ProcessedSeconds, string? WorkerId);

// ---------------------------------------------------------------- responses

public sealed record HeartbeatResponse(bool CancelRequested, DateTimeOffset LeaseExpiresAt);

public sealed record ProbeResponse(bool Ok, long RemainingProcessingSeconds);

public sealed record UploadUrlDto(string FileName, string Key, string Url, string Method, IReadOnlyDictionary<string, string> Headers);

public sealed record UploadUrlsResponse(IReadOnlyList<UploadUrlDto> Files);

public sealed record CandidatesResponse(IReadOnlyList<Guid> Ids);

public sealed record ClipCreatedDto(Guid CandidateId, Guid ClipId, Guid RenderId);

public sealed record ClipsResponse(IReadOnlyList<ClipCreatedDto> Clips);

public sealed record JobPayloadSource(Guid AssetId, string DownloadUrl, string FileName, string ContentType, long SizeBytes, decimal? DurationSeconds);

public sealed record AnalyzePayloadLimits(int MaxRenderHeight, long MaxSourceDurationSeconds, long RemainingProcessingSeconds, string TranscriptionModel, bool AllowWebcamLayout);

public sealed record AnalyzeJobPayload(
    Guid JobId,
    JobKind Kind,
    int Attempt,
    Guid UserId,
    Guid ProjectId,
    int LeaseSeconds,
    JobPayloadSource Source,
    AnalysisSettings Settings,
    AnalyzePayloadLimits Limits);

public sealed record RenderClipInfo(Guid ClipId, Guid RenderId, int Version, string Title, decimal StartSeconds, decimal EndSeconds, string AspectRatio, SubtitleSettings Subtitles, FramingSettings Framing);

public sealed record RenderAnalysisInfo(string? TranscriptUrl, WebcamRegion? WebcamRegion);

public sealed record RenderPayloadLimits(int MaxRenderHeight, bool AllowWebcamLayout);

public sealed record RenderJobPayload(
    Guid JobId,
    JobKind Kind,
    int Attempt,
    Guid UserId,
    Guid ProjectId,
    int LeaseSeconds,
    JobPayloadSource Source,
    RenderClipInfo Clip,
    RenderAnalysisInfo Analysis,
    RenderPayloadLimits Limits);

// ---------------------------------------------------------------- validators

public sealed class ClaimRequestValidator : AbstractValidator<ClaimRequest>
{
    public ClaimRequestValidator()
    {
        RuleForEach(x => x.Kinds).Must(k => Domain.Common.EnumNames.TryParse<JobKind>(k, out _)).WithMessage("Tipo de job inválido.");
    }
}

public sealed class HeartbeatRequestValidator : AbstractValidator<HeartbeatRequest>
{
    public HeartbeatRequestValidator()
    {
        RuleFor(x => x.Progress).InclusiveBetween(0, 100).When(x => x.Progress.HasValue);
        RuleFor(x => x.Message).MaximumLength(500);
    }
}

public sealed class ProbeRequestValidator : AbstractValidator<ProbeRequest>
{
    public ProbeRequestValidator()
    {
        // durationSeconds <= 0 is not a validation error: the API fails the job with invalid_media (409) like the other probe rejections.
        RuleFor(x => x.Width).GreaterThan(0).When(x => x.Width.HasValue);
        RuleFor(x => x.Height).GreaterThan(0).When(x => x.Height.HasValue);
        RuleFor(x => x.Fps).GreaterThan(0).When(x => x.Fps.HasValue);
        RuleFor(x => x.Container).MaximumLength(32);
        RuleFor(x => x.VideoCodec).MaximumLength(64);
        RuleFor(x => x.AudioCodec).MaximumLength(64);
    }
}

public sealed class StatusRequestValidator : AbstractValidator<StatusRequest>
{
    public StatusRequestValidator()
    {
        RuleFor(x => x.Status).NotEmpty().Must(s => Domain.Common.EnumNames.TryParse<JobStatus>(s, out _)).WithMessage("Status inválido.");
        RuleFor(x => x.Progress).InclusiveBetween(0, 100).When(x => x.Progress.HasValue);
        RuleFor(x => x.Message).MaximumLength(500);
    }
}

public sealed class EventRequestValidator : AbstractValidator<EventRequest>
{
    public EventRequestValidator()
    {
        RuleFor(x => x.Message).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.Level).Must(l => Domain.Common.EnumNames.TryParse<JobEventLevel>(l, out _)).WithMessage("Nível inválido.").When(x => x.Level is not null);
    }
}

public sealed class UploadUrlsRequestValidator : AbstractValidator<UploadUrlsRequest>
{
    public static readonly string[] Kinds = ["clip_render", "thumbnail", "analysis", "preview"];

    public UploadUrlsRequestValidator()
    {
        RuleFor(x => x.Files).NotEmpty().WithMessage("Informe os arquivos.");
        RuleFor(x => x.Files.Count).LessThanOrEqualTo(200);
        RuleForEach(x => x.Files).ChildRules(f =>
        {
            f.RuleFor(x => x.Kind).Must(k => Array.IndexOf(Kinds, k) >= 0).WithMessage("Tipo de arquivo inválido (clip_render, thumbnail, analysis ou preview).");
            f.RuleFor(x => x.FileName).NotEmpty().MaximumLength(200);
            f.RuleFor(x => x.SizeBytes).GreaterThan(0).When(x => x.SizeBytes.HasValue);
        });
    }
}

public sealed class CandidatesRequestValidator : AbstractValidator<CandidatesRequest>
{
    public CandidatesRequestValidator()
    {
        RuleFor(x => x.Candidates).NotNull();
        RuleFor(x => x.Candidates.Count).LessThanOrEqualTo(500);
        RuleForEach(x => x.Candidates).ChildRules(c =>
        {
            c.RuleFor(x => x.StartSeconds).GreaterThanOrEqualTo(0);
            c.RuleFor(x => x.EndSeconds).GreaterThan(x => x.StartSeconds).WithMessage("endSeconds deve ser maior que startSeconds.");
            c.RuleFor(x => x.Score).InclusiveBetween(0, 100);
            c.RuleFor(x => x.Category).Must(cat => Domain.Common.EnumNames.TryParse<ClipCategory>(cat, out _)).WithMessage("Categoria inválida.");
            c.RuleFor(x => x.Reasoning).MaximumLength(2000);
        });
    }
}

public sealed class ClipsRequestValidator : AbstractValidator<ClipsRequest>
{
    public ClipsRequestValidator()
    {
        RuleFor(x => x.Clips).NotEmpty();
        RuleForEach(x => x.Clips).ChildRules(c =>
        {
            c.RuleFor(x => x.CandidateId).NotEmpty();
            c.RuleFor(x => x.Title).MaximumLength(200);
            c.RuleFor(x => x.AspectRatio).Must(AspectRatios.IsValid).WithMessage("Formato inválido.").When(x => x.AspectRatio is not null);
            c.RuleFor(x => x.Render).NotNull();
            c.RuleFor(x => x.Render.StorageKey).NotEmpty().When(x => x.Render is not null);
        });
    }
}

public sealed class RenderResultRequestValidator : AbstractValidator<RenderResultRequest>
{
    public RenderResultRequestValidator()
    {
        RuleFor(x => x.RenderId).NotEmpty();
        RuleFor(x => x.Status).Must(s => s is "ready" or "failed").WithMessage("Status deve ser ready ou failed.");
        RuleFor(x => x.StorageKey).NotEmpty().When(x => x.Status == "ready").WithMessage("storageKey é obrigatório quando status=ready.");
        RuleFor(x => x.Error).MaximumLength(2000);
    }
}

public sealed class CompleteRequestValidator : AbstractValidator<CompleteRequest>
{
    public CompleteRequestValidator()
    {
        RuleFor(x => x.ProcessedSeconds).GreaterThanOrEqualTo(0).When(x => x.ProcessedSeconds.HasValue);
    }
}

public sealed class FailRequestValidator : AbstractValidator<FailRequest>
{
    public FailRequestValidator()
    {
        RuleFor(x => x.Code).MaximumLength(80);
        RuleFor(x => x.Message).MaximumLength(2000);
    }
}
