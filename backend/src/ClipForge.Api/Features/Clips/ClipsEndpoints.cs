using ClipForge.Api.Common;
using ClipForge.Api.Services;

namespace ClipForge.Api.Features.Clips;

public static class ClipsEndpoints
{
    public static IEndpointRouteBuilder MapClipsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/clips").WithTags("Clips").WithValidation().RequireAuthorization();

        group.MapGet("/", async (HttpContext http, ClipService service, Guid? projectId, string? category, string? status, string? q, string? sort, string? order, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            var (items, total) = await service.ListAsync(http.User.GetUserId(), projectId, category, status, q, sort, order, p, ps, ct);
            return Results.Ok(new PagedResult<ClipSummaryDto>(items, p, ps, total));
        }).Produces<PagedResult<ClipSummaryDto>>();

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, ClipService service, CancellationToken ct) =>
        {
            var clip = await service.GetOwnedAsync(http.User.GetUserId(), id, ct);
            return Results.Ok(await service.ToDetailAsync(clip, ct));
        }).Produces<ClipDetailDto>();

        group.MapPatch("/{id:guid}", async (Guid id, UpdateClipRequest request, HttpContext http, ClipService service, CancellationToken ct) =>
        {
            var (clip, needsRender) = await service.UpdateAsync(http.User.GetUserId(), id, request, ct);
            return Results.Ok(new UpdateClipResponse(await service.ToDetailAsync(clip, ct), needsRender));
        }).Produces<UpdateClipResponse>();

        group.MapPost("/{id:guid}/render", async (Guid id, HttpContext http, ClipService service, CancellationToken ct) =>
        {
            var (clip, job, render) = await service.RenderAsync(http.User.GetUserId(), id, ct);
            return Results.Accepted(value: new RenderClipResponse(await service.ToDetailAsync(clip, ct), job.ToDto(), render.ToDto()));
        }).Produces<RenderClipResponse>(202);

        group.MapGet("/{id:guid}/download", async (Guid id, Guid? renderId, HttpContext http, ClipService service, CancellationToken ct)
            => Results.Ok(await service.GetDownloadAsync(http.User.GetUserId(), id, renderId, ct)))
            .Produces<DownloadResponse>();

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ClipService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(http.User.GetUserId(), id, ct);
            return Results.NoContent();
        });

        return app;
    }
}
