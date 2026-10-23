using System.Globalization;
using System.Text.RegularExpressions;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Features.Assistant;

/// <summary>Requests the assistant cannot fulfil; each one gets a fixed explanation in the reply.</summary>
public static class UnsupportedKinds
{
    public const string AutoPost = "auto_post";
    public const string Music = "music";
    public const string Effects = "effects";
    public const string EditVideo = "edit_video";
    public const string Other = "other";
    public static readonly string[] All = [AutoPost, Music, Effects, EditVideo, Other];
}

/// <summary>
/// What one request asks to change, flat and not yet validated (null = not asked). The rule layer and the model each
/// produce one; <see cref="AssistantMerge"/> combines them and <see cref="AssistantPatchBuilder"/> validates the result.
/// </summary>
public sealed class RequestedChanges
{
    public List<string>? FocusCategories { get; set; }
    public List<string> AddKeywords { get; } = [];
    public List<string> TooLongKeywords { get; } = [];
    /// <summary>Keywords to take out of the current list ("tira a palavra gg", "não procura mais “gg”").</summary>
    public List<string> RemoveKeywords { get; } = [];
    public bool ClearKeywords { get; set; }
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
    /// <summary>"Todos os momentos viram clipes": autoRenderTop = maxCandidates.</summary>
    public bool AutoRenderAll { get; set; }
    public string? FramingMode { get; set; }
    public List<string> Unsupported { get; } = [];
    public bool Help { get; set; }
    /// <summary>Just a greeting ("oi", "bom dia"): answered with the help text.</summary>
    public bool Greeting { get; set; }
    /// <summary>Just thanks ("valeu", "obrigado").</summary>
    public bool Thanks { get; set; }

    /// <summary>The model's clarifying question (sanitised); the reply uses it only when nothing changes.</summary>
    public string? Question { get; set; }
    /// <summary>The model classified the request as something the assistant cannot do (used only when nothing else applies).</summary>
    public bool ModelSaysOther { get; set; }

    /// <summary>
    /// Values the message rejects, as "field=value" ("não quero reações" → focusCategories=reaction, "sem música" →
    /// unsupported=music, "nada de legenda no topo" → subtitles.position=top, "não quero 10 clipes" → autoRenderTop=10): the
    /// model may not propose them, so a negated mention never comes back as the change it names.
    /// </summary>
    public HashSet<string> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasDurations => MinClipSeconds is not null || TargetClipSeconds is not null || MaxClipSeconds is not null;

    public bool HasSettings => FocusCategories is not null || AddKeywords.Count > 0 || TooLongKeywords.Count > 0 || RemoveKeywords.Count > 0 || ClearKeywords
        || HasDurations || AspectRatio is not null || SubtitlesEnabled is not null || SubtitlePosition is not null || SubtitleStyle is not null
        || Language is not null || Sensitivity is not null || MaxCandidates is not null || AutoRenderTop is not null || AutoRenderAll
        || FramingMode is not null;

    public void Reject(string field, string value) => Rejected.Add(field + "=" + value);

    public void Reject(string field, int value) => Reject(field, value.ToString(CultureInfo.InvariantCulture));

    public bool IsRejected(string field, string? value) => value is not null && Rejected.Contains(field + "=" + value);

    public bool IsRejected(string field, int? value) => value is { } number && IsRejected(field, number.ToString(CultureInfo.InvariantCulture));

    public bool IsRejected(string field, bool? value) => value is { } flag && IsRejected(field, flag ? "true" : "false");

    public void AddUnsupported(string kind)
    {
        if (Array.IndexOf(UnsupportedKinds.All, kind) >= 0 && !Unsupported.Contains(kind)) Unsupported.Add(kind);
    }

    public void AddKeyword(string keyword)
    {
        var clean = Clean(keyword);
        if (clean.Length == 0) return;
        if (clean.Length > AnalysisSettings.CustomKeywordMaxLength)
        {
            if (!TooLongKeywords.Contains(clean, StringComparer.OrdinalIgnoreCase)) TooLongKeywords.Add(clean);
            return;
        }
        RemoveKeywords.RemoveAll(k => SameKeyword(k, clean));
        if (!AddKeywords.Contains(clean, StringComparer.OrdinalIgnoreCase)) AddKeywords.Add(clean);
    }

    public void RemoveKeyword(string keyword)
    {
        var clean = Clean(keyword);
        if (clean.Length is 0 or > AnalysisSettings.CustomKeywordMaxLength || AddKeywords.Any(k => SameKeyword(k, clean))) return;
        if (!RemoveKeywords.Any(k => SameKeyword(k, clean))) RemoveKeywords.Add(clean);
    }

    /// <summary>Keywords compare like the worker matches them: ignoring case and accents.</summary>
    public static bool SameKeyword(string a, string b)
        => string.Equals(FoldedText.Fold(AnalysisSettings.CleanKeyword(a)), FoldedText.Fold(AnalysisSettings.CleanKeyword(b)), StringComparison.Ordinal);

    private static string Clean(string keyword) => AnalysisSettings.CleanKeyword(keyword).Trim('.', ',', ';', ':', '!', '?', ' ');
}

/// <summary>
/// Deterministic rule layer: unambiguous mentions in the user's last message (platforms, explicit numbers, subtitle
/// placement, moment types, spoken language, things the product does not do). Rules decide the fields they detect; the
/// model only fills what the rules did not see, and only when the request mentions that setting (see <see cref="AssistantCues"/>).
/// A negated mention (<see cref="AssistantNegation"/>: "não quero clipes curtos", "nada de legenda no topo") is never
/// applied: a field mentioned only that way keeps its current value unless that is the rejected one (then the first default
/// the user did not reject), and the rejected values are recorded so the model cannot bring them back.
/// </summary>
public static class AssistantRules
{
    // ---------------------------------------------------------------- format
    private static readonly Regex YoutubeShorts = Rx.Create(@"\b(?:youtube|yt)\s+shorts?\b|\bshorts?\s+do\s+(?:youtube|yt)\b");
    private static readonly Regex Vertical = Rx.Create(@"\b(?:tik ?tok|reels?|shorts?|kwai|stories|story|vertical|em pe|celular|retrato)\b|\b9 ?[:x] ?16\b");
    private static readonly Regex Horizontal = Rx.Create(@"\b(?:youtube|yt|horizontal|deitad[oa]s?|widescreen|paisagem)\b|\b16 ?[:x] ?9\b");
    private static readonly Regex Square = Rx.Create(@"\b(?:quadrad[oa]s?|feed)\b|\b1 ?[:x] ?1\b");
    private static readonly string[] AspectOrder = [AspectRatios.Vertical, AspectRatios.Horizontal, AspectRatios.Square];

    // ---------------------------------------------------------------- durations
    private static readonly Regex HalfMinute = Rx.Create(@"\bmeio\s+minuto\b");
    private static readonly Regex MinuteAndHalf = Rx.Create(@"\b(?:(?:um|1)\s+)?minuto\s+e\s+meio\b");
    private static readonly Regex NumberWord = Rx.Create(
        @"\b(cento e vinte|noventa|sessenta|cinquenta|quarenta e cinco|quarenta|trinta e cinco|trinta|vinte e cinco|vinte|quinze|doze|dez|nove|oito|sete|seis|cinco|quatro|tres|dois|duas)\b(?=\s*(?:s\b|segs?\b|segundos?\b|min\b|mins\b|minutos?\b|clipes?\b|cortes?\b|videos?\b|momentos\b))");
    private static readonly Regex OneMinute = Rx.Create(@"\b(?:um|uma)\s+(?=minutos?\b)");
    private const string Unit = @"(s|segs?|segundos?|min|mins|minutos?)";
    private static readonly Regex Range = Rx.Create(
        @"\b(?:(?:entre|de)\s+(?<a>\d{1,3})\s*(?:s|segs?|segundos?)?\s*(?:e|a|ate|-)|(?<a>\d{1,3})\s*(?:s|segs?|segundos?)?\s*(?:a|ate|-))\s*(?<b>\d{1,3})\s*(?<u>s|segs?|segundos?|min|mins|minutos?)\b");
    // "não quero mais de 40 s" is an upper bound (and "não quero menos de 20 s" a lower one), not a negated length.
    private static readonly Regex MaxOnly = Rx.Create(
        @"\b(?:no maximo|maximo de|ate|nao (?:quero |precisa (?:de )?)?mais (?:de|que|do que)|menos de)\s+(\d{1,3})\s*" + Unit + @"\b");
    private static readonly Regex MinOnly = Rx.Create(
        @"\b(?:no minimo|minimo de|pelo menos|nao (?:quero |precisa (?:de )?)?menos (?:de|que|do que)|mais de)\s+(\d{1,3})\s*" + Unit + @"\b");
    private static readonly Regex Single = Rx.Create(@"\b(\d{1,3})\s*" + Unit + @"\b");
    private const string WantVerb = @"(?:quero|prefiro|prefere|deixa|deixe|faz|faca|gera|gere|so|somente|apenas)\s+(?:os\s+|uns\s+|eles\s+)?";
    private static readonly Regex Short = Rx.Create(
        @"\b(?:curtinh[oa]s?|mais curt[oa]s?|bem curt[oa]s?|rapidinh[oa]s?|(?:clipes?|cortes?|videos?|trechos?)\s+(?:bem\s+|mais\s+)?(?:curt[oa]s?|rapid[oa]s?|breves?)|"
        + WantVerb + @"(?:curt[oa]s|rapid[oa]s))\b");
    private static readonly Regex Long = Rx.Create(
        @"\b(?:mais long[oa]s?|bem long[oa]s?|mais comprid[oa]s?|(?:clipes?|cortes?|videos?|trechos?)\s+(?:bem\s+|mais\s+)?(?:long[oa]s?|comprid[oa]s?|maiores)|"
        + WantVerb + @"(?:long[oa]s|comprid[oa]s))\b");
    private static readonly Regex NegatedTail = Rx.Create(@"\b(?:nao|nem)\s+(?:muito|tao)\s*$");

    /// <summary>"Curtos": 10/15/25 s; "longos": 45/60/90 s (min/target/max).</summary>
    public static readonly (int Min, int Target, int Max) ShortClips = (10, 15, 25);
    public static readonly (int Min, int Target, int Max) LongClips = (45, 60, 90);

    // ---------------------------------------------------------------- subtitles
    private static readonly Regex SubtitleWord = Rx.Create(@"\b(?:legendas?|captions?)\b");
    private static readonly Regex SubtitlesOff = Rx.Create(
        @"\b(?:(?:sem|nada de)\s+(?:as?\s+)?(?:legendas?|captions?)|(?:tira|tirar|tire|remove|remover|desativa|desativar|desliga|desligar|esconde|esconder|dispenso|dispensa)\s+(?:as?\s+)?legendas?"
        + @"|nao\s+(?:quero|precisa(?:\s+de)?|preciso(?:\s+de)?|coloca|coloque|bota|ponha|gera|gere)\s+(?:(?:colocar|por|botar|ter|gerar|usar)\s+)?(?:as?\s+|nenhuma\s+)?legendas?"
        + @"|legendas?\s+(?:desligad|desativad)\w*)\b");
    private static readonly Regex SubtitlesOn = Rx.Create(
        @"\b(?:com\s+(?:as?\s+)?legendas?|(?:coloca|colocar|coloque|poe|por|ponha|bota|botar|ativa|ativar|liga|ligar|adiciona|adicionar|gera|gerar)\s+(?:as?\s+|uma\s+)?legendas?|legendas?\s+(?:ligad|ativad)\w*|quero\s+(?:as?\s+)?legendas?)\b");
    private static readonly (Regex Pattern, string Value)[] Positions =
    [
        (Rx.Create(@"\b(?:no meio|meio da tela|no centro|centro da tela|centralizad[oa]s?|central|do meio|do centro)\b"), SubtitleStyles.Center),
        (Rx.Create(@"\b(?:em cima|encima|no topo|no alto|parte de cima|parte superior|acima|la em cima|de cima|do topo)\b"), SubtitleStyles.Top),
        (Rx.Create(@"\b(?:embaixo|em baixo|parte de baixo|parte inferior|no rodape|abaixo|la embaixo|no pe da tela|de baixo|debaixo)\b"), SubtitleStyles.Bottom),
    ];
    private static readonly string[] PositionOrder = [SubtitleStyles.Bottom, SubtitleStyles.Center, SubtitleStyles.Top];
    private static readonly (Regex Pattern, string Value)[] Styles =
    [
        (Rx.Create(@"\b(?:limpa|simples|minimalista|discreta|clean)\b"), SubtitleStyles.Clean),
        (Rx.Create(@"\b(?:caixa|caixinha|com fundo|fundo preto|tarja|boxed)\b"), SubtitleStyles.Boxed),
        (Rx.Create(@"\b(?:negrito|grossa|destacad[ao]|em destaque|chamativa|bold)\b"), SubtitleStyles.Bold),
    ];
    private static readonly string[] StyleOrder = [SubtitleStyles.Bold, SubtitleStyles.Clean, SubtitleStyles.Boxed];
    private static readonly Regex CameraWord = Rx.Create(@"\b(?:webcam|facecam|camera|cam)\b");
    private static readonly Regex SubtitleToPlacement = Rx.Create(@"^\s*(?:(?:o|a|os|as|de|do|da|no|na|em|com|que|fica|fique|ficar|ficando)\s+)*$");
    private const int PlacementDistance = 25;

    // ---------------------------------------------------------------- focus
    public static readonly (string Category, Regex Pattern)[] Categories =
    [
        (MomentCategories.Funny, Rx.Create(@"\b(?:engracad\w*|risad\w*|rir|rindo|zoeira|zueira|zoacao|comedia|comic[oa]s?|piadas?|humor|divertid\w*|hilari\w*|k{3,}|(?:ha){2,}h?|(?:rs){2,}|memes?|palhacad\w*)\b")),
        (MomentCategories.Reaction, Rx.Create(@"\b(?:reac(?:ao|oes)|reagi\w*|reage|reagindo|sustos?|assust\w*|surpres\w*|jumpscares?)\b")),
        (MomentCategories.Hype, Rx.Create(@"\b(?:comemor\w*|hype|empolga\w*|vibrac\w*|celebr\w*|euforia|gritaria|vitorias?)\b")),
        (MomentCategories.Tension, Rx.Create(@"\b(?:tensao|tensoes|tens[oa]s?|suspense|nervos\w*|apreens\w*|clima pesado)\b")),
        (MomentCategories.Gameplay, Rx.Create(@"\b(?:jogadas?|clutch\w*|kills?|abates?|headshots?|ace|gameplay|lances?|plays?)\b")),
        (MomentCategories.Quote, Rx.Create(@"\b(?:frases?(?!\s*[""“'‘])|citac\w*|falas?\s+marcantes?|bordao|bordoes|quotes?)\b")),
        (MomentCategories.Highlight, Rx.Create(@"\b(?:melhores momentos|principais momentos|highlights?)\b")),
    ];
    // Negation right before a moment type, allowing only articles and fillers in between ("sem as jogadas", "não quero
    // reações", "menos tensão"); checked together with AssistantNegation.
    private static readonly Regex NegationTail = Rx.Create(
        @"\b(?:sem|nao|nada de|menos|tira|tirar|remove|remover|exceto|evita|evitar)\s+(?:(?:os|as|o|a|de|do|da|dos|das|mais|muito|muita|muitos|muitas|tanto|tanta|tantos|tantas|quero|gosto|precisa|nenhum|nenhuma|esses|essas|esse|essa|ter)\s+){0,2}$");
    private static readonly Regex AdditiveFocus = Rx.Create(@"\b(?:tambem|alem d[eiao]s?|junto com|mais isso)\b");
    private static readonly Regex ClearFocus = Rx.Create(
        @"\b(?:sem foco|qualquer (?:tipo|momento)|todos os tipos|tanto faz|nenhum foco|(?:tira|tirar|remove|remover|limpa|limpar)\s+o\s+foco)\b");

    // ---------------------------------------------------------------- keywords
    private static readonly Regex Quoted = Rx.Create(@"[""“”«»]([^""“”«»\r\n]{1,80})[""“”«»]|(?<![\p{L}\p{N}])['‘’]([^'‘’\r\n]{1,80})['‘’](?![\p{L}\p{N}])");
    private static readonly Regex SpokenCue = Rx.Create(
        @"\b(?:quando|sempre que|toda vez que)\s+(?:eu\s+|ele\s+|ela\s+|alguem\s+|a gente\s+|o chat\s+)?(?:falar|fala|falo|falarem|disser|diz|digo|gritar|grita|grito|mencionar|menciona)\s+(?<kw>[^,.;!?""“”«»'‘’\r\n]{1,60})");
    // "palavras-chave (são|:)" is tried before "as palavras", so "as palavras chave são gg, ez" lists "gg, ez".
    private static readonly Regex WordCue = Rx.Create(
        @"\b(?:(?:as?\s+)?palavras?[ -]chaves?(?:\s+(?:sao|seriam|serao|e)\b)?|a palavra|as palavras|o termo|os termos|a expressao|as expressoes)\s*:?\s+(?<kw>[^.;!?""“”«»'‘’\r\n]{1,80})");
    private static readonly Regex ClearKeywords = Rx.Create(
        @"\b(?:sem palavras?[ -]chaves?|(?:tira|tirar|remove|remover|limpa|limpar|apaga|apagar)\s+(?:as\s+|todas as\s+)?palavras?(?:[ -]chaves?)?)\b");
    private static readonly Regex KeywordListSeparator = new(@",|\s+e\s+|\s+ou\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly HashSet<string> KeywordStopWords = new(StringComparer.Ordinal)
    {
        "e", "eh", "ou", "com", "pro", "pra", "para", "no", "na", "nos", "nas", "mas", "porque", "tambem", "sao",
    };
    /// <summary>An imperative or setting verb starts another request: a keyword, and a keyword list, end before it.</summary>
    private static readonly HashSet<string> KeywordBreakVerbs = new(StringComparer.Ordinal)
    {
        "quero", "queria", "prefiro", "coloca", "coloque", "colocar", "bota", "bote", "botar", "poe", "por", "ponha", "deixa", "deixe", "deixar",
        "faz", "faca", "fazer", "muda", "mude", "mudar", "tira", "tire", "tirar", "remove", "remova", "remover", "troca", "troque", "trocar",
        "usa", "use", "usar", "gera", "gere", "gerar", "ativa", "ative", "ativar", "liga", "ligue", "ligar", "desliga", "desligue",
        "aumenta", "aumente", "diminui", "diminua", "ajusta", "ajuste", "pega", "pegue", "procura", "procure", "marca", "marque",
        "adiciona", "adicione", "inclui", "inclua", "manda", "mande", "posta", "poste",
    };
    /// <summary>Skipped at the start of a keyword: articles and a repeated cue ("a palavra clutch e a palavra ace").</summary>
    private static readonly HashSet<string> LeadingArticles = new(StringComparer.Ordinal)
    {
        "o", "a", "os", "as", "um", "uma", "palavra", "palavras", "palavra-chave", "palavras-chave", "termo", "termos", "expressao", "expressoes",
    };
    private static readonly HashSet<string> TrailingFillers = new(StringComparer.Ordinal)
    {
        "o", "a", "os", "as", "um", "uma", "de", "do", "da", "dos", "das", "em", "no", "na", "com", "pra", "pro", "para",
    };

    // ---------------------------------------------------------------- language
    private static readonly Regex ChatLanguage = Rx.Create(
        @"\b(?:responde|responda|fala comigo|escreve|escreva|me responda|conversa)\s+(?:comigo\s+)?(?:em\s+)?(?:ingles|portugues|espanhol)\b");
    private static readonly Regex Translation = Rx.Create(@"\b(?:traduz\w*|traducao|traducoes|traduzid[oa]s?)\b");
    private static readonly (string Code, Regex Pattern)[] Languages =
    [
        ("en", Rx.Create(@"\b(?:ingles|english)\b")),
        ("pt", Rx.Create(@"\b(?:portugues|pt-?br)\b")),
        ("es", Rx.Create(@"\b(?:espanhol|espanol|spanish|castelhano)\b")),
        ("auto", Rx.Create(@"\b(?:detect\w*\s+(?:o\s+)?idioma|idioma\s+automatic\w*|automatic\w*\s+o\s+idioma)\b")),
    ];
    private static readonly string[] LanguageOrder = ["auto", "pt", "en", "es"];

    // ---------------------------------------------------------------- counts, sensitivity, framing
    private static readonly Regex ClipCount = Rx.Create(@"\b(\d{1,3})\s*(?:clipes?|cortes?|videos?|shorts)\b");
    private static readonly Regex MomentCount = Rx.Create(@"\b(\d{1,3})\s*(?:momentos|candidatos)\b");
    private static readonly Regex AllMoments = Rx.Create(
        @"\b(?:todos os momentos (?:encontrados|viram clipes?|em clipes?)|todos viram clipes?|o maximo de clipes|todos os clipes possiveis|clipes? de todos os momentos encontrados)\b");
    private static readonly Regex SensitivityValue = Rx.Create(@"\bsensibilidade\s*(?:de|em|para|pra|=|:|no|na)?\s*(\d{1,3})\b");
    private static readonly Regex SensitivityUp = Rx.Create(
        @"\b(?:mais sensivel|aumenta\w*\s+(?:a\s+)?sensibilidade|sensibilidade\s+(?:mais\s+)?alta|pega\w*\s+mais\s+(?:momentos|coisas)|mais momentos)\b");
    private static readonly Regex SensitivityDown = Rx.Create(
        @"\b(?:menos sensivel|diminu\w*\s+(?:a\s+)?sensibilidade|sensibilidade\s+(?:mais\s+)?baixa|menos momentos|so os melhores|mais exigente|mais rigoroso)\b");
    private static readonly Regex WebcamOff = Rx.Create(
        @"\b(?:sem|tira|tirar|tire|remove|remover|desliga|desligar|desativa|desativar|nada de|nao\s+(?:quero|preciso|precisa|uso|tenho|tem)(?:\s+de)?)\s+"
        + @"(?:(?:o|a)\s+)?(?:(?:layout|modo)\s+(?:de|da|com)\s+(?:a\s+)?)?(?:webcam|facecam|camera)\b");
    private static readonly Regex WebcamOn = Rx.Create(
        @"\b(?:webcam|facecam|layout com (?:a )?(?:camera|cam)|(?:camera|cam) (?:em cima|separad\w*|dividid\w*))\b");
    private static readonly Regex FramingCenter = Rx.Create(
        @"\b(?:enquadramento\s+(?:central|centralizado|no centro)|centraliz\w*\s+(?:o\s+)?(?:video|enquadramento|imagem))\b");
    private static readonly Regex FramingAuto = Rx.Create(@"\benquadramento\s+automatic\w*\b");
    private static readonly string[] FramingOrder = [FramingModes.Auto, FramingModes.Center, FramingModes.WebcamSplit];

    // ---------------------------------------------------------------- unsupported, help
    private static readonly Regex AutoPost = Rx.Create(
        @"\b(?:posta|poste|postem|publique|publiquem|agende|compartilhe)\b|\b(?:publica|agenda|compartilha)\s+(?:no|na|nos|nas|em|pra|para|os|as|o|a|meus|minhas|esses|essas)\b|\b(?:pode|consegue|da pra|da para|quero que (?:voce|vc)|tem como|faz)\s+(?:\w+\s+){0,2}(?:postar|publicar|agendar|compartilhar|subir|enviar|mandar)\b|\b(?:postar|publicar|subir|enviar|mandar)\s+(?:\w+\s+){0,3}(?:pra mim|por mim|automaticamente|direto|sozinho)\b|\bpostagem automatica\b");
    private static readonly Regex Music = Rx.Create(@"\b(?:musica\w*|trilha(?:\s+sonora)?|som de fundo|audio de fundo|batida|beat|soundtrack)\b");
    private static readonly Regex QuoteEffect = Rx.Create(@"\bfrases?\s+de\s+efeito\b");
    private static readonly Regex Effects = Rx.Create(
        @"\b(?:efeitos?|transic(?:ao|oes)|filtros?|zoom|emojis?|stickers?|figurinhas?|vinhetas?|animac(?:ao|oes)|slow ?motion|camera lenta|glitch)\b");
    private static readonly Regex EditVideo = Rx.Create(
        @"\b(?:edit(?:a|ar|e|em)\s+(?:o|meu|a|minha|os|meus)\s+(?:video|videos|live|lives|gravac\w*)|(?:corta|cortar|corte)\s+(?:o|meu|a|minha)\s+(?:video|live|gravac\w*)|(?:remove|remover|apaga|apagar|tira|tirar)\s+(?:a|o|essa|esse|uma|um|as|os)\s+(?:parte|partes|trecho|trechos|pedaco|cena|cenas)|aceler\w*|narrac\w*|dubla\w*|muda\w*\s+(?:a\s+)?voz|desfoc\w*|blur|censur\w*|bipa\w*)\b");
    private static readonly Regex Greeting = Rx.Create(@"^\W*(?:oi+|ola|opa|eai|e ai|salve|bom dia|boa tarde|boa noite|hello|hi)\b\W*(?:tudo bem\W*)?$");
    private static readonly Regex Thanks = Rx.Create(@"^\W*(?:(?:muito\s+)?obrigad[oa]s?|valeu|vlw|brigad[oa]|thanks|obg)\b(?:\W*(?:mesmo|demais|ai))?\W*$");
    private static readonly Regex Help = Rx.Create(
        @"\b(?:ajuda|help|o que (?:voce|vc|tu) (?:faz|pode|consegue|sabe)|como (?:voce |vc )?funciona|como (?:eu )?(?:te )?uso|o que (?:da|eu posso) (?:pra |para )?(?:fazer|mudar|configurar)|quais (?:sao )?(?:as )?(?:opcoes|configuracoes))\b");

    /// <summary>Applies every rule to the user's last message.</summary>
    public static RequestedChanges Analyze(string message, AnalysisSettings current)
    {
        var text = new FoldedText(message);
        var result = new RequestedChanges();
        DetectAspect(text, result, current);
        DetectDurations(text, result, current);
        DetectSubtitles(text, result, current);
        DetectFocus(text, result, current);
        DetectKeywords(text, result);
        DetectLanguage(text, result, current);
        DetectCounts(text, result);
        DetectSensitivity(text, result, current);
        DetectFraming(text, result, current);
        DetectUnsupported(text, result);
        result.Help = text.Has(Help) && !result.HasSettings;
        result.Greeting = text.Has(Greeting);
        result.Thanks = text.Has(Thanks);
        return result;
    }

    private static bool Negated(FoldedText text, Match match) => AssistantNegation.IsNegated(text, match.Index, match.Length);

    private static bool Inside(Match match, IEnumerable<Match> spans) => spans.Any(s => match.Index >= s.Index && match.Index < s.Index + s.Length);

    /// <summary>
    /// Sets a single-valued field from its mentions. One value mentioned (not negated): that value; several: ambiguous (the
    /// model decides). Only negated mentions: the current value when the user did not reject it (the reply says it is
    /// already set), else the first of <paramref name="order"/> the user did not reject. Rejected values are recorded.
    /// </summary>
    private static void Decide(RequestedChanges result, string field, IReadOnlyCollection<string> found, IReadOnlyCollection<string> rejected,
        string? current, string[] order, Action<string> set)
    {
        var positive = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var refused = rejected.Distinct(StringComparer.OrdinalIgnoreCase).Where(v => !positive.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var value in refused) result.Reject(field, value);
        if (positive.Count == 1)
        {
            set(positive[0]);
            return;
        }
        if (positive.Count > 1 || refused.Count == 0) return;
        var keep = current is not null && !refused.Contains(current, StringComparer.OrdinalIgnoreCase)
            ? current
            : order.FirstOrDefault(v => !refused.Contains(v, StringComparer.OrdinalIgnoreCase));
        if (keep is not null) set(keep);
    }

    private static void DetectAspect(FoldedText text, RequestedChanges result, AnalysisSettings current)
    {
        // "YouTube Shorts" is vertical: the "youtube" of that phrase is not the horizontal platform.
        var shorts = YoutubeShorts.Matches(text.Folded).ToList();
        var found = new List<string>();
        var rejected = new List<string>();
        void Mention(Match match, string value) => (Negated(text, match) ? rejected : found).Add(value);
        foreach (Match match in Vertical.Matches(text.Folded)) Mention(match, AspectRatios.Vertical);
        foreach (Match match in Horizontal.Matches(text.Folded))
        {
            if (!Inside(match, shorts)) Mention(match, AspectRatios.Horizontal);
        }
        foreach (Match match in Square.Matches(text.Folded)) Mention(match, AspectRatios.Square);
        Decide(result, "aspectRatio", found, rejected, current.AspectRatio, AspectOrder, value => result.AspectRatio = value);
    }

    /// <summary>
    /// The folded text with spoken numbers before a unit or count noun turned into digits ("trinta segundos" → "30 segundos"),
    /// padded with spaces so that every position still points at the same place of the message (negation checks use it).
    /// </summary>
    private static string Numeric(string folded)
    {
        var text = Replace(MinuteAndHalf, folded, _ => "90 s");
        text = Replace(HalfMinute, text, _ => "30 s");
        text = Replace(OneMinute, text, _ => "1 ");
        return Replace(NumberWord, text, m => m.Groups[1].Value switch
        {
            "cento e vinte" => "120", "noventa" => "90", "sessenta" => "60", "cinquenta" => "50", "quarenta e cinco" => "45",
            "quarenta" => "40", "trinta e cinco" => "35", "trinta" => "30", "vinte e cinco" => "25", "vinte" => "20", "quinze" => "15",
            "doze" => "12", "dez" => "10", "nove" => "9", "oito" => "8", "sete" => "7", "seis" => "6", "cinco" => "5", "quatro" => "4",
            "tres" => "3", _ => "2",
        });
    }

    private static string Replace(Regex pattern, string text, Func<Match, string> replacement)
        => pattern.Replace(text, m => replacement(m) is var digits && digits.Length <= m.Length ? digits.PadRight(m.Length) : m.Value);

    private static string Blank(string text, Match match)
        => string.Concat(text.AsSpan(0, match.Index), new string(' ', match.Length), text.AsSpan(match.Index + match.Length));

    private static int Seconds(string number, string unit)
    {
        var value = int.Parse(number, CultureInfo.InvariantCulture);
        return unit.StartsWith("min", StringComparison.Ordinal) ? value * 60 : value;
    }

    private static void SetDurations(RequestedChanges result, (int Min, int Target, int Max) durations)
    {
        result.MinClipSeconds = durations.Min;
        result.TargetClipSeconds = durations.Target;
        result.MaxClipSeconds = durations.Max;
    }

    private static void DetectDurations(FoldedText text, RequestedChanges result, AnalysisSettings current)
    {
        var numeric = Numeric(text.Folded);
        var rest = numeric;

        foreach (Match range in Range.Matches(numeric))
        {
            var unit = range.Groups["u"].Value;
            var a = Seconds(range.Groups["a"].Value, unit);
            var b = Seconds(range.Groups["b"].Value, unit);
            if (Negated(text, range))
            {
                result.Reject("clipSeconds", a);
                result.Reject("clipSeconds", b);
                rest = Blank(rest, range);
                continue;
            }
            var (min, max) = a <= b ? (a, b) : (b, a);
            SetDurations(result, (min, (int)Math.Round((min + max) / 2.0, MidpointRounding.AwayFromZero), max));
            return;
        }

        foreach (Match match in MaxOnly.Matches(rest))
        {
            rest = Blank(rest, match);
            var seconds = Seconds(match.Groups[1].Value, match.Groups[2].Value);
            if (Negated(text, match)) result.Reject("clipSeconds", seconds);
            else result.MaxClipSeconds ??= seconds;
        }
        foreach (Match match in MinOnly.Matches(rest))
        {
            rest = Blank(rest, match);
            var seconds = Seconds(match.Groups[1].Value, match.Groups[2].Value);
            if (Negated(text, match)) result.Reject("clipSeconds", seconds);
            else result.MinClipSeconds ??= seconds;
        }

        var singles = new List<int>();
        foreach (Match match in Single.Matches(rest))
        {
            var seconds = Seconds(match.Groups[1].Value, match.Groups[2].Value);
            if (Negated(text, match)) result.Reject("clipSeconds", seconds);
            else singles.Add(seconds);
        }
        singles = singles.Distinct().ToList();
        if (singles.Count == 1)
        {
            result.TargetClipSeconds = singles[0];
            return;
        }
        if (singles.Count > 1 || result.HasDurations) return; // two different lengths: ambiguous, ask the model / the user

        bool wantsShort = false, wantsLong = false, refusesShort = false, refusesLong = false;
        foreach (Match match in Short.Matches(text.Folded))
        {
            if (NegatedLength(text, match)) refusesShort = true;
            else wantsShort = true;
        }
        foreach (Match match in Long.Matches(text.Folded))
        {
            if (NegatedLength(text, match)) refusesLong = true;
            else wantsLong = true;
        }
        if (wantsShort != wantsLong)
        {
            SetDurations(result, wantsShort ? ShortClips : LongClips);
            return;
        }
        if (wantsShort || !(refusesShort || refusesLong)) return;

        // Only refused lengths ("não quero clipes curtos", "nada de clipes longos"): the current lengths stay unless they are
        // what the user refuses; then the defaults (15/30/60 s), which are neither short nor long.
        var target = current.TargetClipSeconds;
        var refusedNow = (refusesShort && target <= ShortClips.Max) || (refusesLong && target >= LongClips.Min);
        var defaults = new AnalysisSettings();
        SetDurations(result, refusedNow
            ? (defaults.MinClipSeconds, defaults.TargetClipSeconds, defaults.MaxClipSeconds)
            : (current.MinClipSeconds, current.TargetClipSeconds, current.MaxClipSeconds));
    }

    private static bool NegatedLength(FoldedText text, Match match)
        => Negated(text, match) || NegatedTail.IsMatch(text.Folded[Math.Max(0, match.Index - 15)..match.Index]);

    private static void DetectSubtitles(FoldedText text, RequestedChanges result, AnalysisSettings current)
    {
        var offs = SubtitlesOff.Matches(text.Folded).ToList();
        bool off = false, on = false;
        foreach (var match in offs)
        {
            if (Negated(text, match)) continue; // "não quero ficar sem legenda"
            if (PlacementFollows(text, match))
            {
                // "não quero legenda no meio", "nada de legenda no topo": the placement is refused, not the subtitles.
                result.Reject("subtitles.enabled", "false");
                continue;
            }
            off = true;
        }
        foreach (Match match in SubtitlesOn.Matches(text.Folded))
        {
            if (Inside(match, offs)) continue; // the "quero legenda" of "não quero legenda"
            if (!Negated(text, match)) on = true;
            else if (!PlacementFollows(text, match)) result.Reject("subtitles.enabled", "true");
        }
        if (off) result.SubtitlesEnabled = false;
        else if (on) result.SubtitlesEnabled = true;

        var mentions = SubtitleWord.Matches(text.Folded).ToList();
        if (mentions.Count == 0) return;
        var cameras = CameraWord.Matches(text.Folded).ToList();
        var subtitles = current.Subtitles ?? new SubtitleSettings();
        Placement(text, result, Positions, mentions, cameras, "subtitles.position", subtitles.Position, PositionOrder, value => result.SubtitlePosition = value);
        Placement(text, result, Styles, mentions, cameras, "subtitles.style", subtitles.Style, StyleOrder, value => result.SubtitleStyle = value);
        // Refusing where or how the subtitles show ("a legenda não pode ficar no meio") is not asking to turn them off.
        if (!off && result.Rejected.Any(r => r.StartsWith("subtitles.position=", StringComparison.Ordinal) || r.StartsWith("subtitles.style=", StringComparison.Ordinal)))
        {
            result.Reject("subtitles.enabled", "false");
        }
    }

    /// <summary>A subtitle position or style later in the same clause (not one that belongs to the webcam: "sem legenda webcam em cima").</summary>
    private static bool PlacementFollows(FoldedText text, Match match)
    {
        var from = match.Index + match.Length;
        var tail = text.Folded[from..AssistantNegation.ClauseEnd(text, from)];
        if (CameraWord.Match(tail) is { Success: true } camera) tail = tail[..camera.Index];
        return Positions.Any(p => p.Pattern.IsMatch(tail)) || Styles.Any(s => s.Pattern.IsMatch(tail));
    }

    /// <summary>
    /// The value whose phrase is closest to a "legenda" mention (at most 25 characters away, same sentence and nearer to
    /// it than to a webcam mention, so "legenda embaixo e webcam em cima" is read correctly). Ties between different
    /// values are ambiguous. Negated phrases ("não quero a legenda em cima") are refused instead (<see cref="Decide"/>).
    /// </summary>
    private static void Placement(FoldedText text, RequestedChanges result, (Regex Pattern, string Value)[] options, List<Match> mentions, List<Match> cameras,
        string field, string? current, string[] order, Action<string> set)
    {
        var folded = text.Folded;
        string? best = null;
        var bestDistance = int.MaxValue;
        var tie = false;
        var rejected = new List<string>();
        foreach (var (pattern, value) in options)
        {
            foreach (Match match in pattern.Matches(folded))
            {
                var distance = mentions.Min(m => Distance(folded, m, match));
                if (distance > PlacementDistance) continue;
                if (cameras.Count > 0 && cameras.Min(c => Distance(folded, c, match)) < distance) continue;
                if (Negated(text, match) || AfterRefusedSubtitles(text, mentions, match))
                {
                    rejected.Add(value);
                }
                else if (distance < bestDistance)
                {
                    best = value;
                    bestDistance = distance;
                    tie = false;
                }
                else if (distance == bestDistance && value != best)
                {
                    tie = true;
                }
            }
        }
        if (tie)
        {
            foreach (var value in rejected.Distinct()) result.Reject(field, value);
            return;
        }
        Decide(result, field, best is null ? Array.Empty<string>() : new[] { best }, rejected, current, order, set);
    }

    /// <summary>A placement right after a refused "legenda" ("nada de legenda no topo", "tira a legenda de cima").</summary>
    private static bool AfterRefusedSubtitles(FoldedText text, List<Match> mentions, Match placement)
        => mentions.Any(m => m.Index + m.Length <= placement.Index
            && SubtitleToPlacement.IsMatch(text.Folded.AsSpan(m.Index + m.Length, placement.Index - m.Index - m.Length))
            && Negated(text, m));

    private static int Distance(string folded, Match a, Match b)
    {
        var (start, end) = a.Index <= b.Index ? (a.Index + a.Length, b.Index) : (b.Index + b.Length, a.Index);
        if (end < start) return 0; // overlapping
        var between = folded[start..end];
        return between.IndexOfAny(['.', ';', '!', '?', '\n']) >= 0 ? int.MaxValue : end - start;
    }

    private static void DetectFocus(FoldedText text, RequestedChanges result, AnalysisSettings current)
    {
        var mentioned = new List<(int Index, string Category)>();
        var removed = new List<string>();
        foreach (var (category, pattern) in Categories)
        {
            foreach (Match match in pattern.Matches(text.Folded))
            {
                if (IsNegatedType(text.Folded, match.Index) || Negated(text, match))
                {
                    if (!removed.Contains(category)) removed.Add(category);
                }
                else
                {
                    mentioned.Add((match.Index, category));
                }
            }
        }
        foreach (var category in removed) result.Reject("focusCategories", category);
        var positive = mentioned.OrderBy(m => m.Index).Select(m => m.Category).Distinct().Where(c => !removed.Contains(c)).ToList();

        if (positive.Count == 0 && text.Has(ClearFocus))
        {
            result.FocusCategories = [];
            return;
        }
        var currentFocus = AnalysisSettings.NormalizeFocusCategories(current.FocusCategories);
        if (positive.Count > 0)
        {
            result.FocusCategories = text.Has(AdditiveFocus)
                ? AnalysisSettings.NormalizeFocusCategories(currentFocus.Where(c => !removed.Contains(c)).Concat(positive))
                : positive;
        }
        else if (removed.Any(currentFocus.Contains))
        {
            result.FocusCategories = currentFocus.Where(c => !removed.Contains(c)).ToList();
        }
    }

    private static bool IsNegatedType(string folded, int index)
    {
        var start = Math.Max(0, index - 25);
        var tail = folded[start..index];
        var cut = tail.LastIndexOfAny([',', '.', ';', '!', '?']);
        if (cut >= 0) tail = tail[(cut + 1)..];
        return NegationTail.IsMatch(tail);
    }

    private static void DetectKeywords(FoldedText text, RequestedChanges result)
    {
        var lists = new List<Match>();
        foreach (Match match in WordCue.Matches(text.Folded))
        {
            var group = match.Groups["kw"];
            var keywords = KeywordList(text.OriginalSpan(group.Index, group.Length)).ToList();
            if (keywords.Count > 0) lists.Add(match);
            foreach (var keyword in keywords) Keyword(text, result, match, keyword);
        }

        // "tira as palavras-chave" clears the list; "tira as palavras gg e ez" only takes those out.
        foreach (Match match in ClearKeywords.Matches(text.Folded))
        {
            var targeted = lists.Any(l => l.Index < match.Index + match.Length && match.Index < l.Index + l.Length);
            if (!targeted && !Negated(text, match)) result.ClearKeywords = true;
        }

        foreach (Match match in Quoted.Matches(text.Original))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            // A quoted word or short expression is something to look for; a quoted sentence is just the user quoting.
            if (value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 5) Keyword(text, result, match, value);
        }

        foreach (Match match in SpokenCue.Matches(text.Folded))
        {
            var group = match.Groups["kw"];
            var spoken = text.OriginalSpan(group.Index, group.Length);
            // "quando eu falar de/sobre X" is a topic, not a word to look for.
            var start = FoldedText.Fold(spoken).TrimStart();
            if (start.StartsWith("de ", StringComparison.Ordinal) || start.StartsWith("sobre ", StringComparison.Ordinal)) continue;
            if (CutKeyword(spoken).Keyword is { } keyword) Keyword(text, result, match, keyword);
        }
    }

    /// <summary>
    /// A keyword the user named. A setting or moment-type word ("legenda", "os clipes curtos", "engraçados") is the request
    /// itself and is dropped (game terms such as "clutch" stay: the user named them as words); a negated mention ("tira a
    /// palavra gg") takes the keyword out of the list instead.
    /// </summary>
    private static void Keyword(FoldedText text, RequestedChanges result, Match cue, string keyword)
    {
        if (AssistantCues.IsSettingPhrase(keyword, allowGameTerms: true)) return;
        if (Negated(text, cue)) result.RemoveKeyword(keyword);
        else result.AddKeyword(keyword);
    }

    /// <summary>
    /// The keywords of an unquoted list ("gg, ez e clutch"). The list ends where another request starts — an imperative
    /// ("coloca", "deixa", "quero"…) or a setting word ("legenda", "clipes"…) — so "a palavra gg e coloca legenda no meio"
    /// lists only "gg".
    /// </summary>
    private static IEnumerable<string> KeywordList(string list)
    {
        foreach (var item in KeywordListSeparator.Split(list))
        {
            var (keyword, endsList) = CutKeyword(item);
            if (keyword is not null) yield return keyword;
            if (endsList) yield break;
        }
    }

    /// <summary>
    /// An unquoted keyword: leading connectives and articles are skipped, it ends at the next connective ("clutch e quero…" →
    /// "clutch") and has at most 4 words. <c>EndsList</c>: it stopped at an imperative or a setting word.
    /// </summary>
    private static (string? Keyword, bool EndsList) CutKeyword(string raw)
    {
        var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var taken = new List<string>();
        var endsList = false;
        foreach (var word in words)
        {
            var folded = FoldedText.Fold(word).Trim('.', ',', ';', ':', '!', '?');
            var isStopWord = KeywordStopWords.Contains(folded);
            if (taken.Count == 0 && (isStopWord || LeadingArticles.Contains(folded))) continue;
            if (KeywordBreakVerbs.Contains(folded) || AssistantCues.IsSettingToken(folded, allowGameTerms: true))
            {
                endsList = true;
                break;
            }
            if (isStopWord) break;
            taken.Add(word);
            if (taken.Count == 4) break;
        }
        while (taken.Count > 0 && TrailingFillers.Contains(FoldedText.Fold(taken[^1]))) taken.RemoveAt(taken.Count - 1);
        var keyword = string.Join(' ', taken).Trim('.', ',', ';', ':', '!', '?', ' ');
        return (keyword.Length == 0 ? null : keyword, endsList);
    }

    private static void DetectLanguage(FoldedText text, RequestedChanges result, AnalysisSettings current)
    {
        if (Translation.Matches(text.Folded).Any(m => !Negated(text, m)))
        {
            // Subtitles come out in the spoken language: a translation is not something the analysis does.
            result.AddUnsupported(UnsupportedKinds.Other);
            return;
        }
        var chat = ChatLanguage.Matches(text.Folded).ToList(); // "responde em português" is about the chat, not the video
        var found = new List<string>();
        var rejected = new List<string>();
        foreach (var (code, pattern) in Languages)
        {
            foreach (Match match in pattern.Matches(text.Folded))
            {
                if (!Inside(match, chat)) (Negated(text, match) ? rejected : found).Add(code);
            }
        }
        Decide(result, "language", found, rejected, current.Language, LanguageOrder, value => result.Language = value);
    }

    private static void DetectCounts(FoldedText text, RequestedChanges result)
    {
        var numeric = Numeric(text.Folded);
        var clips = Numbers(text, numeric, ClipCount, result, "autoRenderTop");
        if (clips.Count == 1) result.AutoRenderTop = clips[0];
        else if (clips.Count == 0 && AllMoments.Matches(text.Folded).Any(m => !Negated(text, m))) result.AutoRenderAll = true;

        var moments = Numbers(text, numeric, MomentCount, result, "maxCandidates");
        if (moments.Count == 1) result.MaxCandidates = moments[0];
    }

    /// <summary>The distinct numbers of a count pattern; negated ones ("não quero 10 clipes") are rejected instead.</summary>
    private static List<int> Numbers(FoldedText text, string numeric, Regex pattern, RequestedChanges result, string field)
    {
        var values = new List<int>();
        foreach (Match match in pattern.Matches(numeric))
        {
            var value = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (Negated(text, match)) result.Reject(field, value);
            else values.Add(value);
        }
        return values.Distinct().ToList();
    }

    private static void DetectSensitivity(FoldedText text, RequestedChanges result, AnalysisSettings current)
    {
        foreach (Match match in SensitivityValue.Matches(text.Folded))
        {
            var value = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (Negated(text, match))
            {
                result.Reject("sensitivity", value);
                continue;
            }
            result.Sensitivity = value;
            return;
        }
        bool up = false, down = false, refused = false;
        foreach (Match match in SensitivityUp.Matches(text.Folded))
        {
            if (Negated(text, match)) refused = true;
            else up = true;
        }
        foreach (Match match in SensitivityDown.Matches(text.Folded))
        {
            if (Negated(text, match)) refused = true;
            else down = true;
        }
        if (up != down) result.Sensitivity = Math.Clamp(current.Sensitivity + (up ? 20 : -20), 0, 100);
        else if (!up && refused) result.Sensitivity = current.Sensitivity; // "não quero mais sensível": it stays as it is
    }

    private static void DetectFraming(FoldedText text, RequestedChanges result, AnalysisSettings current)
    {
        var found = new List<string>();
        var rejected = new List<string>();
        // "sem a webcam", "não quero o layout de webcam", "nada de webcam" (but not "não quero tirar a webcam").
        var offs = WebcamOff.Matches(text.Folded).Where(m => !Negated(text, m)).ToList();
        if (offs.Count > 0) rejected.Add(FramingModes.WebcamSplit);
        foreach (Match match in WebcamOn.Matches(text.Folded))
        {
            if (Inside(match, offs)) continue;
            if (Negated(text, match)) rejected.Add(FramingModes.WebcamSplit);
            else if (offs.Count == 0) found.Add(FramingModes.WebcamSplit);
        }
        foreach (Match match in FramingCenter.Matches(text.Folded)) (Negated(text, match) ? rejected : found).Add(FramingModes.Center);
        foreach (Match match in FramingAuto.Matches(text.Folded)) (Negated(text, match) ? rejected : found).Add(FramingModes.Auto);
        Decide(result, "framing.mode", found, rejected, current.Framing?.Mode ?? FramingModes.Auto, FramingOrder, value => result.FramingMode = value);
    }

    private static void DetectUnsupported(FoldedText text, RequestedChanges result)
    {
        var quoteEffects = QuoteEffect.Matches(text.Folded).ToList(); // "frases de efeito" are quotes, not effects
        Unsupported(text, result, AutoPost, UnsupportedKinds.AutoPost, []);
        Unsupported(text, result, Music, UnsupportedKinds.Music, []);
        Unsupported(text, result, Effects, UnsupportedKinds.Effects, quoteEffects);
        Unsupported(text, result, EditVideo, UnsupportedKinds.EditVideo, []);
    }

    /// <summary>An unsupported request; when it is only refused ("sem música") there is nothing to explain.</summary>
    private static void Unsupported(FoldedText text, RequestedChanges result, Regex pattern, string kind, List<Match> exclude)
    {
        bool asked = false, refused = false;
        foreach (Match match in pattern.Matches(text.Folded))
        {
            if (Inside(match, exclude)) continue;
            if (Negated(text, match)) refused = true;
            else asked = true;
        }
        if (asked) result.AddUnsupported(kind);
        else if (refused) result.Reject("unsupported", kind);
    }
}
