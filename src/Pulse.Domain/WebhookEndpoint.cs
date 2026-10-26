namespace Pulse.Domain;

public enum WebhookFormat
{
    /// <summary>Pulse's own signed JSON document.</summary>
    Json = 0,

    /// <summary>{"text": "..."} for a Slack incoming webhook.</summary>
    Slack = 1,

    /// <summary>{"content": "..."} for a Discord webhook.</summary>
    Discord = 2,
}

/// <summary>
/// An address that receives an organization's alerts. The signing secret is stored encrypted: Pulse must be
/// able to read it back to sign each request, but it is never sent to the browser after it was created.
/// </summary>
public sealed class WebhookEndpoint
{
    public const int UrlMaxLength = 2048;

    private WebhookEndpoint()
    {
        Url = string.Empty;
        SecretCipher = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Url { get; private set; }

    public string SecretCipher { get; private set; }

    public WebhookFormat Format { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static WebhookEndpoint Create(Guid organizationId, string url, string secretCipher, WebhookFormat format, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(url.Length, UrlMaxLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretCipher);

        return new WebhookEndpoint
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Url = url,
            SecretCipher = secretCipher,
            Format = format,
            CreatedAt = now,
        };
    }
}
