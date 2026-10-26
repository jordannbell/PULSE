namespace Pulse.Domain;

/// <summary>A period during which a service was down.</summary>
public sealed class Incident
{
    public const int CauseMaxLength = 300;

    private Incident()
    {
        Cause = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ServiceId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public string Cause { get; private set; }

    public static Incident Open(Guid serviceId, DateTimeOffset at, string cause) =>
        new()
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId,
            StartedAt = at,
            Cause = cause.Length > CauseMaxLength ? cause[..CauseMaxLength] : cause,
        };

    public void Resolve(DateTimeOffset at)
    {
        if (ResolvedAt is not null)
        {
            throw new InvalidOperationException("The incident is already resolved.");
        }

        ResolvedAt = at < StartedAt ? StartedAt : at;
    }
}
