using ClipForge.Api.Features.Assistant;
using ClipForge.Api.HostedServices;
using ClipForge.Api.Services;
using ClipForge.Infrastructure.Email;
using ClipForge.Infrastructure.Payments;
using ClipForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Respawn;

namespace ClipForge.Api.Tests.Infrastructure;

/// <summary>
/// Boots the API against the clipforge_test database (ConnectionStrings:Test / ConnectionStrings__Test), applies the
/// migrations once per run and resets the data between tests with Respawn.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string InternalApiKey = "test-internal-api-key-0123456789";
    public const string JwtSigningKey = "test-only-jwt-signing-key-with-at-least-32-characters";
    public const string StorageSigningKey = "test-only-storage-signing-key-with-32-chars-min";

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "clipforge-tests", Guid.NewGuid().ToString("N"));

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("ConnectionStrings__Test")
        ?? "Host=localhost;Port=5432;Database=clipforge_test;Username=clipforge;Password=clipforge";

    public FakePaymentProvider Payments { get; } = new();

    /// <summary>
    /// The assistant's model server in tests (llama-server look-alike). Every factory uses it unless
    /// <see cref="UseLlmStub"/> is turned off before the host starts (the one test against the real local server).
    /// </summary>
    public StubLlmHandler Llm { get; } = new();

    public bool UseLlmStub { get; set; } = true;

    /// <summary>Settings merged over the defaults; set them before the host starts (e.g. a one-off factory with a reverse proxy configured).</summary>
    public Dictionary<string, string?> ExtraSettings { get; } = new();

    private Respawner? _respawner;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Settings are passed as host settings (command-line args for WebApplicationBuilder apps) so they are
        // visible to Program.cs before the host is built (ConfigureAppConfiguration would only apply at Build()).
        foreach (var (key, value) in TestSettings().Concat(ExtraSettings))
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(Payments);
            if (UseLlmStub)
            {
                services.AddHttpClient(AssistantHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => Llm);
            }
        });
    }

    private Dictionary<string, string?> TestSettings() => new()
    {
        ["ConnectionStrings:Default"] = ConnectionString,
        ["ConnectionStrings:Test"] = ConnectionString,
        ["Database:MigrateOnStartup"] = "true",
        ["Auth:JwtSigningKey"] = JwtSigningKey,
        ["Auth:CookieSecure"] = "false",
        ["Auth:RequireConfirmedEmail"] = "false",
        ["Auth:AccessTokenMinutes"] = "15",
        ["Auth:RefreshTokenDays"] = "30",
        ["Internal:ApiKey"] = InternalApiKey,
        ["Storage:Provider"] = "Local",
        ["Storage:SigningKey"] = StorageSigningKey,
        ["Storage:Local:RootPath"] = StorageRoot,
        ["Api:PublicBaseUrl"] = "http://localhost",
        ["Api:WorkerBaseUrl"] = "http://localhost",
        ["Frontend:BaseUrl"] = "http://localhost:3000",
        ["Email:Provider"] = "Log",
        ["Payments:Provider"] = "None",
        ["RateLimiting:AuthPerMinute"] = "100000",
        ["RateLimiting:RefreshPerMinute"] = "100000",
        ["RateLimiting:UploadsPerMinute"] = "100000",
        ["RateLimiting:TransfersPerMinute"] = "100000",
        ["RateLimiting:AssistantPerMinute"] = "100000",
        // The assistant talks to the stub handler (never to a model server on this machine) and probes it on every call.
        ["Assistant:Provider"] = "local",
        ["Assistant:Local:BaseUrl"] = "http://llm.test",
        ["Assistant:Local:Model"] = "",
        ["Assistant:Local:WarmupOnStartup"] = "false",
        ["Assistant:StatusCacheSeconds"] = "0",
        ["Assistant:TimeoutSeconds"] = "10",
        ["Assistant:Gemini:ApiKey"] = "",
        ["Jobs:LeaseSeconds"] = "180",
        ["Jobs:MaxAttempts"] = "3",
        ["Jobs:ReaperIntervalSeconds"] = "3600",
        ["Retention:TempUploadHours"] = "24",
        ["Retention:CleanupIntervalMinutes"] = "600",
        ["Retention:AccountPurgeDelayHours"] = "0",
        ["Serilog:MinimumLevel:Default"] = "Warning",
    };

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(StorageRoot);
        // Force the host to start (applies migrations + seeds plans).
        _ = Server;
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            // TRUNCATE … CASCADE instead of disabling triggers on the clips <-> clip_renders FK cycle (needs no superuser).
            DbAdapter = new TruncateCascadePostgresAdapter(),
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory"), new Respawn.Graph.Table("plans")],
        });
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await _respawner!.ResetAsync(connection);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Plans.AnyAsync())
        {
            await PlanSeeder.SeedAsync(scope.ServiceProvider, CancellationToken.None);
        }
        // E-mails of the previous test still in the background outbox must not land in this one.
        await Outbox.WaitIdleAsync(TimeSpan.FromSeconds(10));
        Emails.Clear();
        Payments.Reset();
        Llm.Reset();
    }

    public LogEmailSender Emails => Services.GetRequiredService<LogEmailSender>();

    public EmailOutbox Outbox => Services.GetRequiredService<EmailOutbox>();

    public AsyncServiceScope CreateAsyncScope() => Services.CreateAsyncScope();

    /// <summary>Also used by one-off factories (<c>await using var factory = new ApiFactory()</c>): removes their storage root.</summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
