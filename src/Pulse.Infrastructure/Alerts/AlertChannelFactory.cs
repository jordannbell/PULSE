using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Pulse.Application;
using Pulse.Application.Alerts;

namespace Pulse.Infrastructure.Alerts;

/// <summary>
/// Builds the channels one organization has set up: its webhooks, and an e-mail to the owner. The owner's
/// address receives alerts only once it is confirmed, so nobody can send alerts to someone else's mailbox.
/// </summary>
internal sealed class AlertChannelFactory(
    IAccountStore accounts,
    ISecretProtector protector,
    IMailSender mail,
    WebhookHttp http,
    ILogger<AlertChannelFactory> logger) : IAlertChannelFactory
{
    public async Task<IReadOnlyList<IAlertChannel>> CreateAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var organization = await accounts.FindOrganizationAsync(organizationId, cancellationToken);
        if (organization is null)
        {
            return [];
        }

        var channels = new List<IAlertChannel>();
        if (organization.EmailAlerts
            && mail.IsAvailable
            && await accounts.FindOwnerAsync(organization.Id, cancellationToken) is { IsEmailVerified: true } owner)
        {
            channels.Add(new EmailAlertChannel(mail, owner.Email));
        }

        foreach (var webhook in await accounts.ListWebhooksAsync(organization.Id, cancellationToken))
        {
            try
            {
                channels.Add(new WebhookAlertChannel(http.Client, webhook.Url, protector.Unprotect(webhook.SecretCipher), webhook.Format));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Wrong encryption key or damaged value: skip this webhook, never the whole alert.
                logger.LogWarning("The secret of webhook {WebhookId} cannot be read (was the encryption key changed?)", webhook.Id);
            }
        }

        return channels;
    }
}
