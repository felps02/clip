using ClipForge.Api.Features.Assistant;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Tests.Unit;

/// <summary>The reply is composed by the server from the validated outcome only.</summary>
public class AssistantReplyComposerTests
{
    private static readonly PlanLimits Free = new() { MaxCandidatesPerJob = 15, MaxAutoRenderPerJob = 15 };
    private static readonly PlanLimits Creator = new() { MaxCandidatesPerJob = 30, MaxAutoRenderPerJob = 30, AllowCustomSubtitleStyles = true, AllowWebcamLayout = true };

    private static readonly string[] Openers = ["Configurei:", "Novas configurações:", "Ajustes aplicados:"];

    /// <summary>Every field the reply can describe as changed, with words that only its phrase uses.</summary>
    private static readonly (string Field, string[] Words)[] FieldWords =
    [
        ("focusCategories", ["foco em", "sem foco"]),
        ("customKeywords", ["palavra-chave", "palavras-chave"]),
        ("targetClipSeconds", ["clipes de "]),
        ("aspectRatio", ["formato "]),
        ("subtitles.enabled", ["legendas ativadas", "legendas desativadas"]),
        ("subtitles.position", ["legenda no meio", "legenda em cima", "legenda embaixo"]),
        ("subtitles.style", ["legenda no estilo"]),
        ("language", ["idioma do áudio"]),
        ("sensitivity", ["sensibilidade "]),
        ("maxCandidates", ["momentos por análise"]),
        ("autoRenderTop", ["clipes automáticos", "viram clipes", "clipe automático"]),
        ("framing.mode", ["enquadramento "]),
    ];

    private static (AssistantOutcome Outcome, string Reply) Compose(string message, string modelJson, PlanLimits? limits = null, AnalysisSettings? current = null)
    {
        limits ??= Creator;
        current = PlanLimitHelpers.CapSettings(current ?? new AnalysisSettings { AspectRatio = "16:9" }, limits);
        var merged = AssistantMerge.Merge(AssistantRules.Analyze(message, current), AssistantModelOutput.Parse(modelJson), message);
        var outcome = AssistantPatchBuilder.Apply(current, merged, limits, null);
        return (outcome, AssistantReplyComposer.Compose(outcome, 1));
    }

    /// <summary>The first sentence lists exactly the changes of the patch; no field outside the patch is described as changed.</summary>
    private static void ShouldOnlyClaimThePatch(AssistantOutcome outcome, string reply)
    {
        var claim = reply.Split(". ")[0];
        if (outcome.Changes.Count == 0)
        {
            Openers.ShouldAllBe(opener => !reply.Contains(opener));
            return;
        }
        Openers.Count(claim.Contains).ShouldBe(1);
        var changedFields = outcome.Changes.Select(c => c.Field).ToHashSet();
        foreach (var (field, words) in FieldWords)
        {
            var mentioned = words.Any(w => claim.Contains(w, StringComparison.Ordinal));
            if (changedFields.Contains(field) || (field == "targetClipSeconds" && (changedFields.Contains("minClipSeconds") || changedFields.Contains("maxClipSeconds"))))
            {
                mentioned.ShouldBeTrue($"{field} changed but the reply does not say so: {reply}");
            }
            else
            {
                mentioned.ShouldBeFalse($"{field} is not in the patch but the reply claims it: {reply}");
            }
        }
        foreach (var change in outcome.Changes)
        {
            claim.ShouldContain(change.Phrase);
        }
    }

    public static TheoryData<string, string> HallucinatingModel => new()
    {
        // message, model output (copies context, invents fields and unsupported kinds)
        { "quero clipes mais engraçados, de uns 30 segundos, pro tiktok", """{"intent":"configure","patch":{"focusCategories":["funny","reaction"],"minClipSeconds":30,"targetClipSeconds":30,"aspectRatio":"1:1","subtitlesEnabled":false,"language":"pt","sensitivity":80,"maxCandidates":15,"autoRenderTop":3,"framingMode":"webcam-split","customKeywords":["tiktok"]},"unsupported":["auto_post","music","effects","edit_video"],"question":"Quer que eu poste?"}""" },
        { "legenda no meio", """{"intent":"configure","patch":{"subtitlePosition":"center","subtitleStyle":"boxed","aspectRatio":"9:16","sensitivity":10},"unsupported":["effects"],"question":null}""" },
        { "posta no tiktok pra mim", """{"intent":"configure","patch":{"aspectRatio":"9:16","language":"auto","maxCandidates":15,"autoRenderTop":15},"unsupported":["auto_post","music","effects","edit_video"],"question":null}""" },
        { "deixa melhor", """{"intent":"configure","patch":{"targetClipSeconds":10,"focusCategories":["hype"]},"unsupported":[],"question":"O que você quer mudar?"}""" },
        { "quero 5 clipes de frases marcantes em inglês", """{"intent":"configure","patch":{"autoRenderTop":5,"focusCategories":["quote","funny"],"language":"en","subtitlesEnabled":false},"unsupported":[],"question":null}""" },
        { "o que você faz?", """{"intent":"help","patch":{"aspectRatio":"9:16"},"unsupported":["other"],"question":null}""" },
        // negated requests (round-3 review): the model repeats the refused value
        { "não quero webcam", """{"intent":"configure","patch":{"framingMode":"webcam-split"},"unsupported":[],"question":null}""" },
        { "não quero legenda no meio, quero embaixo", """{"intent":"configure","patch":{"subtitlesEnabled":false,"subtitlePosition":"center"},"unsupported":[],"question":null}""" },
        { "a legenda não pode ficar no meio", """{"intent":"configure","patch":{"subtitlesEnabled":false,"subtitlePosition":"center"},"unsupported":[],"question":null}""" },
        { "não quero clipes curtos", """{"intent":"configure","patch":{"minClipSeconds":10,"targetClipSeconds":15,"maxClipSeconds":25},"unsupported":[],"question":null}""" },
        { "a live não é em inglês", """{"intent":"configure","patch":{"language":"en"},"unsupported":[],"question":null}""" },
        { "sem música", """{"intent":"configure","patch":{},"unsupported":["music"],"question":null}""" },
    };

    [Fact]
    public void Replies_to_negated_requests_never_confirm_the_refused_change()
    {
        Compose("não quero webcam", """{"intent":"configure","patch":{"framingMode":"center"},"unsupported":[],"question":null}""").Reply
            .ShouldBe("Isso já está configurado: enquadramento automático.");
        Compose("não quero legenda no meio, quero embaixo", """{"intent":"configure","patch":{"subtitlePosition":"bottom"},"unsupported":[],"question":null}""").Reply
            .ShouldBe("Isso já está configurado: legenda embaixo.");
        Compose("a live não é em inglês", """{"intent":"configure","patch":{"language":"en"},"unsupported":[],"question":null}""").Reply
            .ShouldBe("Isso já está configurado: idioma do áudio detectado automaticamente.");
        Compose("não quero mais sensível", """{"intent":"configure","patch":{"sensitivity":40},"unsupported":[],"question":null}""").Reply
            .ShouldBe("Isso já está configurado: sensibilidade 50.");
    }

    [Fact]
    public void Keywords_in_the_reply_are_only_the_words_the_user_named()
    {
        var (outcome, reply) = Compose("marca a palavra gg e coloca legenda no meio",
            """{"intent":"configure","patch":{"customKeywords":["gg"],"subtitlePosition":"center"},"unsupported":[],"question":null}""");
        outcome.Patch.CustomKeywords.ShouldBe(["gg"]);
        reply.ShouldBe("Feito! Novas configurações: palavra-chave “gg” e legenda no meio da tela.");

        Compose("as palavras chave são gg, ez, clutch", StubOutput.Empty).Reply
            .ShouldBe("Feito! Novas configurações: foco em jogadas e palavras-chave “gg”, “ez” e “clutch”.");
    }

    [Theory]
    [MemberData(nameof(HallucinatingModel))]
    public void The_reply_never_claims_a_change_that_is_not_in_the_validated_patch(string message, string modelJson)
    {
        foreach (var limits in new[] { Free, Creator })
        {
            var (outcome, reply) = Compose(message, modelJson, limits);
            ShouldOnlyClaimThePatch(outcome, reply);
            reply.ShouldNotContain("Quer que eu poste");
        }
    }

    [Fact]
    public void The_reference_request_describes_its_three_changes()
    {
        var (outcome, reply) = Compose("quero clipes mais engraçados, de uns 30 segundos, pro tiktok",
            """{"intent":"configure","patch":{"focusCategories":["funny"]},"unsupported":[],"question":null}""");
        outcome.Changes.Select(c => c.Label).ShouldBe(["Foco: momentos engraçados", "Duração: 20–40 s (alvo 30 s)", "Formato: 9:16 (vertical)"]);
        reply.ShouldBe("Feito! Novas configurações: foco em momentos engraçados, clipes de 20 a 40 s (alvo de 30 s) e formato 9:16 (vertical).");
    }

    [Fact]
    public void Unsupported_requests_get_fixed_explanations()
    {
        var (outcome, reply) = Compose("posta no instagram e coloca uma música", StubOutput.Empty);
        outcome.Changes.ShouldBeEmpty();
        reply.ShouldContain("Ainda não consigo postar automaticamente: baixe os clipes e publique.");
        reply.ShouldContain("Ainda não consigo adicionar música aos clipes.");
        reply.ShouldContain(AssistantReplyComposer.ShortHelp);
    }

    [Fact]
    public void Changes_and_unsupported_together()
    {
        var (_, reply) = Compose("posta no tiktok pra mim", StubOutput.Empty);
        reply.ShouldStartWith("Feito! Novas configurações: formato 9:16 (vertical).");
        reply.ShouldEndWith("Ainda não consigo postar automaticamente: baixe os clipes e publique.");
    }

    [Fact]
    public void The_model_question_is_used_only_when_nothing_changed()
    {
        var (_, vague) = Compose("deixa melhor", """{"intent":"configure","patch":{},"unsupported":[],"question":"O que você quer mudar: o tipo de momento, a duração ou o formato?"}""");
        vague.ShouldBe("O que você quer mudar: o tipo de momento, a duração ou o formato?");

        var (_, changed) = Compose("pro tiktok", """{"intent":"configure","patch":{},"unsupported":[],"question":"Qual duração você prefere?"}""");
        changed.ShouldNotContain("Qual duração");
    }

    [Fact]
    public void Help_greeting_thanks_and_nothing()
    {
        Compose("o que você consegue fazer?", """{"intent":"help","patch":{},"unsupported":[],"question":null}""").Reply.ShouldBe(AssistantReplyComposer.HelpText);
        Compose("oi", StubOutput.Empty).Reply.ShouldBe("Olá! " + AssistantReplyComposer.HelpText);
        Compose("valeu!", StubOutput.Empty).Reply.ShouldBe(AssistantReplyComposer.ThanksText);
        Compose("hmm", StubOutput.Empty).Reply.ShouldBe(AssistantReplyComposer.NothingText + " " + AssistantReplyComposer.ShortHelp);
    }

    [Fact]
    public void Plan_limits_are_explained_instead_of_applied()
    {
        var (outcome, reply) = Compose("legenda no meio e layout com webcam", StubOutput.Empty, Free);
        outcome.Changes.ShouldBeEmpty();
        reply.ShouldContain("não estão no seu plano");
        reply.ShouldContain("O layout com webcam não está disponível no seu plano.");
        reply.ShouldNotContain("Configurei");
    }

    [Fact]
    public void Values_already_set_are_reported_as_such()
    {
        var (outcome, reply) = Compose("pro youtube", StubOutput.Empty);
        outcome.Changes.ShouldBeEmpty();
        reply.ShouldBe("Isso já está configurado: formato 16:9 (horizontal).");
    }

    [Fact]
    public void Openers_rotate_deterministically()
    {
        var current = PlanLimitHelpers.CapSettings(new AnalysisSettings(), Creator);
        var outcome = AssistantPatchBuilder.Apply(current, new RequestedChanges { Language = "en" }, Creator, null);
        AssistantReplyComposer.Compose(outcome, 0).ShouldStartWith("Pronto!");
        AssistantReplyComposer.Compose(outcome, 1).ShouldStartWith("Feito!");
        AssistantReplyComposer.Compose(outcome, 2).ShouldStartWith("Combinado!");
        AssistantReplyComposer.Compose(outcome, 3).ShouldStartWith("Pronto!");
    }

    private static class StubOutput
    {
        public const string Empty = """{"intent":"other","patch":{},"unsupported":[],"question":null}""";
    }
}
