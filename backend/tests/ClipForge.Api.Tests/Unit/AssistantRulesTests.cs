using ClipForge.Api.Features.Assistant;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Tests.Unit;

/// <summary>The deterministic rule layer of the assistant (unambiguous mentions in the user's message).</summary>
public class AssistantRulesTests
{
    private static AnalysisSettings Current(Action<AnalysisSettings>? change = null)
    {
        var settings = new AnalysisSettings();
        change?.Invoke(settings);
        return PlanLimitHelpers.CapSettings(settings, new PlanLimits { MaxCandidatesPerJob = 30, MaxAutoRenderPerJob = 30, AllowCustomSubtitleStyles = true, AllowWebcamLayout = true });
    }

    private static RequestedChanges Rules(string message, AnalysisSettings? current = null) => AssistantRules.Analyze(message, current ?? Current());

    // ---------------------------------------------------------------- format

    [Theory]
    [InlineData("quero clipes pro tiktok")]
    [InlineData("vou usar nos Reels")]
    [InlineData("é pro TikTok")]
    [InlineData("pros shorts")]
    [InlineData("quero no youtube shorts")]
    [InlineData("formato vertical, 9:16")]
    public void Vertical_platforms_mean_9_16(string message) => Rules(message).AspectRatio.ShouldBe("9:16");

    [Theory]
    [InlineData("vou subir no YouTube")]
    [InlineData("quero horizontal")]
    [InlineData("formato 16:9")]
    public void YouTube_and_horizontal_mean_16_9(string message) => Rules(message).AspectRatio.ShouldBe("16:9");

    [Theory]
    [InlineData("formato quadrado")]
    [InlineData("é pro feed do instagram")]
    public void Square_and_feed_mean_1_1(string message) => Rules(message).AspectRatio.ShouldBe("1:1");

    [Fact]
    public void Two_different_platforms_are_ambiguous() => Rules("quero pro tiktok e pro youtube").AspectRatio.ShouldBeNull();

    // ---------------------------------------------------------------- clip lengths

    [Theory]
    [InlineData("quero clipes curtinhos")]
    [InlineData("clipes mais curtos")]
    [InlineData("cortes bem rápidos")]
    [InlineData("quero curtos")]
    public void Short_clips_are_10_15_25(string message)
    {
        var rules = Rules(message);
        (rules.MinClipSeconds, rules.TargetClipSeconds, rules.MaxClipSeconds).ShouldBe((10, 15, 25));
    }

    [Theory]
    [InlineData("quero clipes mais longos")]
    [InlineData("clipes longos")]
    [InlineData("prefiro os longos")]
    public void Long_clips_are_45_60_90(string message)
    {
        var rules = Rules(message);
        (rules.MinClipSeconds, rules.TargetClipSeconds, rules.MaxClipSeconds).ShouldBe((45, 60, 90));
    }

    [Theory]
    [InlineData("clipes de uns 30 segundos", 30)]
    [InlineData("clipes de 45s", 45)]
    [InlineData("quero com trinta segundos", 30)]
    [InlineData("clipes de 1 minuto", 60)]
    [InlineData("clipes de meio minuto", 30)]
    [InlineData("uns 20 seg cada", 20)]
    public void Explicit_seconds_set_the_target(string message, int seconds)
    {
        var rules = Rules(message);
        rules.TargetClipSeconds.ShouldBe(seconds);
        rules.MinClipSeconds.ShouldBeNull();
        rules.MaxClipSeconds.ShouldBeNull();
    }

    [Theory]
    [InlineData("entre 20 e 40 segundos", 20, 30, 40)]
    [InlineData("de 15 a 45 s", 15, 30, 45)]
    [InlineData("clipes 20-40s", 20, 30, 40)]
    public void Explicit_ranges_set_min_and_max(string message, int min, int target, int max)
    {
        var rules = Rules(message);
        (rules.MinClipSeconds, rules.TargetClipSeconds, rules.MaxClipSeconds).ShouldBe((min, target, max));
    }

    [Fact]
    public void Upper_bound_alone_sets_only_the_maximum()
    {
        var rules = Rules("no máximo 40 segundos");
        rules.MaxClipSeconds.ShouldBe(40);
        rules.MinClipSeconds.ShouldBeNull();
        rules.TargetClipSeconds.ShouldBeNull();
    }

    [Theory]
    [InlineData("não muito curtos")]
    [InlineData("a live é longa")]
    [InlineData("eu curto clipes engraçados")]
    public void Lengths_need_an_unambiguous_mention(string message) => Rules(message).HasDurations.ShouldBeFalse();

    [Fact]
    public void Numbers_win_over_short_and_long()
    {
        var rules = Rules("clipes curtos de 12 segundos");
        rules.TargetClipSeconds.ShouldBe(12);
        rules.MinClipSeconds.ShouldBeNull();
    }

    // ---------------------------------------------------------------- subtitles

    [Theory]
    [InlineData("legenda no meio", "center")]
    [InlineData("coloca a legenda no centro da tela", "center")]
    [InlineData("legenda em cima", "top")]
    [InlineData("quero a legenda no topo", "top")]
    [InlineData("legenda embaixo", "bottom")]
    [InlineData("põe as legendas lá embaixo", "bottom")]
    public void Subtitle_position(string message, string position) => Rules(message).SubtitlePosition.ShouldBe(position);

    [Theory]
    [InlineData("sem legenda")]
    [InlineData("tira as legendas")]
    [InlineData("não quero legenda")]
    public void Subtitles_off(string message)
    {
        Rules(message).SubtitlesEnabled.ShouldBe(false);
    }

    [Fact]
    public void Subtitles_on() => Rules("com legenda, por favor").SubtitlesEnabled.ShouldBe(true);

    [Fact]
    public void Position_words_near_the_webcam_are_not_about_subtitles()
    {
        var rules = Rules("legenda embaixo e a webcam em cima");
        rules.SubtitlePosition.ShouldBe("bottom");
        rules.FramingMode.ShouldBe("webcam-split");
    }

    [Fact]
    public void Position_words_without_subtitles_change_nothing() => Rules("quero o jogo no meio").SubtitlePosition.ShouldBeNull();

    [Theory]
    [InlineData("legenda limpa", "clean")]
    [InlineData("legenda com caixa", "boxed")]
    [InlineData("legenda em negrito", "bold")]
    public void Subtitle_style(string message, string style) => Rules(message).SubtitleStyle.ShouldBe(style);

    // ---------------------------------------------------------------- moment types

    [Theory]
    [InlineData("quero os momentos engraçados", "funny")]
    [InlineData("as partes de zoeira e risada", "funny")]
    [InlineData("pega as comemorações", "hype")]
    [InlineData("muito hype", "hype")]
    [InlineData("as melhores jogadas", "gameplay")]
    [InlineData("cada clutch e kill", "gameplay")]
    [InlineData("frases marcantes", "quote")]
    [InlineData("momentos de tensão", "tension")]
    [InlineData("suspense", "tension")]
    [InlineData("reações", "reaction")]
    [InlineData("os sustos", "reaction")]
    public void Moment_types(string message, string category) => Rules(message).FocusCategories.ShouldBe([category]);

    [Fact]
    public void Several_moment_types_keep_the_order_of_the_message()
        => Rules("quero reações e momentos engraçados").FocusCategories.ShouldBe(["reaction", "funny"]);

    [Fact]
    public void Negated_types_are_removed_from_the_focus()
        => Rules("sem tensão", Current(s => s.FocusCategories = ["tension", "funny"])).FocusCategories.ShouldBe(["funny"]);

    [Fact]
    public void Also_adds_to_the_current_focus()
        => Rules("engraçados também", Current(s => s.FocusCategories = ["hype"])).FocusCategories.ShouldBe(["hype", "funny"]);

    [Fact]
    public void A_negation_of_another_setting_does_not_negate_the_type()
    {
        var rules = Rules("sem legenda e engraçados");
        rules.FocusCategories.ShouldBe(["funny"]);
        rules.SubtitlesEnabled.ShouldBe(false);
    }

    [Fact]
    public void Clearing_the_focus() => Rules("qualquer tipo de momento", Current(s => s.FocusCategories = ["hype"])).FocusCategories.ShouldBe([]);

    // ---------------------------------------------------------------- spoken language

    [Theory]
    [InlineData("a live é em inglês", "en")]
    [InlineData("o vídeo é em português", "pt")]
    [InlineData("eles falam espanhol", "es")]
    public void Spoken_language(string message, string language) => Rules(message).Language.ShouldBe(language);

    [Fact]
    public void The_chat_language_is_not_the_spoken_language() => Rules("responde em português").Language.ShouldBeNull();

    [Fact]
    public void Translation_is_not_supported()
    {
        var rules = Rules("traduz a legenda pro inglês");
        rules.Language.ShouldBeNull();
        rules.Unsupported.ShouldBe(["other"]);
    }

    // ---------------------------------------------------------------- keywords

    [Fact]
    public void Quoted_words_become_keywords() => Rules("quando eu falar \"que isso\" quero o clipe").AddKeywords.ShouldBe(["que isso"]);

    [Fact]
    public void Words_after_quando_eu_falar_become_keywords()
    {
        var rules = Rules("procura quando eu falar clutch e quero clipes curtinhos");
        rules.AddKeywords.ShouldBe(["clutch"]);
        rules.TargetClipSeconds.ShouldBe(15);
    }

    [Fact]
    public void Keyword_lists() => Rules("palavras-chave: clutch, ace e headshot").AddKeywords.ShouldBe(["clutch", "ace", "headshot"]);

    [Fact]
    public void Talking_about_a_topic_is_not_a_keyword() => Rules("quando eu falar de jogos").AddKeywords.ShouldBeEmpty();

    // ---------------------------------------------------------------- counts, sensitivity

    [Theory]
    [InlineData("quero 10 clipes", 10)]
    [InlineData("gera dez clipes", 10)]
    public void Clip_counts(string message, int count) => Rules(message).AutoRenderTop.ShouldBe(count);

    [Fact]
    public void A_clip_is_not_a_count() => Rules("quero um clipe engraçado").AutoRenderTop.ShouldBeNull();

    [Fact]
    public void Every_moment_becomes_a_clip() => Rules("todos os momentos encontrados viram clipes").AutoRenderAll.ShouldBeTrue();

    [Fact]
    public void More_sensitive() => Rules("deixa mais sensível", Current(s => s.Sensitivity = 50)).Sensitivity.ShouldBe(70);

    // ---------------------------------------------------------------- unsupported requests, help

    [Theory]
    [InlineData("posta no tiktok pra mim", "auto_post")]
    [InlineData("pode publicar os clipes automaticamente?", "auto_post")]
    [InlineData("coloca uma música de fundo", "music")]
    [InlineData("adiciona efeitos e transições", "effects")]
    [InlineData("corta o vídeo pra mim", "edit_video")]
    [InlineData("acelera essa parte", "edit_video")]
    public void Unsupported_requests(string message, string kind) => Rules(message).Unsupported.ShouldContain(kind);

    [Fact]
    public void Posting_yourself_is_not_a_request_to_post()
    {
        var rules = Rules("quero postar no youtube");
        rules.Unsupported.ShouldBeEmpty();
        rules.AspectRatio.ShouldBe("16:9");
    }

    [Fact]
    public void A_quote_of_effect_is_a_quote_not_an_effect()
    {
        var rules = Rules("quero frases de efeito");
        rules.Unsupported.ShouldBeEmpty();
        rules.FocusCategories.ShouldBe(["quote"]);
    }

    [Fact]
    public void Posting_also_reads_the_platform()
    {
        var rules = Rules("posta no tiktok pra mim");
        rules.AspectRatio.ShouldBe("9:16");
        rules.Unsupported.ShouldBe(["auto_post"]);
    }

    [Theory]
    [InlineData("o que você faz?")]
    [InlineData("me ajuda")]
    [InlineData("quais são as opções?")]
    public void Help(string message) => Rules(message).Help.ShouldBeTrue();

    [Fact]
    public void Greetings_and_thanks()
    {
        Rules("oi!").Greeting.ShouldBeTrue();
        Rules("valeu").Thanks.ShouldBeTrue();
        Rules("oi, quero clipes engraçados").Greeting.ShouldBeFalse();
    }

    [Fact]
    public void Accents_and_case_do_not_matter()
    {
        var rules = Rules("CLIPES ENGRAÇADOS PRO TIKTOK, LEGENDA NO MEIO, ÁUDIO EM INGLÊS");
        rules.FocusCategories.ShouldBe(["funny"]);
        rules.AspectRatio.ShouldBe("9:16");
        rules.SubtitlePosition.ShouldBe("center");
        rules.Language.ShouldBe("en");
    }

    // ---------------------------------------------------------------- negated mentions never become the change they name
    // A field mentioned only in a negated way keeps its current value (the reply says it is already set) unless that is the
    // refused value; then it takes the first default the user did not refuse.

    [Theory]
    [InlineData("não quero webcam")]
    [InlineData("quero sem o layout de webcam")]
    [InlineData("nada de webcam")]
    [InlineData("não preciso de webcam")]
    [InlineData("a webcam não precisa aparecer")]
    public void Refusing_the_webcam_never_selects_the_webcam_layout(string message)
    {
        var rules = Rules(message);
        rules.FramingMode.ShouldBe("auto");
        rules.IsRejected("framing.mode", "webcam-split").ShouldBeTrue();
        Rules(message, Current(s => s.Framing.Mode = "webcam-split")).FramingMode.ShouldBe("auto");
        Rules(message, Current(s => s.Framing.Mode = "center")).FramingMode.ShouldBe("center"); // already without webcam: unchanged
    }

    [Fact]
    public void A_refused_subtitle_position_is_dropped_and_the_wanted_one_kept()
    {
        var rules = Rules("não quero legenda no meio, quero embaixo", Current(s => s.Subtitles.Position = "center"));
        rules.SubtitlePosition.ShouldBe("bottom");
        rules.SubtitlesEnabled.ShouldBeNull();
        rules.IsRejected("subtitles.position", "center").ShouldBeTrue();
        rules.IsRejected("subtitles.enabled", "false").ShouldBeTrue(); // the placement was refused, not the subtitles
    }

    [Theory]
    [InlineData("não quero a legenda em cima", "top")]
    [InlineData("nada de legenda no topo", "top")]
    [InlineData("a legenda não pode ficar no meio", "center")]
    [InlineData("legenda no meio não", "center")]
    [InlineData("não quero que deixe a legenda no meio", "center")]
    public void Refusing_a_subtitle_position_never_selects_it_nor_turns_the_subtitles_off(string message, string refused)
    {
        var rules = Rules(message);
        rules.SubtitlesEnabled.ShouldBeNull();
        rules.SubtitlePosition.ShouldBe("bottom"); // the current position, which the user did not refuse
        rules.IsRejected("subtitles.position", refused).ShouldBeTrue();
        Rules(message, Current(s => s.Subtitles.Position = refused)).SubtitlePosition.ShouldBe("bottom"); // moved to the default
    }

    [Fact]
    public void Refusing_a_subtitle_style()
    {
        var bold = Rules("não quero legenda em negrito");
        bold.SubtitleStyle.ShouldBe("clean"); // bold is the current style: the next one
        bold.SubtitlesEnabled.ShouldBeNull();
        Rules("não quero legenda com caixa").SubtitleStyle.ShouldBe("bold"); // already not boxed
    }

    [Theory]
    [InlineData("sem legenda")]
    [InlineData("não quero legenda nos clipes")]
    [InlineData("nada de legenda")]
    [InlineData("não precisa colocar legenda")]
    [InlineData("não quero legenda, quero o vídeo limpo")]
    [InlineData("sem legenda webcam em cima")]
    public void Refusing_the_subtitles_without_a_placement_turns_them_off(string message) => Rules(message).SubtitlesEnabled.ShouldBe(false);

    [Theory]
    [InlineData("não quero clipes curtos")]
    [InlineData("evita clipes curtos")]
    [InlineData("clipes curtos não")]
    public void Refusing_short_clips_never_makes_them_short(string message)
    {
        var rules = Rules(message);
        (rules.MinClipSeconds, rules.TargetClipSeconds, rules.MaxClipSeconds).ShouldBe((15, 30, 60)); // the current lengths
        var fromShort = Rules(message, Current(s => { s.MinClipSeconds = 10; s.TargetClipSeconds = 15; s.MaxClipSeconds = 25; }));
        (fromShort.MinClipSeconds, fromShort.TargetClipSeconds, fromShort.MaxClipSeconds).ShouldBe((15, 30, 60)); // the defaults
    }

    [Theory]
    [InlineData("nada de clipes longos")]
    [InlineData("sem clipes longos")]
    [InlineData("não quero clipes mais longos")]
    public void Refusing_long_clips_never_makes_them_long(string message)
    {
        var rules = Rules(message);
        (rules.MinClipSeconds, rules.TargetClipSeconds, rules.MaxClipSeconds).ShouldBe((15, 30, 60));
        var fromLong = Rules(message, Current(s => { s.MinClipSeconds = 45; s.TargetClipSeconds = 60; s.MaxClipSeconds = 90; }));
        (fromLong.MinClipSeconds, fromLong.TargetClipSeconds, fromLong.MaxClipSeconds).ShouldBe((15, 30, 60));
    }

    [Fact]
    public void A_refused_length_next_to_a_wanted_one()
    {
        var rules = Rules("clipes curtos não, quero clipes longos");
        (rules.MinClipSeconds, rules.TargetClipSeconds, rules.MaxClipSeconds).ShouldBe((45, 60, 90));
        var preferred = Rules("não gosto de clipes longos, prefiro curtos");
        (preferred.MinClipSeconds, preferred.TargetClipSeconds, preferred.MaxClipSeconds).ShouldBe((10, 15, 25));
        Rules("não quero clipes de 60 segundos, quero de 30 segundos").TargetClipSeconds.ShouldBe(30);
    }

    [Fact]
    public void A_refused_number_of_seconds_is_not_applied()
    {
        var rules = Rules("não quero clipes de 60 segundos");
        rules.HasDurations.ShouldBeFalse();
        rules.IsRejected("clipSeconds", 60).ShouldBeTrue();
    }

    [Fact]
    public void Bounds_and_approximations_are_not_negations()
    {
        Rules("clipes de mais ou menos 30 segundos").TargetClipSeconds.ShouldBe(30);
        var max = Rules("não quero mais de 40 segundos");
        max.MaxClipSeconds.ShouldBe(40);
        max.MinClipSeconds.ShouldBeNull();
        Rules("não quero mais de 10 clipes").AutoRenderTop.ShouldBe(10);
    }

    [Fact]
    public void Refusing_a_format()
    {
        Rules("não é pro tiktok").AspectRatio.ShouldBe("16:9"); // 9:16 is the current format: the next one
        Rules("não é pro tiktok", Current(s => s.AspectRatio = "1:1")).AspectRatio.ShouldBe("1:1"); // already not vertical
        Rules("não quero vertical, quero horizontal").AspectRatio.ShouldBe("16:9");
        Rules("vou postar no youtube, não no tiktok").AspectRatio.ShouldBe("16:9");
    }

    [Theory]
    [InlineData("a live não é em inglês")]
    [InlineData("a live nao e em ingles")]
    [InlineData("o áudio não está em inglês")]
    public void Refusing_a_language_never_selects_it(string message)
    {
        Rules(message).Language.ShouldBe("auto"); // already detected automatically
        Rules(message, Current(s => s.Language = "en")).Language.ShouldBe("auto");
    }

    [Fact]
    public void A_refused_language_next_to_a_wanted_one() => Rules("não é em inglês, é em português").Language.ShouldBe("pt");

    [Theory]
    [InlineData("não quero mais sensível")]
    [InlineData("não precisa ficar menos sensível")]
    public void Refusing_a_sensitivity_change_keeps_it(string message) => Rules(message, Current(s => s.Sensitivity = 50)).Sensitivity.ShouldBe(50);

    [Fact]
    public void A_refused_count_is_not_applied()
    {
        var rules = Rules("não quero 10 clipes");
        rules.AutoRenderTop.ShouldBeNull();
        rules.IsRejected("autoRenderTop", 10).ShouldBeTrue();
    }

    [Theory]
    [InlineData("sem música", "music")]
    [InlineData("não quero efeitos nem transições", "effects")]
    [InlineData("não posta nada, só quero os arquivos", "auto_post")]
    public void Refused_unsupported_things_need_no_explanation(string message, string kind)
    {
        var rules = Rules(message);
        rules.Unsupported.ShouldBeEmpty();
        rules.IsRejected("unsupported", kind).ShouldBeTrue();
    }

    [Fact]
    public void A_rhetorical_question_about_a_capability_is_a_request()
    {
        Rules("não tem como colocar música?").Unsupported.ShouldBe(["music"]);
        Rules("não dá pra colocar a legenda em cima?").SubtitlePosition.ShouldBe("top");
    }

    [Fact]
    public void A_moment_type_refused_further_from_the_negation()
    {
        var rules = Rules("não quero que tenha reações", Current(s => s.FocusCategories = ["reaction", "funny"]));
        rules.FocusCategories.ShouldBe(["funny"]);
        rules.IsRejected("focusCategories", "reaction").ShouldBeTrue();
    }

    [Theory]
    [InlineData("não sei mexer nisso, coloca a legenda em cima")]
    [InlineData("não sei mexer nisso coloca a legenda em cima")]
    [InlineData("sem webcam, legenda em cima")]
    [InlineData("sem webcam legenda em cima")]
    public void A_negation_of_something_else_does_not_reach_the_placement(string message) => Rules(message).SubtitlePosition.ShouldBe("top");

    [Fact]
    public void Double_negatives_not_only_and_why_not_are_not_refusals()
    {
        Rules("não esquece de colocar legenda", Current(s => s.Subtitles.Enabled = false)).SubtitlesEnabled.ShouldBe(true);
        Rules("não só engraçados, reações também").FocusCategories.ShouldBe(["funny", "reaction"]);
        Rules("não quero perder nenhuma jogada").FocusCategories.ShouldBe(["gameplay"]);
        Rules("não tem problema ser em inglês").Language.ShouldBe("en");
        var whyNot = Rules("por que não clipes curtos?");
        (whyNot.MinClipSeconds, whyNot.TargetClipSeconds, whyNot.MaxClipSeconds).ShouldBe((10, 15, 25));
    }

    [Fact]
    public void Refusing_the_subtitles_does_not_refuse_the_webcam_after_them()
    {
        var rules = Rules("sem legenda webcam em cima");
        rules.SubtitlesEnabled.ShouldBe(false);
        rules.FramingMode.ShouldBe("webcam-split");
    }

    [Fact]
    public void Taking_the_subtitles_off_a_position_moves_them_instead_of_turning_them_off()
    {
        var rules = Rules("pode tirar a legenda de cima?", Current(s => s.Subtitles.Position = "top"));
        rules.SubtitlesEnabled.ShouldBeNull();
        rules.SubtitlePosition.ShouldBe("bottom");
        Rules("não quero nenhuma legenda").SubtitlesEnabled.ShouldBe(false);
    }

    // ---------------------------------------------------------------- keyword lists

    [Fact]
    public void A_keyword_list_ends_where_another_request_starts()
    {
        var subtitles = Rules("marca a palavra gg e coloca legenda no meio");
        subtitles.AddKeywords.ShouldBe(["gg"]);
        subtitles.SubtitlePosition.ShouldBe("center");

        var lengths = Rules("procura a palavra clutch e deixa os clipes curtos");
        lengths.AddKeywords.ShouldBe(["clutch"]);
        (lengths.MinClipSeconds, lengths.TargetClipSeconds, lengths.MaxClipSeconds).ShouldBe((10, 15, 25));

        Rules("as palavras gg, ez e clutch nos clipes curtos").AddKeywords.ShouldBe(["gg", "ez", "clutch"]);
        Rules("a palavra gg legenda no meio").AddKeywords.ShouldBe(["gg"]);
        Rules("a palavra clutch e a palavra ace").AddKeywords.ShouldBe(["clutch", "ace"]);
    }

    [Theory]
    [InlineData("as palavras chave são gg, ez, clutch")]
    [InlineData("as palavras-chave são gg, ez e clutch")]
    [InlineData("palavras-chave: gg, ez e clutch")]
    public void Palavras_chave_introduces_the_list(string message) => Rules(message).AddKeywords.ShouldBe(["gg", "ez", "clutch"]);

    [Fact]
    public void A_palavra_chave_e() => Rules("a palavra-chave é gg").AddKeywords.ShouldBe(["gg"]);

    [Theory]
    [InlineData("coloca a legenda \"embaixo\"")]
    [InlineData("quero clipes \"engraçados\"")]
    [InlineData("quando eu falar legenda")]
    [InlineData("a palavra os clipes curtos")]
    public void Setting_and_moment_words_are_never_rule_keywords(string message) => Rules(message).AddKeywords.ShouldBeEmpty();

    [Fact]
    public void A_named_game_term_is_a_keyword_and_a_focus()
    {
        var rules = Rules("quando eu falar \"clutch\" quero o clipe");
        rules.AddKeywords.ShouldBe(["clutch"]);
        rules.FocusCategories.ShouldBe(["gameplay"]);
    }

    [Fact]
    public void Refusing_a_keyword_takes_it_out()
    {
        var one = Rules("tira a palavra gg");
        one.RemoveKeywords.ShouldBe(["gg"]);
        one.AddKeywords.ShouldBeEmpty();
        one.ClearKeywords.ShouldBeFalse();

        var two = Rules("tira as palavras gg e ez");
        two.RemoveKeywords.ShouldBe(["gg", "ez"]);
        two.ClearKeywords.ShouldBeFalse();

        Rules("não procura mais \"que isso\"").RemoveKeywords.ShouldBe(["que isso"]);
        Rules("tira as palavras-chave").ClearKeywords.ShouldBeTrue();

        var gameTerm = Rules("tira a palavra-chave clutch"); // taking a game term out of the keywords is not asking for gameplay
        gameTerm.RemoveKeywords.ShouldBe(["clutch"]);
        gameTerm.FocusCategories.ShouldBeNull();
    }
}
