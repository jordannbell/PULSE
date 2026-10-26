using System.Text.RegularExpressions;

namespace Pulse.Domain;

public enum Plan
{
    Free = 0,
    Pro = 1,
}

/// <summary>
/// A customer's space: it owns its services, its webhooks and its public status page (<c>/s/{slug}</c>).
/// Everything a customer sees or changes belongs to exactly one organization.
/// </summary>
public sealed partial class Organization
{
    public const int NameMaxLength = 80;
    public const int SlugMinLength = 3;
    public const int SlugMaxLength = 40;

    private Organization()
    {
        Name = string.Empty;
        Slug = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>The short name in the public address. Fixed once chosen, so shared links keep working.</summary>
    public string Slug { get; private set; }

    public Plan Plan { get; private set; }

    /// <summary>Whether incidents are also sent by e-mail to the account's verified address.</summary>
    public bool EmailAlerts { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }

    public static bool IsValidSlug(string? slug) => slug is not null && SlugPattern().IsMatch(slug);

    public static Organization Create(string name, string slug, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Trim().Length, NameMaxLength);
        if (!IsValidSlug(slug))
        {
            throw new ArgumentException("The address name is not valid.", nameof(slug));
        }

        return new Organization
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Slug = slug,
            Plan = Plan.Free,
            CreatedAt = now,
        };
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Trim().Length, NameMaxLength);
        Name = name.Trim();
    }

    public void ChangePlan(Plan plan) => Plan = plan;

    public void SetEmailAlerts(bool enabled) => EmailAlerts = enabled;

    // Lower-case letters, digits and hyphens; starts and ends with a letter or a digit.
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$")]
    private static partial Regex SlugPattern();
}
