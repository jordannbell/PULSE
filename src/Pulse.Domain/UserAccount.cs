namespace Pulse.Domain;

/// <summary>
/// The person who owns an organization. Holds what login needs: the password hash (never the password),
/// the failed-attempt counter and the temporary lock that slows down password guessing.
/// </summary>
public sealed class UserAccount
{
    public const int EmailMaxLength = 254;

    /// <summary>Wrong passwords in a row before the account is locked.</summary>
    public const int MaxFailedLogins = 5;

    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private UserAccount()
    {
        Email = string.Empty;
        PasswordHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>Always stored trimmed and in lower case: "Ana@Mail.fr" and "ana@mail.fr" are the same account.</summary>
    public string Email { get; private set; }

    public string PasswordHash { get; private set; }

    public DateTimeOffset? EmailVerifiedAt { get; private set; }

    public int FailedLogins { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsEmailVerified => EmailVerifiedAt is not null;

    public static string NormalizeEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }

    public static UserAccount Create(Guid organizationId, string email, string passwordHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var normalized = NormalizeEmail(email);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(normalized.Length, EmailMaxLength);

        return new UserAccount
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Email = normalized,
            PasswordHash = passwordHash,
            CreatedAt = now,
        };
    }

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>Counts a wrong password. The fifth in a row locks the account for fifteen minutes.</summary>
    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLogins++;
        if (FailedLogins >= MaxFailedLogins)
        {
            LockedUntil = now + LockDuration;
            FailedLogins = 0;
        }
    }

    public void RecordSuccessfulLogin()
    {
        FailedLogins = 0;
        LockedUntil = null;
    }

    public void MarkEmailVerified(DateTimeOffset now) => EmailVerifiedAt ??= now;

    public void ChangePassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        FailedLogins = 0;
        LockedUntil = null;
    }
}
