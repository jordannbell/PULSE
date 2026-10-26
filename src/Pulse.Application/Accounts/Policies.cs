using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Pulse.Contracts;
using Pulse.Domain;

namespace Pulse.Application.Accounts;

public sealed record PlanLimits(int MaxServices, int MinIntervalSeconds, int MaxWebhooks);

/// <summary>What each plan allows. There is no payment yet: the plans show how limits are enforced.</summary>
public static class PlanPolicy
{
    public static PlanLimits For(Plan plan) =>
        plan switch
        {
            Plan.Pro => new PlanLimits(MaxServices: 50, MinIntervalSeconds: 30, MaxWebhooks: 5),
            _ => new PlanLimits(MaxServices: 5, MinIntervalSeconds: 300, MaxWebhooks: 1),
        };

    public static PlanTier ToTier(this Plan plan) => plan == Plan.Pro ? PlanTier.Pro : PlanTier.Free;

    public static Plan ToPlan(this PlanTier tier) => tier == PlanTier.Pro ? Plan.Pro : Plan.Free;
}

/// <summary>The rules a password must meet. Length matters most; composition rules only push people to "Password1!".</summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    /// <summary>Returns the reason a password is refused, or null when it is acceptable.</summary>
    public static string? Check(string? password, string? email)
    {
        if (password is null || password.Length < MinLength)
        {
            return ApiMessages.PasswordTooShort;
        }

        if (password.Length > MaxLength)
        {
            return ApiMessages.PasswordTooLong;
        }

        if (email is not null && string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return ApiMessages.PasswordIsEmail;
        }

        return null;
    }
}

public static class EmailPolicy
{
    /// <summary>A plausible address: one @, a dotted domain, no display name, within the standard length.</summary>
    public static bool IsValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > UserAccount.EmailMaxLength)
        {
            return false;
        }

        var trimmed = email.Trim();
        return MailAddress.TryCreate(trimmed, out var parsed)
            && string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase)
            && parsed.Host.Contains('.', StringComparison.Ordinal);
    }
}

public static class SlugPolicy
{
    // Names that would clash with a page or a route of the site, or that a customer should not hold.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "admin", "administrator", "api", "app", "account", "assets", "auth", "demo", "health", "help",
        "login", "logout", "pulse", "root", "s", "settings", "signup", "static", "status", "support", "www",
    };

    public static bool IsReserved(string slug) => Reserved.Contains(slug);
}

/// <summary>Random secrets and how they are stored. A token is given to the client once; only its hash is kept.</summary>
public static class Tokens
{
    /// <summary>256 random bits as 43 URL-safe characters.</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The value stored in the database. SHA-256 is enough: the token is random, not a password.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
