using ClipForge.Api.Common;
using ClipForge.Api.Features.Clips;
using ClipForge.Api.Services;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Projects;

public static class ProjectsEndpoints
{
    public static IEndpointRouteBuilder MapProjectsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").WithTags("Projects").WithValidation().RequireAuthorization();

        group.MapPost("/", async (CreateProjectRequest request, HttpContext http, ProjectService service, CancellationToken ct) =>
        {
            var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
            var (project, job, _) = await service.CreateAsync(http.User.GetUserId(), request, key, ct);
            return Results.Created($"/api/projects/{project.Id}", new CreateProjectResponse(project.ToDto(), job.ToDto()));
        }).Produces<CreateProjectResponse>(201);

        group.MapGet("/", async (HttpContext http, ProjectService service, string? status, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            var (items, total) = await service.ListAsync(http.User.GetUserId(), status, p, ps, ct);
            return Results.Ok(new PagedResult<ProjectSummaryDto>(items, p, ps, total));
        }).Produces<PagedResult<ProjectSummaryDto>>();

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, ProjectService service, CancellationToken ct)
            => Results.Ok(await service.GetDetailAsync(http.User.GetUserId(), id, ct)))
            .Produces<ProjectDetailResponse>();

        group.MapPatch("/{id:guid}", async (Guid id, UpdateProjectRequest request, HttpContext http, ProjectService service, CancellationToken ct) =>
        {
            var project = await service.UpdateTitleAsync(http.User.GetUserId(), id, request.Title, ct);
            return Results.Ok(new ProjectResponse(project.ToDto()));
        }).Produces<ProjectResponse>();

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ProjectService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(http.User.GetUserId(), id, ct);
            return Results.Accepted();
        });

        group.MapPost("/{id:guid}/jobs", async (Guid id, CreateJobRequest? request, HttpContext http, ProjectService service, CancellationToken ct) =>
        {
            var job = await service.CreateAnalyzeJobAsync(http.User.GetUserId(), id, request?.Settings, ct);
            return Results.Created($"/api/projects/{id}/jobs/{job.Id}", new JobResponse(job.ToDto()));
        }).Produces<JobResponse>(201);

        group.MapGet("/{id:guid}/jobs/{jobId:guid}", async (Guid id, Guid jobId, HttpContext http, ProjectService service, CancellationToken ct) =>
        {
            var job = await service.GetJobAsync(http.User.GetUserId(), id, jobId, ct);
            var events = await service.GetJobEventsAsync(job.Id, 20, ct);
            return Results.Ok(job.ToDto(events));
        }).Produces<JobDto>();

        group.MapGet("/{id:guid}/jobs/{jobId:guid}/events", async (Guid id, Guid jobId, HttpContext http, ProjectService service, CancellationToken ct) =>
        {
            var job = await service.GetJobAsync(http.User.GetUserId(), id, jobId, ct);
            var events = await service.GetJobEventsAsync(job.Id, null, ct);
            return Results.Ok(new JobEventsResponse(events.Select(e => e.ToDto()).ToList()));
        }).Produces<JobEventsResponse>();

        group.MapPost("/{id:guid}/jobs/{jobId:guid}/cancel", async (Guid id, Guid jobId, HttpContext http, ProjectService service, CancellationToken ct) =>
        {
            var job = await service.CancelJobAsync(http.User.GetUserId(), id, jobId, ct);
            return Results.Accepted(value: new JobResponse(job.ToDto()));
        }).Produces<JobResponse>(202);

        group.MapGet("/{id:guid}/candidates", async (Guid id, HttpContext http, ProjectService service, string? sort, string? category, CancellationToken ct)
            => Results.Ok(await service.GetCandidatesAsync(http.User.GetUserId(), id, sort, category, ct)))
            .Produces<CandidatesResponse>();

        group.MapPost("/{id:guid}/candidates/{candidateId:guid}/clips", async (Guid id, Guid candidateId, CreateClipFromCandidateRequest? request, HttpContext http, ProjectService service, ClipService clips, CancellationToken ct) =>
        {
            var (clip, job) = await service.CreateClipFromCandidateAsync(http.User.GetUserId(), id, candidateId, request ?? new CreateClipFromCandidateRequest(null, null, null, null, null, null), ct);
            var summary = await clips.ToSummaryAsync(clip, ct);
            return Results.Accepted(value: new ClipJobResponse(summary, job.ToDto()));
        }).Produces<ClipJobResponse>(202);

        // Every ready clip in one zip, streamed (cookie auth: the web links it with a plain <a href>).
        group.MapGet("/{id:guid}/clips/archive", async (Guid id, HttpContext http, ClipsArchiveService archive, CancellationToken ct)
            => await archive.PrepareAsync(http.User.GetUserId(), id, ct))
            .Produces(StatusCodes.Status200OK, contentType: "application/zip");

        return app;
    }
}
