using ClipForge.Api.Options;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Services;

/// <summary>Creates render versions + render_clip jobs (shared by the clip editor and "clip from candidate").</summary>
public sealed class RenderService
{
    private readonly AppDbContext _db;
    private readonly QuotaService _quotas;
    private readonly JobEvents _events;
    private readonly IClock _clock;
    private readonly JobsOptions _jobs;

    public RenderService(AppDbContext db, QuotaService quotas, JobEvents events, IClock clock, IOptions<JobsOptions> jobs)
    {
        _db = db;
        _quotas = quotas;
        _events = events;
        _clock = clock;
        _jobs = jobs.Value;
    }

    /// <summary>
    /// Checks the renders/month quota and the concurrent-jobs cap, records the usage, creates
    /// clip_renders(version n+1, pending) and a queued render_clip job. The caller saves changes, inside
    /// <see cref="QuotaService.WithUserLockAsync{T}"/> so the check and the insert are serialised per user.
    /// </summary>
    public async Task<(ProcessingJob Job, ClipRender Render)> RequestRenderAsync(Clip clip, CancellationToken ct)
    {
        var pending = await _db.ClipRenders.AnyAsync(r => r.ClipId == clip.Id && (r.Status == RenderStatus.Pending || r.Status == RenderStatus.Rendering), ct);
        if (pending || clip.Status == ClipStatus.Rendering)
        {
            throw AppException.Conflict("Já existe uma renderização em andamento para este clipe.");
        }

        var usage = await _quotas.EnsureCanRenderAsync(clip.UserId, ct);
        var entitlement = usage.Entitlement;
        var now = _clock.UtcNow;

        var version = (await _db.ClipRenders.Where(r => r.ClipId == clip.Id).MaxAsync(r => (int?)r.Version, ct) ?? 0) + 1;
        var render = new ClipRender
        {
            ClipId = clip.Id,
            UserId = clip.UserId,
            Version = version,
            AspectRatio = clip.AspectRatio,
            HasSubtitles = clip.Subtitles.Enabled,
            Status = RenderStatus.Pending,
            CreatedAt = now,
        };
        var job = new ProcessingJob
        {
            UserId = clip.UserId,
            ProjectId = clip.ProjectId,
            Kind = JobKind.RenderClip,
            Status = JobStatus.Queued,
            Priority = entitlement.Limits.Priority,
            MaxAttempts = Math.Max(1, _jobs.MaxAttempts),
            Input = JsonDefaults.Serialize(new RenderJobInput { ClipId = clip.Id, RenderId = render.Id }),
            QueuedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            StatusMessage = "Na fila para renderização",
        };
        render.JobId = job.Id;

        _db.ClipRenders.Add(render);
        _db.ProcessingJobs.Add(job);
        _quotas.RecordUsage(entitlement, clip.UserId, UsageKind.Render, 1, job.Id, clip.ProjectId, clip.Id,
            JsonDefaults.Serialize(new { renderId = render.Id, version }));
        _events.Add(job, $"Renderização v{version} solicitada", null, JobStatus.Queued);

        clip.Status = ClipStatus.Rendering;
        clip.UpdatedAt = now;
        return (job, render);
    }
}
