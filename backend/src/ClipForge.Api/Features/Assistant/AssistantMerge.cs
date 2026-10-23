using System.Text.Json;
using System.Text.RegularExpressions;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Features.Assistant;

/// <summary>Structured output of the model: <c>{ intent, patch, unsupported, question }</c> (the model never writes the reply).</summary>
public sealed class AssistantModelOutput
{
    public string Intent { get; set; } = "other";
    public AssistantModelPatch Patch { get; set; } = new();
    public List<string> Unsupported { get; set; } = [];
    public string? Question { get; set; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Parses the model's JSON leniently: text around the object is ignored and a field of the wrong type only loses that
    /// field. Unparseable output yields an empty result (the rules still apply).
    /// </summary>
    public static AssistantModelOutput Parse(string? content)
    {
        var output = new AssistantModelOutput();
        if (string.IsNullOrWhiteSpace(content)) return output;
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return output;
        try
        {
            using var document = JsonDocument.Parse(content[start..(end + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return output;
            if (TryGet(root, "intent", JsonValueKind.String) is { } intent) output.Intent = intent.GetString()!.Trim().ToLowerInvariant();
            if (TryGet(root, "question", JsonValueKind.String) is { } question) output.Question = question.GetString();
            if (TryGet(root, "unsupported", JsonValueKind.Array) is { } unsupported)
            {
                output.Unsupported = unsupported.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim().ToLowerInvariant()).Distinct().ToList();
            }
            if (TryGet(root, "patch", JsonValueKind.Object) is { } patch) output.Patch = AssistantModelPatch.From(patch);
        }
        catch (JsonException)
        {
        }
        return output;
    }

    private static JsonElement? TryGet(JsonElement element, string name, JsonValueKind kind)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == kind) return property.Value;
        }
        return null;
    }
}

/// <summary>The model's flat patch (only the fields it believes the user asked to change).</summary>
public sealed class AssistantModelPatch
{
    public List<string>? FocusCategories { get; set; }
    public List<string>? CustomKeywords { get; set; }
    public int? MinClipSeconds { get; set; }
    public int? TargetClipSeconds { get; set; }
    public int? MaxClipSeconds { get; set; }
    public string? AspectRatio { get; set; }
    public bool? SubtitlesEnabled { get; set; }
    public string? SubtitlePosition { get; set; }
    public string? SubtitleStyle { get; set; }
    public string? Language { get; set; }
    public int? Sensitivity { get; set; }
    public int? MaxCandidates { get; set; }
    public int? AutoRenderTop { get; set; }
    public string? FramingMode { get; set; }

    public static AssistantModelPatch From(JsonElement patch)
    {
        var result = new AssistantModelPatch();
        foreach (var property in patch.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name.ToLowerInvariant())
            {
                case "focuscategories": result.FocusCategories = Strings(value); break;
                case "customkeywords": result.CustomKeywords = Strings(value); break;
                case "minclipseconds": result.MinClipSeconds = Int(value); break;
                case "targetclipseconds": result.TargetClipSeconds = Int(value); break;
                case "maxclipseconds": result.MaxClipSeconds = Int(value); break;
                case "aspectratio": result.AspectRatio = Str(value); break;
                case "subtitlesenabled": result.SubtitlesEnabled = value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null; break;
                case "subtitleposition": result.SubtitlePosition = Str(value); break;
                case "subtitlestyle": result.SubtitleStyle = Str(value); break;
                case "language": result.Language = Str(value); break;
                case "sensitivity": result.Sensitivity = Int(value); break;
                case "maxcandidates": result.MaxCandidates = Int(value); break;
                case "autorendertop": result.AutoRenderTop = Int(value); break;
                case "framingmode": result.FramingMode = Str(value); break;
            }
        }
        return result;
    }

    private static string? Str(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim().ToLowerInvariant() : null;

    private static int? Int(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
        ? (int)Math.Clamp(Math.Round(number), int.MinValue, int.MaxValue)
        : null;

    private static List<string>? Strings(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
        : null;
}

/// <summary>
/// Words that show a request is about a setting. A small model copies context values and invents fields, so a field it
/// returns is accepted only when the user's message talks about that setting; explicit mentions are decided by the rules.
/// </summary>
public static class AssistantCues
{
    public static readonly Regex Aspect = Rx.Create(
        @"\b(?:formato|vertical|horizontal|quadrad\w*|tik ?tok|reels?|shorts?|youtube|yt|instagram|insta|kwai|celular|tela|propor\w*|deitad\w*|em pe|widescreen|feed|stories|story|paisagem|retrato)\b|\b\d{1,2} ?[:x] ?\d{1,2}\b");
    public static readonly Regex Durations = Rx.Create(
        @"\b(?:segundos?|segs?|minutos?|min|curt\w*|long\w*|dura\w*|tamanho|rapid\w*|comprid\w*|breves?|tempo)\b|\b\d+ ?s\b");
    public static readonly Regex Subtitles = Rx.Create(@"\b(?:legendas?|captions?)\b");
    public static readonly Regex SubtitlePlacement = Rx.Create(
        @"\b(?:meio|centro|central\w*|cima|encima|topo|alto|superior|acima|baixo|embaixo|debaixo|inferior|rodape|abaixo|posic\w*)\b");
    public static readonly Regex SubtitleStyle = Rx.Create(
        @"\b(?:estilo|fonte|letras?|limpa|simples|minimalista|discreta|caixa|fundo|tarja|negrito|grossa|destac\w*|destaque|chamativa)\b");
    public static readonly Regex Language = Rx.Create(@"\b(?:idioma|lingua|ingles|portugues|espanhol|english|spanish|espanol|falad\w*|sotaque)\b");
    public static readonly Regex Sensitivity = Rx.Create(@"\b(?:sensib\w*|rigor\w*|exigen\w*|criterio\w*|mais momentos|menos momentos)\b");
    public static readonly Regex MomentCount = Rx.Create(@"\b\d{1,3}\s*(?:momentos|candidatos)\b|\b(?:mais|menos) momentos\b|\bquantidade\b|\bquantos momentos\b");
    public static readonly Regex ClipCount = Rx.Create(@"\b\d{1,3}\s*(?:clipes?|cortes?|videos?)\b|\btodos\b|\bquantidade\b|\bquantos (?:clipes|cortes)\b");
    public static readonly Regex Framing = Rx.Create(@"\b(?:webcam|facecam|camera|cam|enquadr\w*|rosto|centraliz\w*|split|dividid\w*|layout)\b");
    public static readonly Regex Keywords = Rx.Create(
        @"\b(?:palavras?|termos?|expressa\w*|falar|fala|falo|falarem|disser|diz|digo|grit\w*|menciona\w*|chave)\b|[""“”«»]");

    public static readonly IReadOnlyDictionary<string, Regex> Focus = new Dictionary<string, Regex>
    {
        [MomentCategories.Funny] = Rx.Create(@"\b(?:engracad\w*|risad\w*|rir|rindo|zoeira|zueira|zoac\w*|zoar|comedia|comic\w*|piada\w*|humor\w*|divertid\w*|hilari\w*|k{3,}\w*|(?:ha){2,}\w*|memes?|palhac\w*|graca)\b"),
        [MomentCategories.Reaction] = Rx.Create(@"\b(?:reac\w*|reagi\w*|reage|sustos?|assust\w*|surpres\w*|espant\w*|choc\w*)\b"),
        [MomentCategories.Hype] = Rx.Create(@"\b(?:comemor\w*|hype|empolg\w*|vibra\w*|celebr\w*|euforia|grit\w*|vitori\w*|ganh\w*|venc\w*|epic\w*|insan\w*|animad\w*|explos\w*)\b"),
        [MomentCategories.Tension] = Rx.Create(@"\b(?:tens\w*|suspense|nervos\w*|apreens\w*|clima|medo|terror|panico|dram\w*)\b"),
        [MomentCategories.Gameplay] = Rx.Create(@"\b(?:jogad\w*|clutch\w*|kills?|abates?|headshots?|ace|gameplay|lances?|plays?|habilidad\w*|skills?|mira|partidas?|jogos?|games?)\b"),
        [MomentCategories.Quote] = Rx.Create(@"\b(?:frases?|citac\w*|falas?|bordao|bordoes|quotes?|disse|marcantes?|conselhos?|reflex\w*)\b"),
        [MomentCategories.Highlight] = Rx.Create(@"\b(?:destaques?|highlights?|melhores|principais|top|importantes?)\b"),
    };

    public static readonly IReadOnlyDictionary<string, Regex> Unsupported = new Dictionary<string, Regex>
    {
        [UnsupportedKinds.AutoPost] = Rx.Create(@"\b(?:post\w*|public\w*|agend\w*|compartilh\w*|upload|subir|suba|envi\w*|mand\w*)\b"),
        [UnsupportedKinds.Music] = Rx.Create(@"\b(?:music\w*|trilha|som|audio|batida|beat)\b"),
        [UnsupportedKinds.Effects] = Rx.Create(@"\b(?:efeito\w*|transic\w*|filtro\w*|zoom|emojis?|stickers?|figurinha\w*|vinheta\w*|anima\w*|slow|lenta|glitch)\b"),
        [UnsupportedKinds.EditVideo] = Rx.Create(@"\b(?:edit\w*|cort\w*|remov\w*|apag\w*|tira\w*|aceler\w*|narra\w*|dubla\w*|voz|desfoc\w*|blur|censur\w*|bip\w*)\b"),
    };

    /// <summary>One word of a setting ("legenda", "clipes", "curtos", "embaixo", "9:16", "30s").</summary>
    private static readonly Regex SettingToken = Rx.Create(
        @"^(?:clipes?|cortes?|videos?|legendas?|captions?|momentos?|segundos?|segs?|minutos?|mins?|formato|vertical|horizontal|quadrad[oa]s?|curt[oa]s?|curtinh[oa]s?|long[oa]s?"
        + @"|tik|tok|tiktok|reels?|shorts?|youtube|instagram|insta|kwai|stories|musicas?|efeitos?|idioma|ingles|portugues|espanhol|webcam|facecam|camera"
        + @"|enquadramento|sensibilidade|sensivel|foco|duracao|posicao|estilo|audio|palavras?|palavras?-chaves?|chaves?|termos?|embaixo|cima|encima|meio|centro|topo"
        + @"|negrito|caixa|marcantes?|melhores|principais|\d{1,2}[:x]\d{1,2}|\d{1,3}(?:s|segs?|segundos?|min|minutos?)?)$");

    /// <summary>Game terms are moment-type words too, but a user who names one as a word to look for wants that word.</summary>
    private static readonly Regex GameTerm = Rx.Create(@"^(?:clutch\w*|kills?|abates?|headshots?|ace)$");

    private static readonly HashSet<string> PhraseFillers = new(StringComparer.Ordinal)
    {
        "o", "a", "os", "as", "um", "uma", "de", "do", "da", "dos", "das", "no", "na", "nos", "nas", "em", "com", "mais", "bem", "muito", "pra", "pro", "para", "e", "so",
    };

    /// <summary>
    /// A word of a setting or of a moment type ("legenda", "curtos", "engraçados"). Game terms ("clutch", "kill") count as
    /// moment-type words unless <paramref name="allowGameTerms"/> (the rule layer: the user named them as words to look for).
    /// </summary>
    public static bool IsSettingToken(string folded, bool allowGameTerms) => SettingToken.IsMatch(folded) || IsMomentWords(folded, allowGameTerms);

    /// <summary>
    /// A keyword made only of setting words, moment-type words and fillers ("legenda", "os clipes curtos", "momentos
    /// engraçados", "em cima") is the request itself, never a word the user wants found in the speech. Applied to the
    /// keywords of both the rule layer and the model.
    /// </summary>
    public static bool IsSettingPhrase(string keyword, bool allowGameTerms)
    {
        var folded = FoldedText.Fold(AnalysisSettings.CleanKeyword(keyword)).Trim('.', ',', ';', ':', '!', '?', ' ');
        if (folded.Length == 0) return false;
        if (IsMomentWords(folded, allowGameTerms)) return true; // "melhores momentos", "clima pesado"
        var settingWords = 0;
        foreach (var token in folded.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (PhraseFillers.Contains(token)) continue;
            if (!IsSettingToken(token, allowGameTerms)) return false;
            settingWords++;
        }
        return settingWords > 0;
    }

    private static bool IsMomentWords(string folded, bool allowGameTerms)
        => AssistantRules.Categories.Any(c => c.Pattern.Match(folded) is { Success: true } m && m.Length == folded.Length)
           && !(allowGameTerms && GameTerm.IsMatch(folded));

    /// <summary>A keyword the model extracted is kept only when the user typed it and it is not a setting or moment-type word.</summary>
    public static bool IsGroundedKeyword(string keyword, FoldedText text)
    {
        var folded = FoldedText.Fold(AnalysisSettings.CleanKeyword(keyword));
        if (folded.Length is 0 or > AnalysisSettings.CustomKeywordMaxLength) return false;
        if (!text.Folded.Contains(folded, StringComparison.Ordinal)) return false;
        return !IsSettingPhrase(keyword, allowGameTerms: false);
    }
}

/// <summary>
/// Combines the rule layer and the model's patch into one <see cref="RequestedChanges"/>: rules win, a model field counts only
/// when the message talks about that setting, and a value the message rejects (<see cref="RequestedChanges.Rejected"/>,
/// <see cref="RequestedChanges.RemoveKeywords"/>) never comes from the model.
/// </summary>
public static class AssistantMerge
{
    private static readonly string[] Intents = ["configure", "question", "help", "other"];

    public static RequestedChanges Merge(RequestedChanges rules, AssistantModelOutput? model, string message)
    {
        var text = new FoldedText(message);
        var patch = model?.Patch ?? new AssistantModelPatch();
        var merged = new RequestedChanges();
        foreach (var rejected in rules.Rejected) merged.Rejected.Add(rejected);

        // Moment types: explicit mentions decide; otherwise each category the model saw must be supported by the text.
        if (rules.FocusCategories is not null)
        {
            merged.FocusCategories = rules.FocusCategories;
        }
        else if (patch.FocusCategories is { Count: > 0 })
        {
            var accepted = AnalysisSettings.NormalizeFocusCategories(patch.FocusCategories)
                .Where(c => text.Has(AssistantCues.Focus[c]) && !rules.IsRejected("focusCategories", c)).ToList();
            if (accepted.Count > 0) merged.FocusCategories = accepted;
        }

        merged.ClearKeywords = rules.ClearKeywords;
        foreach (var keyword in rules.RemoveKeywords) merged.RemoveKeyword(keyword);
        foreach (var keyword in rules.AddKeywords) merged.AddKeyword(keyword);
        foreach (var keyword in rules.TooLongKeywords) merged.AddKeyword(keyword);
        if (patch.CustomKeywords is { Count: > 0 } && text.Has(AssistantCues.Keywords))
        {
            foreach (var keyword in patch.CustomKeywords.Where(k => AssistantCues.IsGroundedKeyword(k, text) && !rules.RemoveKeywords.Any(r => RequestedChanges.SameKeyword(r, k))))
            {
                merged.AddKeyword(keyword);
            }
        }

        // Clip lengths travel as a group: explicit numbers / "curtos" / "longos" from the rules, else the model's.
        if (rules.HasDurations)
        {
            merged.MinClipSeconds = rules.MinClipSeconds;
            merged.TargetClipSeconds = rules.TargetClipSeconds;
            merged.MaxClipSeconds = rules.MaxClipSeconds;
        }
        else if ((patch.MinClipSeconds ?? patch.TargetClipSeconds ?? patch.MaxClipSeconds) is not null && text.Has(AssistantCues.Durations)
                 && !new[] { patch.MinClipSeconds, patch.TargetClipSeconds, patch.MaxClipSeconds }.Any(v => rules.IsRejected("clipSeconds", v)))
        {
            merged.MinClipSeconds = patch.MinClipSeconds;
            merged.TargetClipSeconds = patch.TargetClipSeconds;
            merged.MaxClipSeconds = patch.MaxClipSeconds;
        }

        merged.AspectRatio = rules.AspectRatio
            ?? (AspectRatios.IsValid(patch.AspectRatio) && text.Has(AssistantCues.Aspect) && !rules.IsRejected("aspectRatio", patch.AspectRatio) ? patch.AspectRatio : null);
        merged.SubtitlesEnabled = rules.SubtitlesEnabled
            ?? (patch.SubtitlesEnabled is { } enabled && text.Has(AssistantCues.Subtitles) && !rules.IsRejected("subtitles.enabled", enabled) ? enabled : null);
        merged.SubtitlePosition = rules.SubtitlePosition
            ?? (SubtitleStyles.IsPosition(patch.SubtitlePosition) && text.Has(AssistantCues.Subtitles) && text.Has(AssistantCues.SubtitlePlacement)
                && !rules.IsRejected("subtitles.position", patch.SubtitlePosition) ? patch.SubtitlePosition : null);
        merged.SubtitleStyle = rules.SubtitleStyle
            ?? (SubtitleStyles.IsStyle(patch.SubtitleStyle) && text.Has(AssistantCues.Subtitles) && text.Has(AssistantCues.SubtitleStyle)
                && !rules.IsRejected("subtitles.style", patch.SubtitleStyle) ? patch.SubtitleStyle : null);
        merged.Language = rules.Language
            ?? (EditingPreferences.IsLanguage(patch.Language) && text.Has(AssistantCues.Language) && !rules.Unsupported.Contains(UnsupportedKinds.Other)
                && !rules.IsRejected("language", patch.Language) ? patch.Language : null);
        merged.Sensitivity = rules.Sensitivity
            ?? (patch.Sensitivity is { } sensitivity && text.Has(AssistantCues.Sensitivity) && !rules.IsRejected("sensitivity", sensitivity) ? sensitivity : null);
        merged.MaxCandidates = rules.MaxCandidates
            ?? (patch.MaxCandidates is { } candidates && text.Has(AssistantCues.MomentCount) && !rules.IsRejected("maxCandidates", candidates) ? candidates : null);
        merged.AutoRenderAll = rules.AutoRenderAll;
        merged.AutoRenderTop = rules.AutoRenderTop
            ?? (!rules.AutoRenderAll && patch.AutoRenderTop is { } top && text.Has(AssistantCues.ClipCount) && !rules.IsRejected("autoRenderTop", top) ? top : null);
        merged.FramingMode = rules.FramingMode
            ?? (patch.FramingMode is { } mode && Array.IndexOf(FramingModes.All, mode) >= 0 && text.Has(AssistantCues.Framing) && !rules.IsRejected("framing.mode", mode) ? mode : null);

        foreach (var kind in rules.Unsupported) merged.AddUnsupported(kind);
        foreach (var kind in model?.Unsupported ?? [])
        {
            if (AssistantCues.Unsupported.TryGetValue(kind, out var cue) && text.Has(cue) && !rules.IsRejected("unsupported", kind)) merged.AddUnsupported(kind);
        }
        merged.ModelSaysOther = model?.Unsupported.Contains(UnsupportedKinds.Other) == true;

        var intent = model is not null && Array.IndexOf(Intents, model.Intent) >= 0 ? model.Intent : "other";
        merged.Help = rules.Help || (intent == "help" && !merged.HasSettings);
        merged.Greeting = rules.Greeting;
        merged.Thanks = rules.Thanks;
        merged.Question = SanitizeQuestion(model?.Question);
        return merged;
    }

    private static readonly Regex UnsafeQuestion = Rx.Create(
        @"\b(?:post\w*|public\w*|agend\w*|music\w*|efeito\w*|filtro\w*|transic\w*|edit\w*|ajustei|mudei|alterei|configurei|apliquei|pronto|feito)\b");

    /// <summary>
    /// The model's clarifying question, only when it is one short question (at most 160 characters) that offers nothing the
    /// product does not do and claims no change.
    /// </summary>
    public static string? SanitizeQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var text = string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 160 || !text.EndsWith('?') || text.Count(c => c == '?') > 1) return null;
        if (UnsafeQuestion.IsMatch(FoldedText.Fold(text))) return null;
        return text;
    }
}
