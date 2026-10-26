using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pulse.Application;
using Pulse.Application.Alerts;
using Pulse.Infrastructure.Alerts;
using Pulse.Infrastructure.Demo;
using Pulse.Infrastructure.Mail;
using Pulse.Infrastructure.Persistence;
using Pulse.Infrastructure.Probing;
using Pulse.Infrastructure.Scheduling;
using Pulse.Infrastructure.Security;

namespace Pulse.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPulseInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<PulseDbContext>(o => o.UseSqlite(connectionString));
        services.AddScoped<IPulseStore, EfPulseStore>();
        services.AddScoped<IAccountStore, EfAccountStore>();
        services.TryAddSingleton<IHostResolver, SystemHostResolver>();
        services.TryAddSingleton<IServiceProbe, HttpServiceProbe>();
        services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.TryAddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.TryAddSingleton<IMailSender, SmtpMailSender>();
        services.AddHostedService<CheckScheduler>();
        AddAlerts(services);
        return services;
    }

    private static void AddAlerts(IServiceCollection services)
    {
        services.AddSingleton<AlertQueue>();
        services.AddSingleton<IAlertPublisher>(sp => sp.GetRequiredService<AlertQueue>());
        services.AddSingleton<WebhookHttp>();
        services.AddScoped<IAlertChannelFactory, AlertChannelFactory>();
        services.AddHostedService<AlertDispatcher>();
    }

    /// <summary>When <c>Pulse:SeedDemoData</c> is on, adds the demonstration organization if it is missing.</summary>
    public static async Task SeedDemoDataAsync(this IServiceProvider provider, CancellationToken cancellationToken = default)
    {
        await using var scope = provider.CreateAsyncScope();
        if (!scope.ServiceProvider.GetRequiredService<IOptions<PulseOptions>>().Value.SeedDemoData)
        {
            return;
        }

        var seeder = new DemoDataSeeder(
            scope.ServiceProvider.GetRequiredService<IPulseStore>(),
            scope.ServiceProvider.GetRequiredService<IAccountStore>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>());
        await seeder.SeedAsync(cancellationToken);
    }

    /// <summary>Creates or updates the database. Fine for one instance; run it as a separate job when scaling out.</summary>
    public static async Task MigratePulseDatabaseAsync(this IServiceProvider provider, CancellationToken cancellationToken = default)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
