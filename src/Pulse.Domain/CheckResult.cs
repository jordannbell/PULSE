namespace Pulse.Domain;

/// <summary>The outcome of one probe of a service. Never changes once recorded.</summary>
public sealed class CheckResult
{
    public const int ErrorMaxLength = 300;

    private CheckResult()
    {
    }

    public Guid Id { get; private init; }

    public Guid ServiceId { get; private init; }

    public DateTimeOffset CheckedAt { get; private init; }

    public bool Success { get; private init; }

    public int? StatusCode { get; private init; }

    public int LatencyMs { get; private init; }

    public string? Error { get; private init; }

    public static CheckResult Succeeded(Guid serviceId, DateTimeOffset at, int statusCode, int latencyMs) =>
        new()
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId,
            CheckedAt = at,
            Success = true,
            StatusCode = statusCode,
            LatencyMs = latencyMs,
        };

    public static CheckResult Failed(
        Guid serviceId,
        DateTimeOffset at,
        int latencyMs,
        string error,
        int? statusCode = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId,
            CheckedAt = at,
            Success = false,
            StatusCode = statusCode,
            LatencyMs = latencyMs,
            Error = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error,
        };
}
