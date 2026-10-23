using System.Text.Json;
using ClipForge.Api.Services;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;
using FluentValidation;

namespace ClipForge.Api.Features.Projects;

public sealed record SourceInput(string Kind, Guid? MediaAssetId, string? Url);

public sealed record CreateProjectRequest(string? Title, SourceInput Source, AnalysisSettings? Settings);

public sealed record UpdateProjectRequest(string Title);

public sealed record CreateJobRequest(AnalysisSettings? Settings);

public sealed record CreateClipFromCandidateRequest(
    string? Title,
    string? AspectRatio,
    SubtitleSettings? Subtitles,
    FramingSettings? Framing,
    decimal? StartSeconds,
    decimal? EndSeconds);

public sealed record ProjectDto(
    Guid Id,
    string Title,
    SourceKind SourceKind,
    SourcePlatform SourcePlatform,
    string? SourceUrl,
    ProjectStatus Status,
    AnalysisSettings Settings,
    Guid? MediaAssetId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProjectSummaryDto(
    Guid Id,
    string Title,
    SourceKind SourceKind,
    SourcePlatform SourcePlatform,
    ProjectStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    decimal? DurationSeconds,
    JobSummaryDto? LatestJob,
    int CandidateCount,
    int ClipCount,
    string? ThumbnailUrl);

public sealed record ProjectSourceDto(AssetDto? Asset, bool Previewable, string? PreviewUrl);

public sealed record ProjectDetailResponse(ProjectDto Project, IReadOnlyList<JobSummaryDto> Jobs, int CandidateCount, int ClipCount, ProjectSourceDto Source);

public sealed record CreateProjectResponse(ProjectDto Project, JobDto Job);

public sealed record ProjectResponse(ProjectDto Project);

public sealed record JobResponse(JobDto Job);

public sealed record JobEventsResponse(IReadOnlyList<JobEventDto> Items);

public sealed record CandidateDto(
    Guid Id,
    int Rank,
    decimal StartSeconds,
    decimal EndSeconds,
    decimal DurationSeconds,
    ClipCategory Category,
    int Score,
    string? Reasoning,
    string? Transcript,
    JsonElement? Signals,
    CandidateStatus Status,
    Guid? ClipId,
    string? PreviewUrl,
    bool Previewable);

public sealed record SignalAvailabilityDto(bool Available);

public sealed record CandidatesSignalsDto(SignalAvailabilityDto Transcript, SignalAvailabilityDto Audio, SignalAvailabilityDto Visual);

public sealed record CandidatesResponse(IReadOnlyList<CandidateDto> Items, CandidatesSignalsDto Signals);

public sealed record ClipJobResponse(ClipSummaryDto Clip, JobDto Job);

public static class ProjectMapping
{
    public static ProjectDto ToDto(this Project p)
        => new(p.Id, p.Title, p.SourceKind, p.SourcePlatform, p.SourceUrl, p.Status, p.Settings, p.MediaAssetId, p.CreatedAt, p.UpdatedAt);
}

public sealed class SubtitleSettingsValidator : AbstractValidator<SubtitleSettings>
{
    public SubtitleSettingsValidator()
    {
        RuleFor(x => x.Style).Must(s => Array.IndexOf(SubtitleStyles.All, s) >= 0).WithMessage("Estilo de legenda inválido (bold, clean ou boxed).");
        RuleFor(x => x.Position).Must(s => Array.IndexOf(SubtitleStyles.Positions, s) >= 0).WithMessage("Posição de legenda inválida (bottom, center ou top).");
    }
}

public sealed class FramingSettingsValidator : AbstractValidator<FramingSettings>
{
    public FramingSettingsValidator()
    {
        RuleFor(x => x.Mode).Must(m => Array.IndexOf(FramingModes.All, m) >= 0).WithMessage("Modo de enquadramento inválido (auto, center ou webcam-split).");
        RuleFor(x => x.FocusX).InclusiveBetween(0, 1).When(x => x.FocusX.HasValue);
        RuleFor(x => x.FocusY).InclusiveBetween(0, 1).When(x => x.FocusY.HasValue);
        RuleFor(x => x.WebcamRegion!).Must(r => r.IsValid()).WithMessage("Região da webcam inválida.").When(x => x.WebcamRegion is not null);
    }
}

public sealed class AnalysisSettingsValidator : AbstractValidator<AnalysisSettings>
{
    public AnalysisSettingsValidator()
    {
        RuleFor(x => x.MinClipSeconds).InclusiveBetween(AnalysisSettings.ClipSecondsMin, AnalysisSettings.ClipSecondsMax);
        RuleFor(x => x.MaxClipSeconds).InclusiveBetween(AnalysisSettings.ClipSecondsMin, AnalysisSettings.ClipSecondsMax).GreaterThanOrEqualTo(x => x.MinClipSeconds).WithMessage("maxClipSeconds deve ser maior ou igual a minClipSeconds.");
        RuleFor(x => x.TargetClipSeconds).InclusiveBetween(AnalysisSettings.ClipSecondsMin, AnalysisSettings.ClipSecondsMax);
        RuleFor(x => x.Sensitivity).InclusiveBetween(0, 100);
        RuleFor(x => x.MaxCandidates).InclusiveBetween(1, 100);
        RuleFor(x => x.AutoRenderTop).InclusiveBetween(0, 50).When(x => x.AutoRenderTop.HasValue);
        RuleFor(x => x.AspectRatio).Must(AspectRatios.IsValid).WithMessage("Formato inválido (9:16, 16:9 ou 1:1).");
        RuleFor(x => x.Language).NotEmpty().Must(PlanLimitHelpers.IsLanguageCode).WithMessage("Idioma inválido.");
        RuleFor(x => x.Subtitles).NotNull().SetValidator(new SubtitleSettingsValidator());
        RuleFor(x => x.Framing).NotNull().SetValidator(new FramingSettingsValidator());
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.FocusCategories).Must(list => list is null || list.Count <= 50)
            .WithMessage("Categorias de foco demais.");
        RuleForEach(x => x.FocusCategories).Must(c => MomentCategories.IsValid(c?.Trim().ToLowerInvariant()))
            .WithMessage("Categoria de foco inválida (highlight, reaction, funny, hype, tension, gameplay ou quote).");
        // At most 20 distinct keywords (case-insensitive, after trimming): repeated spellings are merged, not counted.
        RuleFor(x => x.CustomKeywords).Must(list => list is null || (list.Count <= 100
                && list.Select(AnalysisSettings.CleanKeyword).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= AnalysisSettings.MaxCustomKeywords))
            .WithMessage($"Informe no máximo {AnalysisSettings.MaxCustomKeywords} palavras-chave.");
        RuleForEach(x => x.CustomKeywords).Must(k => AnalysisSettings.CleanKeyword(k).Length is >= 1 and <= AnalysisSettings.CustomKeywordMaxLength)
            .WithMessage($"Cada palavra-chave deve ter de 1 a {AnalysisSettings.CustomKeywordMaxLength} caracteres.");
        RuleFor(x => x.Instructions).Must(i => i is null || i.Trim().Length <= AnalysisSettings.InstructionsMaxLength)
            .WithMessage($"As instruções devem ter no máximo {AnalysisSettings.InstructionsMaxLength} caracteres.");
    }
}

public sealed class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.Source).NotNull().WithMessage("Informe a origem do projeto.");
        When(x => x.Source is not null, () =>
        {
            RuleFor(x => x.Source.Kind).Must(k => k is "upload" or "link" or "live").WithMessage("Tipo de origem inválido (upload ou link).");
            RuleFor(x => x.Source.MediaAssetId).NotNull().WithMessage("Informe o arquivo enviado (mediaAssetId).").When(x => x.Source.Kind == "upload");
            RuleFor(x => x.Source.Url).NotEmpty().WithMessage("Informe o link.").MaximumLength(2048).When(x => x.Source.Kind == "link");
        });
        RuleFor(x => x.Settings!).SetValidator(new AnalysisSettingsValidator()).When(x => x.Settings is not null);
    }
}

public sealed class UpdateProjectRequestValidator : AbstractValidator<UpdateProjectRequest>
{
    public UpdateProjectRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().WithMessage("Informe o título.").MaximumLength(200);
    }
}

public sealed class CreateJobRequestValidator : AbstractValidator<CreateJobRequest>
{
    public CreateJobRequestValidator()
    {
        RuleFor(x => x.Settings!).SetValidator(new AnalysisSettingsValidator()).When(x => x.Settings is not null);
    }
}

public sealed class CreateClipFromCandidateRequestValidator : AbstractValidator<CreateClipFromCandidateRequest>
{
    public CreateClipFromCandidateRequestValidator()
    {
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.AspectRatio).Must(AspectRatios.IsValid).WithMessage("Formato inválido (9:16, 16:9 ou 1:1).").When(x => x.AspectRatio is not null);
        RuleFor(x => x.Subtitles!).SetValidator(new SubtitleSettingsValidator()).When(x => x.Subtitles is not null);
        RuleFor(x => x.Framing!).SetValidator(new FramingSettingsValidator()).When(x => x.Framing is not null);
        RuleFor(x => x.StartSeconds).GreaterThanOrEqualTo(0).When(x => x.StartSeconds.HasValue);
        RuleFor(x => x.EndSeconds).GreaterThan(0).When(x => x.EndSeconds.HasValue);
        RuleFor(x => x).Must(x => !x.StartSeconds.HasValue || !x.EndSeconds.HasValue || x.EndSeconds > x.StartSeconds)
            .WithMessage("endSeconds deve ser maior que startSeconds.").OverridePropertyName("endSeconds");
    }
}
