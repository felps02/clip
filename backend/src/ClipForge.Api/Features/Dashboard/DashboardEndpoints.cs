using ClipForge.Api.Common;
using ClipForge.Api.Features.Clips;
using ClipForge.Api.Services;
using ClipForge.Domain.Jobs;
using ClipForge.Domain.Plans;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Dashboard;

public sealed record DashboardUsageDto(long ProcessingSecondsUsed, long ProcessingSecondsLimit, int RendersUsed, int RendersLimit, long StorageBytesUsed, long StorageBytesLimit);

public sealed record DashboardWarningDto(string Code, string Message);

public sealed record DashboardSummaryResponse(
    int ClipsTotal,
    int ClipsThisPeriod,
    IReadOnlyList<JobSummaryDto> ActiveJobs,
    decimal MinutesProcessedThisPeriod,
    IReadOnlyList<ClipSummaryDto> RecentClips,
    DashboardUsageDto Usage,
    IReadOnlyList<DashboardWarningDto> Warnings);

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard/summary", async (HttpContext http, AppDbContext db, QuotaService quotas, ClipService clips, MediaUrls urls, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var usage = await quotas.GetUsageAsync(userId, ct);
            var limits = usage.Entitlement.Limits;
            var periodStart = usage.Entitlement.PeriodStart;

            var clipsQuery = db.Clips.Where(c => c.UserId == userId && c.DeletedAt == null && c.Project.DeletedAt == null);
            var clipsTotal = await clipsQuery.CountAsync(ct);
            var clipsThisPeriod = await clipsQuery.CountAsync(c => c.CreatedAt >= periodStart, ct);

            var activeJobs = await db.ProcessingJobs
                .Where(j => j.UserId == userId && j.Project.DeletedAt == null && JobStateMachine.ActiveStatuses.Contains(j.Status))
                .OrderByDescending(j => j.CreatedAt)
                .Take(20)
                .Select(j => new { Job = j, j.Project.Title })
                .ToListAsync(ct);

            var recent = await clipsQuery.OrderByDescending(c => c.CreatedAt).Take(6)
                .Include(c => c.Project).ToListAsync(ct);
            var recentRenders = await clips.LoadCurrentRendersAsync(recent, ct);
            var recentDtos = new List<ClipSummaryDto>(recent.Count);
            foreach (var clip in recent)
            {
                recentDtos.Add(clip.ToSummary(clip.Project.Title, await urls.ThumbnailAsync(clip.ThumbnailStorageKey, ct), recentRenders.GetValueOrDefault(clip.Id)));
            }

            var warnings = new List<DashboardWarningDto>();
            var limitSeconds = limits.MonthlyProcessingSeconds;
            if (limitSeconds > 0)
            {
                var ratio = (double)(usage.ProcessingSecondsUsed + usage.ProcessingSecondsReserved) / limitSeconds;
                if (ratio >= 1.0)
                {
                    warnings.Add(new("processing_quota_exhausted", $"Você usou todos os {limits.MonthlyProcessingMinutes} minutos de processamento do período. Faça upgrade do plano para continuar."));
                }
                else if (ratio >= 0.8)
                {
                    warnings.Add(new("processing_quota_warning", $"Você já usou {Math.Round(ratio * 100)}% dos minutos de processamento do período."));
                }
            }
            if (!PlanLimitHelpers.IsUnlimited(limits.MaxRendersPerMonth) && usage.RendersUsed >= limits.MaxRendersPerMonth)
            {
                warnings.Add(new("render_quota_exhausted", $"Limite de {limits.MaxRendersPerMonth} renderizações do período atingido."));
            }
            if (limits.StorageQuotaBytes > 0 && usage.StorageBytesUsed >= limits.StorageQuotaBytes * 0.9)
            {
                warnings.Add(new("storage_quota_warning", "Seu armazenamento está quase cheio. Exclua projetos antigos ou faça upgrade."));
            }

            var response = new DashboardSummaryResponse(
                clipsTotal,
                clipsThisPeriod,
                activeJobs.Select(x => x.Job.ToSummary(x.Title)).ToList(),
                Math.Round(usage.ProcessingSecondsUsed / 60m, 1),
                recentDtos,
                new DashboardUsageDto((long)Math.Round(usage.ProcessingSecondsUsed), limitSeconds, usage.RendersUsed, limits.MaxRendersPerMonth,
                    usage.StorageBytesUsed, limits.StorageQuotaBytes),
                warnings);
            return Results.Ok(response);
        }).WithTags("Dashboard").RequireAuthorization().Produces<DashboardSummaryResponse>();

        return app;
    }
}
