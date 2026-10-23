using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Options;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string JwtSigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "clipforge";
    public string Audience { get; set; } = "clipforge-web";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
    public bool CookieSecure { get; set; }
    /// <summary>
    /// Optional Domain attribute of cf_access / cf_refresh (e.g. "example.com"). Empty (default) = host-only cookies of
    /// the API host. Set it when the web app runs on a sibling subdomain (app.example.com next to api.example.com) and
    /// must see cf_access (the web's server-side route guard reads it).
    /// </summary>
    public string? CookieDomain { get; set; }
    public bool RequireConfirmedEmail { get; set; }
    public int LockoutMaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 5;

    public const string AccessCookieName = "cf_access";
    public const string RefreshCookieName = "cf_refresh";
    public const string RefreshCookiePath = "/api/auth";
}

public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    public int LeaseSeconds { get; set; } = 180;
    public int MaxAttempts { get; set; } = 3;
    public int ReaperIntervalSeconds { get; set; } = 30;
    /// <summary>Validity of the signed source URL handed to workers (must cover the longest job).</summary>
    public int SourceUrlHours { get; set; } = 12;
}

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public int TempUploadHours { get; set; } = 24;
    public int CleanupIntervalMinutes { get; set; } = 15;
    /// <summary>Grace period between the deletion request and the purge of the account.</summary>
    public int AccountPurgeDelayHours { get; set; } = 24;
}

public sealed class InternalOptions
{
    public const string SectionName = "Internal";
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";
    public string BaseUrl { get; set; } = "http://localhost:3000";
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; set; } = ["http://localhost:3000"];
}

public sealed class BrandingOptions
{
    public const string SectionName = "Branding";
    public string Name { get; set; } = "ClipForge";
    public string SupportEmail { get; set; } = "suporte@example.com";
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public bool MigrateOnStartup { get; set; }
}

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";
    /// <summary>Credential endpoints (register, login, password reset, e-mail verification, change password, account deletion): per IP.</summary>
    public int AuthPerMinute { get; set; } = 10;
    /// <summary>POST /api/auth/refresh: per IP, its own window (the web calls it on page loads and after every expired access token).</summary>
    public int RefreshPerMinute { get; set; } = 60;
    public int UploadsPerMinute { get; set; } = 30;
    /// <summary>Signed transfer endpoints (Local storage PUT): per IP, sized for worker artifact bursts.</summary>
    public int TransfersPerMinute { get; set; } = 600;
    /// <summary>POST /api/assistant/chat: per user.</summary>
    public int AssistantPerMinute { get; set; } = 20;
}

/// <summary>
/// The settings assistant (section "Assistant", ADR-010): the product's own model served by llama.cpp (local, default),
/// Google's Gemini API on its free tier (gemini) or nothing (none). No paid provider is wired.
/// </summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>local | gemini | none.</summary>
    public string Provider { get; set; } = "local";
    /// <summary>Upper bound of one model call (the first call before the prompt cache is warm is the slowest).</summary>
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>How long GET /api/assistant/status reuses its last probe of the provider.</summary>
    public int StatusCacheSeconds { get; set; } = 10;
    public LocalAssistantOptions Local { get; set; } = new();
    public GeminiAssistantOptions Gemini { get; set; } = new();

    public string NormalizedProvider => (Provider ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "local" or "llama" or "llamacpp" or "llama.cpp" => "local",
        "gemini" or "google" => "gemini",
        _ => "none",
    };
}

public sealed class LocalAssistantOptions
{
    /// <summary>OpenAI-compatible llama-server (scripts/dev-llm.ps1, Docker service "llm").</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8090";
    /// <summary>Label shown by GET /api/assistant/status; empty = the model file the server reports.</summary>
    public string? Model { get; set; }
    /// <summary>Send one request with the system prompt at startup so the server caches its prefix.</summary>
    public bool WarmupOnStartup { get; set; } = true;
}

public sealed class GeminiAssistantOptions
{
    /// <summary>Free-tier key from Google AI Studio (secret: environment / user-secrets only).</summary>
    public string? ApiKey { get; set; }
    /// <summary>Model id; verify it in Google AI Studio (free tier: Flash models).</summary>
    public string Model { get; set; } = "gemini-2.5-flash";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    /// <summary>generationConfig.thinkingConfig.thinkingBudget (0 = no thinking, fastest); null = not sent.</summary>
    public int? ThinkingBudget { get; set; } = 0;
}

/// <summary>
/// Trusted reverse proxies (section "ReverseProxy"). When enabled, X-Forwarded-For / X-Forwarded-Proto sent by the
/// listed proxies replace the connection's remote address and scheme, so per-IP rate limiting, lockout and session
/// IPs see the real client instead of the proxy. Loopback is always trusted.
/// </summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    public bool Enabled { get; set; }
    /// <summary>IP addresses of the proxies in front of the API (besides loopback).</summary>
    public string[] KnownProxies { get; set; } = [];
    /// <summary>CIDR ranges of the proxies in front of the API (besides loopback), e.g. 10.0.0.0/8.</summary>
    public string[] KnownNetworks { get; set; } = [];
    /// <summary>Trust the forwarded headers from any peer: only when the API is reachable exclusively through the proxy.</summary>
    public bool TrustAllProxies { get; set; }
    /// <summary>Number of proxy hops to resolve (one per trusted proxy between the client and the API).</summary>
    public int ForwardLimit { get; set; } = 1;
}

public sealed class MediaOptions
{
    public const string SectionName = "Media";
    public int UploadUrlMinutes { get; set; } = 120;
    public int DownloadUrlMinutes { get; set; } = 15;
    public int PreviewUrlMinutes { get; set; } = 30;
    public int ThumbnailUrlMinutes { get; set; } = 60;
}

/// <summary>One plan in the "Plans" configuration section (keyed by plan code).</summary>
public sealed class PlanConfig
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public int? PriceMonthlyCents { get; set; }
    public string Currency { get; set; } = "BRL";
    public string? ProviderPriceId { get; set; }
    public PlanLimits Limits { get; set; } = new();
    public List<string> Features { get; set; } = new();
}
