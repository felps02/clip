using System.Net;
using System.Text;
using System.Text.Json;

namespace ClipForge.Api.Tests.Infrastructure;

/// <summary>A request the assistant's HttpClient sent (body read as text).</summary>
public sealed record RecordedLlmRequest(HttpMethod Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Primary handler of the assistant's HttpClient in tests: answers like llama-server (<c>/health</c>, <c>/v1/models</c>,
/// <c>/v1/chat/completions</c>) with a configurable model output, or like an unreachable server. Records every request.
/// Shared by the handler rotations of IHttpClientFactory, so it is never disposed.
/// </summary>
public sealed class StubLlmHandler : HttpMessageHandler
{
    public const string EmptyOutput = """{"intent":"other","patch":{},"unsupported":[],"question":null}""";

    private readonly object _gate = new();
    private readonly List<RecordedLlmRequest> _requests = [];

    /// <summary>The model output (message content) returned by <c>/v1/chat/completions</c>.</summary>
    public string ChatContent { get; set; } = EmptyOutput;

    /// <summary>False: <c>/health</c> answers 503 (model loading).</summary>
    public bool Healthy { get; set; } = true;

    /// <summary>True: every request fails like a refused connection.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Overrides every answer when set.</summary>
    public Func<HttpRequestMessage, string?, HttpResponseMessage>? Responder { get; set; }

    public IReadOnlyList<RecordedLlmRequest> Requests
    {
        get
        {
            lock (_gate) return _requests.ToList();
        }
    }

    public IReadOnlyList<RecordedLlmRequest> ChatRequests => Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/v1/chat/completions", StringComparison.Ordinal)).ToList();

    public void Reset()
    {
        lock (_gate) _requests.Clear();
        ChatContent = EmptyOutput;
        Healthy = true;
        Unreachable = false;
        Responder = null;
    }

    public static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string ChatCompletion(string content)
        => JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
            model = "C:/models/Qwen3-1.7B-Q4_K_M.gguf",
            timings = new { prompt_n = 120, cache_n = 1100, predicted_n = 40 },
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (_gate) _requests.Add(new RecordedLlmRequest(request.Method, request.RequestUri!, body, headers));

        if (Unreachable) throw new HttpRequestException("Connection refused (stub)");
        if (Responder is { } responder) return responder(request, body);

        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/health", StringComparison.Ordinal))
        {
            return Healthy
                ? JsonResponse(HttpStatusCode.OK, """{"status":"ok"}""")
                : JsonResponse(HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"message":"Loading model"}}""");
        }
        if (path.EndsWith("/v1/models", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, """{"object":"list","data":[{"id":"C:/models/Qwen3-1.7B-Q4_K_M.gguf","object":"model"}]}""");
        }
        if (path.EndsWith("/v1/chat/completions", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, ChatCompletion(ChatContent));
        }
        return JsonResponse(HttpStatusCode.NotFound, "{}");
    }

    protected override void Dispose(bool disposing)
    {
        // Intentionally empty: the instance outlives the handler rotations of IHttpClientFactory.
    }
}
