using ClipForge.Api.Options;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.HostedServices;

/// <summary>Upserts the plans of the "Plans" configuration section on startup (ADR-007).</summary>
public sealed class PlanSeeder : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PlanSeeder> _logger;

    public PlanSeeder(IServiceProvider services, ILogger<PlanSeeder> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        await SeedAsync(scope.ServiceProvider, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static async Task<int> SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var db = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var logger = services.GetRequiredService<ILogger<PlanSeeder>>();

        var configured = configuration.GetSection("Plans").Get<Dictionary<string, PlanConfig>>() ?? new();
        if (configured.Count == 0)
        {
            logger.LogWarning("No plans configured in the Plans section; nothing to seed");
            return 0;
        }

        var existing = await db.Plans.ToDictionaryAsync(p => p.Code, ct);
        var now = clock.UtcNow;
        var count = 0;
        foreach (var (code, config) in configured)
        {
            var planCode = code.Trim().ToLowerInvariant();
            if (!existing.TryGetValue(planCode, out var plan))
            {
                plan = new Plan { Code = planCode };
                db.Plans.Add(plan);
            }
            plan.Name = string.IsNullOrWhiteSpace(config.Name) ? planCode : config.Name;
            plan.Description = config.Description;
            plan.IsActive = config.IsActive;
            plan.SortOrder = config.SortOrder;
            plan.PriceMonthlyCents = config.PriceMonthlyCents;
            plan.Currency = string.IsNullOrWhiteSpace(config.Currency) ? "BRL" : config.Currency;
            plan.ProviderPriceId = string.IsNullOrWhiteSpace(config.ProviderPriceId) ? null : config.ProviderPriceId;
            plan.Limits = config.Limits ?? new();
            plan.Features = config.Features ?? new();
            plan.UpdatedAt = now;
            count++;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} plans from configuration", count);
        return count;
    }
}
