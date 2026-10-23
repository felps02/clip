using System.Text.Json.Nodes;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Features.Assistant;

/// <summary>One message sent to the model (role user | assistant); the system prompt travels separately.</summary>
public sealed record AssistantPromptMessage(string Role, string Content);

/// <summary>What a provider sends: the fixed system prompt and the conversation, whose last message carries the context.</summary>
public sealed record AssistantPromptRequest(string SystemPrompt, IReadOnlyList<AssistantPromptMessage> Messages);

/// <summary>
/// The model's instructions. The system prompt is byte-stable (no dates, ids, settings or limits in it), so llama.cpp keeps
/// its prefix cached (<c>cache_prompt</c>) and only the short per-request part is processed on each call; the current
/// settings, plan caps and file duration go after the request in the last user message (a small model copies context it
/// reads before the request).
/// </summary>
public static class AssistantPrompt
{
    public const string SchemaName = "assistant_patch";
    public const int MaxTokens = 300;
    public const double Temperature = 0.2;
    private const int HistoryMessages = 6;
    private const int HistoryMessageLength = 400;

    public static readonly string SystemPrompt = string.Join("\n",
        "Você configura a análise de um app que encontra os melhores momentos de lives e vídeos gravados e gera clipes curtos. Leia o pedido do usuário e responda só com JSON. Você não escreve a resposta para o usuário.",
        "",
        "Campos de \"patch\" (inclua só o que o usuário pediu para mudar no último pedido):",
        "- focusCategories: tipos de momento a priorizar: \"funny\" (engraçado, risada, zoeira), \"reaction\" (reação, susto), \"hype\" (comemoração, empolgação), \"tension\" (tensão, suspense), \"gameplay\" (jogada, clutch, kill), \"quote\" (frase marcante), \"highlight\" (destaque geral).",
        "- customKeywords: palavras que a pessoa fala no vídeo e quer encontrar (só quando ela pede para procurar uma palavra).",
        "- minClipSeconds, targetClipSeconds, maxClipSeconds: duração dos clipes em segundos (5 a 120).",
        "- aspectRatio: \"9:16\" (vertical: TikTok, Reels, Shorts), \"16:9\" (horizontal: YouTube), \"1:1\" (quadrado, feed).",
        "- subtitlesEnabled: true ou false. subtitlePosition: \"bottom\" (embaixo), \"center\" (no meio), \"top\" (em cima). subtitleStyle: \"bold\" (destaque), \"clean\" (simples), \"boxed\" (com fundo).",
        "- language: idioma falado no vídeo: \"pt\", \"en\", \"es\" ou \"auto\".",
        "- sensitivity: 0 a 100 (maior = mais momentos). maxCandidates: quantos momentos procurar. autoRenderTop: quantos clipes gerar.",
        "- framingMode: \"auto\", \"center\" ou \"webcam-split\" (webcam em cima e jogo embaixo).",
        "",
        "Regras:",
        "- O patch traz só o que o pedido muda (normalmente 1 a 3 campos). Um campo que o pedido não menciona fica de fora: nunca copie o contexto (configuração atual, limites do plano) para o patch.",
        "- O app não posta em redes sociais, não coloca música, não aplica efeitos, filtros ou transições e não edita o conteúdo do vídeo. Esses pedidos vão em \"unsupported\": \"auto_post\", \"music\", \"effects\", \"edit_video\" ou \"other\". Nos outros pedidos, \"unsupported\" é [].",
        "- intent: \"configure\" (quer mudar algo), \"help\" (quer saber o que você faz), \"question\" (outra pergunta), \"other\".",
        "- question: uma pergunta curta em português só quando o pedido for vago e nada puder ser mudado; senão null.",
        "",
        "Exemplos (pedido, contexto e JSON):",
        "Pedido: quero clipes mais engraçados pro tiktok",
        "Contexto: foco atual=nenhum; duração atual=15-60 s (alvo 30 s); formato atual=16:9; legenda atual=ligada, embaixo, bold; idioma atual=auto; sensibilidade atual=50; limite do plano: 15 momentos e 15 clipes.",
        "{\"intent\":\"configure\",\"patch\":{\"focusCategories\":[\"funny\"],\"aspectRatio\":\"9:16\"},\"unsupported\":[],\"question\":null}",
        "Pedido: clipes de uns 20 segundos com a legenda no meio",
        "{\"intent\":\"configure\",\"patch\":{\"targetClipSeconds\":20,\"subtitlePosition\":\"center\"},\"unsupported\":[],\"question\":null}",
        "Pedido: quando eu falar \"que isso\" quero o clipe",
        "{\"intent\":\"configure\",\"patch\":{\"customKeywords\":[\"que isso\"]},\"unsupported\":[],\"question\":null}",
        "Pedido: vou postar no youtube, a live é em inglês e sem legenda",
        "{\"intent\":\"configure\",\"patch\":{\"aspectRatio\":\"16:9\",\"language\":\"en\",\"subtitlesEnabled\":false},\"unsupported\":[],\"question\":null}",
        "Pedido: pega as jogadas e os momentos de tensão",
        "{\"intent\":\"configure\",\"patch\":{\"focusCategories\":[\"gameplay\",\"tension\"]},\"unsupported\":[],\"question\":null}",
        "Pedido: posta os clipes no instagram e coloca uma música",
        "Contexto: foco atual=funny; duração atual=10-25 s (alvo 15 s); formato atual=9:16; legenda atual=ligada, embaixo, bold; idioma atual=pt; sensibilidade atual=50; limite do plano: 30 momentos e 30 clipes.",
        "{\"intent\":\"configure\",\"patch\":{},\"unsupported\":[\"auto_post\",\"music\"],\"question\":null}",
        "Pedido: o que você consegue fazer?",
        "{\"intent\":\"help\",\"patch\":{},\"unsupported\":[],\"question\":null}",
        "Pedido: deixa melhor",
        "{\"intent\":\"configure\",\"patch\":{},\"unsupported\":[],\"question\":\"O que você quer mudar: o tipo de momento, a duração ou o formato dos clipes?\"}");

    /// <summary>The model input for a conversation: earlier turns (bounded) and the last request followed by its context.</summary>
    public static AssistantPromptRequest Build(IReadOnlyList<AssistantChatMessage> conversation, AnalysisSettings current, PlanLimits limits, decimal? sourceSeconds)
    {
        var messages = new List<AssistantPromptMessage>();
        var history = conversation.Take(conversation.Count - 1).TakeLast(HistoryMessages);
        foreach (var message in history)
        {
            var content = Truncate((message.Content ?? string.Empty).Trim(), HistoryMessageLength);
            if (content.Length == 0) continue;
            messages.Add(message.Role == "assistant" ? new AssistantPromptMessage("assistant", content) : new AssistantPromptMessage("user", "Pedido: " + content));
        }
        // Chat templates expect user/assistant turns to alternate and to start with the user.
        while (messages.Count > 0 && messages[0].Role != "user") messages.RemoveAt(0);
        var request = (conversation[^1].Content ?? string.Empty).Trim();
        messages.Add(new AssistantPromptMessage("user", "Pedido: " + request + "\n" + Context(current, limits, sourceSeconds)));
        return new AssistantPromptRequest(SystemPrompt, Collapse(messages));
    }

    /// <summary>The request used to warm the prompt cache at startup (same prefix as every real request).</summary>
    public static AssistantPromptRequest Warmup()
        => new(SystemPrompt, [new AssistantPromptMessage("user", "Pedido: oi\n" + Context(new AnalysisSettings { AutoRenderTop = 15, MaxCandidates = 15 }, new PlanLimits(), null))]);

    /// <summary>Current settings and plan caps, in the format of the examples.</summary>
    public static string Context(AnalysisSettings current, PlanLimits limits, decimal? sourceSeconds)
    {
        var focus = current.FocusCategories.Count == 0 ? "nenhum" : string.Join(", ", current.FocusCategories);
        var keywords = current.CustomKeywords.Count == 0 ? "nenhuma" : string.Join(", ", current.CustomKeywords);
        var subtitles = (current.Subtitles.Enabled ? "ligada" : "desligada") + ", " + AssistantLabels.PositionName(current.Subtitles.Position) + ", " + current.Subtitles.Style;
        var plan = $"limite do plano: {limits.MaxCandidatesPerJob} momentos e {limits.MaxAutoRenderPerJob} clipes"
            + (limits.AllowCustomSubtitleStyles ? string.Empty : ", sem legenda personalizada")
            + (limits.AllowWebcamLayout ? string.Empty : ", sem webcam");
        var video = sourceSeconds is > 0 and { } seconds ? $"; vídeo de {PtText.Seconds(seconds)} s" : string.Empty;
        return $"Contexto: foco atual={focus}; palavras atuais={keywords}; duração atual={current.MinClipSeconds}-{current.MaxClipSeconds} s (alvo {current.TargetClipSeconds} s); "
            + $"formato atual={current.AspectRatio}; legenda atual={subtitles}; idioma atual={current.Language}; sensibilidade atual={current.Sensitivity}; "
            + $"momentos atuais={current.MaxCandidates}; clipes atuais={current.AutoRenderTop ?? current.MaxCandidates}; enquadramento atual={current.Framing.Mode}; {plan}{video}.";
    }

    /// <summary>Consecutive messages of the same role are joined (a dropped empty turn must not break the alternation).</summary>
    private static List<AssistantPromptMessage> Collapse(List<AssistantPromptMessage> messages)
    {
        var result = new List<AssistantPromptMessage>();
        foreach (var message in messages)
        {
            if (result.Count > 0 && result[^1].Role == message.Role)
            {
                result[^1] = result[^1] with { Content = result[^1].Content + "\n" + message.Content };
            }
            else
            {
                result.Add(message);
            }
        }
        return result;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd() + "…";

    private static readonly string[] Categories = MomentCategories.All;
    private static readonly string[] Unsupported = UnsupportedKinds.All;
    private static readonly string[] Intents = ["configure", "question", "help", "other"];

    /// <summary>JSON schema of the output for llama.cpp's <c>response_format: json_schema</c> (converted to a grammar).</summary>
    public static JsonObject LocalSchema()
    {
        static JsonObject Integer(int min, int max) => new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max };
        static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v!).ToArray()) };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["intent"] = Enum(Intents),
                ["patch"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["focusCategories"] = new JsonObject { ["type"] = "array", ["items"] = Enum(Categories), ["maxItems"] = Categories.Length },
                        ["customKeywords"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = AnalysisSettings.CustomKeywordMaxLength },
                            ["maxItems"] = AnalysisSettings.MaxCustomKeywords,
                        },
                        ["minClipSeconds"] = Integer(1, 600),
                        ["targetClipSeconds"] = Integer(1, 600),
                        ["maxClipSeconds"] = Integer(1, 600),
                        ["aspectRatio"] = Enum(AspectRatios.All),
                        ["subtitlesEnabled"] = new JsonObject { ["type"] = "boolean" },
                        ["subtitlePosition"] = Enum(SubtitleStyles.Positions),
                        ["subtitleStyle"] = Enum(SubtitleStyles.All),
                        ["language"] = Enum(EditingPreferences.Languages),
                        ["sensitivity"] = Integer(0, 100),
                        ["maxCandidates"] = Integer(1, 100),
                        ["autoRenderTop"] = Integer(0, 100),
                        ["framingMode"] = Enum(FramingModes.All),
                    },
                    ["additionalProperties"] = false,
                },
                ["unsupported"] = new JsonObject { ["type"] = "array", ["items"] = Enum(Unsupported), ["maxItems"] = Unsupported.Length },
                ["question"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["maxLength"] = 160 },
            },
            ["required"] = new JsonArray("intent", "patch", "unsupported", "question"),
            ["additionalProperties"] = false,
        };
    }

    /// <summary>The same schema in the OpenAPI subset of Gemini's <c>generationConfig.responseSchema</c>.</summary>
    public static JsonObject GeminiSchema()
    {
        static JsonObject Type(string type) => new() { ["type"] = type };
        static JsonObject Enum(params string[] values) => new() { ["type"] = "STRING", ["format"] = "enum", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v!).ToArray()) };
        return new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject
            {
                ["intent"] = Enum(Intents),
                ["patch"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject
                    {
                        ["focusCategories"] = new JsonObject { ["type"] = "ARRAY", ["items"] = Enum(Categories) },
                        ["customKeywords"] = new JsonObject { ["type"] = "ARRAY", ["items"] = Type("STRING") },
                        ["minClipSeconds"] = Type("INTEGER"),
                        ["targetClipSeconds"] = Type("INTEGER"),
                        ["maxClipSeconds"] = Type("INTEGER"),
                        ["aspectRatio"] = Enum(AspectRatios.All),
                        ["subtitlesEnabled"] = Type("BOOLEAN"),
                        ["subtitlePosition"] = Enum(SubtitleStyles.Positions),
                        ["subtitleStyle"] = Enum(SubtitleStyles.All),
                        ["language"] = Enum(EditingPreferences.Languages),
                        ["sensitivity"] = Type("INTEGER"),
                        ["maxCandidates"] = Type("INTEGER"),
                        ["autoRenderTop"] = Type("INTEGER"),
                        ["framingMode"] = Enum(FramingModes.All),
                    },
                },
                ["unsupported"] = new JsonObject { ["type"] = "ARRAY", ["items"] = Enum(Unsupported) },
                ["question"] = new JsonObject { ["type"] = "STRING", ["nullable"] = true },
            },
            ["required"] = new JsonArray("intent", "patch", "unsupported", "question"),
            ["propertyOrdering"] = new JsonArray("intent", "patch", "unsupported", "question"),
        };
    }
}
