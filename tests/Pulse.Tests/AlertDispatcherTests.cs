using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulse.Application.Alerts;
using Pulse.Infrastructure.Alerts;

namespace Pulse.Tests;

public class AlertDispatcherTests
{
    private static AlertDispatcher Dispatcher(AlertQueue queue, IAlertChannelFactory factory, int attempts = 3)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => factory);
        return new AlertDispatcher(
            queue,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Wrap(new AlertOptions { MaxAttempts = attempts, RetryDelaySeconds = 0 }),
            new FakeTimeProvider(),
            NullLogger<AlertDispatcher>.Instance);
    }

    private static AlertQueue NewQueue() => new(NullLogger<AlertQueue>.Instance);

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task A_failing_channel_is_retried_until_it_works()
    {
        var channel = new FlakyChannel(failuresBeforeSuccess: 2);

        await Dispatcher(NewQueue(), new FixedChannelFactory(channel)).DeliverAsync(channel, Alerts.Opened(), CancellationToken.None);

        Assert.Equal(3, channel.Calls);
        Assert.Single(channel.Delivered);
    }

    [Fact]
    public async Task A_channel_that_keeps_failing_is_given_up_on_after_the_configured_number_of_tries()
    {
        var channel = new FlakyChannel(failuresBeforeSuccess: int.MaxValue);

        await Dispatcher(NewQueue(), new FixedChannelFactory(channel), attempts: 4).DeliverAsync(channel, Alerts.Opened(), CancellationToken.None);

        Assert.Equal(4, channel.Calls);
        Assert.Empty(channel.Delivered);
    }

    [Fact]
    public async Task A_channel_that_crashes_does_not_stop_the_dispatcher()
    {
        var crashing = new CrashingChannel();

        await Dispatcher(NewQueue(), new FixedChannelFactory(crashing)).DeliverAsync(crashing, Alerts.Opened(), CancellationToken.None);

        Assert.Equal(1, crashing.Calls); // a bug is not retried
    }

    [Fact]
    public async Task Queued_alerts_reach_every_channel_and_one_broken_channel_does_not_block_the_others()
    {
        var queue = NewQueue();
        var good = new RecordingChannel("Good");
        var broken = new FlakyChannel(failuresBeforeSuccess: int.MaxValue, "Broken");
        var dispatcher = Dispatcher(queue, new FixedChannelFactory(broken, good));
        await dispatcher.StartAsync(CancellationToken.None);

        queue.Publish(Alerts.Opened("A"));
        queue.Publish(Alerts.Resolved("A"));
        var delivered = await WaitAsync(() => good.Received.Count == 2);
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.True(delivered);
        Assert.Equal([AlertKind.IncidentOpened, AlertKind.IncidentResolved], good.Received.Select(a => a.Kind));
        Assert.Equal(6, broken.Calls); // 3 tries for each of the two alerts
    }

    [Fact]
    public async Task An_organization_with_no_channel_costs_nothing_and_the_next_alert_is_still_delivered()
    {
        var queue = NewQueue();
        var recorder = new RecordingChannel();
        var withChannels = Guid.NewGuid();
        var dispatcher = Dispatcher(queue, new PerOrganizationFactory(withChannels, recorder));
        await dispatcher.StartAsync(CancellationToken.None);

        queue.Publish(Alerts.Opened("No channels", organizationId: Guid.NewGuid()));
        queue.Publish(Alerts.Opened("Has channels", organizationId: withChannels));
        var delivered = await WaitAsync(() => recorder.Received.Count == 1);
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.True(delivered);
    }

    [Fact]
    public async Task A_factory_that_fails_loses_that_alert_only()
    {
        var queue = NewQueue();
        var recorder = new RecordingChannel();
        var dispatcher = Dispatcher(queue, new ExplodingThenWorkingFactory(recorder));
        await dispatcher.StartAsync(CancellationToken.None);

        queue.Publish(Alerts.Opened("Lost"));
        queue.Publish(Alerts.Opened("Delivered"));
        var delivered = await WaitAsync(() => recorder.Received.Count == 1);
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.True(delivered);
        Assert.Equal("Delivered", recorder.Received[0].ServiceName);
    }

    [Fact]
    public void When_the_queue_overflows_the_oldest_alerts_are_dropped_and_publishing_never_blocks()
    {
        var queue = NewQueue();
        var total = AlertQueue.Capacity + 5;

        for (var i = 0; i < total; i++)
        {
            queue.Publish(Alerts.Opened($"Service {i}"));
        }

        var kept = new List<string>();
        while (queue.Reader.TryRead(out var alert))
        {
            kept.Add(alert.ServiceName);
        }

        Assert.Equal(AlertQueue.Capacity, kept.Count);
        Assert.Equal("Service 5", kept[0]);
        Assert.Equal($"Service {total - 1}", kept[^1]);
    }

    private sealed class CrashingChannel : IAlertChannel
    {
        public string Name => "Crashing";

        public int Calls { get; private set; }

        public Task SendAsync(AlertEvent alert, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("bug");
        }
    }

    /// <summary>Gives its channel to one organization only: every other organization has none.</summary>
    private sealed class PerOrganizationFactory(Guid organizationWithChannel, IAlertChannel channel) : IAlertChannelFactory
    {
        public Task<IReadOnlyList<IAlertChannel>> CreateAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IAlertChannel>>(organizationId == organizationWithChannel ? [channel] : []);
    }
    private sealed class ExplodingThenWorkingFactory(IAlertChannel channel) : IAlertChannelFactory
    {
        private int _calls;

        public Task<IReadOnlyList<IAlertChannel>> CreateAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("database unavailable")
                : Task.FromResult<IReadOnlyList<IAlertChannel>>([channel]);
    }
}
