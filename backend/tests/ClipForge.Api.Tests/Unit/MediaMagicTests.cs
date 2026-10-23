using System.Text;
using ClipForge.Domain.Enums;
using ClipForge.Domain.Media;

namespace ClipForge.Api.Tests.Unit;

public class MediaMagicTests
{
    [Fact]
    public void Detects_mp4_from_ftyp_at_offset_4()
    {
        var header = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0 };
        var detected = MediaMagic.Detect(header);
        detected.ShouldNotBeNull();
        detected.Container.ShouldBe("mp4");
        detected.ContentType.ShouldBe("video/mp4");
        detected.IsVideo.ShouldBeTrue();
    }

    [Fact]
    public void Detects_mov_from_qt_brand()
    {
        var header = new byte[] { 0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70, (byte)'q', (byte)'t', (byte)' ', (byte)' ', 0, 0, 0, 0 };
        MediaMagic.Detect(header)!.Container.ShouldBe("mov");
    }

    [Fact]
    public void Detects_matroska_and_webm_from_ebml_header()
    {
        var mkv = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x00, 0x00, 0x00 }.Concat(Encoding.ASCII.GetBytes("matroska")).ToArray();
        MediaMagic.Detect(mkv)!.Container.ShouldBe("mkv");
        var webm = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x00, 0x00, 0x00 }.Concat(Encoding.ASCII.GetBytes("B\x82webm")).ToArray();
        MediaMagic.Detect(webm)!.Container.ShouldBe("webm");
    }

    [Fact]
    public void Detects_wav_and_mp3()
    {
        var wav = Encoding.ASCII.GetBytes("RIFF\x24\x08\x00\x00WAVEfmt ");
        MediaMagic.Detect(wav)!.Container.ShouldBe("wav");
        MediaMagic.Detect(wav)!.IsVideo.ShouldBeFalse();

        MediaMagic.Detect(Encoding.ASCII.GetBytes("ID3\x04\x00\x00\x00\x00\x00\x00"))!.Container.ShouldBe("mp3");
        MediaMagic.Detect(new byte[] { 0xFF, 0xFB, 0x90, 0x00, 0, 0, 0, 0 })!.Container.ShouldBe("mp3");
    }

    [Fact]
    public void Rejects_text_and_unknown_headers()
    {
        MediaMagic.Detect(Encoding.ASCII.GetBytes("hello world, this is not a video file at all")).ShouldBeNull();
        MediaMagic.Detect([]).ShouldBeNull();
        MediaMagic.Detect(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }).ShouldBeNull(); // PNG
        MediaMagic.Detect(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }).ShouldBeNull(); // invalid MPEG sync (reserved bits)
    }

    [Theory]
    [InlineData("video/mp4", true)]
    [InlineData("video/quicktime", true)]
    [InlineData("video/x-matroska", true)]
    [InlineData("video/webm", true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("audio/wav", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData("text/plain", false)]
    [InlineData("image/png", false)]
    [InlineData(null, false)]
    public void Content_type_allow_list(string? contentType, bool allowed)
    {
        MediaMagic.IsAllowedContentType(contentType).ShouldBe(allowed);
    }

    [Fact]
    public void Safe_extension_and_storage_keys()
    {
        MediaMagic.SafeExtension("My Live.MP4", "video/mp4").ShouldBe(".mp4");
        MediaMagic.SafeExtension("weird.e$x", "video/webm").ShouldBe(".webm");
        MediaMagic.SafeExtension(null, "unknown/type").ShouldBe(".bin");

        var userId = Guid.NewGuid();
        var key = StorageKeys.SourceKey(userId, Guid.NewGuid(), ".mp4");
        StorageKeys.IsValid(key).ShouldBeTrue();
        StorageKeys.BelongsToUser(key, userId).ShouldBeTrue();
        StorageKeys.BelongsToUser(key, Guid.NewGuid()).ShouldBeFalse();
        StorageKeys.IsValid("users/../etc/passwd").ShouldBeFalse();
        StorageKeys.IsValid("/users/x").ShouldBeFalse();
        StorageKeys.IsValid("users//x").ShouldBeFalse();
        StorageKeys.SanitizeFileName("../../evil name?.mp4").ShouldBe("evil_name_.mp4");
        StorageKeys.DownloadFileName("Reação épica #1 - Live!").ShouldBe("reacao-epica-1-live.mp4");
        StorageKeys.DownloadFileName("").ShouldBe("clip.mp4");
    }

    [Theory]
    [InlineData("https://www.twitch.tv/videos/123456", DetectedPlatform.Twitch)]
    [InlineData("twitch.tv/somestreamer", DetectedPlatform.Twitch)]
    [InlineData("https://clips.twitch.tv/SomeClip", DetectedPlatform.Twitch)]
    [InlineData("https://www.youtube.com/watch?v=abc", DetectedPlatform.Youtube)]
    [InlineData("https://youtu.be/abc", DetectedPlatform.Youtube)]
    [InlineData("https://m.youtube.com/watch?v=abc", DetectedPlatform.Youtube)]
    [InlineData("https://kick.com/streamer", DetectedPlatform.Kick)]
    [InlineData("https://example.com/video.mp4", DetectedPlatform.Unknown)]
    [InlineData("https://nottwitch.tv/x", DetectedPlatform.Unknown)]
    [InlineData("ftp://twitch.tv/x", DetectedPlatform.Unknown)]
    [InlineData("", DetectedPlatform.Unknown)]
    [InlineData("not a url", DetectedPlatform.Unknown)]
    public void Platform_detection(string url, DetectedPlatform expected)
    {
        PlatformDetector.Detect(url).ShouldBe(expected);
    }
}
