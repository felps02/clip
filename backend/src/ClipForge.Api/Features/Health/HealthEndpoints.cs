using ClipForge.Infrastructure.Persistence;
using ClipForge.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Health;

/// <summary><c>GET /health/ready</c>: <c>status</c> is <c>ready</c> or <c>not_ready</c>; each check is ok, failed or timeout.</summary>
public sealed record ReadinessResponse(string Status, IReadOnlyDictionary<string, string> Checks);

/// <summary>Readiness probes: each one runs with its own timeout, in parallel, and never throws.</summary>
public static class Readiness
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string TimedOut = "timeout";

    /// <summary>Time each dependency gets to answer (database SELECT 1, storage check).</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs one probe: <c>ok</c>, <c>timeout</c> (no answer within <paramref name="timeout"/>, even when the probe ignores
    /// its token) or <c>failed</c> (it threw; the exception is logged, never returned to the caller).
    /// </summary>
    public static async Task<string> RunAsync(string name, Func<CancellationToken, Task> probe, TimeSpan timeout, ILogger logger, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probeTask = Task.Run(() => probe(cts.Token), CancellationToken.None);
        try
        {
            var winner = await Task.WhenAny(probeTask, Task.Delay(timeout, delay.Token));
            await delay.CancelAsync();
            if (winner != probeTask)
            {
                await cts.CancelAsync();
                _ = probeTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                logger.LogWarning("Readiness check {Check} did not answer within {Seconds} s", name, timeout.TotalSeconds);
                return TimedOut;
            }
            await probeTask;
            return Ok;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Readiness check {Check} did not answer within {Seconds} s", name, timeout.TotalSeconds);
            return TimedOut;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Readiness check {Check} failed", name);
            return Failed;
        }
    }
}

public static class HealthEndpoints
{
    /// <summary>
    /// Readiness (load balancers route traffic only to ready instances): the database answers <c>SELECT 1</c> and the
    /// object storage is usable (for S3 a bucket check), each within 3 s. Liveness stays <c>GET /health</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/ready", async (AppDbContext db, IObjectStorage storage, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("ClipForge.Api.Readiness");
            var database = Readiness.RunAsync("database", token => db.Database.ExecuteSqlRawAsync("SELECT 1", token), Readiness.CheckTimeout, logger, ct);
            var objects = Readiness.RunAsync("storage", storage.CheckReadyAsync, Readiness.CheckTimeout, logger, ct);
            var checks = new Dictionary<string, string>
            {
                ["database"] = await database,
                ["storage"] = await objects,
            };
            var ready = checks.Values.All(value => value == Readiness.Ok);
            return Results.Json(new ReadinessResponse(ready ? "ready" : "not_ready", checks), statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).WithTags("Health").AllowAnonymous().Produces<ReadinessResponse>().Produces<ReadinessResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }
}
