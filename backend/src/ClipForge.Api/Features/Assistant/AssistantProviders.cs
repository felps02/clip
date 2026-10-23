using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClipForge.Api.Options;
using ClipForge.Domain.Common;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Features.Assistant;

/// <summary>The provider cannot answer now (down, loading, timed out, out of free quota): 503 assistant_unavailable.</summary>
public sealed class AssistantUnavailableException : Exception
{
    public AssistantUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public sealed record AssistantModelReply(AssistantModelOutput Output, string? Model);

/// <summary>A model that turns a request into the structured output (never into the user-facing reply).</summary>
public interface IAssistantModel
{
    /// <summary>local | gemini | none.</summary>
    string Provider { get; }

    Task<AssistantStatusDto> ProbeAsync(CancellationToken ct);

    Task<AssistantModelReply> CompleteAsync(AssistantPromptRequest request, CancellationToken ct);

    /// <summary>Fills the provider's prompt cache with the system prompt (no-op where there is none).</summary>
    Task WarmupAsync(CancellationToken ct);
}

public static class AssistantHttp
{
    /// <summary>Named HttpClient of the providers (tests replace its primary handler).</summary>
    public const string ClientName = "assistant";

    public static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}

/// <summary>Provider <c>none</c>: the assistant is off in this environment.</summary>
public sealed class DisabledAssistantModel : IAssistantModel
{
    public const string Reason = "O assistente está desativado neste ambiente.";

    public string Provider => "none";

    public Task<AssistantStatusDto> ProbeAsync(CancellationToken ct) => Task.FromResult(new AssistantStatusDto(false, Provider, null, Reason));

    public Task<AssistantModelReply> CompleteAsync(AssistantPromptRequest request, CancellationToken ct) => throw new AssistantUnavailableException(Reason);

    public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The product's own model: an OpenAI-compatible llama.cpp <c>llama-server</c> (default Qwen3-1.7B Q4_K_M) at
/// <c>Assistant:Local:BaseUrl</c>. JSON-schema constrained output, thinking disabled, prompt prefix cached.
/// </summary>
public sealed class LocalLlmAssistantModel : IAssistantModel
{
    private readonly IHttpClientFactory _http;
    private readonly AssistantOptions _options;
    private readonly ILogger<LocalLlmAssistantModel> _logger;
    private string? _serverModel;

    public LocalLlmAssistantModel(IHttpClientFactory http, IOptions<AssistantOptions> options, ILogger<LocalLlmAssistantModel> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public string Provider => "local";

    private string BaseUrl => (_options.Local.BaseUrl ?? string.Empty).Trim().TrimEnd('/');

    /// <summary>The configured label, else the model file the server reports (file name only, never its path).</summary>
    private string? Label => string.IsNullOrWhiteSpace(_options.Local.Model) ? _serverModel : _options.Local.Model.Trim();

    public async Task<AssistantStatusDto> ProbeAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var client = _http.CreateClient(AssistantHttp.ClientName);
            using var response = await client.GetAsync(BaseUrl + "/health", cts.Token);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return new AssistantStatusDto(false, Provider, Label, "O modelo local ainda está carregando. Tente em instantes.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new AssistantStatusDto(false, Provider, Label, "O servidor do modelo local respondeu com erro.");
            }
            _serverModel ??= await ReadModelNameAsync(client, cts.Token);
            return new AssistantStatusDto(true, Provider, Label, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Local LLM probe failed");
            return new AssistantStatusDto(false, Provider, Label, "O servidor do modelo local não está acessível.");
        }
    }

    private async Task<string?> ReadModelNameAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(BaseUrl + "/v1/models", ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var id = json?["data"] is JsonArray { Count: > 0 } data ? data[0]?["id"]?.GetValue<string>() : null;
            if (string.IsNullOrWhiteSpace(id)) return null;
            var name = id[(id.LastIndexOfAny(['/', '\\']) + 1)..];
            return name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public async Task<AssistantModelReply> CompleteAsync(AssistantPromptRequest request, CancellationToken ct)
    {
        var body = BuildBody(request, AssistantPrompt.MaxTokens, includeSchema: true);
        var json = await PostAsync(body, TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)), ct);
        string? content;
        try
        {
            content = json?["choices"] is JsonArray { Count: > 0 } choices ? choices[0]?["message"]?["content"]?.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            content = null;
        }
        if (content is null) _logger.LogWarning("Local LLM answer without message content");
        return new AssistantModelReply(AssistantModelOutput.Parse(content), Label);
    }

    public async Task WarmupAsync(CancellationToken ct)
    {
        // One token is enough: what matters is that the server processes (and caches) the system prompt.
        var body = BuildBody(AssistantPrompt.Warmup(), maxTokens: 1, includeSchema: false);
        await PostAsync(body, TimeSpan.FromSeconds(Math.Max(300, _options.TimeoutSeconds)), ct);
    }

    /// <summary>The <c>/v1/chat/completions</c> body (public for tests: the request contract with llama-server).</summary>
    public static JsonObject BuildBody(AssistantPromptRequest request, int maxTokens, bool includeSchema)
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        foreach (var message in request.Messages) messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });
        var body = new JsonObject
        {
            ["messages"] = messages,
            ["temperature"] = AssistantPrompt.Temperature,
            ["max_tokens"] = maxTokens,
            ["cache_prompt"] = true,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["stream"] = false,
        };
        if (includeSchema)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = AssistantPrompt.SchemaName, ["strict"] = true, ["schema"] = AssistantPrompt.LocalSchema() },
            };
        }
        return body;
    }

    private async Task<JsonNode?> PostAsync(JsonObject body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var client = _http.CreateClient(AssistantHttp.ClientName);
            using var content = AssistantHttp.Json(body);
            using var response = await client.PostAsync(BaseUrl + "/v1/chat/completions", content, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Local LLM answered HTTP {Status}", (int)response.StatusCode);
                throw new AssistantUnavailableException(response.StatusCode == HttpStatusCode.ServiceUnavailable
                    ? "O modelo local ainda está carregando. Tente em instantes."
                    : "O servidor do modelo local respondeu com erro.");
            }
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var timings = json?["timings"];
            _logger.LogInformation(
                "Local LLM call took {ElapsedMs} ms (prompt {PromptTokens} tokens, {CachedTokens} cached; {GeneratedTokens} generated)",
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, Number(timings?["prompt_n"]), Number(timings?["cache_n"]), Number(timings?["predicted_n"]));
            return json;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Local LLM call timed out after {Seconds} s", timeout.TotalSeconds);
            throw new AssistantUnavailableException("O assistente demorou demais para responder. Tente novamente em instantes.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Local LLM unreachable: {Message}", ex.Message);
            throw new AssistantUnavailableException("O servidor do modelo local não está acessível.", ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning(ex, "Local LLM call failed");
            throw new AssistantUnavailableException("O servidor do modelo local respondeu em um formato inesperado.", ex);
        }
    }

    private static long? Number(JsonNode? node)
    {
        try
        {
            return node?.GetValue<long>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Optional free cloud provider: Google's Gemini API (<c>generateContent</c> with <c>responseMimeType</c> application/json and
/// a <c>responseSchema</c>), key from Google AI Studio. On the free tier Google may use prompts to improve its products.
/// </summary>
public sealed class GeminiAssistantModel : IAssistantModel
{
    private readonly IHttpClientFactory _http;
    private readonly AssistantOptions _options;
    private readonly ILogger<GeminiAssistantModel> _logger;

    public GeminiAssistantModel(IHttpClientFactory http, IOptions<AssistantOptions> options, ILogger<GeminiAssistantModel> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public string Provider => "gemini";

    private string Model => string.IsNullOrWhiteSpace(_options.Gemini.Model) ? "gemini-2.5-flash" : _options.Gemini.Model.Trim();

    private string ModelUrl => (string.IsNullOrWhiteSpace(_options.Gemini.BaseUrl) ? "https://generativelanguage.googleapis.com/v1beta" : _options.Gemini.BaseUrl.Trim().TrimEnd('/'))
        + "/models/" + Uri.EscapeDataString(Model);

    private bool HasKey => !string.IsNullOrWhiteSpace(_options.Gemini.ApiKey);

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-goog-api-key", _options.Gemini.ApiKey!.Trim()); // header, never the URL (keys stay out of logs)
        return request;
    }

    public async Task<AssistantStatusDto> ProbeAsync(CancellationToken ct)
    {
        if (!HasKey) return new AssistantStatusDto(false, Provider, Model, "Chave da API do Gemini não configurada.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = NewRequest(HttpMethod.Get, ModelUrl);
            using var response = await _http.CreateClient(AssistantHttp.ClientName).SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode) return new AssistantStatusDto(true, Provider, Model, null);
            return new AssistantStatusDto(false, Provider, Model, ErrorReason(response.StatusCode));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Gemini probe failed");
            return new AssistantStatusDto(false, Provider, Model, "O Gemini não está acessível a partir do servidor.");
        }
    }

    private static string ErrorReason(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => "O limite gratuito do Gemini foi atingido. Tente mais tarde.",
        HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound => "Chave ou modelo do Gemini inválido.",
        _ => "O Gemini está indisponível no momento.",
    };

    public async Task<AssistantModelReply> CompleteAsync(AssistantPromptRequest prompt, CancellationToken ct)
    {
        if (!HasKey) throw new AssistantUnavailableException("Chave da API do Gemini não configurada.");
        var body = BuildBody(prompt, _options.Gemini.ThinkingBudget);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        try
        {
            using var request = NewRequest(HttpMethod.Post, ModelUrl + ":generateContent");
            request.Content = AssistantHttp.Json(body);
            using var response = await _http.CreateClient(AssistantHttp.ClientName).SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini answered HTTP {Status}", (int)response.StatusCode);
                throw new AssistantUnavailableException(ErrorReason(response.StatusCode));
            }
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var parts = json?["candidates"] is JsonArray { Count: > 0 } candidates && candidates[0]?["content"]?["parts"] is JsonArray array ? array : null;
            var text = parts is null ? null : string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? string.Empty));
            return new AssistantModelReply(AssistantModelOutput.Parse(text), json?["modelVersion"]?.GetValue<string>() ?? Model);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistantUnavailableException("O assistente demorou demais para responder. Tente novamente em instantes.");
        }
        catch (HttpRequestException ex)
        {
            throw new AssistantUnavailableException("O Gemini não está acessível a partir do servidor.", ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning(ex, "Unexpected Gemini answer");
            throw new AssistantUnavailableException("O Gemini respondeu em um formato inesperado.", ex);
        }
    }

    /// <summary>The <c>generateContent</c> body (public for tests).</summary>
    public static JsonObject BuildBody(AssistantPromptRequest prompt, int? thinkingBudget)
    {
        var contents = new JsonArray();
        foreach (var message in prompt.Messages)
        {
            contents.Add(new JsonObject
            {
                ["role"] = message.Role == "assistant" ? "model" : "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = message.Content }),
            });
        }
        var generation = new JsonObject
        {
            ["responseMimeType"] = "application/json",
            ["responseSchema"] = AssistantPrompt.GeminiSchema(),
            ["temperature"] = AssistantPrompt.Temperature,
            ["maxOutputTokens"] = AssistantPrompt.MaxTokens,
        };
        if (thinkingBudget is >= 0 and { } budget) generation["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = budget };
        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt.SystemPrompt }) },
            ["contents"] = contents,
            ["generationConfig"] = generation,
        };
    }

    public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary><c>GET /api/assistant/status</c> reuses its last probe for <c>Assistant:StatusCacheSeconds</c>.</summary>
public sealed class AssistantStatusCache
{
    private readonly IAssistantModel _model;
    private readonly IClock _clock;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Entry? _entry;

    public AssistantStatusCache(IAssistantModel model, IClock clock, IOptions<AssistantOptions> options)
    {
        _model = model;
        _clock = clock;
        _ttl = TimeSpan.FromSeconds(Math.Max(0, options.Value.StatusCacheSeconds));
    }

    public async Task<AssistantStatusDto> GetAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _entry) is { } fresh && _clock.UtcNow < fresh.Expires) return fresh.Status;
        await _gate.WaitAsync(ct);
        try
        {
            if (_entry is { } again && _clock.UtcNow < again.Expires) return again.Status;
            var status = await _model.ProbeAsync(ct);
            Volatile.Write(ref _entry, new Entry(status, _clock.UtcNow + _ttl));
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forget the cached status (after a failed call, the next request probes again).</summary>
    public void Invalidate() => Volatile.Write(ref _entry, null);

    private sealed record Entry(AssistantStatusDto Status, DateTimeOffset Expires);
}

/// <summary>Warms llama.cpp's prompt cache once at startup (local provider), in the background.</summary>
public sealed class AssistantWarmup : BackgroundService
{
    private readonly IAssistantModel _model;
    private readonly AssistantOptions _options;
    private readonly ILogger<AssistantWarmup> _logger;

    public AssistantWarmup(IAssistantModel model, IOptions<AssistantOptions> options, ILogger<AssistantWarmup> logger)
    {
        _model = model;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_model.Provider != "local" || !_options.Local.WarmupOnStartup) return;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 2 : 20), stoppingToken);
                var started = Stopwatch.GetTimestamp();
                await _model.WarmupAsync(stoppingToken);
                _logger.LogInformation("Assistant prompt cache warmed in {Seconds:0.0} s", Stopwatch.GetElapsedTime(started).TotalSeconds);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Assistant prompt cache warm-up attempt {Attempt} failed: {Message}", attempt, ex.Message);
            }
        }
    }
}
