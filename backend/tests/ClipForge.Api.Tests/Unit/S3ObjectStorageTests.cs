using Amazon.S3;
using Amazon.S3.Model;
using ClipForge.Api.Tests.Infrastructure;

namespace ClipForge.Api.Tests.Unit;

/// <summary>
/// S3ObjectStorage.WriteAsync through the real AWS SDK over an in-memory S3 endpoint (<see cref="FakeS3Handler"/>): a
/// live segment arrives as a stream without a length (the request body), which a plain PutObject cannot send.
/// </summary>
public class S3ObjectStorageTests
{
    private const string Key = "users/0195f0a4-0000-7000-8000-000000000001/live/0195f0a4-0000-7000-8000-000000000002/seg_00000.mkv";
    private const int PartSize = 64 * 1024;

    [Fact]
    public async Task The_sdk_itself_cannot_put_a_stream_without_a_length()
    {
        // Why WriteAsync buffers parts: the SDK needs the content length of a PutObject before it sends anything.
        var s3 = new FakeS3Handler();
        using var client = s3.CreateClient();
        var request = new PutObjectRequest { BucketName = FakeS3Handler.Bucket, Key = Key, InputStream = new UnseekableStream(Bytes(1000)), AutoCloseStream = false };
        var error = await Should.ThrowAsync<AmazonS3Exception>(() => client.PutObjectAsync(request));
        error.Message.ShouldContain("content length", Case.Insensitive);
        s3.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stream_without_a_length_that_fits_in_one_part_is_one_put()
    {
        var s3 = new FakeS3Handler();
        var bytes = Bytes(10_000);
        await s3.CreateStorage(PartSize).WriteAsync(Key, new UnseekableStream(bytes, maxRead: 3000), "video/x-matroska");

        s3.Objects[Key].Content.ShouldBe(bytes);
        s3.Objects[Key].ContentType.ShouldBe("video/x-matroska");
        s3.Calls.ToArray().ShouldBe(new[] { $"PutObject {Key}" });
    }

    [Fact]
    public async Task A_longer_stream_without_a_length_goes_up_in_parts()
    {
        var s3 = new FakeS3Handler();
        var bytes = Bytes(3 * PartSize + 123);
        await s3.CreateStorage(PartSize).WriteAsync(Key, new UnseekableStream(bytes, maxRead: 7000), "video/x-matroska");

        s3.Objects[Key].Content.ShouldBe(bytes);
        s3.Objects[Key].ContentType.ShouldBe("video/x-matroska");
        s3.Calls.ToArray().ShouldBe(new[]
        {
            $"CreateMultipartUpload {Key}",
            $"UploadPart {Key} #1 {PartSize}",
            $"UploadPart {Key} #2 {PartSize}",
            $"UploadPart {Key} #3 {PartSize}",
            $"UploadPart {Key} #4 123",
            $"CompleteMultipartUpload {Key} 4",
        });
        s3.OpenUploads.ShouldBe(0);
    }

    [Fact]
    public async Task A_stream_failing_midway_aborts_the_multipart_upload()
    {
        var s3 = new FakeS3Handler();
        var stream = new UnseekableStream(Bytes(3 * PartSize), maxRead: 7000, failAfter: 2 * PartSize + 10);
        await Should.ThrowAsync<IOException>(() => s3.CreateStorage(PartSize).WriteAsync(Key, stream, "video/x-matroska"));

        s3.Objects.ShouldBeEmpty();
        s3.Calls.Last().ShouldBe($"AbortMultipartUpload {Key}");
        s3.OpenUploads.ShouldBe(0);
    }

    [Fact]
    public async Task A_seekable_stream_is_still_one_put_with_the_content_type_of_its_key()
    {
        var s3 = new FakeS3Handler();
        var bytes = Bytes(3 * PartSize);
        await s3.CreateStorage(PartSize).WriteAsync(Key, new MemoryStream(bytes), null);

        s3.Objects[Key].Content.ShouldBe(bytes);
        s3.Objects[Key].ContentType.ShouldBe("video/x-matroska");
        s3.Calls.ToArray().ShouldBe(new[] { $"PutObject {Key}" });
    }

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 % 251);
        return bytes;
    }
}

/// <summary>A read-only stream that cannot seek nor tell its length (like a request body), optionally failing midway.</summary>
public sealed class UnseekableStream(byte[] content, int maxRead = int.MaxValue, long failAfter = long.MaxValue) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= failAfter) throw new IOException("The client went away.");
        var read = (int)Math.Min(Math.Min(count, maxRead), Math.Min(content.Length - _position, failAfter - _position));
        Array.Copy(content, _position, buffer, offset, read);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromResult(Read(buffer, offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var array = new byte[buffer.Length];
        var read = Read(array, 0, array.Length);
        array.AsSpan(0, read).CopyTo(buffer.Span);
        return ValueTask.FromResult(read);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
