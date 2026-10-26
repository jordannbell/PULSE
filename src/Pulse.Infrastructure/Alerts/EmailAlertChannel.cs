using Pulse.Application;
using Pulse.Application.Alerts;

namespace Pulse.Infrastructure.Alerts;

/// <summary>Sends the alert by e-mail to one address: the confirmed address of the organization's owner.</summary>
internal sealed class EmailAlertChannel(IMailSender mail, string recipient) : IAlertChannel
{
    public string Name => "E-mail";

    public async Task SendAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);

        try
        {
            await mail.SendAsync(new OutgoingMail(recipient, AlertFormatter.Subject(alert), AlertFormatter.Body(alert)), cancellationToken);
        }
        catch (MailDeliveryException ex)
        {
            throw new AlertDeliveryException(ex.Message, ex);
        }
    }
}
