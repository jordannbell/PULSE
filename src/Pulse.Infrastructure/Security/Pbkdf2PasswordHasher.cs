using Microsoft.AspNetCore.Identity;
using Pulse.Application;

namespace Pulse.Infrastructure.Security;

/// <summary>
/// Password hashing by ASP.NET Core's own hasher (PBKDF2 with HMAC-SHA512, a random salt per password, a
/// work factor that can be raised later: old hashes are then flagged so they are renewed at the next login).
/// </summary>
internal sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Nobody = new();

    // A real hash of a throw-away password: verifying against it costs the same as against a real account.
    private static readonly string DecoyHash = Hasher.HashPassword(Nobody, Guid.NewGuid().ToString("N"));

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Hasher.HashPassword(Nobody, password);
    }

    public PasswordCheck Verify(string? storedHash, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (storedHash is null)
        {
            _ = Hasher.VerifyHashedPassword(Nobody, DecoyHash, password);
            return PasswordCheck.Wrong;
        }

        return Hasher.VerifyHashedPassword(Nobody, storedHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Correct,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.CorrectNeedsNewHash,
            _ => PasswordCheck.Wrong,
        };
    }
}
