using System.IO.Compression;
using ClipForge.Api.Common;

namespace ClipForge.Api.Tests.Unit;

/// <summary>The archive writer streams entry data; only ZipArchive's few synchronous header/descriptor bytes are held.</summary>
public class SyncWriteBufferingStreamTests
{
    /// <summary>A response body look-alike: write-only, not seekable, synchronous I/O forbidden.</summary>
    private sealed class AsyncOnlySink : Stream
    {
        public MemoryStream Written { get; } = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Written.Write(buffer.Span);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_zip_of_large_entries_goes_out_without_synchronous_io_or_buffering()
    {
        var sink = new AsyncOnlySink();
        var adapter = new SyncWriteBufferingStream(sink);
        var entries = new Dictionary<string, byte[]>
        {
            ["01-primeiro.mp4"] = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 31 % 251)).ToArray(),
            ["02-segundo.mp4"] = Enumerable.Range(0, 1_500_000).Select(i => (byte)(i * 17 % 241)).ToArray(),
        };

        var zip = await ZipArchive.CreateAsync(adapter, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, CancellationToken.None);
        foreach (var (name, bytes) in entries)
        {
            var target = await zip.CreateEntry(name, CompressionLevel.NoCompression).OpenAsync(CancellationToken.None);
            await new MemoryStream(bytes).CopyToAsync(target);
            await target.DisposeAsync();
        }
        await zip.DisposeAsync();
        await adapter.FlushAsync();

        adapter.MaxPendingBytes.ShouldBeLessThan(64 * 1024); // headers, descriptors, central directory — never entry data

        using var read = new ZipArchive(new MemoryStream(sink.Written.ToArray()), ZipArchiveMode.Read);
        read.Entries.Select(e => e.FullName).ShouldBe(entries.Keys.ToList());
        foreach (var entry in read.Entries)
        {
            using var copy = new MemoryStream();
            await using (var stream = entry.Open()) await stream.CopyToAsync(copy);
            copy.ToArray().ShouldBe(entries[entry.FullName]);
            entry.CompressedLength.ShouldBe(entry.Length);
        }
    }
}
