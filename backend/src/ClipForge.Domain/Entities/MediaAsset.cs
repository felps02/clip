using ClipForge.Domain.Enums;

namespace ClipForge.Domain.Entities;

public class MediaAsset
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public MediaAssetKind Kind { get; set; } = MediaAssetKind.SourceUpload;
    public string StorageKey { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public MediaAssetStatus Status { get; set; } = MediaAssetStatus.Pending;
    public string? Sha256 { get; set; }
    public decimal? DurationSeconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? Fps { get; set; }
    public string? Container { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public bool? HasAudio { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UploadedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsUsable => (Status is MediaAssetStatus.Uploaded or MediaAssetStatus.Verified) && DeletedAt is null;

    private static readonly string[] PreviewableContainers = ["mp4", "mov", "webm"];

    public bool IsPreviewable => Container is not null && Array.IndexOf(PreviewableContainers, Container.ToLowerInvariant()) >= 0;
}
