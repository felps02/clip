using ClipForge.Api.Features.Projects;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Features.Assistant;

/// <summary>One validated change: chip label, phrase used in the reply and the new value.</summary>
public sealed record AssistantChange(string Field, string Label, string Phrase, object? Value)
{
    public AssistantChangeDto ToDto() => new(Field, Label, Value);
}

/// <summary>Result of validating a request against the current settings and the plan: what changes and what to explain.</summary>
public sealed class AssistantOutcome
{
    public AssistantOutcome(AnalysisSettings current, AnalysisSettings proposed)
    {
        Current = current;
        Proposed = proposed;
    }

    public AnalysisSettings Current { get; }
    public AnalysisSettings Proposed { get; }
    public AssistantSettingsPatch Patch { get; } = new();
    public List<AssistantChange> Changes { get; } = [];
    /// <summary>Requested values that were already set (phrases).</summary>
    public List<string> Already { get; } = [];
    /// <summary>Sentences about what was dropped or capped (plan limits, invalid values).</summary>
    public List<string> Notes { get; } = [];
    public List<string> Unsupported { get; } = [];
    public bool Help { get; init; }
    public bool Greeting { get; init; }
    public bool Thanks { get; init; }
    public bool ModelSaysOther { get; init; }
    public string? Question { get; init; }
}

/// <summary>
/// Validates and caps a request exactly like project settings (the same validator and plan caps), with the assistant's
/// own bounds for clip lengths (5-120 s, min ≤ target ≤ max), then lists the real changes. Only what ends up in
/// <see cref="AssistantOutcome.Patch"/> is ever described as changed.
/// </summary>
public static class AssistantPatchBuilder
{
    public const int ClipSecondsMin = 5;
    public const int ClipSecondsMax = 120;

    public static AssistantOutcome Apply(AnalysisSettings current, RequestedChanges requested, PlanLimits limits, decimal? sourceSeconds)
    {
        var proposed = current.Clone();
        var already = new List<string>();
        var notes = new List<string>();

        // ---------------------------------------------------------------- moment types
        if (requested.FocusCategories is { } focus)
        {
            var normalized = AnalysisSettings.NormalizeFocusCategories(focus);
            if (normalized.ToHashSet().SetEquals(current.FocusCategories)) already.Add(AssistantLabels.FocusPhrase(normalized));
            else proposed.FocusCategories = normalized;
        }

        // ---------------------------------------------------------------- keywords
        foreach (var tooLong in requested.TooLongKeywords)
        {
            notes.Add($"Palavras-chave têm no máximo {AnalysisSettings.CustomKeywordMaxLength} caracteres: deixei de fora {PtText.Quote(tooLong)}.");
        }
        if (requested.ClearKeywords || requested.AddKeywords.Count > 0 || requested.RemoveKeywords.Count > 0)
        {
            var kept = requested.ClearKeywords
                ? new List<string>()
                : proposed.CustomKeywords.Where(k => !requested.RemoveKeywords.Any(r => RequestedChanges.SameKeyword(r, k))).ToList();
            var combined = kept.Concat(requested.AddKeywords).ToList();
            var distinct = combined.Select(AnalysisSettings.CleanKeyword).Where(k => k.Length is > 0 and <= AnalysisSettings.CustomKeywordMaxLength)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinct > AnalysisSettings.MaxCustomKeywords)
            {
                notes.Add($"O limite é de {AnalysisSettings.MaxCustomKeywords} palavras-chave: fiquei com as {AnalysisSettings.MaxCustomKeywords} primeiras.");
            }
            var merged = AnalysisSettings.NormalizeCustomKeywords(combined);
            if (merged.SequenceEqual(current.CustomKeywords, StringComparer.OrdinalIgnoreCase))
            {
                if (requested.AddKeywords.Count > 0) already.Add(AssistantLabels.KeywordsPhrase(requested.AddKeywords));
                else if (requested.RemoveKeywords.Count > 0 && !requested.ClearKeywords) already.Add(AssistantLabels.WithoutKeywordsPhrase(requested.RemoveKeywords));
                else if (current.CustomKeywords.Count == 0) already.Add("sem palavras-chave");
            }
            else
            {
                proposed.CustomKeywords = merged;
            }
        }

        // ---------------------------------------------------------------- clip lengths
        if (requested.HasDurations)
        {
            var resolved = ResolveDurations(current, requested.MinClipSeconds, requested.TargetClipSeconds, requested.MaxClipSeconds);
            if (resolved is not { } durations)
            {
                notes.Add($"A duração dos clipes precisa ficar entre {ClipSecondsMin} e {ClipSecondsMax} segundos: não mudei a duração.");
            }
            else if (durations.Min == current.MinClipSeconds && durations.Target == current.TargetClipSeconds && durations.Max == current.MaxClipSeconds)
            {
                already.Add(AssistantLabels.DurationPhrase(durations.Min, durations.Target, durations.Max));
            }
            else
            {
                proposed.MinClipSeconds = durations.Min;
                proposed.TargetClipSeconds = durations.Target;
                proposed.MaxClipSeconds = durations.Max;
            }
        }

        // ---------------------------------------------------------------- format
        if (requested.AspectRatio is { } aspect && AspectRatios.IsValid(aspect))
        {
            if (aspect == current.AspectRatio) already.Add(AssistantLabels.AspectPhrase(aspect));
            else proposed.AspectRatio = aspect;
        }

        // ---------------------------------------------------------------- subtitles (custom placement only when the plan allows)
        var subtitles = (proposed.Subtitles ?? new SubtitleSettings()).Clone();
        if (!limits.AllowCustomSubtitleStyles
            && ((requested.SubtitlePosition is { } p && p != SubtitleStyles.Bottom) || (requested.SubtitleStyle is { } s && s != SubtitleStyles.Bold)))
        {
            notes.Add("Posição e estilo de legenda personalizados não estão no seu plano: a legenda fica embaixo, no estilo destaque.");
        }
        if (requested.SubtitlePosition is { } position && SubtitleStyles.IsPosition(position)
            && (limits.AllowCustomSubtitleStyles || position == SubtitleStyles.Bottom))
        {
            if (position == current.Subtitles.Position) already.Add(AssistantLabels.PositionPhrase(position));
            else subtitles.Position = position;
        }
        if (requested.SubtitleStyle is { } style && SubtitleStyles.IsStyle(style) && (limits.AllowCustomSubtitleStyles || style == SubtitleStyles.Bold))
        {
            if (style == current.Subtitles.Style) already.Add(AssistantLabels.StylePhrase(style));
            else subtitles.Style = style;
        }
        if (requested.SubtitlesEnabled is { } enabled)
        {
            // "coloca a legenda no meio" asks where the subtitles go: that they stay on goes without saying.
            var impliedByPlacement = enabled && (requested.SubtitlePosition is not null || requested.SubtitleStyle is not null);
            if (enabled != current.Subtitles.Enabled) subtitles.Enabled = enabled;
            else if (!impliedByPlacement) already.Add(AssistantLabels.SubtitlesEnabledPhrase(enabled));
        }
        else if (!subtitles.Enabled && (subtitles.Position != current.Subtitles.Position || subtitles.Style != current.Subtitles.Style))
        {
            subtitles.Enabled = true; // asking where or how the subtitles look means showing them
        }
        proposed.Subtitles = subtitles;

        // ---------------------------------------------------------------- spoken language
        if (requested.Language is { } language && EditingPreferences.IsLanguage(language))
        {
            if (string.Equals(language, current.Language, StringComparison.OrdinalIgnoreCase)) already.Add(AssistantLabels.LanguagePhrase(language));
            else proposed.Language = language;
        }

        // ---------------------------------------------------------------- sensitivity
        if (requested.Sensitivity is { } sensitivity)
        {
            if (sensitivity is < 0 or > 100) notes.Add("A sensibilidade vai de 0 a 100: não mudei a sensibilidade.");
            else if (sensitivity == current.Sensitivity) already.Add(AssistantLabels.SensitivityPhrase(sensitivity));
            else proposed.Sensitivity = sensitivity;
        }

        // ---------------------------------------------------------------- moments and clips (plan caps)
        var planCandidates = Math.Max(1, limits.MaxCandidatesPerJob);
        var planRenders = Math.Max(0, limits.MaxAutoRenderPerJob);
        if (requested.MaxCandidates is { } candidates)
        {
            if (candidates < 1)
            {
                notes.Add("A análise procura pelo menos 1 momento: não mudei a quantidade de momentos.");
            }
            else
            {
                var capped = Math.Min(Math.Min(candidates, 100), planCandidates);
                if (capped < candidates) notes.Add($"Seu plano permite até {planCandidates} momentos por análise: usei {capped}.");
                if (capped != current.MaxCandidates) proposed.MaxCandidates = capped;
                else if (capped == candidates) already.Add(AssistantLabels.CandidatesPhrase(capped));
            }
        }
        if (requested.AutoRenderAll)
        {
            if (planRenders < proposed.MaxCandidates) notes.Add($"Seu plano gera até {planRenders} clipes automáticos por análise.");
            proposed.AutoRenderTop = Math.Min(proposed.MaxCandidates, planRenders);
        }
        else if (requested.AutoRenderTop is { } top)
        {
            if (top < 0)
            {
                notes.Add("A quantidade de clipes não pode ser negativa: não mudei a quantidade de clipes.");
            }
            else
            {
                var capped = Math.Min(Math.Min(top, 50), planRenders);
                if (capped < top) notes.Add($"Seu plano gera até {planRenders} clipes automáticos por análise: usei {capped}.");
                if (capped > proposed.MaxCandidates)
                {
                    // More clips than moments searched: search as many moments as the plan allows.
                    proposed.MaxCandidates = Math.Min(capped, planCandidates);
                    capped = Math.Min(capped, proposed.MaxCandidates);
                }
                if (capped != current.AutoRenderTop || proposed.MaxCandidates != current.MaxCandidates) proposed.AutoRenderTop = capped;
                else if (capped == top) already.Add(AssistantLabels.AutoRenderPhrase(capped, proposed.MaxCandidates));
            }
        }
        proposed.AutoRenderTop = Math.Min(proposed.AutoRenderTop ?? proposed.MaxCandidates, proposed.MaxCandidates);

        // ---------------------------------------------------------------- framing (webcam layout only when the plan allows)
        if (requested.FramingMode is { } mode && Array.IndexOf(FramingModes.All, mode) >= 0)
        {
            if (mode == FramingModes.WebcamSplit && !limits.AllowWebcamLayout)
            {
                notes.Add("O layout com webcam não está disponível no seu plano.");
            }
            else if (mode == current.Framing.Mode)
            {
                already.Add(AssistantLabels.FramingPhrase(mode));
            }
            else
            {
                proposed.Framing = proposed.Framing.Clone();
                proposed.Framing.Mode = mode;
            }
        }

        // ---------------------------------------------------------------- safety net: the project settings rules
        var final = PlanLimitHelpers.CapSettings(proposed, limits);
        if (!new AnalysisSettingsValidator().Validate(final).IsValid)
        {
            notes.Add("Não consegui aplicar essas mudanças com segurança: nada foi alterado.");
            final = current.Clone();
        }

        var outcome = new AssistantOutcome(current, final)
        {
            Help = requested.Help,
            Greeting = requested.Greeting,
            Thanks = requested.Thanks,
            ModelSaysOther = requested.ModelSaysOther,
            Question = requested.Question,
        };
        Diff(outcome, current, final);
        if (sourceSeconds is > 0 and { } seconds && outcome.Patch.MinClipSeconds is { } newMin && newMin > seconds)
        {
            notes.Add($"O vídeo tem {PtText.Seconds(seconds)} s, menos que a duração mínima dos clipes ({newMin} s).");
        }
        outcome.Already.AddRange(already.Distinct());
        outcome.Notes.AddRange(notes.Distinct());
        outcome.Unsupported.AddRange(requested.Unsupported);
        return outcome;
    }

    /// <summary>
    /// Clip lengths from a request: an explicit target alone becomes a range around it (30 s → 20-40 s); a min or max alone
    /// moves the other bound only when needed; the target always ends up between min and max. Null when a requested value
    /// is outside 5-120 s.
    /// </summary>
    public static (int Min, int Target, int Max)? ResolveDurations(AnalysisSettings current, int? min, int? target, int? max)
    {
        if (new[] { min, target, max }.Any(v => v is < ClipSecondsMin or > ClipSecondsMax)) return null;
        if (target is { } only && min is null && max is null)
        {
            return (Math.Max(ClipSecondsMin, Round(only * 2 / 3.0)), only, Math.Min(ClipSecondsMax, Round(only * 4 / 3.0)));
        }

        var low = min ?? current.MinClipSeconds;
        var high = max ?? current.MaxClipSeconds;
        if (low > high)
        {
            if (min is not null && max is not null) (low, high) = (high, low);
            else if (min is not null) high = Math.Max(low, Math.Min(ClipSecondsMax, Round(low * 4 / 3.0)));
            else low = Math.Min(high, Math.Max(ClipSecondsMin, Round(high * 2 / 3.0)));
        }
        var resolvedTarget = target ?? Math.Clamp(current.TargetClipSeconds, low, high);
        if (resolvedTarget < low) low = resolvedTarget;
        if (resolvedTarget > high) high = resolvedTarget;
        return (low, resolvedTarget, high);
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static void Diff(AssistantOutcome outcome, AnalysisSettings before, AnalysisSettings after)
    {
        var patch = outcome.Patch;
        var changes = outcome.Changes;

        if (!after.FocusCategories.ToHashSet().SetEquals(before.FocusCategories))
        {
            patch.FocusCategories = after.FocusCategories;
            changes.Add(new AssistantChange("focusCategories", AssistantLabels.FocusLabel(after.FocusCategories), AssistantLabels.FocusPhrase(after.FocusCategories), after.FocusCategories));
        }
        if (!after.CustomKeywords.SequenceEqual(before.CustomKeywords, StringComparer.Ordinal))
        {
            patch.CustomKeywords = after.CustomKeywords;
            changes.Add(new AssistantChange("customKeywords", AssistantLabels.KeywordsLabel(after.CustomKeywords), AssistantLabels.KeywordsChangePhrase(after.CustomKeywords), after.CustomKeywords));
        }
        var minChanged = after.MinClipSeconds != before.MinClipSeconds;
        var targetChanged = after.TargetClipSeconds != before.TargetClipSeconds;
        var maxChanged = after.MaxClipSeconds != before.MaxClipSeconds;
        if (minChanged || targetChanged || maxChanged)
        {
            patch.MinClipSeconds = after.MinClipSeconds;
            patch.TargetClipSeconds = after.TargetClipSeconds;
            patch.MaxClipSeconds = after.MaxClipSeconds;
            var (field, value) = targetChanged ? ("targetClipSeconds", after.TargetClipSeconds)
                : minChanged ? ("minClipSeconds", after.MinClipSeconds)
                : ("maxClipSeconds", after.MaxClipSeconds);
            changes.Add(new AssistantChange(field, AssistantLabels.DurationLabel(after.MinClipSeconds, after.TargetClipSeconds, after.MaxClipSeconds),
                AssistantLabels.DurationPhrase(after.MinClipSeconds, after.TargetClipSeconds, after.MaxClipSeconds), value));
        }
        if (after.AspectRatio != before.AspectRatio)
        {
            patch.AspectRatio = after.AspectRatio;
            changes.Add(new AssistantChange("aspectRatio", "Formato: " + AssistantLabels.AspectName(after.AspectRatio), AssistantLabels.AspectPhrase(after.AspectRatio), after.AspectRatio));
        }
        if (after.Subtitles.Enabled != before.Subtitles.Enabled)
        {
            (patch.Subtitles ??= new()).Enabled = after.Subtitles.Enabled;
            changes.Add(new AssistantChange("subtitles.enabled", after.Subtitles.Enabled ? "Legendas: ativadas" : "Legendas: desativadas",
                AssistantLabels.SubtitlesEnabledPhrase(after.Subtitles.Enabled), after.Subtitles.Enabled));
        }
        if (after.Subtitles.Position != before.Subtitles.Position)
        {
            (patch.Subtitles ??= new()).Position = after.Subtitles.Position;
            changes.Add(new AssistantChange("subtitles.position", "Posição da legenda: " + AssistantLabels.PositionName(after.Subtitles.Position),
                AssistantLabels.PositionPhrase(after.Subtitles.Position), after.Subtitles.Position));
        }
        if (after.Subtitles.Style != before.Subtitles.Style)
        {
            (patch.Subtitles ??= new()).Style = after.Subtitles.Style;
            changes.Add(new AssistantChange("subtitles.style", "Estilo da legenda: " + AssistantLabels.StyleName(after.Subtitles.Style),
                AssistantLabels.StylePhrase(after.Subtitles.Style), after.Subtitles.Style));
        }
        if (!string.Equals(after.Language, before.Language, StringComparison.Ordinal))
        {
            patch.Language = after.Language;
            changes.Add(new AssistantChange("language", "Idioma do áudio: " + AssistantLabels.LanguageName(after.Language), AssistantLabels.LanguagePhrase(after.Language), after.Language));
        }
        if (after.Sensitivity != before.Sensitivity)
        {
            patch.Sensitivity = after.Sensitivity;
            changes.Add(new AssistantChange("sensitivity", $"Sensibilidade: {after.Sensitivity}", AssistantLabels.SensitivityPhrase(after.Sensitivity), after.Sensitivity));
        }
        if (after.MaxCandidates != before.MaxCandidates)
        {
            patch.MaxCandidates = after.MaxCandidates;
            changes.Add(new AssistantChange("maxCandidates", $"Momentos por análise: até {after.MaxCandidates}", AssistantLabels.CandidatesPhrase(after.MaxCandidates), after.MaxCandidates));
        }
        if (after.AutoRenderTop != before.AutoRenderTop)
        {
            var top = after.AutoRenderTop ?? after.MaxCandidates;
            patch.AutoRenderTop = top;
            changes.Add(new AssistantChange("autoRenderTop", AssistantLabels.AutoRenderLabel(top, after.MaxCandidates), AssistantLabels.AutoRenderPhrase(top, after.MaxCandidates), top));
        }
        if (after.Framing.Mode != before.Framing.Mode)
        {
            patch.Framing = new AssistantFramingPatch { Mode = after.Framing.Mode };
            changes.Add(new AssistantChange("framing.mode", "Enquadramento: " + AssistantLabels.FramingName(after.Framing.Mode), AssistantLabels.FramingPhrase(after.Framing.Mode), after.Framing.Mode));
        }
    }
}

/// <summary>pt-BR names, chip labels and reply phrases of the settings.</summary>
public static class AssistantLabels
{
    public static string CategoryName(string category) => category switch
    {
        MomentCategories.Highlight => "destaques",
        MomentCategories.Reaction => "reações",
        MomentCategories.Funny => "momentos engraçados",
        MomentCategories.Hype => "hype e comemorações",
        MomentCategories.Tension => "tensão",
        MomentCategories.Gameplay => "jogadas",
        MomentCategories.Quote => "frases marcantes",
        _ => category,
    };

    public static string FocusLabel(IReadOnlyList<string> categories)
        => categories.Count == 0 ? "Foco: todos os tipos de momento" : "Foco: " + PtText.JoinList(categories.Select(CategoryName).ToList());

    public static string FocusPhrase(IReadOnlyList<string> categories)
        => categories.Count == 0 ? "sem foco específico (todos os tipos de momento)" : "foco em " + PtText.JoinList(categories.Select(CategoryName).ToList());

    public static string KeywordsLabel(IReadOnlyList<string> keywords)
        => keywords.Count == 0 ? "Palavras-chave: nenhuma" : "Palavras-chave: " + string.Join(", ", keywords);

    public static string KeywordsChangePhrase(IReadOnlyList<string> keywords)
        => keywords.Count == 0 ? "sem palavras-chave" : KeywordsPhrase(keywords);

    public static string KeywordsPhrase(IReadOnlyList<string> keywords)
        => (keywords.Count == 1 ? "palavra-chave " : "palavras-chave ") + PtText.JoinList(keywords.Select(PtText.Quote).ToList());

    public static string WithoutKeywordsPhrase(IReadOnlyList<string> keywords)
        => (keywords.Count == 1 ? "sem a palavra-chave " : "sem as palavras-chave ") + PtText.JoinList(keywords.Select(PtText.Quote).ToList());

    public static string DurationLabel(int min, int target, int max) => $"Duração: {min}–{max} s (alvo {target} s)";

    public static string DurationPhrase(int min, int target, int max) => $"clipes de {min} a {max} s (alvo de {target} s)";

    public static string AspectName(string aspect) => aspect switch
    {
        AspectRatios.Vertical => "9:16 (vertical)",
        AspectRatios.Horizontal => "16:9 (horizontal)",
        AspectRatios.Square => "1:1 (quadrado)",
        _ => aspect,
    };

    public static string AspectPhrase(string aspect) => "formato " + AspectName(aspect);

    public static string SubtitlesEnabledPhrase(bool enabled) => enabled ? "legendas ativadas" : "legendas desativadas";

    public static string PositionName(string position) => position switch
    {
        SubtitleStyles.Center => "no meio",
        SubtitleStyles.Top => "em cima",
        _ => "embaixo",
    };

    public static string PositionPhrase(string position) => position switch
    {
        SubtitleStyles.Center => "legenda no meio da tela",
        SubtitleStyles.Top => "legenda em cima",
        _ => "legenda embaixo",
    };

    public static string StyleName(string style) => style switch
    {
        SubtitleStyles.Clean => "limpa",
        SubtitleStyles.Boxed => "com caixa",
        _ => "destaque",
    };

    public static string StylePhrase(string style) => "legenda no estilo " + StyleName(style);

    public static string LanguageName(string language) => language.ToLowerInvariant() switch
    {
        "auto" => "detectar automaticamente",
        "pt" or "pt-br" => "português",
        "en" => "inglês",
        "es" => "espanhol",
        _ => language,
    };

    public static string LanguagePhrase(string language)
        => language == "auto" ? "idioma do áudio detectado automaticamente" : "idioma do áudio em " + LanguageName(language);

    public static string SensitivityPhrase(int sensitivity) => $"sensibilidade {sensitivity}";

    public static string CandidatesPhrase(int candidates) => $"até {candidates} momentos por análise";

    public static string AutoRenderLabel(int top, int candidates)
        => top >= candidates ? "Clipes automáticos: todos os momentos encontrados" : $"Clipes automáticos: {top}";

    public static string AutoRenderPhrase(int top, int candidates)
        => top >= candidates ? "todos os momentos encontrados viram clipes" : top == 1 ? "1 clipe automático" : $"{top} clipes automáticos";

    public static string FramingName(string mode) => mode switch
    {
        FramingModes.Center => "centralizado",
        FramingModes.WebcamSplit => "webcam + jogo",
        _ => "automático",
    };

    public static string FramingPhrase(string mode) => "enquadramento " + FramingName(mode);
}
