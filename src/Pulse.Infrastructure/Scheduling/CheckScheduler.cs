using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pulse.Application;

namespace Pulse.Infrastructure.Scheduling;

/// <summary>Wakes up every few seconds and runs the checks that are due.</summary>
internal sealed class CheckScheduler(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IOptions<PulseOptions> options,
    ILogger<CheckScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.SchedulerEnabled)
        {
            logger.LogInformation("Scheduler is disabled");
            return;
        }

        var lastPurge = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SchedulerTickSeconds), clock);

        while (await WaitForNextTickAsync(timer, stoppingToken))
        {
            try
            {
                // A fresh scope per tick: the store (and its change tracking) must not outlive one round.
                await using var scope = scopes.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<CheckRunner>();
                await runner.RunDueAsync(stoppingToken);

                if (clock.GetUtcNow() - lastPurge >= PurgeEvery)
                {
                    var purged = await runner.PurgeExpiredAsync(stoppingToken);
                    var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
                    var closed = await accounts.PurgeExpiredSessionsAsync(clock.GetUtcNow(), stoppingToken);
                    lastPurge = clock.GetUtcNow();
                    logger.LogInformation("Purged {Count} expired check results and {Sessions} expired sessions and links", purged, closed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed round must not kill the scheduler; the next tick tries again.
                logger.LogError(ex, "Scheduler round failed");
            }
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
