namespace Pulse.Application.Alerts;

public enum AlertKind
{
    IncidentOpened,
    IncidentResolved,

    /// <summary>Sent on demand to check that a channel is set up correctly.</summary>
    Test,
}

/// <summary>What happened to a service of an organization. Meant for its owner: it carries the monitored address.</summary>
public sealed record AlertEvent(
    AlertKind Kind,
    Guid OrganizationId,
    Guid ServiceId,
    string ServiceName,
    string ServiceUrl,
    DateTimeOffset IncidentStartedAt,
    DateTimeOffset? IncidentResolvedAt,
    string Cause,
    DateTimeOffset SentAt)
{
    public TimeSpan? Duration => IncidentResolvedAt - IncidentStartedAt;

    public static AlertEvent TestMessage(Guid organizationId, DateTimeOffset now) =>
        new(AlertKind.Test, organizationId, Guid.Empty, "Pulse (test)", "https://pulse.invalid/", now, null, "Test message sent from the account.", now);
}
