using ClipForge.Api.Common;
using ClipForge.Domain.Common;
using ClipForge.Domain.Media;
using ClipForge.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace ClipForge.Api.Features.Media;

/// <summary>
/// Local storage transfer endpoints (only mapped when Storage:Provider=Local). Anonymous but HMAC-signed:
/// PUT /api/storage/upload?key&amp;exp&amp;max&amp;sig and GET /api/storage/download?key&amp;exp&amp;sig&amp;name.
/// The PUT is also bound to the state of its target (<see cref="UploadTargetGuard"/>): 409 once the upload was
/// completed/deleted or the job stopped running.
/// </summary>
public static class StorageEndpoints
{
    /// <summary>
    /// Signed transfers are not part of the per-user "uploads" budget: the HMAC signature (key, expiry, max size)
    /// already bounds who can call them, and the worker PUTs every clip, thumbnail and analysis file of a job in a
    /// burst. They get their own, larger per-IP window (RateLimiting:TransfersPerMinute).
    /// </summary>
    public const string TransfersRateLimitPolicy = "transfers";

    public static IEndpointRouteBuilder MapLocalStorageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/storage").WithTags("Storage").AllowAnonymous();

        group.MapPut("/upload", UploadAsync)
            .WithMetadata(new DisableRequestSizeLimitAttribute())
            .RequireRateLimiting(TransfersRateLimitPolicy);

        group.MapGet("/download", Download);

        return app;
    }

    private static async Task<IResult> UploadAsync(
        HttpContext http,
        [FromQuery] string? key,
        [FromQuery] long? exp,
        [FromQuery] long? max,
        [FromQuery] string? sig,
        ISignedUrlService signer,
        LocalFileObjectStorage storage,
        UploadTargetGuard guard,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key) || exp is null || max is null || string.IsNullOrEmpty(sig) || !StorageKeys.IsValid(key))
        {
            throw AppException.Forbidden("URL de upload inválida.");
        }
        if (!signer.VerifyUpload("PUT", key, exp.Value, max.Value, sig))
        {
            throw AppException.Forbidden("URL de upload inválida ou expirada.");
        }
        await guard.EnsureAcceptsAsync(key, ct);

        var limit = Math.Max(1, max.Value);
        if (http.Request.ContentLength is { } declared && declared > limit)
        {
            throw new AppException("payload_too_large", 413, "O arquivo excede o tamanho declarado.");
        }

        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }

        await using var staged = await storage.StageLimitedAsync(key, http.Request.Body, limit, ct);
        await guard.CommitAsync(key, staged.Commit, ct);
        return Results.Ok(new { key, sizeBytes = staged.SizeBytes });
    }

    private static IResult Download(
        [FromQuery] string? key,
        [FromQuery] long? exp,
        [FromQuery] string? sig,
        [FromQuery] string? name,
        ISignedUrlService signer,
        LocalFileObjectStorage storage)
    {
        if (string.IsNullOrEmpty(key) || exp is null || string.IsNullOrEmpty(sig) || !StorageKeys.IsValid(key))
        {
            throw AppException.Forbidden("URL de download inválida.");
        }
        if (!signer.VerifyDownload(key, exp.Value, sig))
        {
            throw AppException.Forbidden("URL de download inválida ou expirada.");
        }

        var path = storage.GetFullPath(key);
        if (!File.Exists(path)) throw AppException.NotFound("Arquivo não encontrado.");

        var contentType = MediaMagic.ContentTypeForKey(key);
        var fileName = string.IsNullOrWhiteSpace(name) ? null : StorageKeys.SanitizeFileName(name);
        return Results.File(path, contentType, fileName, enableRangeProcessing: true);
    }
}
