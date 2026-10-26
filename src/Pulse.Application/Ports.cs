using System.Net;
using Pulse.Domain;

namespace Pulse.Application;

public interface IHostResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Checks one service. Implementations report every failure as a failed <see cref="CheckResult"/>.</summary>
public interface IServiceProbe
{
    Task<CheckResult> ProbeAsync(MonitoredService service, CancellationToken cancellationToken);
}

public readonly record struct UptimeCounts(int Total, int Succeeded);

/// <summary>
/// The monitoring data. Every method that a customer can reach takes the organization id: a service of
/// another organization is simply not found, whatever id is asked for.
/// </summary>
public interface IPulseStore
{
    Task AddServiceAsync(MonitoredService service, CancellationToken cancellationToken);

    Task<MonitoredService?> FindServiceAsync(Guid organizationId, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<MonitoredService>> ListServicesAsync(
        Guid organizationId,
        bool publicOnly,
        CancellationToken cancellationToken);

    Task<int> CountServicesAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Services whose next check is due, across every organization. Used by the scheduler only.</summary>
    Task<IReadOnlyList<MonitoredService>> ListDueAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task RemoveServiceAsync(MonitoredService service, CancellationToken cancellationToken);

    Task AddCheckAsync(CheckResult check, CancellationToken cancellationToken);

    Task<IReadOnlyList<CheckResult>> ListRecentChecksAsync(
        Guid serviceId,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, UptimeCounts>> GetUptimeAsync(
        IReadOnlyCollection<Guid> serviceIds,
        DateTimeOffset since,
        CancellationToken cancellationToken);

    Task<int> PurgeChecksOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);

    Task AddIncidentAsync(Incident incident, CancellationToken cancellationToken);

    Task<Incident?> FindOpenIncidentAsync(Guid serviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Incident>> ListIncidentsAsync(
        IReadOnlyCollection<Guid> serviceIds,
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Every incident of these services that overlaps the period starting at <paramref name="since"/>, open ones included.</summary>
    Task<IReadOnlyList<Incident>> ListIncidentsOverlappingAsync(
        IReadOnlyCollection<Guid> serviceIds,
        DateTimeOffset since,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves pending changes. Returns false, after discarding them, when another writer got there first
    /// (for example the service was deleted while it was being checked).
    /// </summary>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Accounts, organizations, sessions and the other things that belong to a customer.</summary>
public interface IAccountStore
{
    Task AddOrganizationAsync(Organization organization, CancellationToken cancellationToken);

    Task<Organization?> FindOrganizationAsync(Guid id, CancellationToken cancellationToken);

    Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Deletes the organization and everything it owns: users, sessions, services, history, webhooks.</summary>
    Task RemoveOrganizationAsync(Organization organization, CancellationToken cancellationToken);

    Task AddUserAsync(UserAccount user, CancellationToken cancellationToken);

    Task<UserAccount?> FindUserAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finds a user by e-mail address (already normalized).</summary>
    Task<UserAccount?> FindUserByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<UserAccount?> FindOwnerAsync(Guid organizationId, CancellationToken cancellationToken);

    Task AddSessionAsync(UserSession session, CancellationToken cancellationToken);

    Task<UserSession?> FindSessionAsync(string tokenHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserSession>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken);

    Task RemoveSessionsAsync(IEnumerable<UserSession> sessions, CancellationToken cancellationToken);

    Task<int> PurgeExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task AddEmailTokenAsync(EmailToken token, CancellationToken cancellationToken);

    Task<EmailToken?> FindEmailTokenAsync(string tokenHash, EmailTokenPurpose purpose, CancellationToken cancellationToken);

    Task RemoveEmailTokensAsync(Guid userId, EmailTokenPurpose purpose, CancellationToken cancellationToken);

    Task AddWebhookAsync(WebhookEndpoint webhook, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookEndpoint>> ListWebhooksAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<WebhookEndpoint?> FindWebhookAsync(Guid organizationId, Guid id, CancellationToken cancellationToken);

    Task RemoveWebhookAsync(WebhookEndpoint webhook, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves pending changes. Returns false, after discarding them, when a uniqueness rule was broken in
    /// the meantime (two sign-ups with the same e-mail address at the same moment, for instance).
    /// </summary>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);
}

public enum PasswordCheck
{
    Wrong = 0,
    Correct = 1,

    /// <summary>Correct, but stored with older settings: store a fresh hash.</summary>
    CorrectNeedsNewHash = 2,
}

public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>
    /// Checks a password. When there is no stored hash (no such account) it still does the same amount of
    /// work and answers <see cref="PasswordCheck.Wrong"/>, so the time taken does not reveal that.
    /// </summary>
    PasswordCheck Verify(string? storedHash, string password);
}

/// <summary>Reversible encryption for the few secrets Pulse has to read back (webhook signing keys).</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    string Unprotect(string protectedText);
}

public sealed record OutgoingMail(string To, string Subject, string Body);

public sealed class MailDeliveryException : Exception
{
    public MailDeliveryException(string message)
        : base(message)
    {
    }

    public MailDeliveryException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public MailDeliveryException()
    {
    }
}

public interface IMailSender
{
    /// <summary>False when the server has no mail settings: nothing can be sent.</summary>
    bool IsAvailable { get; }

    Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken);
}
