using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Pulse.Application;
using Pulse.Application.Accounts;

namespace Pulse.Infrastructure.Mail;

/// <summary>Sends e-mails through the SMTP server named in <c>Pulse:Mail</c>. With no server set, nothing is sent.</summary>
internal sealed class SmtpMailSender(IOptions<MailOptions> options) : IMailSender
{
    private static readonly TimeSpan SmtpTimeout = TimeSpan.FromSeconds(15);

    private readonly MailOptions _mail = options.Value;

    public bool IsAvailable => _mail.IsConfigured;

    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (!IsAvailable)
        {
            throw new MailDeliveryException("No mail server is configured.");
        }

        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_mail.From));
        message.To.Add(MailboxAddress.Parse(mail.To));
        message.Subject = mail.Subject;
        message.Body = new TextPart("plain") { Text = mail.Body };

        using var smtp = new SmtpClient { Timeout = (int)SmtpTimeout.TotalMilliseconds };
        try
        {
            await smtp.ConnectAsync(_mail.Host, _mail.Port, ToSocketOptions(_mail.Security), cancellationToken);
            if (!string.IsNullOrEmpty(_mail.User))
            {
                await smtp.AuthenticateAsync(_mail.User, _mail.Password, cancellationToken);
            }

            await smtp.SendAsync(message, cancellationToken);
            await smtp.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (AuthenticationException ex)
        {
            throw new MailDeliveryException("The mail server refused the user name or password.", ex);
        }
        catch (SmtpCommandException ex)
        {
            throw new MailDeliveryException($"The mail server refused the message (code {(int)ex.StatusCode}).", ex);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ProtocolException or ServiceNotConnectedException or SslHandshakeException or TimeoutException)
        {
            throw new MailDeliveryException("The mail server could not be reached.", ex);
        }
    }

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) =>
        security switch
        {
            SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        };
}
