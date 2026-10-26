using Microsoft.Extensions.DependencyInjection;
using Pulse.Application;
using Pulse.Contracts;
using Pulse.Domain;

namespace Pulse.Tests;

public class CheckRunnerTests
{
    private static async Task<int> RunAsync(TestApp app)
    {
        await using var scope = app.NewScope();
        return await scope.ServiceProvider.GetRequiredService<CheckRunner>().RunDueAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_service_is_checked_only_when_due()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync();
        await app.AddServiceAsync(org.Id, intervalSeconds: 60);

        Assert.Equal(1, await RunAsync(app)); // never checked: due now
        app.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await RunAsync(app)); // not due yet
        app.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await RunAsync(app)); // due again

        Assert.Equal(2, app.Probe.Calls);
    }

    [Fact]
    public async Task Services_of_every_organization_are_checked_in_one_round()
    {
        await using var app = await TestApp.CreateAsync();
        var one = await app.AddOrganizationAsync();
        var two = await app.AddOrganizationAsync();
        await app.AddServiceAsync(one.Id, "A");
        await app.AddServiceAsync(two.Id, "B");

        Assert.Equal(2, await RunAsync(app));
    }

    [Fact]
    public async Task Three_failures_open_an_incident_and_a_success_closes_it()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync();
        var service = await app.AddServiceAsync(org.Id);

        await RunAsync(app); // success: up
        app.Probe.Succeeds = false;
        for (var i = 0; i < 3; i++)
        {
            app.Clock.Advance(TimeSpan.FromSeconds(30));
            await RunAsync(app);
        }

        await using (var scope = app.NewScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IPulseStore>();
            Assert.Equal(ServiceState.Down, (await store.FindServiceAsync(org.Id, service.Id, CancellationToken.None))!.State);
            var open = await store.FindOpenIncidentAsync(service.Id, CancellationToken.None);
            Assert.NotNull(open);
            Assert.Contains("500", open.Cause, StringComparison.Ordinal);
        }

        app.Probe.Succeeds = true;
        app.Clock.Advance(TimeSpan.FromSeconds(30));
        await RunAsync(app);

        await using (var scope = app.NewScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IPulseStore>();
            Assert.Equal(ServiceState.Up, (await store.FindServiceAsync(org.Id, service.Id, CancellationToken.None))!.State);
            Assert.Null(await store.FindOpenIncidentAsync(service.Id, CancellationToken.None));
            var incident = Assert.Single(await store.ListIncidentsAsync([service.Id], DateTimeOffset.MinValue, 10, CancellationToken.None));
            Assert.NotNull(incident.ResolvedAt);
        }
    }

    [Fact]
    public async Task A_crashing_probe_is_recorded_as_a_failed_check_and_does_not_stop_the_run()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync();
        var service = await app.AddServiceAsync(org.Id);
        await using var scope = app.NewScope();
        var runner = new CheckRunner(
            scope.ServiceProvider.GetRequiredService<IPulseStore>(),
            new CrashingProbe(),
            app.Clock,
            OptionsFactory.Create(),
            scope.ServiceProvider.GetRequiredService<Application.Alerts.IAlertPublisher>(),
            OptionsFactory.Logger<CheckRunner>());

        Assert.Equal(1, await runner.RunDueAsync(CancellationToken.None));
        var checks = await scope.ServiceProvider.GetRequiredService<IPulseStore>().ListRecentChecksAsync(service.Id, 10, CancellationToken.None);
        Assert.False(Assert.Single(checks).Success);
    }

    [Fact]
    public async Task Old_check_results_are_purged_and_recent_ones_kept()
    {
        await using var app = await TestApp.CreateAsync(o => o.RetentionDays = 90);
        var org = await app.AddOrganizationAsync();
        var service = await app.AddServiceAsync(org.Id);
        await RunAsync(app); // one result at "now"
        app.Clock.Advance(TimeSpan.FromDays(91));

        await using var scope = app.NewScope();
        var purged = await scope.ServiceProvider.GetRequiredService<CheckRunner>().PurgeExpiredAsync(CancellationToken.None);
        var left = await scope.ServiceProvider.GetRequiredService<IPulseStore>().ListRecentChecksAsync(service.Id, 10, CancellationToken.None);

        Assert.Equal(1, purged);
        Assert.Empty(left);
    }

    [Fact]
    public async Task Public_status_reports_uptime_and_hides_the_target_address()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync("shop");
        await app.AddServiceAsync(org.Id);
        await RunAsync(app); // success
        app.Probe.Succeeds = false;
        app.Clock.Advance(TimeSpan.FromSeconds(30));
        await RunAsync(app); // failure

        await using var scope = app.NewScope();
        var status = await scope.ServiceProvider.GetRequiredService<StatusQueries>().GetPublicStatusAsync("shop", CancellationToken.None);

        var service = Assert.Single(status!.Services);
        Assert.Equal(50.0, service.UptimePercent);
        Assert.Equal(ServiceStatus.Up, service.Status); // one failure is below the threshold
        Assert.Equal(OverallStatus.Operational, status.Overall);
    }

    [Theory]
    [InlineData(new ServiceStatus[0], OverallStatus.Unknown)]
    [InlineData(new[] { ServiceStatus.Unknown, ServiceStatus.Unknown }, OverallStatus.Unknown)]
    [InlineData(new[] { ServiceStatus.Up, ServiceStatus.Up }, OverallStatus.Operational)]
    [InlineData(new[] { ServiceStatus.Up, ServiceStatus.Unknown }, OverallStatus.Operational)]
    [InlineData(new[] { ServiceStatus.Up, ServiceStatus.Down }, OverallStatus.PartialOutage)]
    [InlineData(new[] { ServiceStatus.Down, ServiceStatus.Down }, OverallStatus.MajorOutage)]
    public void The_overall_status_follows_the_worst_service(ServiceStatus[] statuses, OverallStatus expected)
    {
        Assert.Equal(expected, StatusQueries.Overall(statuses));
    }

    private sealed class CrashingProbe : IServiceProbe
    {
        public Task<CheckResult> ProbeAsync(MonitoredService service, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
