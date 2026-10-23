using System.Threading.Channels;
using ClipForge.Infrastructure.Email;

namespace ClipForge.Api.Services;

/// <summary>
/// In-memory queue of notification e-mails (job completed/failed, quota warnings, billing). Requests only enqueue;
/// <see cref="HostedServices.EmailDispatcher"/> delivers through <see cref="IEmailSender"/>, so SMTP latency or
/// outages never fail or slow down the request that produced the e-mail. Bounded: when full, new e-mails are dropped
/// (and logged by the caller). Messages still queued when the process stops are lost (notifications are best effort;
/// the in-app notification always exists).
/// </summary>
public sealed class EmailOutbox
{
    public const int Capacity = 1000;

    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    private int _pending;

    public ChannelReader<EmailMessage> Reader => _channel.Reader;

    /// <summary>E-mails queued or being delivered.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public bool TryEnqueue(EmailMessage message)
    {
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(message)) return true;
        Interlocked.Decrement(ref _pending);
        return false;
    }

    /// <summary>Called by the dispatcher once a message was handled (sent or failed).</summary>
    public void MarkHandled() => Interlocked.Decrement(ref _pending);

    /// <summary>Waits until every queued e-mail was handled (tests and graceful shutdown). False on timeout.</summary>
    public async Task<bool> WaitIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Pending > 0)
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(10);
        }
        return true;
    }
}
