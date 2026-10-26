using Microsoft.Extensions.Options;
using Pulse.Contracts;

namespace Pulse.Application;

/// <summary>Builds what the public status page of an organization shows.</summary>
public sealed class StatusQueries(
    IPulseStore store,
    IAccountStore accounts,
    ServiceInsights insights,
    TimeProvider clock,
    IOptions<PulseOptions> options)
{
    private const int MaxIncidents = 10;

    /// <summary>The page of the organization with this slug, or null when there is none.</summary>
    public async Task<PublicStatusDto?> GetPublicStatusAsync(string slug, CancellationToken cancellationToken)
    {
        var organization = await accounts.FindOrganizationBySlugAsync(slug, cancellationToken);
        if (organization is null)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var windowDays = options.Value.RetentionDays;
        var since = now.AddDays(-windowDays);

        // Only the services the owner chose to show: the others stay private.
        var services = await store.ListServicesAsync(organization.Id, publicOnly: true, cancellationToken);
        var insight = await insights.LoadAsync(services, cancellationToken);
        var incidents = await store.ListIncidentsAsync([.. services.Select(s => s.Id)], since, MaxIncidents, cancellationToken);

        var names = services.ToDictionary(s => s.Id, s => s.Name);
        var publicServices = services
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new PublicServiceDto(s.Name, s.State.ToStatus(), insight[s.Id].UptimePercent, insight[s.Id].History))
            .ToList();
        var publicIncidents = incidents
            .Where(i => names.ContainsKey(i.ServiceId))
            .Select(i => new IncidentDto(names[i.ServiceId], i.StartedAt, i.ResolvedAt, i.Cause))
            .ToList();

        return new PublicStatusDto(
            organization.Name,
            organization.Slug,
            Overall(publicServices.Select(s => s.Status).ToList()),
            now,
            windowDays,
            publicServices,
            publicIncidents,
            IsDemo: string.Equals(organization.Slug, DemoOrganization.Slug, StringComparison.Ordinal));
    }

    internal static double? UptimePercent(UptimeCounts counts) =>
        counts.Total == 0 ? null : Math.Round(100.0 * counts.Succeeded / counts.Total, 2);

    internal static OverallStatus Overall(IReadOnlyCollection<ServiceStatus> statuses)
    {
        var down = statuses.Count(s => s == ServiceStatus.Down);
        if (statuses.Count == 0 || statuses.All(s => s == ServiceStatus.Unknown))
        {
            return OverallStatus.Unknown;
        }

        if (down == statuses.Count)
        {
            return OverallStatus.MajorOutage;
        }

        return down > 0 ? OverallStatus.PartialOutage : OverallStatus.Operational;
    }
}

/// <summary>The example organization that holds the demonstration data.</summary>
public static class DemoOrganization
{
    public const string Slug = "demo";
    public const string Name = "Pulse (démonstration)";
}
