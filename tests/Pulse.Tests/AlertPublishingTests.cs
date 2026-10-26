using Microsoft.Extensions.DependencyInjection;
using Pulse.Application;
using Pulse.Application.Alerts;
using Pulse.Domain;
using Pulse.Infrastructure.Alerts;

namespace Pulse.Tests;

/// <summary>When, and only when, a check calls for an alert, and for which organization.</summary>
public class AlertPublishingTests
{
    private static async Task RunAsync(TestApp app, bool succeeds)
    {
        app.Probe.Succeeds = succeeds;
        await using var scope = app.NewScope();
        await scope.ServiceProvider.GetRequiredService<CheckRunner>().RunDueAsync(CancellationToken.None);
        app.Clock.Advance(TimeSpan.FromSeconds(30));
    }

    private static List<AlertEvent> Drain(TestApp app)
    {
        var reader = app.Services.GetRequiredService<AlertQueue>().Reader;
        var events = new List<AlertEvent>();
        while (reader.TryRead(out var alert))
        {
            events.Add(alert);
        }

        return events;
    }

    [Fact]
    public async Task A_steady_service_raises_no_alert()
    {
        await using var app = await TestApp.CreateAsync();
        await app.AddServiceAsync((await app.AddOrganizationAsync()).Id);

        for (var i = 0; i < 4; i++)
        {
            await RunAsync(app, succeeds: true);
        }

        Assert.Empty(Drain(app));
    }

    [Fact]
    public async Task Failures_below_the_threshold_raise_no_alert()
    {
        await using var app = await TestApp.CreateAsync();
        await app.AddServiceAsync((await app.AddOrganizationAsync()).Id);
        await RunAsync(app, succeeds: true);

        await RunAsync(app, succeeds: false);
        await RunAsync(app, succeeds: false);

        Assert.Empty(Drain(app));
    }

    [Fact]
    public async Task The_failure_that_opens_an_incident_raises_exactly_one_alert_for_the_owning_organization()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync();
        var service = await app.AddServiceAsync(org.Id, "Shop");
        await RunAsync(app, succeeds: true);

        for (var i = 0; i < 6; i++)
        {
            await RunAsync(app, succeeds: false); // threshold is 3: the 3rd opens, the next three only extend
        }

        var alert = Assert.Single(Drain(app));
        Assert.Equal(AlertKind.IncidentOpened, alert.Kind);
        Assert.Equal(org.Id, alert.OrganizationId);
        Assert.Equal(service.Id, alert.ServiceId);
        Assert.Equal("Shop", alert.ServiceName);
        Assert.Equal("https://example.com/", alert.ServiceUrl);
        Assert.Contains("500", alert.Cause, StringComparison.Ordinal);
        Assert.Null(alert.IncidentResolvedAt);
    }

    [Fact]
    public async Task Each_alert_names_the_organization_of_its_own_service()
    {
        await using var app = await TestApp.CreateAsync();
        var one = await app.AddOrganizationAsync();
        var two = await app.AddOrganizationAsync();
        await app.AddServiceAsync(one.Id, "A");
        await app.AddServiceAsync(two.Id, "B");
        await RunAsync(app, succeeds: true);

        for (var i = 0; i < 3; i++)
        {
            await RunAsync(app, succeeds: false);
        }

        var alerts = Drain(app);
        Assert.Equal(2, alerts.Count);
        Assert.Equal(one.Id, alerts.Single(a => a.ServiceName == "A").OrganizationId);
        Assert.Equal(two.Id, alerts.Single(a => a.ServiceName == "B").OrganizationId);
    }

    [Fact]
    public async Task The_recovery_raises_one_resolved_alert_with_the_incident_times()
    {
        await using var app = await TestApp.CreateAsync();
        await app.AddServiceAsync((await app.AddOrganizationAsync()).Id);
        await RunAsync(app, succeeds: true);
        for (var i = 0; i < 3; i++)
        {
            await RunAsync(app, succeeds: false);
        }

        Drain(app); // the "opened" alert
        await RunAsync(app, succeeds: true);

        var alert = Assert.Single(Drain(app));
        Assert.Equal(AlertKind.IncidentResolved, alert.Kind);
        Assert.NotNull(alert.IncidentResolvedAt);
        Assert.True(alert.IncidentResolvedAt >= alert.IncidentStartedAt);
        Assert.True(alert.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task Two_outages_raise_two_pairs_of_alerts()
    {
        await using var app = await TestApp.CreateAsync();
        await app.AddServiceAsync((await app.AddOrganizationAsync()).Id);

        await RunAsync(app, succeeds: true);
        for (var outage = 0; outage < 2; outage++)
        {
            for (var i = 0; i < 3; i++)
            {
                await RunAsync(app, succeeds: false);
            }

            await RunAsync(app, succeeds: true);
        }

        Assert.Equal(
            [AlertKind.IncidentOpened, AlertKind.IncidentResolved, AlertKind.IncidentOpened, AlertKind.IncidentResolved],
            Drain(app).Select(a => a.Kind));
    }

    [Fact]
    public async Task A_service_deleted_during_the_check_that_would_open_its_incident_raises_no_alert()
    {
        await using var app = await TestApp.CreateAsync(o => o.FailureThreshold = 1);
        var org = await app.AddOrganizationAsync();
        var service = await app.AddServiceAsync(org.Id);
        app.Probe.Succeeds = false;

        await using var scope = app.NewScope();
        var runner = new CheckRunner(
            scope.ServiceProvider.GetRequiredService<IPulseStore>(),
            new DeletingProbe(app, org.Id, service.Id),
            app.Clock,
            OptionsFactory.Create(o => o.FailureThreshold = 1),
            scope.ServiceProvider.GetRequiredService<IAlertPublisher>(),
            OptionsFactory.Logger<CheckRunner>());

        await runner.RunDueAsync(CancellationToken.None);

        Assert.Empty(Drain(app)); // an alert must never announce an incident that was not saved
    }

    private sealed class DeletingProbe(TestApp app, Guid organizationId, Guid victim) : IServiceProbe
    {
        public async Task<CheckResult> ProbeAsync(MonitoredService service, CancellationToken cancellationToken)
        {
            await using var scope = app.NewScope();
            await scope.ServiceProvider.GetRequiredService<ServiceCatalog>().DeleteAsync(organizationId, victim, cancellationToken);
            return CheckResult.Failed(service.Id, app.Clock.GetUtcNow(), 5, "Expected status 200, got 500.", 500);
        }
    }
}
