using Pulse.Application;
using Pulse.Domain;

namespace Pulse.Infrastructure.Demo;

/// <summary>
/// Fills an empty database with three example services and an invented history, so a fresh install shows
/// a lively status page. Nothing here is real data: the page says so when this ran.
/// </summary>
internal sealed class DemoDataSeeder(IPulseStore store, IAccountStore accounts, TimeProvider clock)
{
    private const int CheckEveryMinutes = 60;
    private const int CheckEveryMinutesDuringIncident = 5;
    private const int FailureThreshold = 3;

    /// <summary>An outage written as "N days ago, at this time, for this long".</summary>
    private sealed record Outage(int DaysAgo, int Hour, int Minute, int DurationMinutes, int? StatusCode, string Cause);

    private sealed record DemoService(string Name, string Url, int CreatedDaysAgo, Outage[] Outages);

    private static readonly DemoService[] Services =
    [
        new("Site vitrine", "https://example.com/", 100,
            [new Outage(61, 14, 10, 7, 503, "Expected status 200, got 503.")]),
        new("API publique", "https://example.org/", 100,
            [
                new Outage(34, 2, 30, 95, 500, "Expected status 200, got 500."),
                new Outage(12, 9, 0, 12, null, "No answer within 10 s."),
            ]),
        new("Paiements", "https://example.net/", 70,
            [new Outage(5, 16, 45, 18, null, "The server could not be reached.")]),
    ];

    /// <summary>
    /// Creates the demonstration organization (public page <c>/s/demo</c>, no owner: nobody can sign in to
    /// it) unless it exists. Returns true when it added something.
    /// </summary>
    public async Task<bool> SeedAsync(CancellationToken cancellationToken)
    {
        if (await accounts.SlugExistsAsync(DemoOrganization.Slug, cancellationToken))
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var organization = Organization.Create(DemoOrganization.Name, DemoOrganization.Slug, now.AddDays(-100));
        await accounts.AddOrganizationAsync(organization, cancellationToken);
        foreach (var demo in Services)
        {
            await SeedServiceAsync(organization.Id, demo, now, cancellationToken);
        }

        await store.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task SeedServiceAsync(Guid organizationId, DemoService demo, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var createdAt = now.AddDays(-demo.CreatedDaysAgo);
        var service = MonitoredService.Create(organizationId, demo.Name, demo.Url, 3600, 10, 200, isPublic: true, createdAt);
        await store.AddServiceAsync(service, cancellationToken);

        var windows = demo.Outages
            .Select(o =>
            {
                var day = now.AddDays(-o.DaysAgo).UtcDateTime.Date;
                var start = new DateTimeOffset(day.AddHours(o.Hour).AddMinutes(o.Minute), TimeSpan.Zero);
                return (Outage: o, Start: start, End: start.AddMinutes(o.DurationMinutes));
            })
            .ToList();

        foreach (var (outage, start, end) in windows)
        {
            var incident = Incident.Open(service.Id, start, outage.Cause);
            incident.Resolve(end);
            await store.AddIncidentAsync(incident, cancellationToken);
        }

        var index = 0;
        for (var at = createdAt; at < now.AddMinutes(-CheckEveryMinutes); at = at.AddMinutes(CheckEveryMinutes), index++)
        {
            await store.AddCheckAsync(CheckAt(service.Id, at, index, windows), cancellationToken);
        }

        // Inside an outage, checks are denser so the failure shows in the recent-checks list (it also lowers the uptime figure a little more than the downtime alone would).
        foreach (var (outage, start, end) in windows)
        {
            for (var at = start; at < end; at = at.AddMinutes(CheckEveryMinutesDuringIncident))
            {
                await store.AddCheckAsync(CheckAt(service.Id, at, 0, windows), cancellationToken);
            }
        }

        var last = CheckResult.Succeeded(service.Id, now.AddMinutes(-CheckEveryMinutes), 200, 140);
        await store.AddCheckAsync(last, cancellationToken);
        service.ApplyCheck(last, FailureThreshold);
    }

    private static CheckResult CheckAt(
        Guid serviceId,
        DateTimeOffset at,
        int index,
        List<(Outage Outage, DateTimeOffset Start, DateTimeOffset End)> windows)
    {
        var failing = windows.FirstOrDefault(w => at >= w.Start && at < w.End);
        if (failing.Outage is { } outage)
        {
            return CheckResult.Failed(serviceId, at, outage.StatusCode is null ? 10_000 : 40, outage.Cause, outage.StatusCode);
        }

        // A latency that wanders a little, the same every time (no randomness needed).
        return CheckResult.Succeeded(serviceId, at, 200, 90 + (index * 37 % 140));
    }
}
