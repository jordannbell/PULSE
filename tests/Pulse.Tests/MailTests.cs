using System.Text;
using FlakyTarget;
using Microsoft.Extensions.Options;
using MimeKit;
using Pulse.Application;
using Pulse.Application.Accounts;
using Pulse.Application.Alerts;
using Pulse.Infrastructure.Alerts;
using Pulse.Infrastructure.Mail;

namespace Pulse.Tests;

/// <summary>The real SMTP sender (MailKit) delivering to a real SMTP server on the loopback address.</summary>
public sealed class MailTests : IAsyncLifetime
{
    private SmtpSink _smtp = null!;

    public ValueTask InitializeAsync()
    {
        _smtp = new SmtpSink(0);
        _smtp.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _smtp.DisposeAsync();

    private SmtpMailSender Sender(int? port = null, string host = "127.0.0.1") =>
        new(Options.Create(new MailOptions
        {
            Host = host,
            Port = port ?? _smtp.Port,
            Security = SmtpSecurity.None,
            From = "pulse@example.com",
        }));

    private static MimeMessage Parse(ReceivedMail mail) =>
        MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(mail.Data)));

    [Fact]
    public async Task A_mail_reaches_its_recipient_with_the_sender_subject_and_text()
    {
        await Sender().SendAsync(new OutgoingMail("ana@example.com", "Bonjour", "Le texte."), CancellationToken.None);

        var mail = Assert.Single(_smtp.Mails);
        Assert.Equal("pulse@example.com", mail.From);
        Assert.Equal(["ana@example.com"], mail.To);
        var message = Parse(mail);
        Assert.Equal("Bonjour", message.Subject);
        Assert.Contains("Le texte.", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_is_down_is_a_delivery_failure_with_a_safe_message()
    {
        var failure = await Assert.ThrowsAsync<MailDeliveryException>(
            () => Sender(port: 1).SendAsync(new OutgoingMail("ana@example.com", "S", "B"), CancellationToken.None));

        Assert.Equal("The mail server could not be reached.", failure.Message);
    }

    [Fact]
    public async Task With_no_server_configured_nothing_is_sent_and_the_sender_says_so()
    {
        var sender = Sender(host: string.Empty);

        Assert.False(sender.IsAvailable);
        await Assert.ThrowsAsync<MailDeliveryException>(
            () => sender.SendAsync(new OutgoingMail("ana@example.com", "S", "B"), CancellationToken.None));
        Assert.Empty(_smtp.Mails);
    }

    [Fact]
    public async Task An_incident_alert_is_mailed_with_a_clear_subject_the_address_and_the_cause_in_french()
    {
        var channel = new EmailAlertChannel(Sender(), "owner@example.com");

        await channel.SendAsync(Alerts.Opened("Shop", "Expected status 200, got 500."), CancellationToken.None);

        var mail = Assert.Single(_smtp.Mails);
        Assert.Equal(["owner@example.com"], mail.To);
        var message = Parse(mail);
        Assert.Equal("[Pulse] Shop est en panne", message.Subject);
        Assert.Contains("Code 500 reçu, 200 attendu.", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://shop.example/health", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resolution_says_the_service_works_again_and_how_long_it_was_down()
    {
        await new EmailAlertChannel(Sender(), "owner@example.com").SendAsync(Alerts.Resolved("Shop"), CancellationToken.None);

        var message = Parse(Assert.Single(_smtp.Mails));
        Assert.Equal("[Pulse] Shop fonctionne de nouveau", message.Subject);
        Assert.Contains("5 min 30 s", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accents_in_a_service_name_survive_the_trip()
    {
        await new EmailAlertChannel(Sender(), "owner@example.com").SendAsync(Alerts.Opened("Boutique été"), CancellationToken.None);

        var message = Parse(Assert.Single(_smtp.Mails));
        Assert.Equal("[Pulse] Boutique été est en panne", message.Subject);
        Assert.Contains("Boutique été", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_mail_server_becomes_an_alert_delivery_failure()
    {
        var channel = new EmailAlertChannel(Sender(port: 1), "owner@example.com");

        var failure = await Assert.ThrowsAsync<AlertDeliveryException>(() => channel.SendAsync(Alerts.Opened(), CancellationToken.None));

        Assert.Contains("could not be reached", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("smtp.example", true)]
    public void Mail_is_available_only_when_a_server_is_set(string host, bool expected)
    {
        Assert.Equal(expected, new MailOptions { Host = host }.IsConfigured);
    }
}
