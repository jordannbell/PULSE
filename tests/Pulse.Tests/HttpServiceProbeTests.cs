using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Pulse.Application;
using Pulse.Domain;
using Pulse.Infrastructure.Probing;

namespace Pulse.Tests;

/// <summary>Runs the real probe against a small web server started on the loopback address.</summary>
public sealed class HttpServiceProbeTests : IAsyncLifetime
{
    private WebApplication _server = null!;
    private string _baseUrl = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _server = builder.Build();
        _server.MapGet("/ok", () => Results.Ok("fine"));
        _server.MapGet("/broken", () => Results.StatusCode(500));
        _server.MapGet("/moved", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
        _server.MapGet("/slow", async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Results.Ok();
        });
        await _server.StartAsync();
        _baseUrl = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    private static HttpServiceProbe CreateProbe(bool allowPrivate) =>
        new(new SystemHostResolver(), new FakeTimeProvider(), OptionsFactory.Create(o => o.AllowPrivateTargets = allowPrivate));

    private MonitoredService Service(string path, int expected = 200, int timeout = 5) =>
        MonitoredService.Create(Guid.NewGuid(), "Local", _baseUrl + path, 30, timeout, expected, true, DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_healthy_service_gives_a_successful_check_with_its_latency()
    {
        using var probe = CreateProbe(allowPrivate: true);

        var check = await probe.ProbeAsync(Service("/ok"), CancellationToken.None);

        Assert.True(check.Success);
        Assert.Equal(200, check.StatusCode);
        Assert.InRange(check.LatencyMs, 0, 5_000);
    }

    [Fact]
    public async Task An_unexpected_status_is_a_failed_check_that_names_both_codes()
    {
        using var probe = CreateProbe(allowPrivate: true);

        var check = await probe.ProbeAsync(Service("/broken"), CancellationToken.None);

        Assert.False(check.Success);
        Assert.Equal(500, check.StatusCode);
        Assert.Contains("200", check.Error, StringComparison.Ordinal);
        Assert.Contains("500", check.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_expected_status_can_be_set_per_service()
    {
        using var probe = CreateProbe(allowPrivate: true);

        var check = await probe.ProbeAsync(Service("/broken", expected: 500), CancellationToken.None);

        Assert.True(check.Success);
    }

    [Fact]
    public async Task A_redirect_is_reported_and_never_followed()
    {
        using var probe = CreateProbe(allowPrivate: true);

        var check = await probe.ProbeAsync(Service("/moved"), CancellationToken.None);

        Assert.False(check.Success);
        Assert.Equal(302, check.StatusCode);
    }

    [Fact]
    public async Task A_slow_service_fails_when_the_timeout_is_reached()
    {
        using var probe = CreateProbe(allowPrivate: true);

        var check = await probe.ProbeAsync(Service("/slow", timeout: 1), CancellationToken.None);

        Assert.False(check.Success);
        Assert.Contains("1 s", check.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_private_address_is_refused_at_connection_time_unless_allowed()
    {
        using var probe = CreateProbe(allowPrivate: false);

        var check = await probe.ProbeAsync(Service("/ok"), CancellationToken.None);

        Assert.False(check.Success);
        Assert.Equal(HttpServiceProbe.BlockedMessage, check.Error);
    }

    [Fact]
    public async Task A_closed_port_is_a_failed_check_with_a_short_message()
    {
        using var probe = CreateProbe(allowPrivate: true);
        var service = MonitoredService.Create(Guid.NewGuid(), "Closed", "http://127.0.0.1:1/", 30, 3, 200, true, DateTimeOffset.UtcNow);

        var check = await probe.ProbeAsync(service, CancellationToken.None);

        Assert.False(check.Success);
        Assert.Equal("The server could not be reached.", check.Error);
    }

    [Fact]
    public async Task Cancelling_the_run_cancels_the_probe_instead_of_recording_a_failure()
    {
        using var probe = CreateProbe(allowPrivate: true);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.ProbeAsync(Service("/slow", timeout: 30), cts.Token));
    }
}
