using System.Net;
using ClipForge.Api.HostedServices;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Infrastructure.Persistence;
using ClipForge.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace ClipForge.Api.Tests.Integration;

/// <summary>Liveness (GET /health, unchanged) and readiness (GET /health/ready: database SELECT 1 + storage check).</summary>
public class HealthTests : TestBase
{
    public HealthTests(ApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Liveness_is_unchanged()
    {
        var response = await CreateClient().GetAsync("/health");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("ok");
        body.GetProperty("storage").GetString().ShouldBe("Local");
    }

    [Fact]
    public async Task The_openapi_document_lists_the_round_3_endpoints()
    {
        var response = await CreateClient().GetAsync("/openapi/v1.json");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var paths = (await ReadJsonAsync(response)).GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        paths.ShouldContain("/health/ready");
        paths.ShouldContain("/api/assistant/status");
        paths.ShouldContain("/api/assistant/chat");
        paths.ShouldContain("/api/projects/{id}/clips/archive");
    }

    [Fact]
    public async Task Ready_when_database_and_storage_answer()
    {
        var response = await CreateClient().GetAsync("/health/ready");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("ready");
        body.GetProperty("checks").GetProperty("database").GetString().ShouldBe("ok");
        body.GetProperty("checks").GetProperty("storage").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task Not_ready_when_the_storage_check_fails()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.ProviderName.Returns("S3");
        storage.CheckReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("NoSuchBucket")));
        await using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton(storage);
        }));

        var response = await factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") }).GetAsync("/health/ready");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("not_ready");
        body.GetProperty("checks").GetProperty("database").GetString().ShouldBe("ok");
        body.GetProperty("checks").GetProperty("storage").GetString().ShouldBe("failed");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("NoSuchBucket"); // details stay in the log
    }

    [Fact]
    public async Task Not_ready_when_the_database_does_not_answer()
    {
        await using var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.ConfigureTestServices(services =>
            {
                // Nothing listens on port 1: the connection is refused at once. The plan seeder (which needs the
                // database at startup) is left out of this host.
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => AppDbContextFactory.Configure(options, "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2"));
                var seeder = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PlanSeeder));
                services.Remove(seeder);
            });
        });

        var response = await factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") }).GetAsync("/health/ready");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("not_ready");
        body.GetProperty("checks").GetProperty("database").GetString().ShouldBeOneOf("failed", "timeout");
        body.GetProperty("checks").GetProperty("storage").GetString().ShouldBe("ok");
    }
}
