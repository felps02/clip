using System.Text.RegularExpressions;

namespace ClipForge.Api.Features.Assistant;

/// <summary>
/// Whether a mention in the user's message is negated, so the rule layer never turns "não quero clipes curtos", "nada de
/// legenda no topo", "a live não é em inglês" or "a webcam não precisa aparecer" into the change it names.
/// <list type="bullet">
/// <item>"sem", "nada de", "evita", "tira"… negate only the phrase right after them (articles and nouns such as "layout",
/// "clipes", "palavra" in between): "sem o layout de webcam", "nada de clipes longos" — but not "sem webcam legenda no
/// meio" nor "sem legenda webcam em cima" (the subtitles and the webcam are settings of their own).</item>
/// <item>"não", "nem", "nunca", "nenhum" negate the rest of their clause (up to 6 words), until a new request starts ("não
/// sei mexer nisso coloca a legenda em cima") — "não quero a legenda em cima", "a legenda não pode ficar no meio".</item>
/// <item>A "não" right after the mention that ends the clause or comes before a verb: "clipes curtos não", "a webcam não
/// precisa aparecer".</item>
/// </list>
/// Clauses end at punctuation and at "e"/"mas"/"porém"/"então" ("sem legenda e engraçados"); "é" folds to "e", so the verb
/// (typed with its accent, or right after "não") is not a break. Not negations: "não só", double negatives ("não esquece de",
/// "não quero perder nenhuma jogada", "não pode faltar"), "por que não", "não tem problema", bounds ("não quero mais de 10
/// clipes") and rhetorical questions about a capability ("não dá pra colocar a legenda em cima?").
/// </summary>
public static class AssistantNegation
{
    private const int MaxWordsAfterNegation = 6;

    private static readonly Regex ClauseBreak = Rx.Create(@"[,.;:!?()\n]|\b(?:mas|porem|entao|so que|alias)\b|\be\b");

    private static readonly Regex NominalTail = Rx.Create(
        @"\b(?:sem|nada de|exceto|evita|evite|evitar|tira|tire|tirar|remove|remova|remover|dispenso|dispensa|desliga|desligue|desativa|desative)\s+"
        + @"(?:(?:o|a|os|as|um|uma|de|do|da|dos|das|no|na|nos|nas|em|com|mais|muito|muita|muitos|muitas|tanto|tanta|tantos|tantas|esse|essa|esses|essas|isso|ter"
        + @"|layout|modo|clipes?|cortes?|videos?|trechos?|formato|idioma|audio|momentos?|palavras?|palavras?-chaves?|chaves?|termos?|expressao|expressoes"
        + @"|partes?|coisas?|tipo)\s+){0,3}$");

    // "nenhum"/"nenhuma" negate on their own ("nenhuma legenda no meio"); after "não" they are the same negation
    // ("não quero perder nenhuma jogada"), decided by that "não".
    private static readonly Regex VerbalNegation = Rx.Create(@"\b(?:nao|nem|nunca|jamais|nenhum|nenhuma)\b");

    private static readonly Regex PostNegation = Rx.Create(
        @"^\s+(?:nao|nunca)\b(?:\s*(?:[,.;:!?)\n]|$)|\s+(?:precisa|preciso|pode|deve|quero|gosto|curto|fica|ficou|ficaria|rola|da|e|eh|presta|combina|funciona|serve|vai|tem|tenho|uso)\b)");

    private static readonly Regex NotOnly = Rx.Create(@"^(?:nao|nem)\s+(?:so|somente|apenas)\b");
    // "não esquece de", "não deixa de", "não quero perder", "não pode faltar", "não deixa passar": the thing is wanted.
    private static readonly Regex DoubleNegative = Rx.Create(
        @"^nao\s+(?:quero\s+|queria\s+|pode\s+|posso\s+|vai\s+|deve\s+|da\s+pra\s+)?(?:se\s+)?(?:esquec\w*|deix(?:a|e|ar)\s+(?:de|passar|escapar)|perd\w*(?!\s+tempo)|falt\w*|ficar\s+sem)\b");
    // Idioms: "não tem problema ser em inglês", "não faz mal", "não importa".
    private static readonly Regex Idiom = Rx.Create(@"^nao\s+(?:(?:tem|ha)\s+problema|faz\s+mal|importa)\b");
    // "não quero mais de 10 clipes" is an upper bound (and "não quero menos de 20 s" a lower one), not a refusal.
    private static readonly Regex Bound = Rx.Create(@"^nao\s+(?:quero\s+|precisa\s+(?:de\s+)?)?(?:mais|menos)\s+(?:de|que|do que)\b");
    private static readonly Regex EndsWithNegation = Rx.Create(@"\b(?:nao|nem)\s*$");
    private static readonly Regex WhyNot = Rx.Create(@"\bpor\s?que\s+$");
    private static readonly Regex Capability = Rx.Create(
        @"^nao\s+(?:\w+\s+)?(?:tem como|da pra|da para|daria|consegue|conseguiria|pode|podia|poderia|rola|seria possivel)\b");

    /// <summary>A new request inside the clause ends the reach of a "não" (unless it is the verb "não" negates).</summary>
    private static readonly HashSet<string> RestartWords = new(StringComparer.Ordinal)
    {
        "quero", "queria", "prefiro", "coloca", "coloque", "bota", "bote", "poe", "ponha", "deixa", "deixe", "faz", "faca", "muda", "mude",
        "usa", "use", "ativa", "ative", "liga", "ligue", "gera", "gere", "pega", "pegue", "troca", "troque", "aumenta", "aumente",
        "diminui", "diminua", "manda", "mande", "procura", "procure", "marca", "marque", "adiciona", "adicione", "so", "somente", "apenas", "agora",
    };

    /// <summary>Words after which a verb belongs to a subordinate clause, still under the negation ("não quero que deixe…").</summary>
    private static readonly HashSet<string> Subordinators = new(StringComparer.Ordinal) { "que", "se", "quando", "onde", "como" };

    private static readonly HashSet<string> Clitics = new(StringComparer.Ordinal) { "me", "te", "se", "lhe", "nos" };

    /// <summary>True when the mention at <paramref name="index"/> (of <paramref name="length"/> characters) is negated.</summary>
    public static bool IsNegated(FoldedText text, int index, int length)
        => NegatedBefore(text, index) || PostNegation.IsMatch(text.Folded.AsSpan(Math.Min(text.Folded.Length, index + length)));

    /// <summary>Start of the clause that contains <paramref name="index"/>.</summary>
    public static int ClauseStart(FoldedText text, int index)
    {
        var start = 0;
        foreach (Match match in ClauseBreak.Matches(text.Folded))
        {
            if (match.Index + match.Length > index) break;
            if (!IsVerbE(text, match)) start = match.Index + match.Length;
        }
        return start;
    }

    /// <summary>End (exclusive) of the clause that goes on at <paramref name="index"/>.</summary>
    public static int ClauseEnd(FoldedText text, int index)
    {
        foreach (Match match in ClauseBreak.Matches(text.Folded, Math.Min(text.Folded.Length, index)))
        {
            if (!IsVerbE(text, match)) return match.Index;
        }
        return text.Folded.Length;
    }

    private static bool NegatedBefore(FoldedText text, int index)
    {
        var start = ClauseStart(text, index);
        var clause = text.Folded[start..index];
        if (NominalTail.IsMatch(clause)) return true;

        var negations = VerbalNegation.Matches(clause);
        for (var i = negations.Count - 1; i >= 0; i--)
        {
            var negation = negations[i];
            if (negation.Value.StartsWith("nenhum", StringComparison.Ordinal) && negations.Take(i).Any(n => !n.Value.StartsWith("nenhum", StringComparison.Ordinal))) continue;
            if (IsNotANegation(text.Folded, start + negation.Index)) continue;
            var words = clause[(negation.Index + negation.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > MaxWordsAfterNegation) return false;
            // The first word after "não" is what it negates ("não quero", "não coloca"); a later request verb starts anew.
            var attached = words.Length > 0 && Clitics.Contains(words[0]) ? 2 : 1;
            for (var w = attached; w < words.Length; w++)
            {
                if (RestartWords.Contains(words[w]) && !Subordinators.Contains(words[w - 1])) return false;
            }
            return true;
        }
        return false;
    }

    private static bool IsNotANegation(string folded, int at)
    {
        var rest = folded[at..];
        if (NotOnly.IsMatch(rest) || DoubleNegative.IsMatch(rest) || Idiom.IsMatch(rest) || Bound.IsMatch(rest) || WhyNot.IsMatch(folded[..at])) return true;
        if (!Capability.IsMatch(rest)) return false;
        var end = folded.IndexOfAny(['.', '!', '?', '\n'], at);
        return end >= 0 && folded[end] == '?';
    }

    /// <summary>"é" (is) folds to "e" (and): typed with its accent, or right after "não"/"nem", it is the verb.</summary>
    private static bool IsVerbE(FoldedText text, Match match)
    {
        if (match.Value != "e") return false;
        return text.Original[match.Index] is 'é' or 'É' || EndsWithNegation.IsMatch(text.Folded.AsSpan(0, match.Index));
    }
}
