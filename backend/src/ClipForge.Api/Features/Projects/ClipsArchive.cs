using System.IO.Compression;
using ClipForge.Api.Common;
using ClipForge.Domain.Common;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Media;
using ClipForge.Infrastructure.Persistence;
using ClipForge.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Projects;

/// <summary>One file of the archive: the current ready render of a clip.</summary>
public sealed record ClipsArchiveEntry(string Name, string StorageKey, DateTimeOffset? LastModified);

/// <summary>
/// <c>GET /api/projects/{id}/clips/archive</c>: every ready clip of the project in one zip. The archive is written
/// straight to the response body (ZipArchive in Create mode, stored entries) while each render is read from storage, so
/// neither the archive nor a whole clip is ever held in memory. It is not a render: no quota is consumed.
/// </summary>
public sealed class ClipsArchiveService
{
    private readonly AppDbContext _db;
    private readonly ProjectService _projects;
    private readonly IObjectStorage _storage;
    private readonly ILogger<ClipsArchiveService> _logger;

    public ClipsArchiveService(AppDbContext db, ProjectService projects, IObjectStorage storage, ILogger<ClipsArchiveService> logger)
    {
        _db = db;
        _projects = projects;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>Archive file name: the sanitized project title + "-clipes.zip".</summary>
    public static string ArchiveFileName(string? projectTitle) => StorageKeys.DownloadFileName(projectTitle, "-clipes.zip");

    /// <summary>Entry name: two-digit position + sanitized clip title (".mp4").</summary>
    public static string EntryName(int position, string? clipTitle) => $"{position:D2}-{StorageKeys.DownloadFileName(clipTitle)}";

    /// <summary>
    /// Resolves the archive of a project the user owns (404 for anyone else): clips that are not deleted and whose current
    /// render is ready, best first (score, then creation order). 404 <c>not_found</c> when no ready clip remains.
    /// </summary>
    public async Task<ClipsArchiveResult> PrepareAsync(Guid userId, Guid projectId, CancellationToken ct)
    {
        var project = await _projects.GetOwnedAsync(userId, projectId, ct);
        var rows = await (
                from clip in _db.Clips
                join render in _db.ClipRenders on clip.CurrentRenderId equals render.Id
                where clip.ProjectId == project.Id && clip.UserId == userId && clip.DeletedAt == null
                      && render.Status == RenderStatus.Ready && render.StorageKey != null && render.StorageKey != ""
                orderby clip.Score == null, clip.Score descending, clip.CreatedAt, clip.Id
                select new { clip.Title, render.StorageKey, render.CompletedAt })
            .ToListAsync(ct);

        var entries = new List<ClipsArchiveEntry>(rows.Count);
        foreach (var row in rows)
        {
            // A render whose object is gone (purged, restored backup) is left out instead of breaking the whole download.
            if (!StorageKeys.BelongsToUser(row.StorageKey, userId) || await _storage.GetInfoAsync(row.StorageKey!, ct) is null)
            {
                _logger.LogWarning("Archive of project {ProjectId}: render object {Key} is missing, skipped", project.Id, row.StorageKey);
                continue;
            }
            entries.Add(new ClipsArchiveEntry(EntryName(entries.Count + 1, row.Title), row.StorageKey!, row.CompletedAt));
        }
        if (entries.Count == 0) throw AppException.NotFound("Este projeto ainda não tem clipes prontos para baixar.");

        return new ClipsArchiveResult(ArchiveFileName(project.Title), entries, _storage, _logger);
    }
}

/// <summary>Streams the zip to the response (no buffering of the archive).</summary>
public sealed class ClipsArchiveResult : IResult
{
    private readonly IObjectStorage _storage;
    private readonly ILogger _logger;

    public ClipsArchiveResult(string fileName, IReadOnlyList<ClipsArchiveEntry> entries, IObjectStorage storage, ILogger logger)
    {
        FileName = fileName;
        Entries = entries;
        _storage = storage;
        _logger = logger;
    }

    public string FileName { get; }

    public IReadOnlyList<ClipsArchiveEntry> Entries { get; }

    public async Task ExecuteAsync(HttpContext http)
    {
        var ct = http.RequestAborted;
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/zip";
        // The file name is an ASCII slug (letters, digits, dashes), safe inside the quotes.
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{FileName}\"";
        http.Response.Headers.CacheControl = "no-store";

        // ZipArchive still writes each entry's data descriptor synchronously; Kestrel forbids synchronous I/O, so those few
        // bytes are held and sent with the next asynchronous write (entry data itself always streams through).
        var body = new SyncWriteBufferingStream(http.Response.Body);
        try
        {
            // Create mode over the (non-seekable) response body: every entry is written as it is read, with a data
            // descriptor, and the central directory goes out at the end. Clips are already compressed video: stored.
            var zip = await ZipArchive.CreateAsync(body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
            foreach (var entry in Entries)
            {
                var zipEntry = zip.CreateEntry(entry.Name, CompressionLevel.NoCompression);
                if (entry.LastModified is { } modified) zipEntry.LastWriteTime = modified;
                var target = await zipEntry.OpenAsync(ct);
                await using (var source = await _storage.OpenReadAsync(entry.StorageKey, ct))
                {
                    await source.CopyToAsync(target, ct);
                }
                await target.DisposeAsync();
            }
            // Only a complete archive gets its central directory: on any failure above the connection is aborted
            // instead, so a truncated download never looks like a valid zip.
            await zip.DisposeAsync();
            await body.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Clips archive {FileName} failed after the response started; aborting the download", FileName);
            http.Abort();
        }
        catch (OperationCanceledException)
        {
            // The client went away.
        }
    }
}
