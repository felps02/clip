using ClipForge.Api.Options;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Media;
using ClipForge.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Services;

/// <summary>Short-lived signed URLs for browsers and long-lived ones for workers.</summary>
public sealed class MediaUrls
{
    private readonly IObjectStorage _storage;
    private readonly MediaOptions _media;
    private readonly JobsOptions _jobs;

    public MediaUrls(IObjectStorage storage, IOptions<MediaOptions> media, IOptions<JobsOptions> jobs)
    {
        _storage = storage;
        _media = media.Value;
        _jobs = jobs.Value;
    }

    public TimeSpan DownloadTtl => TimeSpan.FromMinutes(_media.DownloadUrlMinutes);
    public TimeSpan PreviewTtl => TimeSpan.FromMinutes(_media.PreviewUrlMinutes);
    public TimeSpan ThumbnailTtl => TimeSpan.FromMinutes(_media.ThumbnailUrlMinutes);
    public TimeSpan WorkerTtl => TimeSpan.FromHours(Math.Max(6, _jobs.SourceUrlHours));

    public Task<string?> PreviewAsync(MediaAsset? asset, CancellationToken ct = default)
        => asset is { IsUsable: true } ? Nullable(_storage.GetSignedDownloadUrlAsync(asset.StorageKey, PreviewTtl, null, UrlAudience.Public, ct)) : Task.FromResult<string?>(null);

    public Task<string?> ThumbnailAsync(string? key, CancellationToken ct = default)
        => string.IsNullOrEmpty(key) ? Task.FromResult<string?>(null) : Nullable(_storage.GetSignedDownloadUrlAsync(key, ThumbnailTtl, null, UrlAudience.Public, ct));

    public Task<string?> StreamAsync(ClipRender? render, CancellationToken ct = default)
        => render is { StorageKey: { Length: > 0 } key } ? Nullable(_storage.GetSignedDownloadUrlAsync(key, DownloadTtl, null, UrlAudience.Public, ct)) : Task.FromResult<string?>(null);

    public Task<string?> DownloadAsync(ClipRender? render, string fileName, CancellationToken ct = default)
        => render is { StorageKey: { Length: > 0 } key } ? Nullable(_storage.GetSignedDownloadUrlAsync(key, DownloadTtl, fileName, UrlAudience.Public, ct)) : Task.FromResult<string?>(null);

    public Task<string> WorkerDownloadAsync(string key, CancellationToken ct = default)
        => _storage.GetSignedDownloadUrlAsync(key, WorkerTtl, null, UrlAudience.Worker, ct);

    public static string DownloadFileName(Clip clip) => StorageKeys.DownloadFileName(clip.Title);

    private static async Task<string?> Nullable(Task<string> task) => await task;
}
