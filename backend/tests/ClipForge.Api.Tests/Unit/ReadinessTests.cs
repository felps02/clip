using System.Diagnostics;
using ClipForge.Api.Features.Health;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipForge.Api.Tests.Unit;

public class ReadinessTests
{
    [Fact]
    public async Task A_probe_that_answers_is_ok()
        => (await Readiness.RunAsync("db", _ => Task.CompletedTask, TimeSpan.FromSeconds(1), NullLogger.Instance, CancellationToken.None)).ShouldBe("ok");

    [Fact]
    public async Task A_probe_that_throws_is_failed()
        => (await Readiness.RunAsync("db", _ => throw new InvalidOperationException("down"), TimeSpan.FromSeconds(1), NullLogger.Instance, CancellationToken.None)).ShouldBe("failed");

    [Fact]
    public async Task A_probe_that_honours_its_token_times_out()
        => (await Readiness.RunAsync("db", token => Task.Delay(TimeSpan.FromSeconds(30), token), TimeSpan.FromMilliseconds(100), NullLogger.Instance, CancellationToken.None)).ShouldBe("timeout");

    [Fact]
    public async Task A_probe_that_ignores_its_token_still_cannot_hold_the_answer()
    {
        var watch = Stopwatch.StartNew();
        var result = await Readiness.RunAsync("storage", _ => Task.Delay(TimeSpan.FromSeconds(10)), TimeSpan.FromMilliseconds(150), NullLogger.Instance, CancellationToken.None);
        result.ShouldBe("timeout");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }
}
