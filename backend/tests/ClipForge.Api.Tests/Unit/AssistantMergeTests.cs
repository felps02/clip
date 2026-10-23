using ClipForge.Api.Features.Assistant;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Tests.Unit;

/// <summary>
/// Rules + model output → requested changes. The model outputs below were produced by the local Qwen3-1.7B (llama.cpp
/// b11836) for these exact messages while the prompt was being tuned: it copies context values, invents fields and lists
/// every unsupported kind. None of that may reach the reply.
/// </summary>
public class AssistantMergeTests
{
    private static readonly PlanLimits Creator = new() { MaxCandidatesPerJob = 30, MaxAutoRenderPerJob = 30, AllowCustomSubtitleStyles = true, AllowWebcamLayout = true };

    private static readonly AnalysisSettings Current = PlanLimitHelpers.CapSettings(
        new AnalysisSettings { AspectRatio = "16:9", MinClipSeconds = 15, TargetClipSeconds = 45, MaxClipSeconds = 60, MaxCandidates = 15, AutoRenderTop = 15, Language = "auto" },
        Creator);

    private static AssistantOutcome Run(string message, string modelJson, Action<AnalysisSettings>? change = null)
    {
        var current = Current.Clone();
        change?.Invoke(current);
        var rules = AssistantRules.Analyze(message, current);
        var merged = AssistantMerge.Merge(rules, AssistantModelOutput.Parse(modelJson), message);
        return AssistantPatchBuilder.Apply(current, merged, Creator, null);
    }

    private static string Output(string patch, string unsupported = "[]")
        => $$"""{"intent":"configure","patch":{{patch}},"unsupported":{{unsupported}},"question":null}""";

    // ---------------------------------------------------------------- negated requests: the local model's outputs for these
    // messages (2026-10-23) and outputs that repeat the refused value; neither may become the change the user refused.

    [Theory]
    [InlineData("""{"framingMode":"center"}""")]
    [InlineData("""{"framingMode":"webcam-split"}""")]
    public void Refusing_the_webcam(string patch)
    {
        var outcome = Run("não quero webcam", Output(patch));
        outcome.Patch.IsEmpty.ShouldBeTrue();
        outcome.Already.ShouldBe(["enquadramento automático"]);

        Run("quero sem o layout de webcam", Output(patch), s => s.Framing.Mode = "webcam-split").Patch.Framing!.Mode.ShouldBe("auto");
    }

    [Fact]
    public void Refusing_one_subtitle_position_for_another_keeps_the_subtitles_on()
    {
        var outcome = Run("não quero legenda no meio, quero embaixo", Output("""{"subtitlesEnabled":false,"subtitlePosition":"center"}"""),
            s => s.Subtitles.Position = "center");
        outcome.Patch.Subtitles!.Position.ShouldBe("bottom");
        outcome.Patch.Subtitles.Enabled.ShouldBeNull();
    }

    [Theory]
    [InlineData("nada de legenda no topo", """{"subtitlesEnabled":false}""")]
    [InlineData("não quero a legenda em cima", """{"subtitlePosition":"top"}""")]
    [InlineData("a legenda não pode ficar no meio", """{"subtitlePosition":"center","subtitlesEnabled":false}""")]
    public void Refusing_a_subtitle_position_that_is_not_the_current_one_changes_nothing(string message, string patch)
    {
        var outcome = Run(message, Output(patch));
        outcome.Patch.Subtitles.ShouldBeNull();
        outcome.Already.ShouldBe(["legenda embaixo"]);
    }

    [Theory]
    [InlineData("não quero clipes curtos", """{"minClipSeconds":10,"targetClipSeconds":15,"maxClipSeconds":25}""")]
    [InlineData("evita clipes curtos", """{"minClipSeconds":30,"maxClipSeconds":30}""")]
    public void Refusing_short_clips_keeps_lengths_that_are_not_short(string message, string patch)
    {
        var outcome = Run(message, Output(patch));
        outcome.Patch.IsEmpty.ShouldBeTrue();
        outcome.Already.ShouldBe(["clipes de 15 a 60 s (alvo de 45 s)"]);
    }

    [Theory]
    [InlineData("nada de clipes longos", """{"minClipSeconds":5,"maxClipSeconds":30}""")]
    [InlineData("sem clipes longos", """{"minClipSeconds":45,"targetClipSeconds":60,"maxClipSeconds":90}""")]
    public void Refusing_long_clips_when_they_are_long_goes_back_to_the_default_lengths(string message, string patch)
    {
        // The current target (45 s) is long: the defaults (15-60 s, target 30 s), not the model's lengths.
        var outcome = Run(message, Output(patch));
        (outcome.Patch.MinClipSeconds, outcome.Patch.TargetClipSeconds, outcome.Patch.MaxClipSeconds).ShouldBe((15, 30, 60));
    }

    [Theory]
    [InlineData("""{"language":"en"}""")]
    [InlineData("""{"language":"pt"}""")]
    public void Refusing_a_language(string patch)
    {
        var outcome = Run("a live não é em inglês", Output(patch));
        outcome.Patch.Language.ShouldBeNull();
        outcome.Already.ShouldBe(["idioma do áudio detectado automaticamente"]);
    }

    [Theory]
    [InlineData("""{"sensitivity":40}""")]
    [InlineData("""{"sensitivity":70}""")]
    public void Refusing_more_sensitivity(string patch)
    {
        var outcome = Run("não quero mais sensível", Output(patch));
        outcome.Patch.Sensitivity.ShouldBeNull();
        outcome.Already.ShouldBe(["sensibilidade 50"]);
    }

    [Fact]
    public void Refused_values_never_come_from_the_model()
    {
        Run("não quero reações", Output("""{"focusCategories":["reaction"]}""")).Patch.FocusCategories.ShouldBeNull();
        Run("não quero 10 clipes", Output("""{"autoRenderTop":10}""")).Patch.AutoRenderTop.ShouldBeNull();
        Run("não quero clipes de 20 segundos", Output("""{"targetClipSeconds":20}""")).Patch.TargetClipSeconds.ShouldBeNull();
        Run("sem música e sem efeitos", Output("{}", """["music","effects"]""")).Unsupported.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- keyword lists (the model's outputs for these messages)

    [Fact]
    public void Keyword_lists_end_where_another_request_starts()
    {
        var subtitles = Run("marca a palavra gg e coloca legenda no meio", Output("""{"customKeywords":["gg"],"subtitlePosition":"center"}"""));
        subtitles.Patch.CustomKeywords.ShouldBe(["gg"]);
        subtitles.Patch.Subtitles!.Position.ShouldBe("center");

        var lengths = Run("procura a palavra clutch e deixa os clipes curtos", Output("""{"customKeywords":["clutch","os clipes curtos"]}"""));
        lengths.Patch.CustomKeywords.ShouldBe(["clutch"]);
        (lengths.Patch.MinClipSeconds, lengths.Patch.TargetClipSeconds, lengths.Patch.MaxClipSeconds).ShouldBe((10, 15, 25));

        Run("as palavras chave são gg, ez, clutch", Output("""{"customKeywords":["gg","ez","clutch"]}""")).Patch.CustomKeywords.ShouldBe(["gg", "ez", "clutch"]);
    }

    [Fact]
    public void A_refused_keyword_is_taken_out_and_never_comes_back_from_the_model()
    {
        var outcome = Run("tira a palavra gg", Output("""{"customKeywords":["gg"]}"""), s => s.CustomKeywords = ["gg", "clutch"]);
        outcome.Patch.CustomKeywords.ShouldBe(["clutch"]);

        var absent = Run("tira a palavra gg", Output("""{"customKeywords":["gg"]}"""));
        absent.Patch.CustomKeywords.ShouldBeNull();
        absent.Already.ShouldBe(["sem a palavra-chave “gg”"]);
    }

    [Fact]
    public void Setting_phrases_are_not_keywords_from_the_model_either()
    {
        var outcome = Run("procura as palavras gg e os clipes curtos", Output("""{"customKeywords":["gg","os clipes curtos","clipes curtos"]}"""));
        outcome.Patch.CustomKeywords.ShouldBe(["gg"]);
    }

    [Fact]
    public void Copied_and_invented_fields_of_the_model_are_dropped()
    {
        var outcome = Run("quero clipes mais engraçados, de uns 30 segundos, pro tiktok",
            """{"intent":"configure","patch":{"focusCategories":["funny"],"minClipSeconds":30,"targetClipSeconds":30,"aspectRatio":"9:16","subtitlesEnabled":false,"subtitlePosition":"bottom","subtitleStyle":"bold","language":"pt","sensitivity":50,"maxCandidates":15,"autoRenderTop":15},"unsupported":["auto_post","music","effects","edit_video"],"question":null}""");

        outcome.Changes.Select(c => c.Field).ShouldBe(["focusCategories", "targetClipSeconds", "aspectRatio"]);
        outcome.Patch.FocusCategories.ShouldBe(["funny"]);
        (outcome.Patch.MinClipSeconds, outcome.Patch.TargetClipSeconds, outcome.Patch.MaxClipSeconds).ShouldBe((20, 30, 40));
        outcome.Patch.AspectRatio.ShouldBe("9:16");
        outcome.Patch.Subtitles.ShouldBeNull();
        outcome.Patch.Language.ShouldBeNull();
        outcome.Unsupported.ShouldBeEmpty();
    }

    [Fact]
    public void Model_fields_need_the_message_to_talk_about_them()
    {
        var outcome = Run("coloca a legenda em cima e procura quando eu falar clutch",
            """{"intent":"configure","patch":{"subtitlePosition":"top","framingMode":"webcam-split"},"unsupported":["auto_post","music","effects","edit_video"],"question":null}""");

        outcome.Patch.Subtitles!.Position.ShouldBe("top");
        outcome.Patch.CustomKeywords.ShouldBe(["clutch"]);
        outcome.Patch.Framing.ShouldBeNull();
        outcome.Unsupported.ShouldBeEmpty();
    }

    [Fact]
    public void Unsupported_kinds_need_a_cue_in_the_message()
    {
        var outcome = Run("posta no tiktok pra mim",
            """{"intent":"configure","patch":{"aspectRatio":"9:16","language":"auto","maxCandidates":15,"autoRenderTop":15},"unsupported":["auto_post","music","effects","edit_video"],"question":null}""");

        outcome.Unsupported.ShouldBe(["auto_post"]);
        outcome.Changes.Select(c => c.Field).ShouldBe(["aspectRatio"]);
    }

    [Fact]
    public void The_model_covers_paraphrases_the_rules_do_not_know()
    {
        // "épicos" is not in the rules' vocabulary, but it is a cue for hype: the model's category is accepted.
        AssistantRules.Analyze("quero os momentos mais épicos", Current).FocusCategories.ShouldBeNull();
        var outcome = Run("quero os momentos mais épicos", """{"intent":"configure","patch":{"focusCategories":["hype","tension"]},"unsupported":[],"question":null}""");
        outcome.Patch.FocusCategories.ShouldBe(["hype"]); // "tension" has no cue in the message
    }

    [Fact]
    public void Rules_win_over_the_model_on_explicit_mentions()
    {
        var outcome = Run("clipes de 50 segundos pro youtube",
            """{"intent":"configure","patch":{"targetClipSeconds":20,"aspectRatio":"9:16"},"unsupported":[],"question":null}""");
        outcome.Patch.TargetClipSeconds.ShouldBe(50);
        outcome.Patch.AspectRatio.ShouldBeNull(); // YouTube is 16:9, which is already the current format
        outcome.Already.ShouldContain("formato 16:9 (horizontal)");
    }

    [Fact]
    public void Keywords_from_the_model_must_be_typed_by_the_user()
    {
        var invented = Run("quando eu falar nossa", """{"intent":"configure","patch":{"customKeywords":["nossa","gg"]},"unsupported":[],"question":null}""");
        invented.Patch.CustomKeywords.ShouldBe(["nossa"]);

        var settingWords = Run("quero clipes engraçados", """{"intent":"configure","patch":{"customKeywords":["engraçados"],"focusCategories":["funny"]},"unsupported":[],"question":null}""");
        settingWords.Patch.CustomKeywords.ShouldBeNull();
    }

    [Fact]
    public void The_question_is_kept_only_when_it_is_a_safe_short_question()
    {
        AssistantMerge.SanitizeQuestion("Qual duração você prefere?").ShouldBe("Qual duração você prefere?");
        AssistantMerge.SanitizeQuestion("Quer que eu poste no Instagram?").ShouldBeNull();
        AssistantMerge.SanitizeQuestion("Pronto, ajustei tudo.").ShouldBeNull();
        AssistantMerge.SanitizeQuestion(new string('a', 170) + "?").ShouldBeNull();
        AssistantMerge.SanitizeQuestion(null).ShouldBeNull();
    }

    [Fact]
    public void Unparseable_model_output_leaves_the_rules()
    {
        var rules = AssistantRules.Analyze("pro tiktok", Current);
        var merged = AssistantMerge.Merge(rules, AssistantModelOutput.Parse("not json at all"), "pro tiktok");
        merged.AspectRatio.ShouldBe("9:16");
    }

    [Fact]
    public void Model_output_parsing_is_lenient()
    {
        var output = AssistantModelOutput.Parse("""
            Aqui está: {
              "intent": "configure",
              "patch": { "targetClipSeconds": "30", "aspectRatio": "9:16", "focusCategories": "funny", "subtitlesEnabled": "yes" },
              "unsupported": ["music", 3],
              "question": null
            } obrigado
            """);
        output.Intent.ShouldBe("configure");
        output.Patch.AspectRatio.ShouldBe("9:16");
        output.Patch.TargetClipSeconds.ShouldBeNull();   // wrong type: only that field is lost
        output.Patch.FocusCategories.ShouldBeNull();
        output.Patch.SubtitlesEnabled.ShouldBeNull();
        output.Unsupported.ShouldBe(["music"]);
    }
}
