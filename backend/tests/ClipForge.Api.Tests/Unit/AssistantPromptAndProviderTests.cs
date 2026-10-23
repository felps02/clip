using System.Net;
using System.Text.Json.Nodes;
using ClipForge.Api.Features.Assistant;
using ClipForge.Api.Options;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Tests.Unit;

/// <summary>The request contract with the providers (llama-server, Gemini) and the byte-stable system prompt.</summary>
public class AssistantPromptAndProviderTests
{
    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static readonly AssistantChatMessage[] Conversation =
    [
        new("user", "quero clipes engraçados"),
        new("assistant", "Pronto! Configurei: foco em momentos engraçados."),
        new("user", "e pro tiktok"),
    ];

    [Fact]
    public void The_system_prompt_is_byte_stable_and_carries_no_request_data()
    {
        var a = AssistantPrompt.Build(Conversation, new AnalysisSettings { AspectRatio = "16:9", Sensitivity = 77 }, new PlanLimits { MaxCandidatesPerJob = 15 }, 750m);
        var b = AssistantPrompt.Build([new AssistantChatMessage("user", "oi")], new AnalysisSettings(), new PlanLimits { MaxCandidatesPerJob = 50 }, null);
        a.SystemPrompt.ShouldBe(b.SystemPrompt);
        a.SystemPrompt.ShouldBe(AssistantPrompt.SystemPrompt);
        a.SystemPrompt.ShouldNotContain("\r");
        a.SystemPrompt.ShouldNotContain("77");
        a.SystemPrompt.ShouldNotContain("750");
        AssistantPrompt.Warmup().SystemPrompt.ShouldBe(AssistantPrompt.SystemPrompt);
    }

    [Fact]
    public void The_last_user_message_carries_the_request_then_the_context()
    {
        var prompt = AssistantPrompt.Build(Conversation, new AnalysisSettings { AspectRatio = "16:9", Sensitivity = 77, AutoRenderTop = 10, MaxCandidates = 20 },
            new PlanLimits { MaxCandidatesPerJob = 15, MaxAutoRenderPerJob = 15 }, 750m);
        prompt.Messages.Select(m => m.Role).ShouldBe(["user", "assistant", "user"]);
        prompt.Messages[0].Content.ShouldBe("Pedido: quero clipes engraçados");
        var last = prompt.Messages[^1].Content;
        last.ShouldStartWith("Pedido: e pro tiktok\nContexto: ");
        last.ShouldContain("formato atual=16:9");
        last.ShouldContain("sensibilidade atual=77");
        last.ShouldContain("limite do plano: 15 momentos e 15 clipes, sem legenda personalizada, sem webcam");
        last.ShouldContain("vídeo de 750 s");
    }

    [Fact]
    public void The_local_request_is_schema_constrained_without_thinking_and_cached()
    {
        var body = LocalLlmAssistantModel.BuildBody(AssistantPrompt.Build(Conversation, new AnalysisSettings(), new PlanLimits(), null), AssistantPrompt.MaxTokens, includeSchema: true);
        body["messages"]![0]!["role"]!.GetValue<string>().ShouldBe("system");
        body["messages"]![0]!["content"]!.GetValue<string>().ShouldBe(AssistantPrompt.SystemPrompt);
        var format = body["response_format"]!;
        format["type"]!.GetValue<string>().ShouldBe("json_schema");
        format["json_schema"]!["name"]!.GetValue<string>().ShouldBe("assistant_patch");
        format["json_schema"]!["strict"]!.GetValue<bool>().ShouldBeTrue();
        var schema = format["json_schema"]!["schema"]!;
        schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["intent", "patch", "unsupported", "question"]);
        schema["properties"]!["patch"]!["properties"]!.AsObject().Select(p => p.Key).ShouldContain("focusCategories");
        schema["properties"]!["unsupported"]!["items"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["auto_post", "music", "effects", "edit_video", "other"]);
        body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().ShouldBeFalse();
        body["cache_prompt"]!.GetValue<bool>().ShouldBeTrue();
        body["temperature"]!.GetValue<double>().ShouldBe(0.2);
        body["max_tokens"]!.GetValue<int>().ShouldBe(300);
    }

    [Fact]
    public void The_gemini_request_uses_a_response_schema_and_no_thinking()
    {
        var body = GeminiAssistantModel.BuildBody(AssistantPrompt.Build(Conversation, new AnalysisSettings(), new PlanLimits(), null), thinkingBudget: 0);
        body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>().ShouldBe(AssistantPrompt.SystemPrompt);
        body["contents"]!.AsArray().Select(c => c!["role"]!.GetValue<string>()).ShouldBe(["user", "model", "user"]);
        var generation = body["generationConfig"]!;
        generation["responseMimeType"]!.GetValue<string>().ShouldBe("application/json");
        generation["responseSchema"]!["type"]!.GetValue<string>().ShouldBe("OBJECT");
        generation["responseSchema"]!["propertyOrdering"]!.AsArray().Count.ShouldBe(4);
        generation["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>().ShouldBe(0);
        generation["maxOutputTokens"]!.GetValue<int>().ShouldBe(300);
        GeminiAssistantModel.BuildBody(AssistantPrompt.Warmup(), thinkingBudget: null)["generationConfig"]!["thinkingConfig"].ShouldBeNull();
    }

    private static GeminiAssistantModel Gemini(StubLlmHandler handler, string? key = "test-key")
        => new(new FixedClientFactory(handler), Microsoft.Extensions.Options.Options.Create(new AssistantOptions
        {
            Provider = "gemini",
            Gemini = new GeminiAssistantOptions { ApiKey = key, Model = "gemini-2.5-flash", BaseUrl = "http://gemini.test/v1beta" },
        }), NullLogger<GeminiAssistantModel>.Instance);

    [Fact]
    public async Task Gemini_generate_content_is_called_with_the_key_in_a_header_and_parsed()
    {
        var handler = new StubLlmHandler
        {
            Responder = (request, _) => StubLlmHandler.JsonResponse(HttpStatusCode.OK, """
                {"candidates":[{"content":{"role":"model","parts":[{"text":"{\"intent\":\"configure\",\"patch\":{\"aspectRatio\":\"9:16\"},\"unsupported\":[],\"question\":null}"}]},"finishReason":"STOP"}],"modelVersion":"gemini-2.5-flash"}
                """),
        };
        var reply = await Gemini(handler).CompleteAsync(AssistantPrompt.Build(Conversation, new AnalysisSettings(), new PlanLimits(), null), CancellationToken.None);
        reply.Output.Patch.AspectRatio.ShouldBe("9:16");
        reply.Model.ShouldBe("gemini-2.5-flash");
        var sent = handler.Requests.ShouldHaveSingleItem();
        sent.Uri.ToString().ShouldBe("http://gemini.test/v1beta/models/gemini-2.5-flash:generateContent");
        sent.Headers["x-goog-api-key"].ShouldBe("test-key");
        sent.Uri.Query.ShouldBeEmpty(); // the key never goes into the URL
        JsonNode.Parse(sent.Body!)!["generationConfig"]!["responseMimeType"]!.GetValue<string>().ShouldBe("application/json");
    }

    [Fact]
    public async Task Gemini_quota_and_missing_key_are_unavailable()
    {
        var limited = new StubLlmHandler { Responder = (_, _) => StubLlmHandler.JsonResponse(HttpStatusCode.TooManyRequests, """{"error":{"status":"RESOURCE_EXHAUSTED"}}""") };
        var error = await Should.ThrowAsync<AssistantUnavailableException>(() => Gemini(limited).CompleteAsync(AssistantPrompt.Warmup(), CancellationToken.None));
        error.Message.ShouldContain("limite gratuito do Gemini");
        (await Gemini(limited).ProbeAsync(CancellationToken.None)).Available.ShouldBeFalse();

        var noKey = await Gemini(new StubLlmHandler(), key: "").ProbeAsync(CancellationToken.None);
        noKey.Available.ShouldBeFalse();
        noKey.Reason.ShouldBe("Chave da API do Gemini não configurada.");
    }

    [Fact]
    public async Task Local_model_reports_the_file_name_never_the_path()
    {
        var local = new LocalLlmAssistantModel(new FixedClientFactory(new StubLlmHandler()), Microsoft.Extensions.Options.Options.Create(new AssistantOptions
        {
            Local = new LocalAssistantOptions { BaseUrl = "http://llm.test" },
        }), NullLogger<LocalLlmAssistantModel>.Instance);
        var status = await local.ProbeAsync(CancellationToken.None);
        status.ShouldBe(new AssistantStatusDto(true, "local", "Qwen3-1.7B-Q4_K_M", null));
    }

    [Fact]
    public async Task Local_model_unreachable_or_slow_is_unavailable()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AssistantOptions { TimeoutSeconds = 1, Local = new LocalAssistantOptions { BaseUrl = "http://llm.test" } });
        var down = new LocalLlmAssistantModel(new FixedClientFactory(new StubLlmHandler { Unreachable = true }), options, NullLogger<LocalLlmAssistantModel>.Instance);
        (await down.ProbeAsync(CancellationToken.None)).Reason.ShouldBe("O servidor do modelo local não está acessível.");
        await Should.ThrowAsync<AssistantUnavailableException>(() => down.CompleteAsync(AssistantPrompt.Warmup(), CancellationToken.None));

        var slowHandler = new SlowHandler();
        var slow = new LocalLlmAssistantModel(new FixedClientFactory(slowHandler), options, NullLogger<LocalLlmAssistantModel>.Instance);
        var error = await Should.ThrowAsync<AssistantUnavailableException>(() => slow.CompleteAsync(AssistantPrompt.Warmup(), CancellationToken.None));
        error.Message.ShouldContain("demorou demais");
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
