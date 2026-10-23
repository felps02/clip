using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Plans;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Tests.Unit;

public class QuotaCalculatorTests
{
    private static PlanLimits Free() => new()
    {
        MonthlyProcessingMinutes = 60,
        MaxSourceDurationMinutes = 120,
        MaxFileSizeMb = 2048,
        MaxActiveProjects = 3,
        MaxRendersPerMonth = 10,
        MaxConcurrentJobs = 1,
        StorageQuotaGb = 5,
    };

    [Fact]
    public void Active_projects_limit_is_enforced_and_zero_means_unlimited()
    {
        QuotaCalculator.CheckActiveProjects(2, Free()).Allowed.ShouldBeTrue();
        var denied = QuotaCalculator.CheckActiveProjects(3, Free());
        denied.Allowed.ShouldBeFalse();
        denied.Code.ShouldBe(ErrorCodes.PlanLimit);
        denied.Message!.ShouldContain("3");
        QuotaCalculator.CheckActiveProjects(500, new PlanLimits { MaxActiveProjects = 0 }).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Concurrent_jobs_limit_is_enforced()
    {
        QuotaCalculator.CheckConcurrentJobs(0, Free()).Allowed.ShouldBeTrue();
        QuotaCalculator.CheckConcurrentJobs(1, Free()).Code.ShouldBe(ErrorCodes.PlanLimit);
    }

    [Fact]
    public void Source_duration_limit_is_enforced()
    {
        QuotaCalculator.CheckSourceDuration(120 * 60, Free()).Allowed.ShouldBeTrue();
        var denied = QuotaCalculator.CheckSourceDuration(120 * 60 + 1, Free());
        denied.Code.ShouldBe(ErrorCodes.PlanLimit);
        denied.Message!.ShouldContain("120");
    }

    [Fact]
    public void Processing_minutes_use_used_plus_reserved_plus_requested()
    {
        var limits = Free(); // 3600 seconds
        QuotaCalculator.CheckProcessingSeconds(1000, 1000, 1600, limits).Allowed.ShouldBeTrue();
        var denied = QuotaCalculator.CheckProcessingSeconds(1000, 1000, 1601, limits);
        denied.Allowed.ShouldBeFalse();
        denied.Code.ShouldBe(ErrorCodes.QuotaExceeded);
        denied.Message!.ShouldContain("26 min"); // remaining = 1600 s → 26 min
        QuotaCalculator.RemainingProcessingSeconds(1000, 1000, limits).ShouldBe(1600);
        QuotaCalculator.RemainingProcessingSeconds(5000, 0, limits).ShouldBe(0);
    }

    [Fact]
    public void Renders_limit_is_enforced_and_zero_means_unlimited()
    {
        QuotaCalculator.CheckRenders(9, Free()).Allowed.ShouldBeTrue();
        QuotaCalculator.CheckRenders(10, Free()).Code.ShouldBe(ErrorCodes.PlanLimit);
        QuotaCalculator.CheckRenders(10_000, new PlanLimits { MaxRendersPerMonth = 0 }).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Upload_checks_file_size_then_storage_quota()
    {
        var limits = Free();
        QuotaCalculator.CheckUpload(100, 0, limits).Allowed.ShouldBeTrue();
        var tooBig = QuotaCalculator.CheckUpload(limits.MaxFileSizeBytes + 1, 0, limits);
        tooBig.Code.ShouldBe(ErrorCodes.PlanLimit);
        tooBig.Status.ShouldBe(413);
        var quota = QuotaCalculator.CheckUpload(1024, limits.StorageQuotaBytes - 10, limits);
        quota.Code.ShouldBe(ErrorCodes.QuotaExceeded);
        QuotaCalculator.CheckUpload(0, 0, limits).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Advisory_lock_key_is_stable_per_user()
    {
        var id = Guid.NewGuid();
        QuotaService.AdvisoryLockKey(id).ShouldBe(QuotaService.AdvisoryLockKey(id));
        QuotaService.AdvisoryLockKey(id).ShouldNotBe(QuotaService.AdvisoryLockKey(Guid.NewGuid()));
    }

    [Fact]
    public void ThrowIfDenied_raises_AppException_with_the_code()
    {
        var ex = Should.Throw<AppException>(() => QuotaCalculator.CheckRenders(10, Free()).ThrowIfDenied());
        ex.Code.ShouldBe(ErrorCodes.PlanLimit);
        ex.Status.ShouldBe(409);
        Should.NotThrow(() => QuotaDecision.Ok.ThrowIfDenied());
    }
}
