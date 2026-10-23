using ClipForge.Api.Common;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Media;
using FluentValidation;

namespace ClipForge.Api.Features.Sources;

public sealed record ValidateSourceRequest(string Url);

public sealed record SourceAlternativeDto(string Kind, string Title, string Description);

public sealed record SourceMetadataDto(string? Title, decimal? DurationSeconds, string? ThumbnailUrl);

public sealed record ValidateSourceResponse(string Platform, bool Supported, string Reason, IReadOnlyList<SourceAlternativeDto> Alternatives, SourceMetadataDto? Metadata);

public sealed class ValidateSourceRequestValidator : AbstractValidator<ValidateSourceRequest>
{
    public ValidateSourceRequestValidator()
    {
        RuleFor(x => x.Url).NotEmpty().WithMessage("Informe o link.").MaximumLength(2048);
    }
}

/// <summary>
/// Platform integration abstraction (ADR-008). Direct processing is only possible when an authorised integration
/// exists; metadata lookups require an official API key and are optional.
/// </summary>
public interface IMediaProvider
{
    DetectedPlatform Platform { get; }

    /// <summary>True when the provider can download/process the VOD for the user (never in the MVP).</summary>
    bool SupportsProcessing { get; }

    /// <summary>Public metadata through the official API, or null when not configured / not available.</summary>
    Task<SourceMetadataDto?> GetMetadataAsync(string url, CancellationToken ct);
}

/// <summary>Default providers: identify the platform, never scrape, no metadata without credentials.</summary>
public sealed class UnsupportedMediaProvider : IMediaProvider
{
    public UnsupportedMediaProvider(DetectedPlatform platform)
    {
        Platform = platform;
    }

    public DetectedPlatform Platform { get; }
    public bool SupportsProcessing => false;
    public Task<SourceMetadataDto?> GetMetadataAsync(string url, CancellationToken ct) => Task.FromResult<SourceMetadataDto?>(null);
}

public sealed class SourceValidationService
{
    private readonly IReadOnlyDictionary<DetectedPlatform, IMediaProvider> _providers;

    public SourceValidationService(IEnumerable<IMediaProvider> providers)
    {
        _providers = providers.GroupBy(p => p.Platform).ToDictionary(g => g.Key, g => g.Last());
    }

    public static readonly IReadOnlyList<SourceAlternativeDto> Alternatives =
    [
        new("upload", "Enviar a gravação", "Baixe o VOD pelo painel da plataforma (ou use sua gravação local do OBS) e envie o arquivo aqui."),
        new("obs_companion", "Companion do OBS", "Conecte o companion ao OBS para capturar os melhores momentos da live em tempo real."),
    ];

    public async Task<ValidateSourceResponse> ValidateAsync(string url, CancellationToken ct)
    {
        var platform = PlatformDetector.Detect(url);
        var platformName = platform.ToString().ToLowerInvariant();

        if (platform == DetectedPlatform.Unknown)
        {
            return new ValidateSourceResponse(platformName, false,
                "Link não reconhecido. No momento só identificamos links da Twitch, do YouTube e do Kick, e nenhuma dessas plataformas permite baixar VODs de terceiros.",
                Alternatives, null);
        }

        _providers.TryGetValue(platform, out var provider);
        var supported = provider?.SupportsProcessing ?? false;
        SourceMetadataDto? metadata = null;
        if (provider is not null)
        {
            try
            {
                metadata = await provider.GetMetadataAsync(url, ct);
            }
            catch (Exception)
            {
                metadata = null;
            }
        }

        var display = platform switch
        {
            DetectedPlatform.Twitch => "Twitch",
            DetectedPlatform.Youtube => "YouTube",
            DetectedPlatform.Kick => "Kick",
            _ => platformName,
        };
        var reason = supported
            ? "Plataforma suportada."
            : $"Links do {display} ainda não podem ser processados diretamente: a plataforma não permite o download de VODs por terceiros e não fazemos scraping.";

        return new ValidateSourceResponse(platformName, supported, reason, supported ? [] : Alternatives, metadata);
    }
}

public static class SourcesEndpoints
{
    public static IEndpointRouteBuilder MapSourcesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sources").WithTags("Sources").WithValidation().RequireAuthorization();

        group.MapPost("/validate", async (ValidateSourceRequest request, SourceValidationService service, CancellationToken ct)
            => Results.Ok(await service.ValidateAsync(request.Url, ct)))
            .Produces<ValidateSourceResponse>();

        return app;
    }
}
