using ClipForge.Domain.Enums;

namespace ClipForge.Domain.Jobs;

/// <summary>
/// Allowed job status transitions (docs/PROCESSING_PIPELINE.md).
///
/// analyze_source: pending → queued → preparing_media → analyzing → detecting_moments → generating_clips → finalizing → completed
/// render_clip:    pending → queued → preparing_media → generating_clips → finalizing → completed
/// Any non-terminal status may go to failed or cancelled. Steps may be skipped forward but never backwards.
/// Re-posting the current running status is not a transition (<see cref="CanTransition"/> is false): the API
/// treats it as an idempotent progress update (<see cref="IsProgressUpdate"/>).
/// </summary>
public static class JobStateMachine
{
    private static readonly JobStatus[] AnalyzeChain =
    [
        JobStatus.Pending, JobStatus.Queued, JobStatus.PreparingMedia, JobStatus.Analyzing,
        JobStatus.DetectingMoments, JobStatus.GeneratingClips, JobStatus.Finalizing, JobStatus.Completed
    ];

    private static readonly JobStatus[] RenderChain =
    [
        JobStatus.Pending, JobStatus.Queued, JobStatus.PreparingMedia, JobStatus.GeneratingClips,
        JobStatus.Finalizing, JobStatus.Completed
    ];

    public static readonly JobStatus[] RunningStatuses =
    [
        JobStatus.PreparingMedia, JobStatus.Analyzing, JobStatus.DetectingMoments,
        JobStatus.GeneratingClips, JobStatus.Finalizing
    ];

    public static readonly JobStatus[] ActiveStatuses =
    [
        JobStatus.Pending, JobStatus.Queued, JobStatus.PreparingMedia, JobStatus.Analyzing,
        JobStatus.DetectingMoments, JobStatus.GeneratingClips, JobStatus.Finalizing
    ];

    public static readonly JobStatus[] TerminalStatuses = [JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled];

    public static bool IsTerminal(JobStatus status) => Array.IndexOf(TerminalStatuses, status) >= 0;

    /// <summary>Claimed by a worker and not finished.</summary>
    public static bool IsRunning(JobStatus status) => Array.IndexOf(RunningStatuses, status) >= 0;

    /// <summary>Not finished (pending, queued or running).</summary>
    public static bool IsActive(JobStatus status) => !IsTerminal(status);

    /// <summary>
    /// The analysis work started (the job went past <c>preparing_media</c>): from here on a cancellation consumes the
    /// minutes reserved at probe time, since the expensive part (decode, transcription, detection) already ran.
    /// </summary>
    public static bool HasStartedProcessing(JobStatus status)
        => status is JobStatus.Analyzing or JobStatus.DetectingMoments or JobStatus.GeneratingClips or JobStatus.Finalizing;

    /// <summary>A worker re-posting the status the job already has while it runs: an idempotent progress update, not a transition.</summary>
    public static bool IsProgressUpdate(JobStatus from, JobStatus to) => from == to && IsRunning(from);

    public static IReadOnlyList<JobStatus> ChainFor(JobKind kind)
        => kind == JobKind.RenderClip ? RenderChain : AnalyzeChain;

    /// <summary>Whether a worker-reported transition is allowed.</summary>
    public static bool CanTransition(JobKind kind, JobStatus from, JobStatus to)
    {
        if (from == to) return false;
        if (IsTerminal(from)) return false;

        if (to is JobStatus.Failed or JobStatus.Cancelled) return true;

        if (to == JobStatus.Queued) return from == JobStatus.Pending;

        if (to == JobStatus.Completed) return IsRunning(from);

        // Running states: must have been claimed (queued → preparing_media happens at claim time) and move forward.
        var chain = ChainFor(kind);
        var fromIndex = IndexOf(chain, from);
        var toIndex = IndexOf(chain, to);
        if (toIndex < 0) return false;

        if (from == JobStatus.Queued) return to == JobStatus.PreparingMedia;
        if (from == JobStatus.Pending) return false;
        if (fromIndex < 0) return false;

        return toIndex > fromIndex;
    }

    public static void EnsureTransition(JobKind kind, JobStatus from, JobStatus to)
    {
        if (!CanTransition(kind, from, to))
        {
            throw new Common.AppException(Common.ErrorCodes.Conflict, 409,
                $"Transição de status inválida: {Common.EnumNames.ToSnake(from)} → {Common.EnumNames.ToSnake(to)}.");
        }
    }

    public static bool CanCancelImmediately(JobStatus status) => status is JobStatus.Pending or JobStatus.Queued;

    public static bool CanRequestCancel(JobStatus status) => IsRunning(status);

    private static int IndexOf(IReadOnlyList<JobStatus> chain, JobStatus status)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            if (chain[i] == status) return i;
        }
        return -1;
    }
}
