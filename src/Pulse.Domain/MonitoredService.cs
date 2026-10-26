namespace Pulse.Domain;

public enum ServiceState
{
    Unknown = 0,
    Up = 1,
    Down = 2,
}

/// <summary>What a recorded check changed for the service.</summary>
public enum StateTransition
{
    None = 0,
    IncidentOpened = 1,
    IncidentResolved = 2,
}

/// <summary>
/// A website or API watched on a schedule. State changes only through <see cref="ApplyCheck"/>, so the
/// rule "N failures in a row open an incident" lives in one place.
/// </summary>
public sealed class MonitoredService
{
    public const int NameMaxLength = 100;
    public const int UrlMaxLength = 2048;
    public const int MinIntervalSeconds = 30;
    public const int MaxIntervalSeconds = 86_400;
    public const int MaxTimeoutSeconds = 30;

    // Required by EF Core, which fills the properties itself.
    private MonitoredService()
    {
        Name = string.Empty;
        Url = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>The customer this service belongs to. It never changes.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Whether the service appears on the organization's public status page.</summary>
    public bool IsPublic { get; private set; } = true;

    public string Name { get; private set; }

    public string Url { get; private set; }

    public int IntervalSeconds { get; private set; }

    public int TimeoutSeconds { get; private set; }

    public int ExpectedStatusCode { get; private set; }

    public ServiceState State { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public DateTimeOffset? LastCheckedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static MonitoredService Create(
        Guid organizationId,
        string name,
        string url,
        int intervalSeconds,
        int timeoutSeconds,
        int expectedStatusCode,
        bool isPublic,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(url.Length, UrlMaxLength);
        ValidateSettings(name, intervalSeconds, timeoutSeconds, expectedStatusCode);

        return new MonitoredService
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name.Trim(),
            Url = url,
            IntervalSeconds = intervalSeconds,
            TimeoutSeconds = timeoutSeconds,
            ExpectedStatusCode = expectedStatusCode,
            IsPublic = isPublic,
            State = ServiceState.Unknown,
            CreatedAt = now,
        };
    }

    /// <summary>Changes what the owner may change. The address stays: to watch another one, add a new service.</summary>
    public void Update(string name, int intervalSeconds, int timeoutSeconds, int expectedStatusCode, bool isPublic)
    {
        ValidateSettings(name, intervalSeconds, timeoutSeconds, expectedStatusCode);
        Name = name.Trim();
        IntervalSeconds = intervalSeconds;
        TimeoutSeconds = timeoutSeconds;
        ExpectedStatusCode = expectedStatusCode;
        IsPublic = isPublic;
    }

    // The application layer reports friendly field errors; these guards protect the invariants for any other caller.
    private static void ValidateSettings(string name, int intervalSeconds, int timeoutSeconds, int expectedStatusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Trim().Length, NameMaxLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalSeconds, MinIntervalSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(intervalSeconds, MaxIntervalSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutSeconds, MaxTimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutSeconds, intervalSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedStatusCode, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expectedStatusCode, 599);
    }

    public bool IsDue(DateTimeOffset now) =>
        LastCheckedAt is null || now - LastCheckedAt.Value >= TimeSpan.FromSeconds(IntervalSeconds);

    /// <summary>
    /// Records a check. The service goes down, and an incident opens, once
    /// <paramref name="failureThreshold"/> checks fail in a row. One success brings it back up.
    /// </summary>
    public StateTransition ApplyCheck(CheckResult check, int failureThreshold)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);

        LastCheckedAt = check.CheckedAt;

        if (check.Success)
        {
            ConsecutiveFailures = 0;
            var wasDown = State == ServiceState.Down;
            State = ServiceState.Up;
            return wasDown ? StateTransition.IncidentResolved : StateTransition.None;
        }

        ConsecutiveFailures++;
        if (ConsecutiveFailures >= failureThreshold && State != ServiceState.Down)
        {
            State = ServiceState.Down;
            return StateTransition.IncidentOpened;
        }

        return StateTransition.None;
    }
}
