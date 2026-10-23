using ClipForge.Api.Common;
using ClipForge.Api.Services;

namespace ClipForge.Api.Features.Internal;

/// <summary>
/// Worker API (docs/API.md "Internal"). Protected by the X-Internal-Api-Key scheme; every /jobs/* call must also
/// carry the id of the worker holding the lease (header X-Worker-Id, or the workerId body field where the request
/// has one): 400 when missing, 409 when the job is leased to another worker or not running.
/// </summary>
public static class InternalEndpoints
{
    public const string WorkerIdHeader = "X-Worker-Id";

    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal").WithTags("Internal").WithValidation()
            .RequireAuthorization(InternalApiKeyAuthenticationHandler.PolicyName);

        group.MapPost("/workers/{workerId}/claim", async (string workerId, ClaimRequest? request, InternalJobService service, CancellationToken ct) =>
        {
            var payload = await service.ClaimAsync(workerId, request?.Kinds, ct);
            return payload is null ? Results.NoContent() : Results.Ok(payload);
        }).Produces<AnalyzeJobPayload>().Produces(204);

        group.MapPost("/jobs/{id:guid}/heartbeat", async (Guid id, HeartbeatRequest? request, HttpContext http, InternalJobService service, CancellationToken ct)
            => Results.Ok(await service.HeartbeatAsync(id, request ?? new HeartbeatRequest(null, null, null), WorkerId(http), ct)))
            .Produces<HeartbeatResponse>();

        group.MapPost("/jobs/{id:guid}/probe", async (Guid id, ProbeRequest request, HttpContext http, InternalJobService service, CancellationToken ct)
            => Results.Ok(await service.ProbeAsync(id, request, WorkerId(http), ct)))
            .Produces<ProbeResponse>();

        group.MapPost("/jobs/{id:guid}/status", async (Guid id, StatusRequest request, HttpContext http, InternalJobService service, CancellationToken ct) =>
        {
            var job = await service.SetStatusAsync(id, request, WorkerId(http), ct);
            return Results.Ok(job.ToDto());
        }).Produces<JobDto>();

        group.MapPost("/jobs/{id:guid}/events", async (Guid id, EventRequest request, HttpContext http, InternalJobService service, CancellationToken ct) =>
        {
            await service.AddEventAsync(id, request, WorkerId(http), ct);
            return Results.Created($"/internal/jobs/{id}/events", null);
        });

        group.MapPost("/jobs/{id:guid}/upload-urls", async (Guid id, UploadUrlsRequest request, HttpContext http, InternalJobService service, CancellationToken ct)
            => Results.Ok(await service.UploadUrlsAsync(id, request, WorkerId(http), ct)))
            .Produces<UploadUrlsResponse>();

        group.MapPost("/jobs/{id:guid}/analysis", async (Guid id, AnalysisRequest request, HttpContext http, InternalJobService service, CancellationToken ct) =>
        {
            await service.StoreAnalysisAsync(id, request, WorkerId(http), ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/jobs/{id:guid}/candidates", async (Guid id, CandidatesRequest request, HttpContext http, InternalJobService service, CancellationToken ct)
            => Results.Ok(await service.ReplaceCandidatesAsync(id, request, WorkerId(http), ct)))
            .Produces<CandidatesResponse>();

        group.MapPost("/jobs/{id:guid}/clips", async (Guid id, ClipsRequest request, HttpContext http, InternalJobService service, CancellationToken ct)
            => Results.Ok(await service.CreateClipsAsync(id, request, WorkerId(http), ct)))
            .Produces<ClipsResponse>();

        group.MapPost("/jobs/{id:guid}/render-result", async (Guid id, RenderResultRequest request, HttpContext http, InternalJobService service, CancellationToken ct) =>
        {
            await service.RenderResultAsync(id, request, WorkerId(http), ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/jobs/{id:guid}/complete", async (Guid id, CompleteRequest? request, HttpContext http, InternalJobService service, CancellationToken ct) =>
        {
            var job = await service.CompleteFromWorkerAsync(id, request ?? new CompleteRequest(null, null, null), WorkerId(http), ct);
            return Results.Ok(job.ToDto());
        }).Produces<JobDto>();

        group.MapPost("/jobs/{id:guid}/fail", async (Guid id, FailRequest request, HttpContext http, InternalJobService service, CancellationToken ct) =>
        {
            var job = await service.FailFromWorkerAsync(id, request, WorkerId(http), ct);
            return Results.Ok(job.ToDto());
        }).Produces<JobDto>();

        return app;
    }

    private static string? WorkerId(HttpContext http)
        => http.Request.Headers.TryGetValue(WorkerIdHeader, out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : null;
}
