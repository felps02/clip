namespace ClipForge.Api.Features.Assistant;

/// <summary>
/// Writes the pt-BR reply from the validated outcome only: the changes that are really in the patch, what was already set,
/// what was capped or dropped (with the reason), fixed explanations for unsupported requests and, only when nothing
/// changed, the model's clarifying question. Small models are not trusted with free text (ADR-010).
/// </summary>
public static class AssistantReplyComposer
{
    private static readonly string[] Openers =
    [
        "Pronto! Configurei: {0}.",
        "Feito! Novas configurações: {0}.",
        "Combinado! Ajustes aplicados: {0}.",
    ];

    public const string HelpText =
        "Posso ajustar a análise antes de você enviar: o foco dos momentos (engraçados, reações, hype, tensão, jogadas ou frases marcantes), "
        + "palavras-chave para procurar na fala, a duração dos clipes, o formato (9:16, 16:9 ou 1:1), as legendas (ligar ou desligar, posição e estilo), "
        + "o idioma do áudio, a sensibilidade e quantos clipes gerar. Exemplo: “quero clipes engraçados de uns 30 segundos pro TikTok”.";

    public const string ShortHelp =
        "Posso ajustar o foco dos momentos, a duração, o formato, as legendas, o idioma do áudio e quantos clipes gerar.";

    public const string ThanksText = "Por nada! Se quiser mudar mais alguma coisa, é só pedir.";

    public const string NothingText = "Não identifiquei nenhuma configuração para mudar nesse pedido.";

    public static readonly IReadOnlyDictionary<string, string> UnsupportedTexts = new Dictionary<string, string>
    {
        [UnsupportedKinds.AutoPost] = "Ainda não consigo postar automaticamente: baixe os clipes e publique.",
        [UnsupportedKinds.Music] = "Ainda não consigo adicionar música aos clipes.",
        [UnsupportedKinds.Effects] = "Ainda não consigo aplicar efeitos, filtros ou transições nos clipes.",
        [UnsupportedKinds.EditVideo] = "Não edito o conteúdo do vídeo: só ajusto as configurações da análise.",
        [UnsupportedKinds.Other] = "Isso eu ainda não consigo fazer.",
    };

    /// <param name="outcome">The validated outcome.</param>
    /// <param name="turn">Number of messages in the conversation (picks one of the openers, deterministically).</param>
    public static string Compose(AssistantOutcome outcome, int turn)
    {
        var parts = new List<string>();
        if (outcome.Changes.Count > 0)
        {
            var opener = Openers[Math.Abs(turn) % Openers.Length];
            parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, opener, PtText.JoinList(outcome.Changes.Select(c => c.Phrase).ToList())));
        }
        if (outcome.Already.Count > 0)
        {
            parts.Add((outcome.Changes.Count > 0 ? "Já estava assim: " : "Isso já está configurado: ") + PtText.JoinList(outcome.Already) + ".");
        }
        parts.AddRange(outcome.Notes);
        foreach (var kind in outcome.Unsupported)
        {
            if (UnsupportedTexts.TryGetValue(kind, out var text)) parts.Add(text);
        }
        if (parts.Count > 0)
        {
            if (outcome.Changes.Count == 0 && outcome.Already.Count == 0 && outcome.Notes.Count == 0) parts.Add(ShortHelp);
            return string.Join(" ", parts);
        }

        // Nothing changed and nothing to explain.
        if (outcome.Thanks) return ThanksText;
        if (outcome.Greeting) return "Olá! " + HelpText;
        if (outcome.Help) return HelpText;
        if (outcome.Question is { Length: > 0 } question) return question;
        if (outcome.ModelSaysOther) return UnsupportedTexts[UnsupportedKinds.Other] + " " + ShortHelp;
        return NothingText + " " + ShortHelp;
    }
}
