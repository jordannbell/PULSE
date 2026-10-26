using Pulse.Application.Alerts;

namespace Pulse.Tests;

/// <summary>A channel that keeps everything it is given.</summary>
internal sealed class RecordingChannel(string name = "Recorder") : IAlertChannel
{
    private readonly List<AlertEvent> _received = [];
    private readonly object _lock = new();

    public string Name { get; } = name;

    public IReadOnlyList<AlertEvent> Received
    {
        get
        {
            lock (_lock)
            {
                return [.. _received];
            }
        }
    }

    public Task SendAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _received.Add(alert);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A channel that fails a set number of times, then works.</summary>
internal sealed class FlakyChannel(int failuresBeforeSuccess, string name = "Flaky") : IAlertChannel
{
    private int _calls;

    public string Name { get; } = name;

    public int Calls => Volatile.Read(ref _calls);

    public List<AlertEvent> Delivered { get; } = [];

    public Task SendAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _calls) <= failuresBeforeSuccess)
        {
            throw new AlertDeliveryException("Simulated failure.");
        }

        lock (Delivered)
        {
            Delivered.Add(alert);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Hands every organization the same fixed channels.</summary>
internal sealed class FixedChannelFactory(params IAlertChannel[] channels) : IAlertChannelFactory
{
    public Task<IReadOnlyList<IAlertChannel>> CreateAsync(Guid organizationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IAlertChannel>>(channels);
}

internal static class Alerts
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static AlertEvent Opened(string name = "Shop", string cause = "Expected status 200, got 500.", Guid? organizationId = null) =>
        new(AlertKind.IncidentOpened, organizationId ?? Guid.NewGuid(), Guid.NewGuid(), name, "https://shop.example/health", Start, null, cause, Start);

    public static AlertEvent Resolved(string name = "Shop", Guid? organizationId = null) =>
        new(AlertKind.IncidentResolved, organizationId ?? Guid.NewGuid(), Guid.NewGuid(), name, "https://shop.example/health", Start, Start.AddMinutes(5).AddSeconds(30), "Expected status 200, got 500.", Start.AddMinutes(5).AddSeconds(30));
}
