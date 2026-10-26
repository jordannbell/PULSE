using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pulse.Application.Alerts;
using Pulse.Domain;

namespace Pulse.Application;

/// <summary>Probes the services that are due, records the results, and opens or closes incidents.</summary>
public sealed class CheckRunner(
    IPulseStore store,
    IServiceProbe probe,
    TimeProvider clock,
    IOptions<PulseOptions> options,
    IAlertPublisher alerts,
    ILogger<CheckRunner> logger)
{
    /// <summary>Runs every check that is due and returns how many were run.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = await store.ListDueAsync(now, cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        // Probes run side by side (they only use the network); results are saved one after the other
        // because the store is not safe for concurrent use.
        var results = new ConcurrentBag<(MonitoredService Service, CheckResult Check)>();
        var parallelism = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Value.MaxParallelProbes,
            CancellationToken = cancellationToken,
        };
        await Parallel.ForEachAsync(due, parallelism, async (service, ct) =>
            results.Add((service, await ProbeSafelyAsync(service, ct))));

        // One save per service: a service deleted while its probe was running fails only its own save
        // and must not cost the other services their results.
        foreach (var (service, check) in results)
        {
            var alert = await RecordAsync(service, check, cancellationToken);
            if (!await store.TrySaveChangesAsync(cancellationToken))
            {
                logger.LogWarning("Service {ServiceId} changed during its check; the result was dropped", service.Id);
                continue;
            }

            // Only what was saved is announced: an alert must never describe an incident that does not exist.
            if (alert is not null)
            {
                alerts.Publish(alert);
            }
        }

        return due.Count;
    }

    /// <summary>Deletes the check results older than the retention window.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow().AddDays(-options.Value.RetentionDays);
        return await store.PurgeChecksOlderThanAsync(cutoff, cancellationToken);
    }

    private async Task<CheckResult> ProbeSafelyAsync(MonitoredService service, CancellationToken cancellationToken)
    {
        try
        {
            return await probe.ProbeAsync(service, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe should never throw, but one broken check must not stop the others.
            logger.LogError(ex, "Probe of service {ServiceId} crashed", service.Id);
            return CheckResult.Failed(service.Id, clock.GetUtcNow(), 0, "The check could not run.");
        }
    }

    /// <summary>Records one check and returns the alert it calls for, if any (to publish once saved).</summary>
    private async Task<AlertEvent?> RecordAsync(MonitoredService service, CheckResult check, CancellationToken cancellationToken)
    {
        await store.AddCheckAsync(check, cancellationToken);

        switch (service.ApplyCheck(check, options.Value.FailureThreshold))
        {
            case StateTransition.IncidentOpened:
                var cause = check.Error ?? $"Unexpected status {check.StatusCode}.";
                var incident = Incident.Open(service.Id, check.CheckedAt, cause);
                await store.AddIncidentAsync(incident, cancellationToken);
                logger.LogWarning("Service {ServiceId} is down", service.Id);
                return ToAlert(AlertKind.IncidentOpened, service, incident, check.CheckedAt);

            case StateTransition.IncidentResolved:
                var open = await store.FindOpenIncidentAsync(service.Id, cancellationToken);
                if (open is null)
                {
                    logger.LogWarning("Service {ServiceId} came back up but had no open incident", service.Id);
                    return null;
                }

                open.Resolve(check.CheckedAt);
                logger.LogInformation("Service {ServiceId} is back up", service.Id);
                return ToAlert(AlertKind.IncidentResolved, service, open, check.CheckedAt);

            default:
                return null;
        }
    }

    private static AlertEvent ToAlert(AlertKind kind, MonitoredService service, Incident incident, DateTimeOffset at) =>
        new(kind, service.OrganizationId, service.Id, service.Name, service.Url, incident.StartedAt, incident.ResolvedAt, incident.Cause, at);
}
