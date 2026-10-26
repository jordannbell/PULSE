using System.Security.Cryptography;
using Pulse.Contracts;
using Pulse.Domain;

namespace Pulse.Application.Accounts;

/// <summary>
/// The webhooks of one organization. The signing secret is made here, shown once in the answer to the
/// creation, and kept encrypted: no later call can read it back.
/// </summary>
public sealed class WebhookService(
    IAccountStore accounts,
    TargetValidator targets,
    ISecretProtector protector,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<WebhookDto>> ListAsync(Guid organizationId, CancellationToken cancellationToken) =>
        [.. (await accounts.ListWebhooksAsync(organizationId, cancellationToken)).Select(ToDto)];

    public async Task<Result<CreatedWebhookDto>> CreateAsync(
        Guid organizationId,
        CreateWebhookRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>();
        var url = request.Url?.Trim() ?? string.Empty;
        if (url.Length is 0 or > WebhookEndpoint.UrlMaxLength)
        {
            errors["url"] = [ApiMessages.WebhookUrlLength];
        }
        else if (await targets.ValidateAsync(url, cancellationToken) is { } targetError)
        {
            errors["url"] = [targetError];
        }

        var organization = await accounts.FindOrganizationAsync(organizationId, cancellationToken);
        if (organization is null)
        {
            return Result.Invalid<CreatedWebhookDto>(Result.CredentialsKey, ApiMessages.BadCredentials);
        }

        var limits = PlanPolicy.For(organization.Plan);
        if (errors.Count == 0 && (await accounts.ListWebhooksAsync(organizationId, cancellationToken)).Count >= limits.MaxWebhooks)
        {
            errors["webhook"] = [$"The {organization.Plan} plan allows at most {limits.MaxWebhooks} webhooks."];
        }

        if (errors.Count > 0)
        {
            return Result.Invalid<CreatedWebhookDto>(errors);
        }

        var secret = "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var webhook = WebhookEndpoint.Create(organizationId, url, protector.Protect(secret), ToDomain(request.Format), clock.GetUtcNow());
        await accounts.AddWebhookAsync(webhook, cancellationToken);
        await accounts.SaveChangesAsync(cancellationToken);
        return Result.Ok(new CreatedWebhookDto(ToDto(webhook), secret));
    }

    public async Task<bool> DeleteAsync(Guid organizationId, Guid id, CancellationToken cancellationToken)
    {
        if (await accounts.FindWebhookAsync(organizationId, id, cancellationToken) is not { } webhook)
        {
            return false;
        }

        await accounts.RemoveWebhookAsync(webhook, cancellationToken);
        await accounts.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static WebhookDto ToDto(WebhookEndpoint webhook) =>
        new(webhook.Id, webhook.Url, ToContract(webhook.Format), webhook.CreatedAt);

    private static WebhookFormat ToDomain(AlertFormat format) =>
        format switch
        {
            AlertFormat.Slack => WebhookFormat.Slack,
            AlertFormat.Discord => WebhookFormat.Discord,
            _ => WebhookFormat.Json,
        };

    private static AlertFormat ToContract(WebhookFormat format) =>
        format switch
        {
            WebhookFormat.Slack => AlertFormat.Slack,
            WebhookFormat.Discord => AlertFormat.Discord,
            _ => AlertFormat.Json,
        };
}
