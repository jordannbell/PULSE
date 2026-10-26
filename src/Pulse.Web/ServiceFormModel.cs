namespace Pulse.Web;

/// <summary>What the "add a service" and "edit a service" forms edit.</summary>
public sealed class ServiceFormModel
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public int IntervalSeconds { get; set; } = 300;

    public int TimeoutSeconds { get; set; } = 10;

    public int ExpectedStatusCode { get; set; } = 200;

    public bool IsPublic { get; set; } = true;

    public static ServiceFormModel From(Pulse.Contracts.ServiceDto service) =>
        new()
        {
            Name = service.Name,
            Url = service.Url,
            IntervalSeconds = service.IntervalSeconds,
            TimeoutSeconds = service.TimeoutSeconds,
            ExpectedStatusCode = service.ExpectedStatusCode,
            IsPublic = service.IsPublic,
        };
}
