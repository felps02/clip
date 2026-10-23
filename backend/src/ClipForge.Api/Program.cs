using System.Net;
using System.Threading.RateLimiting;
using ClipForge.Api.Common;
using ClipForge.Api.Features.Account;
using ClipForge.Api.Features.Assistant;
using ClipForge.Api.Features.Auth;
using ClipForge.Api.Features.Billing;
using ClipForge.Api.Features.Clips;
using ClipForge.Api.Features.Dashboard;
using ClipForge.Api.Features.Health;
using ClipForge.Api.Features.Internal;
using ClipForge.Api.Features.Jobs;
using ClipForge.Api.Features.Media;
using ClipForge.Api.Features.Notifications;
using ClipForge.Api.Features.Plans;
using ClipForge.Api.Features.Projects;
using ClipForge.Api.Features.Sources;
using ClipForge.Api.Features.Webhooks;
using ClipForge.Api.HostedServices;
using ClipForge.Api.Options;
using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Infrastructure;
using ClipForge.Infrastructure.Persistence;
using ClipForge.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- logging (no secrets, structured)
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "ClipForge.Api")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} (cid={CorrelationId}){NewLine}{Exception}"));

// ---------------------------------------------------------------- options
var configuration = builder.Configuration;
builder.Services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<JobsOptions>(configuration.GetSection(JobsOptions.SectionName));
builder.Services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.SectionName));
builder.Services.Configure<InternalOptions>(configuration.GetSection(InternalOptions.SectionName));
builder.Services.Configure<FrontendOptions>(configuration.GetSection(FrontendOptions.SectionName));
builder.Services.Configure<CorsOptions>(configuration.GetSection(CorsOptions.SectionName));
builder.Services.Configure<BrandingOptions>(configuration.GetSection(BrandingOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));
builder.Services.Configure<MediaOptions>(configuration.GetSection(MediaOptions.SectionName));
builder.Services.Configure<ReverseProxyOptions>(configuration.GetSection(ReverseProxyOptions.SectionName));
builder.Services.Configure<AssistantOptions>(configuration.GetSection(AssistantOptions.SectionName));

var authOptions = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
var internalOptions = configuration.GetSection(InternalOptions.SectionName).Get<InternalOptions>() ?? new InternalOptions();
var rateLimits = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
var storageOptions = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
var corsOptions = configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions();
var proxyOptions = configuration.GetSection(ReverseProxyOptions.SectionName).Get<ReverseProxyOptions>() ?? new ReverseProxyOptions();
var assistantOptions = configuration.GetSection(AssistantOptions.SectionName).Get<AssistantOptions>() ?? new AssistantOptions();

if (string.IsNullOrWhiteSpace(internalOptions.ApiKey) || internalOptions.ApiKey.Length < 8)
{
    throw new InvalidOperationException("Internal:ApiKey must be configured (at least 8 characters).");
}

// ---------------------------------------------------------------- infrastructure (db, storage, e-mail, payments)
builder.Services.AddInfrastructure(configuration);

// ---------------------------------------------------------------- identity
builder.Services.AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredUniqueChars = 1;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = Math.Max(1, authOptions.LockoutMaxFailedAttempts);
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(Math.Max(1, authOptions.LockoutMinutes));
        options.SignIn.RequireConfirmedEmail = false; // enforced by AuthService according to Auth:RequireConfirmedEmail
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// ---------------------------------------------------------------- authentication: JWT (cookie or bearer) + internal API key
var signingKey = TokenService.CreateKey(authOptions.JwtSigningKey);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = authOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role",
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token) && !context.Request.Headers.ContainsKey("Authorization")
                    && context.Request.Cookies.TryGetValue(AuthOptions.AccessCookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
                {
                    context.Token = cookie;
                }
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await Problems.WriteAsync(context.HttpContext, 401, ErrorCodes.Unauthorized, "Autenticação necessária.");
            },
            OnForbidden = context => Problems.WriteAsync(context.HttpContext, 403, ErrorCodes.Forbidden, "Acesso negado."),
        };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, InternalApiKeyAuthenticationHandler>(InternalApiKeyAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(InternalApiKeyAuthenticationHandler.PolicyName, policy => policy
        .AddAuthenticationSchemes(InternalApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .RequireClaim("scope", "internal"));
});

// ---------------------------------------------------------------- CORS (credentials)
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(corsOptions.AllowedOrigins.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.TrimEnd('/')).ToArray())
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()
    .WithExposedHeaders(CorrelationIdMiddleware.HeaderName, "Content-Disposition")));

// ---------------------------------------------------------------- rate limiting (auth per IP, uploads per user, signed transfers per IP)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        // Retry-After is set inside WriteAsync: the response is cleared before the problem is written.
        await Problems.WriteAsync(context.HttpContext, 429, ErrorCodes.RateLimited, "Muitas requisições. Tente novamente em instantes.",
            headers: headers => headers.RetryAfter = "60");
    };
    // The client address: behind a configured reverse proxy this is the X-Forwarded-For value resolved by the
    // forwarded-headers middleware, never the proxy itself (which would put every user in one bucket).
    static string ClientKey(HttpContext context) => "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    options.AddPolicy(AuthEndpoints.AuthRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, rateLimits.AuthPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Refresh has its own window: it runs on page loads and after every expired access token, so it must not eat
    // the credential budget, but it is still bounded per IP.
    options.AddPolicy(AuthEndpoints.RefreshRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, rateLimits.RefreshPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy(MediaEndpoints.UploadsRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.TryGetUserId() is { } userId ? "user:" + userId : ClientKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, rateLimits.UploadsPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy(StorageEndpoints.TransfersRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, rateLimits.TransfersPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Assistant chat: per user (every message runs the model).
    options.AddPolicy(AssistantEndpoints.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.TryGetUserId() is { } userId ? "user:" + userId : ClientKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, rateLimits.AssistantPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// ---------------------------------------------------------------- JSON, problem details, validation, OpenAPI
builder.Services.ConfigureHttpJsonOptions(options => ApiJson.Apply(options.SerializerOptions));
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddApiValidators();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();

// ---------------------------------------------------------------- application services
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<SubscriptionService>();
builder.Services.AddScoped<QuotaService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddSingleton<EmailOutbox>();
builder.Services.AddScoped<JobEvents>();
builder.Services.AddScoped<MediaUrls>();
builder.Services.AddScoped<RenderService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<ClipsArchiveService>();
builder.Services.AddScoped<ClipService>();
builder.Services.AddScoped<InternalJobService>();
builder.Services.AddScoped<WebhookService>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddScoped<UploadTargetGuard>();
builder.Services.AddSingleton<SourceValidationService>();
builder.Services.AddSingleton<IMediaProvider>(new UnsupportedMediaProvider(DetectedPlatform.Twitch));
builder.Services.AddSingleton<IMediaProvider>(new UnsupportedMediaProvider(DetectedPlatform.Youtube));
builder.Services.AddSingleton<IMediaProvider>(new UnsupportedMediaProvider(DetectedPlatform.Kick));

// ---------------------------------------------------------------- settings assistant (ADR-010: own local model, free Gemini or none)
builder.Services.AddHttpClient(AssistantHttp.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan); // bounded per call
switch (assistantOptions.NormalizedProvider)
{
    case "local":
        builder.Services.AddSingleton<IAssistantModel, LocalLlmAssistantModel>();
        break;
    case "gemini":
        builder.Services.AddSingleton<IAssistantModel, GeminiAssistantModel>();
        break;
    default:
        builder.Services.AddSingleton<IAssistantModel, DisabledAssistantModel>();
        break;
}
builder.Services.AddSingleton<AssistantStatusCache>();
builder.Services.AddScoped<AssistantService>();

// ---------------------------------------------------------------- hosted services
builder.Services.AddHostedService<PlanSeeder>();
builder.Services.AddHostedService<JobReaper>();
builder.Services.AddHostedService<RetentionCleanup>();
builder.Services.AddHostedService<AccountPurge>();
builder.Services.AddHostedService<EmailDispatcher>();
builder.Services.AddHostedService<AssistantWarmup>();

var app = builder.Build();

// ---------------------------------------------------------------- migrations
var databaseOptions = app.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
if (databaseOptions.MigrateOnStartup)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Database migrations applied");
}

// ---------------------------------------------------------------- pipeline
if (proxyOptions.Enabled)
{
    // Behind the documented HTTPS reverse proxy every connection comes from the proxy: resolve the real client
    // address and scheme from X-Forwarded-* first, and only when the peer is a trusted proxy (loopback plus the
    // configured addresses/networks). Rate limiting, lockout and refresh-token IPs then see the real client.
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = Math.Max(1, proxyOptions.ForwardLimit),
    };
    if (proxyOptions.TrustAllProxies)
    {
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();
    }
    else
    {
        foreach (var proxy in proxyOptions.KnownProxies.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            forwarded.KnownProxies.Add(IPAddress.Parse(proxy.Trim()));
        }
        foreach (var network in proxyOptions.KnownNetworks.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network.Trim()));
        }
        if (forwarded.KnownProxies.Count <= 1 && forwarded.KnownIPNetworks.Count <= 1)
        {
            app.Logger.LogWarning("ReverseProxy is enabled without KnownProxies/KnownNetworks: only a proxy on loopback is trusted");
        }
    }
    app.UseForwardedHeaders(forwarded);
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (http, _, ex) => ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
        : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Debug
        : LogEventLevel.Information;
});
app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("ClipForge API"));

app.MapGet("/health", (IObjectStorage storage) => Results.Ok(new
{
    status = "ok",
    storage = storage.ProviderName,
    time = DateTimeOffset.UtcNow,
})).WithTags("Health").AllowAnonymous();
app.MapHealthEndpoints();

app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapMediaEndpoints();
if (storageOptions.IsLocal)
{
    app.MapLocalStorageEndpoints();
}
app.MapSourcesEndpoints();
app.MapProjectsEndpoints();
app.MapClipsEndpoints();
app.MapDashboardEndpoints();
app.MapJobsEndpoints();
app.MapNotificationsEndpoints();
app.MapPlansEndpoints();
app.MapBillingEndpoints();
app.MapWebhooksEndpoints();
app.MapAssistantEndpoints();
app.MapInternalEndpoints();

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
