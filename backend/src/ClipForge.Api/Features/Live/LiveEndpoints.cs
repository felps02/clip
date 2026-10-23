using ClipForge.Api.Common;
using ClipForge.Domain.Common;

namespace ClipForge.Api.Features.Live;

/// <summary>
/// Live capture API (docs/LIVE_CAPTURE.md §2). User endpoints accept only the user's cookie/JWT (the default scheme);
/// companion endpoints accept only a device token (scheme "Device", <c>Authorization: Bearer cfd_…</c>).
/// </summary>
public static class LiveEndpoints
{
    public static IEndpointRouteBuilder MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------------------------------------------------------------- user (cookie / JWT)
        var user = app.MapGroup("/api/live").WithTags("Live").WithValidation().RequireAuthorization();

        user.MapPost("/companion/tokens", async (CreateDeviceTokenRequest request, HttpContext http, LiveService service, CancellationToken ct) =>
        {
            var created = await service.CreateDeviceAsync(http.User.GetUserId(), request.Name, ct);
            return Results.Created($"/api/live/companion/devices/{created.Device.Id}", created);
        }).Produces<CreateDeviceTokenResponse>(201);

        user.MapGet("/companion/devices", async (HttpContext http, LiveService service, CancellationToken ct)
            => Results.Ok(new CompanionDevicesResponse(await service.ListDevicesAsync(http.User.GetUserId(), ct))))
            .Produces<CompanionDevicesResponse>();

        user.MapDelete("/companion/devices/{id:guid}", async (Guid id, HttpContext http, LiveService service, CancellationToken ct) =>
        {
            await service.RevokeDeviceAsync(http.User.GetUserId(), id, ct);
            return Results.NoContent();
        }).Produces(204);

        user.MapGet("/sessions", async (HttpContext http, LiveService service, string? status, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            return Results.Ok(await service.ListSessionsAsync(http.User.GetUserId(), status, p, ps, ct));
        }).Produces<PagedResult<LiveSessionDto>>();

        user.MapGet("/sessions/{id:guid}", async (Guid id, HttpContext http, LiveService service, CancellationToken ct)
            => Results.Ok(await service.GetDetailAsync(http.User.GetUserId(), id, ct)))
            .Produces<LiveSessionDetailResponse>();

        user.MapPost("/sessions/{id:guid}/stop", async (Guid id, HttpContext http, LiveService service, CancellationToken ct)
            => Results.Accepted(value: new LiveSessionResponse(await service.StopAsync(http.User.GetUserId(), id, ct))))
            .Produces<LiveSessionResponse>(202);

        // ---------------------------------------------------------------- companion (device token)
        var companion = app.MapGroup("/api/live").WithTags("Live companion").WithValidation()
            .RequireAuthorization(DeviceTokenAuthenticationHandler.PolicyName);

        companion.MapGet("/companion/me", async (HttpContext http, LiveService service, CancellationToken ct)
            => Results.Ok(await service.MeAsync(http.User.GetUserId(), http.User.GetDeviceId(), ct)))
            .Produces<CompanionMeResponse>();

        companion.MapPost("/sessions", async (CreateLiveSessionRequest? request, HttpContext http, LiveService service, CancellationToken ct) =>
        {
            var created = await service.CreateSessionAsync(http.User.GetUserId(), http.User.GetDeviceId(),
                request ?? new CreateLiveSessionRequest(null, null, null, null), ct);
            return Results.Created($"/api/live/sessions/{created.Session.Id}", created);
        }).Produces<CreateLiveSessionResponse>(201);

        companion.MapPost("/sessions/{id:guid}/heartbeat", async (Guid id, LiveHeartbeatRequest request, HttpContext http, LiveService service, CancellationToken ct)
            => Results.Ok(await service.HeartbeatAsync(http.User.GetUserId(), id, request.State!, ct)))
            .Produces<LiveHeartbeatResponse>();

        companion.MapPost("/sessions/{id:guid}/segments", async (Guid id, HttpContext http, LiveService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.UploadSegmentAsync(http.User.GetUserId(), id, http.Request, ct);
                return result.Duplicate ? Results.Ok(result) : Results.Created($"/api/live/sessions/{id}/segments/{result.Segment.Sequence}", result);
            }
            catch (AppException ex) when (ex.Status != StatusCodes.Status413PayloadTooLarge)
            {
                // Answer only after the body was read: a client still sending the file would otherwise see a reset
                // connection instead of the problem (and retry a request that can never succeed).
                await LiveService.DrainAsync(http.Request.Body, ct);
                throw;
            }
        }).Produces<SegmentUploadResponse>(201).Produces<SegmentUploadResponse>(200);

        companion.MapPost("/sessions/{id:guid}/end", async (Guid id, EndLiveSessionRequest? request, HttpContext http, LiveService service, CancellationToken ct)
            => Results.Ok(new LiveSessionResponse(await service.EndAsync(http.User.GetUserId(), id, request?.LastSequence, ct))))
            .Produces<LiveSessionResponse>();

        return app;
    }
}
