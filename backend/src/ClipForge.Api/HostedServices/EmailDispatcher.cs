using ClipForge.Api.Services;
using ClipForge.Infrastructure.Email;

namespace ClipForge.Api.HostedServices;

/// <summary>
/// Delivers the notification e-mails of <see cref="EmailOutbox"/> one at a time through <see cref="IEmailSender"/>.
/// A failure (SMTP down, rejected address, timeout) is logged and the next message goes on; it never reaches the
/// request that queued the e-mail.
/// </summary>
public sealed class EmailDispatcher : BackgroundService
{
    /// <summary>Upper bound of one delivery, so a hung SMTP server cannot stall the queue.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(60);

    private readonly EmailOutbox _outbox;
    private readonly IServiceProvider _services;
    private readonly ILogger<EmailDispatcher> _logger;
    private IEmailSender? _sender;

    // The sender is resolved on the first message (as before this queue existed), so a misconfigured provider
    // (e.g. Email:Provider=Smtp without a host) is logged per e-mail instead of stopping the API at startup.
    public EmailDispatcher(EmailOutbox outbox, IServiceProvider services, ILogger<EmailDispatcher> logger)
    {
        _outbox = outbox;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(stoppingToken))
            {
                await DeliverAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>Sends one message; never throws (except for shutdown cancellation).</summary>
    public async Task DeliverAsync(EmailMessage message, CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(SendTimeout);
            _sender ??= _services.GetRequiredService<IEmailSender>();
            await _sender.SendAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification e-mail \"{Subject}\" could not be sent", message.Subject);
        }
        finally
        {
            _outbox.MarkHandled();
        }
    }
}
