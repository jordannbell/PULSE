using Pulse.Application.Accounts;
using Pulse.Contracts;
using Pulse.Domain;

namespace Pulse.Application;

/// <summary>
/// Adds, lists, changes and removes the services of one organization. Every method takes the organization
/// id and never looks beyond it: a service that belongs to someone else is "not found", whatever its id.
/// </summary>
public sealed class ServiceCatalog(
    IPulseStore store,
    IAccountStore accounts,
    TargetValidator targets,
    ServiceInsights insights,
    TimeProvider clock)
{
    public async Task<Result<ServiceDto>> CreateAsync(
        Guid organizationId,
        CreateServiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var limits = await LimitsOfAsync(organizationId, cancellationToken);
        var errors = ValidateSettings(request.Name, request.IntervalSeconds, request.TimeoutSeconds, request.ExpectedStatusCode, limits);
        if (string.IsNullOrWhiteSpace(request.Url) || request.Url.Length > MonitoredService.UrlMaxLength)
        {
            errors["url"] = [$"The address must have 1 to {MonitoredService.UrlMaxLength} characters."];
        }
        else if (await targets.ValidateAsync(request.Url, cancellationToken) is { } targetError)
        {
            // Checked even when another field is wrong, so the person sees every problem in one go.
            errors["url"] = [targetError];
        }

        if (errors.Count == 0 && await store.CountServicesAsync(organizationId, cancellationToken) >= limits.Plan.MaxServices)
        {
            errors["service"] = [$"The {limits.Name} plan allows at most {limits.Plan.MaxServices} services."];
        }

        if (errors.Count > 0)
        {
            return Result.Invalid<ServiceDto>(errors);
        }

        var service = MonitoredService.Create(
            organizationId,
            request.Name,
            request.Url.Trim(),
            request.IntervalSeconds,
            request.TimeoutSeconds,
            request.ExpectedStatusCode,
            request.IsPublic,
            clock.GetUtcNow());

        await store.AddServiceAsync(service, cancellationToken);
        await store.SaveChangesAsync(cancellationToken);
        return Result.Ok(await WithInsightAsync(service, cancellationToken));
    }

    /// <summary>Changes a service. Null when it does not exist (or belongs to someone else).</summary>
    public async Task<Result<ServiceDto>?> UpdateAsync(
        Guid organizationId,
        Guid id,
        UpdateServiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await store.FindServiceAsync(organizationId, id, cancellationToken) is not { } service)
        {
            return null;
        }

        var limits = await LimitsOfAsync(organizationId, cancellationToken);
        var errors = ValidateSettings(request.Name, request.IntervalSeconds, request.TimeoutSeconds, request.ExpectedStatusCode, limits);
        if (errors.Count > 0)
        {
            return Result.Invalid<ServiceDto>(errors);
        }

        service.Update(request.Name, request.IntervalSeconds, request.TimeoutSeconds, request.ExpectedStatusCode, request.IsPublic);
        await store.SaveChangesAsync(cancellationToken);
        return Result.Ok(await WithInsightAsync(service, cancellationToken));
    }

    public async Task<IReadOnlyList<ServiceDto>> ListAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var services = await store.ListServicesAsync(organizationId, publicOnly: false, cancellationToken);
        var insight = await insights.LoadAsync(services, cancellationToken);
        return [.. services.Select(s => s.ToDto(insight[s.Id]))];
    }

    public async Task<ServiceDto?> GetAsync(Guid organizationId, Guid id, CancellationToken cancellationToken) =>
        await store.FindServiceAsync(organizationId, id, cancellationToken) is { } service
            ? await WithInsightAsync(service, cancellationToken)
            : null;

    public async Task<bool> DeleteAsync(Guid organizationId, Guid id, CancellationToken cancellationToken)
    {
        var service = await store.FindServiceAsync(organizationId, id, cancellationToken);
        if (service is null)
        {
            return false;
        }

        await store.RemoveServiceAsync(service, cancellationToken);
        await store.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>The latest checks of a service, newest first, or null when the service does not exist.</summary>
    public async Task<IReadOnlyList<CheckDto>?> RecentChecksAsync(
        Guid organizationId,
        Guid id,
        int limit,
        CancellationToken cancellationToken)
    {
        if (await store.FindServiceAsync(organizationId, id, cancellationToken) is null)
        {
            return null;
        }

        var checks = await store.ListRecentChecksAsync(id, Math.Clamp(limit, 1, 200), cancellationToken);
        return [.. checks.Select(c => new CheckDto(c.CheckedAt, c.Success, c.StatusCode, c.LatencyMs, c.Error))];
    }

    private async Task<ServiceDto> WithInsightAsync(MonitoredService service, CancellationToken cancellationToken)
    {
        var insight = await insights.LoadAsync([service], cancellationToken);
        return service.ToDto(insight[service.Id]);
    }

    private sealed record Limits(string Name, PlanLimits Plan);

    private async Task<Limits> LimitsOfAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var plan = (await accounts.FindOrganizationAsync(organizationId, cancellationToken))?.Plan ?? Plan.Free;
        return new Limits(plan.ToString(), PlanPolicy.For(plan));
    }

    private static Dictionary<string, string[]> ValidateSettings(
        string? rawName,
        int intervalSeconds,
        int timeoutSeconds,
        int expectedStatusCode,
        Limits limits)
    {
        var errors = new Dictionary<string, string[]>();

        var name = rawName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MonitoredService.NameMaxLength)
        {
            errors["name"] = [$"The name must have 1 to {MonitoredService.NameMaxLength} characters."];
        }

        if (intervalSeconds is < MonitoredService.MinIntervalSeconds or > MonitoredService.MaxIntervalSeconds)
        {
            errors["intervalSeconds"] =
                [$"The interval must be between {MonitoredService.MinIntervalSeconds} and {MonitoredService.MaxIntervalSeconds} seconds."];
        }
        else if (intervalSeconds < limits.Plan.MinIntervalSeconds)
        {
            errors["intervalSeconds"] =
                [$"The {limits.Name} plan does not allow checks more often than every {limits.Plan.MinIntervalSeconds} seconds."];
        }

        if (timeoutSeconds < 1 || timeoutSeconds > MonitoredService.MaxTimeoutSeconds || timeoutSeconds > intervalSeconds)
        {
            errors["timeoutSeconds"] =
                [$"The timeout must be between 1 and {MonitoredService.MaxTimeoutSeconds} seconds, and not longer than the interval."];
        }

        if (expectedStatusCode is < 100 or > 599)
        {
            errors["expectedStatusCode"] = ["The expected status code must be between 100 and 599."];
        }

        return errors;
    }
}
