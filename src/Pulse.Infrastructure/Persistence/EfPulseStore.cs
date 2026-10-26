using Microsoft.EntityFrameworkCore;
using Pulse.Application;
using Pulse.Domain;

namespace Pulse.Infrastructure.Persistence;

internal sealed class EfPulseStore(PulseDbContext db) : IPulseStore
{
    // A safety cap: a flapping service over 90 days stays far below it.
    private const int MaxOverlappingIncidents = 5000;

    public async Task AddServiceAsync(MonitoredService service, CancellationToken cancellationToken) =>
        await db.Services.AddAsync(service, cancellationToken);

    public async Task<MonitoredService?> FindServiceAsync(Guid organizationId, Guid id, CancellationToken cancellationToken) =>
        await db.Services.FirstOrDefaultAsync(s => s.Id == id && s.OrganizationId == organizationId, cancellationToken);

    public async Task<IReadOnlyList<MonitoredService>> ListServicesAsync(
        Guid organizationId,
        bool publicOnly,
        CancellationToken cancellationToken) =>
        await db.Services.AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && (!publicOnly || s.IsPublic))
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);

    public async Task<int> CountServicesAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await db.Services.CountAsync(s => s.OrganizationId == organizationId, cancellationToken);

    public async Task<IReadOnlyList<MonitoredService>> ListDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The interval differs per row, so the "is it due" rule runs in memory (the domain owns it).
        // Fine for the service counts of a small installation; revisit with an indexed NextCheckAt column.
        var all = await db.Services.ToListAsync(cancellationToken);
        return [.. all.Where(s => s.IsDue(now))];
    }

    public Task RemoveServiceAsync(MonitoredService service, CancellationToken cancellationToken)
    {
        db.Services.Remove(service);
        return Task.CompletedTask;
    }

    public async Task AddCheckAsync(CheckResult check, CancellationToken cancellationToken) =>
        await db.Checks.AddAsync(check, cancellationToken);

    public async Task<IReadOnlyList<CheckResult>> ListRecentChecksAsync(
        Guid serviceId,
        int limit,
        CancellationToken cancellationToken) =>
        await db.Checks.AsNoTracking()
            .Where(c => c.ServiceId == serviceId)
            .OrderByDescending(c => c.CheckedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, UptimeCounts>> GetUptimeAsync(
        IReadOnlyCollection<Guid> serviceIds,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var rows = await db.Checks.AsNoTracking()
            .Where(c => c.CheckedAt >= since && serviceIds.Contains(c.ServiceId))
            .GroupBy(c => c.ServiceId)
            .Select(g => new { ServiceId = g.Key, Total = g.Count(), Succeeded = g.Count(c => c.Success) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.ServiceId, r => new UptimeCounts(r.Total, r.Succeeded));
    }

    public async Task<int> PurgeChecksOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        await db.Checks.Where(c => c.CheckedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

    public async Task AddIncidentAsync(Incident incident, CancellationToken cancellationToken) =>
        await db.Incidents.AddAsync(incident, cancellationToken);

    public async Task<Incident?> FindOpenIncidentAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        // An incident added earlier in the same unit of work is not in the database yet.
        var pending = db.Incidents.Local.FirstOrDefault(i => i.ServiceId == serviceId && i.ResolvedAt is null);
        return pending ?? await db.Incidents
            .Where(i => i.ServiceId == serviceId && i.ResolvedAt == null)
            .OrderByDescending(i => i.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Incident>> ListIncidentsAsync(
        IReadOnlyCollection<Guid> serviceIds,
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken) =>
        await db.Incidents.AsNoTracking()
            .Where(i => i.StartedAt >= since && serviceIds.Contains(i.ServiceId))
            .OrderByDescending(i => i.StartedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Incident>> ListIncidentsOverlappingAsync(
        IReadOnlyCollection<Guid> serviceIds,
        DateTimeOffset since,
        CancellationToken cancellationToken) =>
        await db.Incidents.AsNoTracking()
            .Where(i => serviceIds.Contains(i.ServiceId) && (i.ResolvedAt == null || i.ResolvedAt >= since))
            .OrderBy(i => i.StartedAt)
            .Take(MaxOverlappingIncidents)
            .ToListAsync(cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken) =>
        await db.SaveChangesAsync(cancellationToken);

    public Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChanges.TryAsync(db, cancellationToken);
}

internal static class SaveChanges
{
    /// <summary>Saves; when the database refuses (conflict, unique rule), drops what was pending and says so.</summary>
    public static async Task<bool> TryAsync(PulseDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Detach only what failed to save: other loaded entities are untouched and still tracked.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }
}
