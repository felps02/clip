using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClipForge.Api.Features.Assistant;
using ClipForge.Api.Options;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ClipForge.Api.Tests.Integration;

/// <summary>A fact that runs only when a llama-server answers at http://127.0.0.1:8090 (scripts/dev-llm.ps1).</summary>
public sealed class LocalLlmFactAttribute : FactAttribute
{
    public const string BaseUrl = "http://127.0.0.1:8090";

    public LocalLlmFactAttribute()
    {
        if (!IsReachable()) Skip = $"No local LLM server at {BaseUrl} (start it with scripts/dev-llm.ps1).";
    }

    private static bool IsReachable()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = client.GetAsync(BaseUrl + "/health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>The reference request against the real local model (Qwen3-1.7B on llama.cpp), end to end through the API.</summary>
public class AssistantLocalLlmTests : TestBase
{
    private readonly ITestOutputHelper _output;

    public AssistantLocalLlmTests(ApiFactory factory, ITestOutputHelper output) : base(factory)
    {
        _output = output;
    }

    [LocalLlmFact]
    public async Task The_reference_request_yields_funny_30_seconds_vertical_and_a_reply_about_only_that()
    {
        await using var real = new ApiFactory { UseLlmStub = false };
        real.ExtraSettings["Assistant:Local:BaseUrl"] = LocalLlmFactAttribute.BaseUrl;
        real.ExtraSettings["Assistant:TimeoutSeconds"] = "300"; // a cold prompt cache on a busy CPU can take minutes
        var client = real.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        client.Timeout = TimeSpan.FromMinutes(6);
        (await client.PostAsJsonAsync("/api/auth/register", new { email = NextEmail("llm"), password = "Password123", displayName = "LLM", acceptedTerms = true }, Json))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var watch = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync("/api/assistant/chat", new
        {
            messages = new[] { new { role = "user", content = "quero clipes mais engraçados, de uns 30 segundos, pro tiktok" } },
            currentSettings = new
            {
                minClipSeconds = 15, targetClipSeconds = 45, maxClipSeconds = 60, aspectRatio = "16:9", sensitivity = 50, maxCandidates = 15, autoRenderTop = 15,
                subtitles = new { enabled = true, style = "bold", position = "bottom" }, language = "auto", framing = new { mode = "auto" },
                focusCategories = Array.Empty<string>(), customKeywords = Array.Empty<string>(),
            },
            context = new { durationSeconds = 600.0 },
        }, Json);
        var text = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"{watch.Elapsed.TotalSeconds:0.0} s: {text}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        var body = await ReadJsonAsync(response);

        var patch = body.GetProperty("settingsPatch");
        patch.GetProperty("focusCategories").EnumerateArray().Select(e => e.GetString()).ShouldBe(["funny"]);
        patch.GetProperty("targetClipSeconds").GetInt32().ShouldBe(30);
        patch.GetProperty("aspectRatio").GetString().ShouldBe("9:16");
        patch.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["aspectRatio", "focusCategories", "maxClipSeconds", "minClipSeconds", "targetClipSeconds"]);
        body.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("field").GetString()).ShouldBe(["focusCategories", "targetClipSeconds", "aspectRatio"]);
        body.GetProperty("provider").GetString().ShouldBe("local");

        var reply = body.GetProperty("reply").GetString()!;
        reply.ShouldContain("momentos engraçados");
        reply.ShouldContain("alvo de 30 s");
        reply.ShouldContain("9:16");
        foreach (var other in new[] { "legenda", "idioma", "sensibilidade", "palavra", "momentos por análise", "clipes automáticos", "enquadramento", "postar", "música", "efeito" })
        {
            reply.ShouldNotContain(other, Case.Insensitive, $"the reply mentions something that did not change: {reply}");
        }
    }
}

/// <summary>
/// Negated requests and keyword lists against the real local model (round-3 review): whatever the model answers, the
/// assistant never applies or confirms the refused change and never turns the rest of the sentence into keywords.
/// </summary>
public class AssistantLocalLlmNegationTests
{
    private static readonly PlanLimits Creator = new() { MaxCandidatesPerJob = 30, MaxAutoRenderPerJob = 30, AllowCustomSubtitleStyles = true, AllowWebcamLayout = true };
    private static readonly JsonSerializerOptions LogJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly ITestOutputHelper _output;
    private readonly LocalLlmAssistantModel _model = new(new Clients(),
        Microsoft.Extensions.Options.Options.Create(new AssistantOptions { TimeoutSeconds = 300, Local = { BaseUrl = LocalLlmFactAttribute.BaseUrl } }),
        NullLogger<LocalLlmAssistantModel>.Instance);

    public AssistantLocalLlmNegationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class Clients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromMinutes(6) };
    }

    private async Task<(AnalysisSettings Settings, string Reply)> AskAsync(string message, Action<AnalysisSettings>? change = null)
    {
        var settings = new AnalysisSettings();
        change?.Invoke(settings);
        var current = PlanLimitHelpers.CapSettings(settings, Creator);
        var reply = await _model.CompleteAsync(AssistantPrompt.Build([new AssistantChatMessage("user", message)], current, Creator, 600m), CancellationToken.None);
        var outcome = AssistantPatchBuilder.Apply(current, AssistantMerge.Merge(AssistantRules.Analyze(message, current), reply.Output, message), Creator, 600m);
        var text = AssistantReplyComposer.Compose(outcome, 1);
        _output.WriteLine($"{message}\n  model: {JsonSerializer.Serialize(reply.Output.Patch, LogJson)}\n  reply: {text}");
        return (outcome.Proposed, text);
    }

    [LocalLlmFact]
    public async Task Negated_requests_and_keyword_lists_never_become_the_opposite_change()
    {
        var webcam = await AskAsync("não quero webcam", s => s.Framing.Mode = "webcam-split");
        webcam.Settings.Framing.Mode.ShouldNotBe("webcam-split");
        webcam.Reply.ShouldNotContain("webcam + jogo");

        var subtitles = await AskAsync("não quero legenda no meio, quero embaixo", s => s.Subtitles.Position = "center");
        subtitles.Settings.Subtitles.Enabled.ShouldBeTrue();
        subtitles.Settings.Subtitles.Position.ShouldBe("bottom");
        subtitles.Reply.ShouldNotContain("desativadas");

        var lengths = await AskAsync("não quero clipes curtos");
        lengths.Settings.TargetClipSeconds.ShouldBeGreaterThan(AssistantRules.ShortClips.Max);

        var language = await AskAsync("a live não é em inglês");
        language.Settings.Language.ShouldNotBe("en");

        var keywords = await AskAsync("marca a palavra gg e coloca legenda no meio");
        keywords.Settings.CustomKeywords.ShouldBe(["gg"]);
        keywords.Settings.Subtitles.Position.ShouldBe("center");

        var list = await AskAsync("as palavras chave são gg, ez, clutch");
        list.Settings.CustomKeywords.ShouldBe(["gg", "ez", "clutch"]);
    }
}
