using System.Collections.Concurrent;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ClipForge.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Log | Smtp</summary>
    public string Provider { get; set; } = "Log";
    public string From { get; set; } = "no-reply@example.com";
    public string? FromName { get; set; }
    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseStartTls { get; set; } = true;
}

public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Development sender: logs the message (never the full body of secrets) and keeps the last messages in memory for tests.</summary>
public sealed class LogEmailSender : IEmailSender
{
    private const int MaxKept = 200;
    private readonly ConcurrentQueue<EmailMessage> _messages = new();
    private readonly ILogger<LogEmailSender> _logger;

    public LogEmailSender(ILogger<LogEmailSender> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<EmailMessage> Messages => _messages.ToArray();

    public EmailMessage? LastTo(string email)
        => _messages.LastOrDefault(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase));

    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { }
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        _messages.Enqueue(message);
        while (_messages.Count > MaxKept && _messages.TryDequeue(out _)) { }
        // Bodies contain one-time links; log only subject/recipient at information level and the body at debug level.
        _logger.LogInformation("E-mail (log provider) to {To}: {Subject}", message.To, message.Subject);
        _logger.LogDebug("E-mail body: {Body}", message.TextBody);
        return Task.CompletedTask;
    }
}

/// <summary>Real delivery through SMTP (MailKit).</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (string.IsNullOrWhiteSpace(_options.Smtp.Host))
        {
            throw new InvalidOperationException("Email:Smtp:Host must be configured when Email:Provider=Smtp.");
        }
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName ?? string.Empty, _options.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        var body = new BodyBuilder { TextBody = message.TextBody };
        if (!string.IsNullOrWhiteSpace(message.HtmlBody)) body.HtmlBody = message.HtmlBody;
        mime.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        var smtp = _options.Smtp;
        var security = smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(smtp.Host!, smtp.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(smtp.Username))
        {
            await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, ct);
        }
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
        _logger.LogInformation("E-mail sent to {To}: {Subject}", message.To, message.Subject);
    }
}
