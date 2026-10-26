using Microsoft.Extensions.Options;
using Pulse.Contracts;
using Pulse.Domain;

namespace Pulse.Application;

public sealed record ServiceInsight(double? UptimePercent, IReadOnlyList<DayDto> History);

/// <summary>The uptime figure and the day-by-day history of services, computed the same way for every screen.</summary>
public sealed class ServiceInsights(IPulseStore store, TimeProvider clock, IOptions<PulseOptions> options)
{
    public async Task<IReadOnlyDictionary<Guid, ServiceInsight>> LoadAsync(
        IReadOnlyCollection<MonitoredService> services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Count == 0)
        {
            return new Dictionary<Guid, ServiceInsight>();
        }

        var now = clock.GetUtcNow();
        var days = options.Value.RetentionDays;
        // The window starts at the beginning of the first day shown, so the oldest bar is complete.
        var since = new DateTimeOffset(DateOnly.FromDateTime(now.UtcDateTime).AddDays(1 - days).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var ids = services.Select(s => s.Id).ToList();
        var uptime = await store.GetUptimeAsync(ids, since, cancellationToken);
        var incidents = (await store.ListIncidentsOverlappingAsync(ids, since, cancellationToken)).ToLookup(i => i.ServiceId);

        return services.ToDictionary(
            s => s.Id,
            s => new ServiceInsight(
                uptime.TryGetValue(s.Id, out var counts) ? StatusQueries.UptimePercent(counts) : null,
                UptimeHistory.Build(now, days, s.CreatedAt, [.. incidents[s.Id]])));
    }
}
