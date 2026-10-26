using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pulse.Application.Alerts;

namespace Pulse.Infrastructure.Alerts;

/// <summary>
/// Delivers queued alerts to the channels of the organization they concern. A failing channel is retried
/// with a growing delay and then given up on; it never blocks the other channels or the alerts behind it.
/// </summary>
internal sealed class AlertDispatcher(
    AlertQueue queue,
    IServiceScopeFactory scopes,
    IOptions<AlertOptions> options,
    TimeProvider clock,
    ILogger<AlertDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var alert in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await DispatchAsync(alert, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    internal async Task DispatchAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        IReadOnlyList<IAlertChannel> channels;
        try
        {
            // A fresh scope per alert: the channels are read from the database as they are now.
            await using var scope = scopes.CreateAsyncScope();
            channels = await scope.ServiceProvider.GetRequiredService<IAlertChannelFactory>().CreateAsync(alert.OrganizationId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The alert channels of organization {OrganizationId} could not be loaded", alert.OrganizationId);
            return;
        }

        // Channels run side by side so a slow mail server does not delay a webhook.
        await Task.WhenAll(channels.Select(channel => DeliverAsync(channel, alert, cancellationToken)));
    }

    internal async Task DeliverAsync(IAlertChannel channel, AlertEvent alert, CancellationToken cancellationToken)
    {
        var maxAttempts = options.Value.MaxAttempts;
        var delay = TimeSpan.FromSeconds(options.Value.RetryDelaySeconds);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await channel.SendAsync(alert, cancellationToken);
                logger.LogInformation("Alert {Kind} for service {ServiceId} sent through {Channel}", alert.Kind, alert.ServiceId, channel.Name);
                return;
            }
            catch (AlertDeliveryException ex)
            {
                logger.LogWarning("Alert {Kind} through {Channel} failed (try {Attempt} of {Max}): {Reason}", alert.Kind, channel.Name, attempt, maxAttempts, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A channel bug must not stop the dispatcher.
                logger.LogError(ex, "Alert channel {Channel} crashed", channel.Name);
                return;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(delay, clock, cancellationToken);
                delay *= 2;
            }
        }

        logger.LogError("Alert {Kind} for service {ServiceId} could not be delivered through {Channel}; giving up", alert.Kind, alert.ServiceId, channel.Name);
    }
}
