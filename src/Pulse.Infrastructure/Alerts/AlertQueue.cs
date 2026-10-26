using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pulse.Application.Alerts;

namespace Pulse.Infrastructure.Alerts;

/// <summary>
/// An in-memory queue between the checks and the delivery. Publishing never waits: if delivery falls
/// far behind, the oldest alerts are dropped (and logged) rather than slowing the checks down.
/// Alerts waiting in the queue are lost if the process stops; see ADR 0003.
/// </summary>
internal sealed class AlertQueue : IAlertPublisher
{
    internal const int Capacity = 1000;

    private readonly Channel<AlertEvent> _channel;
    private readonly ILogger<AlertQueue> _logger;

    public AlertQueue(ILogger<AlertQueue> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<AlertEvent>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            dropped => _logger.LogWarning("Alert queue is full; dropped the oldest alert for service {ServiceId}", dropped.ServiceId));
    }

    public ChannelReader<AlertEvent> Reader => _channel.Reader;

    public void Publish(AlertEvent alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        _channel.Writer.TryWrite(alert);
    }
}
