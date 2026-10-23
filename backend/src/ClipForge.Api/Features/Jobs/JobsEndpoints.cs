using ClipForge.Api.Common;
using ClipForge.Api.Services;
using ClipForge.Domain.Jobs;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Jobs;

public static class JobsEndpoints
{
    public static IEndpointRouteBuilder MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs", async (HttpContext http, AppDbContext db, string? status, int? page, int? pageSize, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var (p, ps) = Paging.Normalize(page, pageSize);
            var query = db.ProcessingJobs.Where(j => j.UserId == userId && j.Project.DeletedAt == null);
            if (!string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(j => JobStateMachine.ActiveStatuses.Contains(j.Status));
            }
            var total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(j => j.CreatedAt)
                .Skip((p - 1) * ps).Take(ps)
                .Select(j => new { Job = j, j.Project.Title })
                .ToListAsync(ct);
            return Results.Ok(new PagedResult<JobSummaryDto>(items.Select(x => x.Job.ToSummary(x.Title)).ToList(), p, ps, total));
        }).WithTags("Jobs").RequireAuthorization().Produces<PagedResult<JobSummaryDto>>();

        return app;
    }
}
