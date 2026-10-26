using Pulse.Contracts;
using Pulse.Domain;

namespace Pulse.Application;

internal static class ServiceMapping
{
    public static ServiceDto ToDto(this MonitoredService service, ServiceInsight? insight = null) =>
        new(
            service.Id,
            service.Name,
            service.Url,
            service.IntervalSeconds,
            service.TimeoutSeconds,
            service.ExpectedStatusCode,
            service.State.ToStatus(),
            service.ConsecutiveFailures,
            service.LastCheckedAt,
            insight?.UptimePercent,
            insight?.History,
            service.IsPublic);

    public static ServiceStatus ToStatus(this ServiceState state) =>
        state switch
        {
            ServiceState.Up => ServiceStatus.Up,
            ServiceState.Down => ServiceStatus.Down,
            _ => ServiceStatus.Unknown,
        };
}
