using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pulse.Application.Accounts;
using Pulse.Application.Alerts;

namespace Pulse.Application;

public static class DependencyInjection
{
    /// <summary>Registers the use cases. The host registers the options and the adapters.</summary>
    public static IServiceCollection AddPulseApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TargetValidator>();
        services.AddScoped<ServiceCatalog>();
        services.AddScoped<CheckRunner>();
        services.AddScoped<ServiceInsights>();
        services.AddScoped<StatusQueries>();
        services.AddScoped<AccountService>();
        services.AddScoped<WebhookService>();
        services.AddScoped<AlertTester>();

        // Enumerable, not TryAddSingleton: ValidateDataAnnotations() already registers a validator for the same
        // options type, and a plain TryAdd would then silently skip ours.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AccountOptions>, AccountOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MailOptions>, MailOptionsValidator>());
        return services;
    }
}
