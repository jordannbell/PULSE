using Pulse.Contracts;
using Pulse.Domain;

namespace Pulse.Application;

/// <summary>
/// Turns a service's incidents into one entry per UTC day, oldest first, ending with today. This is what
/// the 90-day strip on the status page draws.
/// </summary>
public static class UptimeHistory
{
    /// <summary>Total downtime in a day from which the day counts as an outage rather than a degraded day.</summary>
    public static readonly TimeSpan OutageThreshold = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<DayDto> Build(
        DateTimeOffset now,
        int days,
        DateTimeOffset serviceCreatedAt,
        IReadOnlyCollection<Incident> incidents)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        ArgumentNullException.ThrowIfNull(incidents);

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var firstDay = DateOnly.FromDateTime(serviceCreatedAt.UtcDateTime);
        var history = new List<DayDto>(days);

        for (var back = days - 1; back >= 0; back--)
        {
            var day = today.AddDays(-back);
            history.Add(day < firstDay ? new DayDto(day, DayLevel.NoData, 0, 0) : Summarize(day, now, incidents));
        }

        return history;
    }

    private static DayDto Summarize(DateOnly day, DateTimeOffset now, IReadOnlyCollection<Incident> incidents)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = start.AddDays(1);

        var count = 0;
        var downtime = TimeSpan.Zero;
        foreach (var incident in incidents)
        {
            // An incident still open lasts until now; one that spans midnight counts in each day it touches.
            var from = incident.StartedAt > start ? incident.StartedAt : start;
            var until = incident.ResolvedAt ?? now;
            var to = until < end ? until : end;
            var overlaps = from < to || (incident.StartedAt >= start && incident.StartedAt < end);
            if (!overlaps)
            {
                continue;
            }

            count++;
            downtime += to > from ? to - from : TimeSpan.Zero;
        }

        if (count == 0)
        {
            return new DayDto(day, DayLevel.Operational, 0, 0);
        }

        var level = downtime >= OutageThreshold ? DayLevel.Outage : DayLevel.Degraded;
        var minutes = Math.Max(1, (int)Math.Ceiling(downtime.TotalMinutes));
        return new DayDto(day, level, count, minutes);
    }
}
