using ClipForge.Domain.Common;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Jobs;

namespace ClipForge.Api.Tests.Unit;

public class JobStateMachineTests
{
    [Theory]
    [InlineData(JobKind.AnalyzeSource, JobStatus.Pending, JobStatus.Queued, true)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.Queued, JobStatus.PreparingMedia, true)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.PreparingMedia, JobStatus.Analyzing, true)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.Analyzing, JobStatus.DetectingMoments, true)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.DetectingMoments, JobStatus.GeneratingClips, true)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.GeneratingClips, JobStatus.Finalizing, true)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.Finalizing, JobStatus.Completed, true)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.PreparingMedia, JobStatus.DetectingMoments, true)] // forward skip
    [InlineData(JobKind.AnalyzeSource, JobStatus.Analyzing, JobStatus.PreparingMedia, false)] // backwards
    [InlineData(JobKind.AnalyzeSource, JobStatus.Queued, JobStatus.Analyzing, false)] // not claimed
    [InlineData(JobKind.AnalyzeSource, JobStatus.Queued, JobStatus.Completed, false)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.Completed, JobStatus.Analyzing, false)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.Failed, JobStatus.Queued, false)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.Cancelled, JobStatus.Failed, false)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.Analyzing, JobStatus.Failed, true)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.Queued, JobStatus.Cancelled, true)]
    [InlineData(JobKind.AnalyzeSource, JobStatus.Analyzing, JobStatus.Analyzing, false)]
    [InlineData(JobKind.RenderClip, JobStatus.PreparingMedia, JobStatus.GeneratingClips, true)]
    [InlineData(JobKind.RenderClip, JobStatus.PreparingMedia, JobStatus.Analyzing, false)] // not in render chain
    [InlineData(JobKind.RenderClip, JobStatus.GeneratingClips, JobStatus.Finalizing, true)]
    [InlineData(JobKind.RenderClip, JobStatus.GeneratingClips, JobStatus.Completed, true)]
    public void CanTransition_follows_the_documented_state_machine(JobKind kind, JobStatus from, JobStatus to, bool expected)
    {
        JobStateMachine.CanTransition(kind, from, to).ShouldBe(expected);
    }

    [Fact]
    public void EnsureTransition_throws_409_conflict_for_invalid_moves()
    {
        var ex = Should.Throw<AppException>(() => JobStateMachine.EnsureTransition(JobKind.AnalyzeSource, JobStatus.Analyzing, JobStatus.PreparingMedia));
        ex.Status.ShouldBe(409);
        ex.Code.ShouldBe(ErrorCodes.Conflict);
    }

    [Fact]
    public void Progress_update_and_started_processing_helpers()
    {
        JobStateMachine.IsProgressUpdate(JobStatus.Analyzing, JobStatus.Analyzing).ShouldBeTrue();
        JobStateMachine.IsProgressUpdate(JobStatus.PreparingMedia, JobStatus.PreparingMedia).ShouldBeTrue();
        JobStateMachine.IsProgressUpdate(JobStatus.Queued, JobStatus.Queued).ShouldBeFalse();
        JobStateMachine.IsProgressUpdate(JobStatus.Completed, JobStatus.Completed).ShouldBeFalse();
        JobStateMachine.IsProgressUpdate(JobStatus.Analyzing, JobStatus.DetectingMoments).ShouldBeFalse();

        JobStateMachine.HasStartedProcessing(JobStatus.Queued).ShouldBeFalse();
        JobStateMachine.HasStartedProcessing(JobStatus.PreparingMedia).ShouldBeFalse();
        JobStateMachine.HasStartedProcessing(JobStatus.Analyzing).ShouldBeTrue();
        JobStateMachine.HasStartedProcessing(JobStatus.GeneratingClips).ShouldBeTrue();
        JobStateMachine.HasStartedProcessing(JobStatus.Completed).ShouldBeFalse();
    }

    [Fact]
    public void Terminal_running_and_active_sets_are_consistent()
    {
        foreach (var status in Enum.GetValues<JobStatus>())
        {
            JobStateMachine.IsActive(status).ShouldBe(!JobStateMachine.IsTerminal(status));
            if (JobStateMachine.IsRunning(status)) JobStateMachine.IsActive(status).ShouldBeTrue();
        }
        JobStateMachine.CanCancelImmediately(JobStatus.Queued).ShouldBeTrue();
        JobStateMachine.CanCancelImmediately(JobStatus.Analyzing).ShouldBeFalse();
        JobStateMachine.CanRequestCancel(JobStatus.Analyzing).ShouldBeTrue();
        JobStateMachine.CanRequestCancel(JobStatus.Completed).ShouldBeFalse();
    }
}
