using ClipForge.Api.HostedServices;
using ClipForge.Api.Services;
using ClipForge.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ClipForge.Api.Tests.Unit;

public class EmailDispatcherTests
{
    [Fact]
    public async Task A_failing_delivery_is_logged_and_the_queue_goes_on()
    {
        var outbox = new EmailOutbox();
        var sender = Substitute.For<IEmailSender>();
        var delivered = new List<string>();
        sender.SendAsync(Arg.Is<EmailMessage>(m => m.Subject == "smtp down"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP server unavailable"));
        sender.SendAsync(Arg.Is<EmailMessage>(m => m.Subject != "smtp down"), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                delivered.Add(call.Arg<EmailMessage>().Subject);
                return Task.CompletedTask;
            });
        await using var services = new ServiceCollection().AddSingleton(sender).BuildServiceProvider();
        var dispatcher = new EmailDispatcher(outbox, services, NullLogger<EmailDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            outbox.TryEnqueue(new EmailMessage("a@example.com", "smtp down", "body")).ShouldBeTrue();
            outbox.TryEnqueue(new EmailMessage("a@example.com", "second", "body")).ShouldBeTrue();
            outbox.TryEnqueue(new EmailMessage("b@example.com", "third", "body")).ShouldBeTrue();
            (await outbox.WaitIdleAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        delivered.ShouldBe(["second", "third"]);
        outbox.Pending.ShouldBe(0);
    }

    [Fact]
    public void A_full_outbox_refuses_new_messages_instead_of_blocking()
    {
        var outbox = new EmailOutbox();
        for (var i = 0; i < EmailOutbox.Capacity; i++)
        {
            outbox.TryEnqueue(new EmailMessage("a@example.com", $"m{i}", "body")).ShouldBeTrue();
        }
        outbox.TryEnqueue(new EmailMessage("a@example.com", "overflow", "body")).ShouldBeFalse();
        outbox.Pending.ShouldBe(EmailOutbox.Capacity);
    }
}
