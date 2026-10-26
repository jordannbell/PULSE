using Microsoft.Extensions.DependencyInjection;
using Pulse.Application;
using Pulse.Contracts;
using Pulse.Domain;
using Pulse.Infrastructure.Demo;

namespace Pulse.Tests;

public class UptimeHistoryTests
{
    // Mid-day, so "today" has hours before and after.
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = Now.AddDays(-200);

    private static Incident Closed(DateTimeOffset start, TimeSpan duration)
    {
        var incident = Incident.Open(Guid.NewGuid(), start, "boom");
        incident.Resolve(start + duration);
        return incident;
    }

    private static DayDto DayOf(IReadOnlyList<DayDto> history, int daysAgo) =>
        history.Single(d => d.Date == DateOnly.FromDateTime(Now.AddDays(-daysAgo).UtcDateTime));

    [Fact]
    public void The_history_has_one_entry_per_day_oldest_first_and_ends_today()
    {
        var history = UptimeHistory.Build(Now, 90, LongAgo, []);

        Assert.Equal(90, history.Count);
        Assert.Equal(new DateOnly(2026, 6, 15), history[^1].Date);
        Assert.Equal(new DateOnly(2026, 6, 15).AddDays(-89), history[0].Date);
        Assert.Equal(history.Select(h => h.Date).OrderBy(d => d), history.Select(h => h.Date));
        Assert.All(history, d => Assert.Equal(DayLevel.Operational, d.Level));
    }

    [Fact]
    public void Days_before_the_service_existed_have_no_data_and_its_first_day_counts()
    {
        var createdTenDaysAgo = Now.AddDays(-10).AddHours(-3);

        var history = UptimeHistory.Build(Now, 90, createdTenDaysAgo, []);

        Assert.Equal(DayLevel.NoData, DayOf(history, 11).Level);
        Assert.Equal(DayLevel.Operational, DayOf(history, 10).Level);
        Assert.Equal(79, history.Count(d => d.Level == DayLevel.NoData));
    }

    [Fact]
    public void A_short_incident_makes_a_degraded_day_and_reports_its_minutes()
    {
        var incident = Closed(Now.AddDays(-3).AddHours(-2), TimeSpan.FromMinutes(10));

        var day = DayOf(UptimeHistory.Build(Now, 90, LongAgo, [incident]), 3);

        Assert.Equal(DayLevel.Degraded, day.Level);
        Assert.Equal(1, day.Incidents);
        Assert.Equal(10, day.DowntimeMinutes);
    }

    [Fact]
    public void Thirty_minutes_of_downtime_make_an_outage_day()
    {
        var below = Closed(Now.AddDays(-4).AddHours(-2), TimeSpan.FromMinutes(29));
        var exactly = Closed(Now.AddDays(-5).AddHours(-2), TimeSpan.FromMinutes(30));

        var history = UptimeHistory.Build(Now, 90, LongAgo, [below, exactly]);

        Assert.Equal(DayLevel.Degraded, DayOf(history, 4).Level);
        Assert.Equal(DayLevel.Outage, DayOf(history, 5).Level);
    }

    [Fact]
    public void Several_incidents_in_one_day_add_up()
    {
        var morning = Closed(Now.AddDays(-2).AddHours(-8), TimeSpan.FromMinutes(20));
        var evening = Closed(Now.AddDays(-2).AddHours(3), TimeSpan.FromMinutes(15));

        var day = DayOf(UptimeHistory.Build(Now, 90, LongAgo, [morning, evening]), 2);

        Assert.Equal(2, day.Incidents);
        Assert.Equal(35, day.DowntimeMinutes);
        Assert.Equal(DayLevel.Outage, day.Level);
    }

    [Fact]
    public void An_incident_across_midnight_counts_in_each_day_for_its_own_part()
    {
        var midnight = new DateTimeOffset(Now.AddDays(-3).UtcDateTime.Date, TimeSpan.Zero);
        var incident = Closed(midnight.AddMinutes(-20), TimeSpan.FromMinutes(50)); // 23:40 to 00:30

        var history = UptimeHistory.Build(Now, 90, LongAgo, [incident]);

        var before = history.Single(d => d.Date == DateOnly.FromDateTime(midnight.AddDays(-1).UtcDateTime));
        var after = history.Single(d => d.Date == DateOnly.FromDateTime(midnight.UtcDateTime));
        Assert.Equal(20, before.DowntimeMinutes);
        Assert.Equal(30, after.DowntimeMinutes);
        Assert.Equal(1, before.Incidents);
        Assert.Equal(1, after.Incidents);
    }

    [Fact]
    public void An_incident_still_open_lasts_until_now()
    {
        var open = Incident.Open(Guid.NewGuid(), Now.AddHours(-2), "down");

        var today = UptimeHistory.Build(Now, 90, LongAgo, [open])[^1];

        Assert.Equal(120, today.DowntimeMinutes);
        Assert.Equal(DayLevel.Outage, today.Level);
    }

    [Fact]
    public void An_incident_older_than_the_window_does_not_appear()
    {
        var old = Closed(Now.AddDays(-120), TimeSpan.FromHours(3));

        var history = UptimeHistory.Build(Now, 90, LongAgo, [old]);

        Assert.All(history, d => Assert.Equal(DayLevel.Operational, d.Level));
    }

    [Fact]
    public void A_blip_shorter_than_a_minute_still_counts_as_one_minute()
    {
        var blip = Closed(Now.AddDays(-1).AddHours(-1), TimeSpan.FromSeconds(10));

        var day = DayOf(UptimeHistory.Build(Now, 90, LongAgo, [blip]), 1);

        Assert.Equal(DayLevel.Degraded, day.Level);
        Assert.Equal(1, day.DowntimeMinutes);
    }

    private static DemoDataSeeder SeederOf(IServiceProvider services, TestApp app) =>
        new(services.GetRequiredService<IPulseStore>(), services.GetRequiredService<IAccountStore>(), app.Clock);

    [Fact]
    public async Task The_demo_data_creates_the_demo_organization_once_with_a_believable_history()
    {
        await using var app = await TestApp.CreateAsync();
        await using (var scope = app.NewScope())
        {
            var seeder = SeederOf(scope.ServiceProvider, app);
            Assert.True(await seeder.SeedAsync(CancellationToken.None));
            Assert.False(await seeder.SeedAsync(CancellationToken.None)); // never twice
        }

        await using var read = app.NewScope();
        var organization = await read.ServiceProvider.GetRequiredService<IAccountStore>().FindOrganizationBySlugAsync(DemoOrganization.Slug, CancellationToken.None);
        var services = await read.ServiceProvider.GetRequiredService<ServiceCatalog>().ListAsync(organization!.Id, CancellationToken.None);

        Assert.Equal(["API publique", "Paiements", "Site vitrine"], services.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.All(services, s =>
        {
            Assert.Equal(ServiceStatus.Up, s.Status);
            Assert.Equal(90, s.History!.Count);
            Assert.InRange(s.UptimePercent!.Value, 98, 100);
        });

        var api = services.Single(s => s.Name == "API publique");
        Assert.Contains(api.History!, d => d.Level == DayLevel.Outage);
        Assert.Contains(api.History!, d => d.Level == DayLevel.Degraded);
        Assert.True(api.UptimePercent < 100);

        var payments = services.Single(s => s.Name == "Paiements");
        Assert.True(payments.History!.Count(d => d.Level == DayLevel.NoData) >= 15); // created later than the others
    }

    [Fact]
    public async Task Nobody_can_sign_in_to_the_demo_organization_and_it_does_not_touch_real_customers()
    {
        await using var app = await TestApp.CreateAsync();
        var customer = await app.AddOrganizationAsync("customer");
        await app.AddServiceAsync(customer.Id, "Real");
        await using (var scope = app.NewScope())
        {
            await SeederOf(scope.ServiceProvider, app).SeedAsync(CancellationToken.None);
        }

        await using var read = app.NewScope();
        var accounts = read.ServiceProvider.GetRequiredService<IAccountStore>();
        var demo = await accounts.FindOrganizationBySlugAsync(DemoOrganization.Slug, CancellationToken.None);
        Assert.Null(await accounts.FindOwnerAsync(demo!.Id, CancellationToken.None));
        var realServices = await read.ServiceProvider.GetRequiredService<ServiceCatalog>().ListAsync(customer.Id, CancellationToken.None);
        Assert.Equal(["Real"], realServices.Select(s => s.Name));
    }

    [Fact]
    public async Task The_public_page_of_the_demo_is_flagged_and_carries_the_history_of_every_service()
    {
        await using var app = await TestApp.CreateAsync();
        await using (var scope = app.NewScope())
        {
            await SeederOf(scope.ServiceProvider, app).SeedAsync(CancellationToken.None);
        }

        await using var read = app.NewScope();
        var status = await read.ServiceProvider.GetRequiredService<StatusQueries>().GetPublicStatusAsync(DemoOrganization.Slug, CancellationToken.None);

        Assert.True(status!.IsDemo);
        Assert.Equal(OverallStatus.Operational, status.Overall);
        Assert.All(status.Services, s => Assert.Equal(90, s.History.Count));
        Assert.NotEmpty(status.Incidents);
    }
}
