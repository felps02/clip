using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Infrastructure.Persistence;

namespace ClipForge.Api.Services;

/// <summary>Per-job audit trail helper (processing_job_events).</summary>
public sealed class JobEvents
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public JobEvents(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public ProcessingJobEvent Add(ProcessingJob job, string message, JobStatus? from = null, JobStatus? to = null, JobEventLevel level = JobEventLevel.Info, object? data = null)
    {
        var ev = new ProcessingJobEvent
        {
            JobId = job.Id,
            CreatedAt = _clock.UtcNow,
            Level = level,
            FromStatus = from,
            ToStatus = to,
            Message = message.Length > 1000 ? message[..1000] : message,
            Data = data is null ? null : JsonDefaults.Serialize(data),
        };
        _db.ProcessingJobEvents.Add(ev);
        return ev;
    }
}
