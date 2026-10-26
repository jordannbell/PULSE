using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pulse.Application.Alerts;
using Pulse.Domain;
using Pulse.Infrastructure.Alerts;
using Pulse.Infrastructure.Probing;

namespace Pulse.Tests;

/// <summary>The real webhook channel against a real web server on the loopback address.</summary>
public sealed class WebhookAlertChannelTests : IAsyncLifetime
{
    private const string Secret = "0123456789abcdef-secret";

    private readonly ConcurrentQueue<(Dictionary<string, string> Headers, string Body)> _calls = new();
    private readonly List<HttpClient> _clients = [];
    private WebApplication _server = null!;
    private string _baseUrl = null!;
    private int _status = 200;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _server = builder.Build();
        _server.MapPost("/hook", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            // Copy the headers now: the request object is recycled once the response is sent.
            _calls.Enqueue((request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()), await reader.ReadToEndAsync()));
            return Results.StatusCode(_status);
        });
        await _server.StartAsync();
        _baseUrl = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    private WebhookAlertChannel Channel(WebhookFormat format = WebhookFormat.Json, string? url = null, bool allowPrivate = true)
    {
        var client = new GuardedHttp(new SystemHostResolver(), allowPrivate).CreateClient();
        _clients.Add(client);
        return new WebhookAlertChannel(client, url ?? _baseUrl + "/hook", Secret, format);
    }

    [Fact]
    public async Task An_incident_is_posted_as_json_with_the_service_and_the_cause()
    {
        await Channel().SendAsync(Alerts.Opened("Shop", "Expected status 200, got 500."), CancellationToken.None);

        var (headers, body) = Assert.Single(_calls);
        Assert.Equal("incident.opened", headers["X-Pulse-Event"]);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("incident.opened", root.GetProperty("event").GetString());
        Assert.Equal("Shop", root.GetProperty("service").GetProperty("name").GetString());
        Assert.Equal("https://shop.example/health", root.GetProperty("service").GetProperty("url").GetString());
        Assert.Equal("Expected status 200, got 500.", root.GetProperty("incident").GetProperty("cause").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("incident").GetProperty("resolvedAt").ValueKind);
    }

    [Fact]
    public async Task A_resolved_incident_carries_its_duration()
    {
        await Channel().SendAsync(Alerts.Resolved(), CancellationToken.None);

        using var json = JsonDocument.Parse(Assert.Single(_calls).Body);
        Assert.Equal("incident.resolved", json.RootElement.GetProperty("event").GetString());
        Assert.Equal(330, json.RootElement.GetProperty("incident").GetProperty("durationSeconds").GetInt64());
    }

    [Fact]
    public async Task The_signature_can_be_recomputed_by_the_receiver_from_the_secret_alone()
    {
        await Channel().SendAsync(Alerts.Opened(), CancellationToken.None);

        var (headers, body) = Assert.Single(_calls);
        var timestamp = headers["X-Pulse-Timestamp"];
        // What a receiver does: HMAC-SHA256 over "timestamp.body" with the shared secret, hex, lower case.
        var expected = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
        Assert.Equal(expected, headers["X-Pulse-Signature"]);
        Assert.Equal(Alerts.Start.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), timestamp);
    }

    [Fact]
    public async Task A_tampered_body_no_longer_matches_the_signature()
    {
        await Channel().SendAsync(Alerts.Opened(), CancellationToken.None);
        var (headers, body) = Assert.Single(_calls);

        var forged = WebhookAlertChannel.Sign(Secret, headers["X-Pulse-Timestamp"], body.Replace("Shop", "Evil", StringComparison.Ordinal));

        Assert.NotEqual(headers["X-Pulse-Signature"], forged);
    }

    [Fact]
    public async Task The_secret_is_never_sent()
    {
        await Channel().SendAsync(Alerts.Opened(), CancellationToken.None);

        var (headers, body) = Assert.Single(_calls);
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join(';', headers.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slack_gets_a_text_field()
    {
        await Channel(WebhookFormat.Slack).SendAsync(Alerts.Opened("Shop"), CancellationToken.None);

        using var json = JsonDocument.Parse(Assert.Single(_calls).Body);
        Assert.Contains("Shop", json.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discord_gets_a_content_field()
    {
        await Channel(WebhookFormat.Discord).SendAsync(Alerts.Resolved("Shop"), CancellationToken.None);

        using var json = JsonDocument.Parse(Assert.Single(_calls).Body);
        Assert.Contains("Shop", json.RootElement.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_answer_is_a_delivery_failure_that_names_the_status()
    {
        _status = 500;

        var failure = await Assert.ThrowsAsync<AlertDeliveryException>(
            () => Channel().SendAsync(Alerts.Opened(), CancellationToken.None));

        Assert.Contains("HTTP 500", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_receiver_is_a_delivery_failure_with_a_safe_message()
    {
        var failure = await Assert.ThrowsAsync<AlertDeliveryException>(
            () => Channel(url: "http://127.0.0.1:1/hook").SendAsync(Alerts.Opened(), CancellationToken.None));

        Assert.Contains("could not be reached", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_webhook_aimed_at_a_private_address_is_refused_unless_private_targets_are_allowed()
    {
        var failure = await Assert.ThrowsAsync<AlertDeliveryException>(
            () => Channel(allowPrivate: false).SendAsync(Alerts.Opened(), CancellationToken.None));

        Assert.Contains("public server", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task A_redirect_is_a_failure_and_is_not_followed()
    {
        _status = 302;

        await Assert.ThrowsAsync<AlertDeliveryException>(() => Channel().SendAsync(Alerts.Opened(), CancellationToken.None));

        Assert.Single(_calls);
    }

    [Fact]
    public void The_channel_name_tells_webhooks_apart_by_host()
    {
        Assert.Equal("Webhook (hooks.example)", Channel(url: "https://hooks.example/pulse").Name);
    }
}
