namespace ClipForge.Api.Common;

/// <summary>
/// Write-only stream over a response body that never does synchronous I/O on it. Asynchronous writes go straight through;
/// the few small synchronous writes some writers still make (ZipArchive writes each entry's data descriptor and parts of
/// its headers synchronously) are held in a small buffer and sent ahead of the next asynchronous write or flush.
/// <see cref="MaxPendingBytes"/> records the largest amount ever held, so tests can prove nothing larger is buffered.
/// </summary>
public sealed class SyncWriteBufferingStream : Stream
{
    private readonly Stream _inner;
    private readonly MemoryStream _pending = new();
    private long _position;

    public SyncWriteBufferingStream(Stream inner)
    {
        _inner = inner;
    }

    /// <summary>Largest number of bytes held at once from synchronous writes.</summary>
    public long MaxPendingBytes { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _pending.Write(buffer);
        _position += buffer.Length;
        MaxPendingBytes = Math.Max(MaxPendingBytes, _pending.Length);
    }

    public override void WriteByte(byte value) => Write([value]);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await DrainAsync(cancellationToken);
        await _inner.WriteAsync(buffer, cancellationToken);
        _position += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>A synchronous flush only marks the end of a write: the held bytes leave with the next asynchronous call.</summary>
    public override void Flush()
    {
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await DrainAsync(cancellationToken);
        await _inner.FlushAsync(cancellationToken);
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        if (_pending.Length == 0) return;
        await _inner.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), cancellationToken);
        _pending.SetLength(0);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pending.Dispose();
        base.Dispose(disposing);
    }
}
