using Microsoft.Extensions.DependencyInjection;
using Pulse.Application;
using Pulse.Application.Alerts;
using Pulse.Domain;

namespace Pulse.Tests;

public class CheckRunnerRaceTests
{
    [Fact]
    public async Task A_service_deleted_during_its_check_does_not_cost_the_other_services_their_results()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync();
        var doomed = await app.AddServiceAsync(org.Id, "Doomed");
        var survivor = await app.AddServiceAsync(org.Id, "Survivor");

        await using var scope = app.NewScope();
        var runner = new CheckRunner(
            scope.ServiceProvider.GetRequiredService<IPulseStore>(),
            new DeletingProbe(app, org.Id, doomed.Id),
            app.Clock,
            OptionsFactory.Create(),
            scope.ServiceProvider.GetRequiredService<IAlertPublisher>(),
            OptionsFactory.Logger<CheckRunner>());

        var ran = await runner.RunDueAsync(CancellationToken.None);

        Assert.Equal(2, ran);
        await using var verify = app.NewScope();
        var store = verify.ServiceProvider.GetRequiredService<IPulseStore>();
        Assert.Null(await store.FindServiceAsync(org.Id, doomed.Id, CancellationToken.None));
        var survivorChecks = await store.ListRecentChecksAsync(survivor.Id, 10, CancellationToken.None);
        Assert.True(Assert.Single(survivorChecks).Success);
        Assert.NotNull((await store.FindServiceAsync(org.Id, survivor.Id, CancellationToken.None))!.LastCheckedAt);
    }

    /// <summary>Deletes one service, through another scope, while its probe is "running".</summary>
    private sealed class DeletingProbe(TestApp app, Guid organizationId, Guid victim) : IServiceProbe
    {
        public async Task<CheckResult> ProbeAsync(MonitoredService service, CancellationToken cancellationToken)
        {
            if (service.Id == victim)
            {
                await using var scope = app.NewScope();
                var catalog = scope.ServiceProvider.GetRequiredService<ServiceCatalog>();
                Assert.True(await catalog.DeleteAsync(organizationId, victim, cancellationToken));
            }

            return CheckResult.Succeeded(service.Id, app.Clock.GetUtcNow(), 200, 10);
        }
    }
}
