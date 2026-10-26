using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pulse.Application;
using Pulse.Application.Alerts;
using Pulse.Domain;
using Pulse.Infrastructure.Probing;

namespace Pulse.Infrastructure.Alerts;

/// <summary>The one HTTP client shared by every webhook: it cannot be turned against the host's own network.</summary>
internal sealed class WebhookHttp : IDisposable
{
    public WebhookHttp(IHostResolver resolver, IOptions<PulseOptions> options) =>
        Client = new GuardedHttp(resolver, options.Value.AllowPrivateTargets).CreateClient();

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Posts the alert to one web address. Every request is signed so the receiver can tell it comes from
/// Pulse: header <c>X-Pulse-Signature: sha256=HEX</c>, where HEX is HMAC-SHA256 of
/// <c>{X-Pulse-Timestamp}.{body}</c> with the shared secret. The timestamp lets the receiver reject replays.
/// </summary>
internal sealed class WebhookAlertChannel(HttpClient client, string url, string secret, WebhookFormat format) : IAlertChannel
{
    internal const string SignatureHeader = "X-Pulse-Signature";
    internal const string TimestampHeader = "X-Pulse-Timestamp";
    internal const string EventHeader = "X-Pulse-Event";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"Webhook ({uri.Host})" : "Webhook";

    public async Task SendAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var body = BuildBody(alert, format);
        var timestamp = alert.SentAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(EventHeader, AlertFormatter.EventName(alert.Kind));
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(SignatureHeader, Sign(secret, timestamp, body));
        request.Headers.UserAgent.ParseAdd("PulseBot/0.1");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new AlertDeliveryException($"The webhook answered HTTP {(int)response.StatusCode}.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AlertDeliveryException("The webhook did not answer within 10 s.");
        }
        catch (HttpRequestException ex)
        {
            throw new AlertDeliveryException($"The webhook failed: {GuardedHttp.Describe(ex)}", ex);
        }
    }

    /// <summary>The signature a receiver must compute to accept the request.</summary>
    internal static string Sign(string secret, string timestamp, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    internal static string BuildBody(AlertEvent alert, WebhookFormat format) =>
        format switch
        {
            WebhookFormat.Slack => JsonSerializer.Serialize(new { text = AlertFormatter.Headline(alert) }, Json),
            WebhookFormat.Discord => JsonSerializer.Serialize(new { content = AlertFormatter.Headline(alert) }, Json),
            _ => JsonSerializer.Serialize(
                new
                {
                    @event = AlertFormatter.EventName(alert.Kind),
                    sentAt = alert.SentAt,
                    service = new { id = alert.ServiceId, name = alert.ServiceName, url = alert.ServiceUrl },
                    incident = new
                    {
                        startedAt = alert.IncidentStartedAt,
                        resolvedAt = alert.IncidentResolvedAt,
                        durationSeconds = alert.Duration is { } d ? (long?)d.TotalSeconds : null,
                        cause = alert.Cause,
                    },
                    message = AlertFormatter.Headline(alert),
                },
                Json),
        };
}
