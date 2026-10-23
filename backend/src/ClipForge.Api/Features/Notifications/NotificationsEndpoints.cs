using System.Text.Json;
using ClipForge.Api.Common;
using ClipForge.Domain.Common;
using ClipForge.Domain.Enums;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Notifications;

public sealed record NotificationDto(Guid Id, NotificationType Type, string Title, string? Body, JsonElement? Data, DateTimeOffset? ReadAt, DateTimeOffset CreatedAt);

public sealed record UnreadCountResponse(int Count);

public static class NotificationsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, AppDbContext db, bool? unreadOnly, int? page, int? pageSize, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var (p, ps) = Paging.Normalize(page, pageSize);
            var query = db.Notifications.Where(n => n.UserId == userId);
            if (unreadOnly == true) query = query.Where(n => n.ReadAt == null);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(n => n.CreatedAt).Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
            return Results.Ok(new PagedResult<NotificationDto>(
                items.Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, JsonDefaults.ParseElement(n.Data), n.ReadAt, n.CreatedAt)).ToList(),
                p, ps, total));
        }).Produces<PagedResult<NotificationDto>>();

        group.MapGet("/unread-count", async (HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var count = await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);
            return Results.Ok(new UnreadCountResponse(count));
        }).Produces<UnreadCountResponse>();

        group.MapPost("/{id:guid}/read", async (Guid id, HttpContext http, AppDbContext db, IClock clock, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var now = clock.UtcNow;
            var updated = await db.Notifications
                .Where(n => n.Id == id && n.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, n => n.ReadAt ?? now), ct);
            return updated == 0 ? throw AppException.NotFound("Notificação não encontrada.") : Results.NoContent();
        });

        group.MapPost("/read-all", async (HttpContext http, AppDbContext db, IClock clock, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var now = clock.UtcNow;
            await db.Notifications
                .Where(n => n.UserId == userId && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
            return Results.NoContent();
        });

        return app;
    }
}
