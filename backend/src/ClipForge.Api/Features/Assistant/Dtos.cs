using System.Text.Json.Serialization;
using ClipForge.Domain.ValueObjects;
using FluentValidation;

namespace ClipForge.Api.Features.Assistant;

/// <summary>One message of the conversation: <c>role</c> user | assistant.</summary>
public sealed record AssistantChatMessage(string? Role, string? Content);

/// <summary>What the web knows about the chosen file (optional).</summary>
public sealed record AssistantChatContext(string? FileName, decimal? DurationSeconds);

/// <summary>
/// <c>POST /api/assistant/chat</c>: 1-20 messages (each at most 2000 characters, the last one from the user), the settings
/// currently in the form (partial: missing fields take their defaults) and optional file context.
/// </summary>
public sealed record AssistantChatRequest(List<AssistantChatMessage>? Messages, AnalysisSettings? CurrentSettings, AssistantChatContext? Context);

/// <summary>One validated change: <c>field</c> is the settings field (dotted for nested ones), <c>label</c> its pt-BR text.</summary>
public sealed record AssistantChangeDto(string Field, string Label, object? Value);

public sealed record AssistantChatResponse(string Reply, AssistantSettingsPatch SettingsPatch, IReadOnlyList<AssistantChangeDto> Changes, string Provider, string? Model);

/// <summary><c>GET /api/assistant/status</c> (never carries keys or server paths).</summary>
public sealed record AssistantStatusDto(bool Available, string Provider, string? Model, string? Reason);

/// <summary>
/// Validated partial <see cref="AnalysisSettings"/>: only the fields that change are present; nested objects are partial
/// (merge them into the current <c>subtitles</c> / <c>framing</c>). The three clip lengths always travel together.
/// </summary>
public sealed class AssistantSettingsPatch
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? FocusCategories { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? CustomKeywords { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MinClipSeconds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxClipSeconds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TargetClipSeconds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Sensitivity { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxCandidates { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AutoRenderTop { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AspectRatio { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public AssistantSubtitlesPatch? Subtitles { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Language { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public AssistantFramingPatch? Framing { get; set; }

    [JsonIgnore]
    public bool IsEmpty => FocusCategories is null && CustomKeywords is null && MinClipSeconds is null && MaxClipSeconds is null
        && TargetClipSeconds is null && Sensitivity is null && MaxCandidates is null && AutoRenderTop is null && AspectRatio is null
        && Subtitles is null && Language is null && Framing is null;
}

public sealed class AssistantSubtitlesPatch
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Enabled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Style { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Position { get; set; }
}

public sealed class AssistantFramingPatch
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; set; }
}

public sealed class AssistantChatRequestValidator : AbstractValidator<AssistantChatRequest>
{
    public const int MaxMessages = 20;
    public const int MaxMessageLength = 2000;

    public AssistantChatRequestValidator()
    {
        RuleFor(x => x.Messages).NotNull().WithMessage("Informe as mensagens da conversa.")
            .Must(m => m is null || m.Count is >= 1 and <= MaxMessages).WithMessage($"Envie de 1 a {MaxMessages} mensagens.");
        RuleForEach(x => x.Messages).Cascade(CascadeMode.Stop).NotNull().WithMessage("Mensagem inválida.").ChildRules(message =>
        {
            message.RuleFor(m => m.Role).Must(r => r is "user" or "assistant").WithMessage("Papel inválido (user ou assistant).");
            message.RuleFor(m => m.Content).Must(c => !string.IsNullOrWhiteSpace(c)).WithMessage("A mensagem não pode ser vazia.")
                .MaximumLength(MaxMessageLength).WithMessage($"Cada mensagem pode ter no máximo {MaxMessageLength} caracteres.");
        }).When(x => x.Messages is { Count: <= MaxMessages });
        RuleFor(x => x.Messages).Must(m => m![^1]?.Role == "user").WithMessage("A última mensagem deve ser do usuário.")
            .When(x => x.Messages is { Count: >= 1 and <= MaxMessages });
        RuleFor(x => x.Context!.FileName).MaximumLength(255).When(x => x.Context is not null);
        RuleFor(x => x.Context!.DurationSeconds).InclusiveBetween(0, 7 * 24 * 3600).When(x => x.Context?.DurationSeconds is not null);
    }
}
