using ClipForge.Api.Common;
using ClipForge.Api.Options;
using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Media;
using ClipForge.Domain.Plans;
using ClipForge.Infrastructure.Persistence;
using ClipForge.Infrastructure.Storage;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Features.Media;

public sealed record CreateUploadRequest(string FileName, long SizeBytes, string ContentType, decimal? DeclaredDurationSeconds);

public sealed record UploadTargetDto(string Method, string Url, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

public sealed record AssetStatusDto(Guid Id, MediaAssetStatus Status);

public sealed record CreateUploadResponse(AssetStatusDto Asset, UploadTargetDto Upload, IReadOnlyList<string> Warnings);

public sealed record AssetResponse(AssetDto Asset);

public sealed class CreateUploadRequestValidator : AbstractValidator<CreateUploadRequest>
{
    public CreateUploadRequestValidator()
    {
        RuleFor(x => x.FileName).NotEmpty().WithMessage("Informe o nome do arquivo.").MaximumLength(255);
        RuleFor(x => x.SizeBytes).GreaterThan(0).WithMessage("Tamanho do arquivo inválido.");
        RuleFor(x => x.ContentType).NotEmpty().Must(MediaMagic.IsAllowedContentType)
            .WithMessage("Tipo de conteúdo não aceito. Use MP4, MOV, MKV, WebM, MP3 ou WAV.");
        RuleFor(x => x.DeclaredDurationSeconds).GreaterThanOrEqualTo(0).When(x => x.DeclaredDurationSeconds.HasValue);
    }
}

public static class MediaEndpoints
{
    public const string UploadsRateLimitPolicy = "uploads";

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/media").WithTags("Media").WithValidation().RequireAuthorization();

        group.MapPost("/uploads", CreateUploadAsync).RequireRateLimiting(UploadsRateLimitPolicy).Produces<CreateUploadResponse>(201);
        group.MapPost("/uploads/{assetId:guid}/complete", CompleteUploadAsync).RequireRateLimiting(UploadsRateLimitPolicy).Produces<AssetResponse>();

        group.MapGet("/{assetId:guid}", async (Guid assetId, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var asset = await FindOwnedAsync(db, http.User.GetUserId(), assetId, ct);
            return Results.Ok(new AssetResponse(asset.ToDto()));
        }).Produces<AssetResponse>();

        group.MapDelete("/{assetId:guid}", async (Guid assetId, HttpContext http, AppDbContext db, QuotaService quotas, IObjectStorage storage, IClock clock, IOptions<MediaOptions> media, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            // The status changes under the per-user lock that a signed upload takes to move its file into place
            // (UploadTargetGuard): from here on no upload can (re)create the object.
            var key = await quotas.WithUserLockAsync(userId, async () =>
            {
                var asset = await FindOwnedAsync(db, userId, assetId, ct);
                var referenced = await db.Projects.AnyAsync(p => p.MediaAssetId == asset.Id && p.DeletedAt == null, ct);
                if (referenced) throw AppException.Conflict("O arquivo está em uso por um projeto.");
                var now = clock.UtcNow;
                asset.Status = MediaAssetStatus.Deleted;
                asset.DeletedAt = now;
                asset.ExpiresAt = DeletedObjectSweepAt(asset, media.Value, now);
                await db.SaveChangesAsync(ct);
                return asset.StorageKey;
            }, ct);
            await storage.DeleteAsync(key, ct);
            return Results.NoContent();
        });

        return app;
    }

    private static async Task<IResult> CreateUploadAsync(
        CreateUploadRequest request,
        HttpContext http,
        AppDbContext db,
        QuotaService quotas,
        IObjectStorage storage,
        IClock clock,
        IOptions<RetentionOptions> retention,
        IOptions<MediaOptions> media,
        CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var now = clock.UtcNow;
        var contentType = request.ContentType.Trim().ToLowerInvariant();

        // Storage quota check + insert under the per-user lock (the pending row reserves the bytes immediately).
        var (asset, usage) = await quotas.WithUserLockAsync(userId, async () =>
        {
            var usage = await quotas.EnsureCanUploadAsync(userId, request.SizeBytes, ct);
            var asset = new MediaAsset
            {
                UserId = userId,
                Kind = MediaAssetKind.SourceUpload,
                OriginalFileName = StorageKeys.SanitizeFileName(request.FileName, "upload"),
                ContentType = contentType,
                SizeBytes = request.SizeBytes,
                Status = MediaAssetStatus.Pending,
                CreatedAt = now,
                ExpiresAt = now.AddHours(Math.Max(1, retention.Value.TempUploadHours)),
            };
            asset.StorageKey = StorageKeys.SourceKey(userId, asset.Id, MediaMagic.SafeExtension(request.FileName, contentType));
            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync(ct);
            return (asset, usage);
        }, ct);

        var warnings = new List<string>();
        if (request.DeclaredDurationSeconds is { } declared && declared > 0)
        {
            var limits = usage.Entitlement.Limits;
            var duration = QuotaCalculator.CheckSourceDuration(declared, limits);
            if (!duration.Allowed) warnings.Add(duration.Message!);
            var minutes = QuotaCalculator.CheckProcessingSeconds(usage.ProcessingSecondsUsed, usage.ProcessingSecondsReserved, declared, limits);
            if (!minutes.Allowed) warnings.Add(minutes.Message!);
        }

        var target = await storage.GetSignedUploadUrlAsync(asset.StorageKey, TimeSpan.FromMinutes(media.Value.UploadUrlMinutes), request.SizeBytes, contentType, UrlAudience.Public, ct);
        var response = new CreateUploadResponse(
            new AssetStatusDto(asset.Id, asset.Status),
            new UploadTargetDto(target.Method, target.Url, target.Headers, target.ExpiresAt),
            warnings);
        return Results.Created($"/api/media/{asset.Id}", response);
    }

    /// <summary>
    /// When a deleted asset's storage key must be deleted once more: right after the signed upload URL expires, in case
    /// a client re-sent the PUT before then (S3 presigned URLs cannot be revoked; the Local endpoint refuses it anyway).
    /// Null when the URL has already expired. Stored in expires_at; the retention service does the sweep.
    /// </summary>
    public static DateTimeOffset? DeletedObjectSweepAt(MediaAsset asset, MediaOptions media, DateTimeOffset now)
    {
        var urlExpiry = asset.CreatedAt.AddMinutes(Math.Max(1, media.UploadUrlMinutes)).AddMinutes(1);
        return urlExpiry > now ? urlExpiry : null;
    }

    private static Task<IResult> CompleteUploadAsync(Guid assetId, HttpContext http, AppDbContext db, QuotaService quotas, IObjectStorage storage, IClock clock, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        // Verified under the per-user lock that a signed upload takes to move its file into place: once the asset is
        // "uploaded" the object can no longer change (UploadTargetGuard refuses the PUT).
        return quotas.WithUserLockAsync(userId, () => CompleteUploadLockedAsync(assetId, userId, db, storage, clock, ct), ct);
    }

    private static async Task<IResult> CompleteUploadLockedAsync(Guid assetId, Guid userId, AppDbContext db, IObjectStorage storage, IClock clock, CancellationToken ct)
    {
        var asset = await FindOwnedAsync(db, userId, assetId, ct);
        if (asset.Status is MediaAssetStatus.Uploaded or MediaAssetStatus.Verified)
        {
            return Results.Ok(new AssetResponse(asset.ToDto()));
        }
        if (asset.Status != MediaAssetStatus.Pending)
        {
            throw AppException.InvalidMedia("Upload não está pendente.");
        }

        var info = await storage.GetInfoAsync(asset.StorageKey, ct);
        if (info is null)
        {
            throw AppException.InvalidMedia("Arquivo não encontrado. Envie o arquivo antes de concluir o upload.");
        }
        if (info.SizeBytes != asset.SizeBytes)
        {
            await storage.DeleteAsync(asset.StorageKey, ct);
            throw AppException.InvalidMedia($"Tamanho do arquivo não confere (esperado {asset.SizeBytes} bytes, recebido {info.SizeBytes}).");
        }

        var header = await storage.ReadHeadAsync(asset.StorageKey, MediaMagic.HeaderLength, ct);
        var detected = MediaMagic.Detect(header);
        if (detected is null)
        {
            await storage.DeleteAsync(asset.StorageKey, ct);
            throw AppException.InvalidMedia("Formato de arquivo não reconhecido. Envie um vídeo MP4, MOV, MKV ou WebM (ou áudio MP3/WAV).");
        }

        asset.Status = MediaAssetStatus.Uploaded;
        asset.Container = detected.Container;
        if (asset.ContentType == "application/octet-stream") asset.ContentType = detected.ContentType;
        asset.UploadedAt = clock.UtcNow;
        asset.ExpiresAt = null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new AssetResponse(asset.ToDto()));
    }

    public static async Task<MediaAsset> FindOwnedAsync(AppDbContext db, Guid userId, Guid assetId, CancellationToken ct)
    {
        var asset = await db.MediaAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.UserId == userId && a.DeletedAt == null, ct);
        return asset ?? throw AppException.NotFound("Arquivo não encontrado.");
    }
}
