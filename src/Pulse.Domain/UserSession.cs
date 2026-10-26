namespace Pulse.Domain;

/// <summary>
/// A signed-in browser or client. The token itself is given to the client once and never stored: only its
/// SHA-256 hash is kept, so a copy of the database cannot be used to sign in. Deleting the row signs out.
/// </summary>
public sealed class UserSession
{
    private UserSession()
    {
        TokenHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public static UserSession Start(Guid userId, string tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        return new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            LastSeenAt = now,
        };
    }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;

    public void Touch(DateTimeOffset now) => LastSeenAt = now;
}
