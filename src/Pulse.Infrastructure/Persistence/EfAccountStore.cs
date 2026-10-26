using Microsoft.EntityFrameworkCore;
using Pulse.Application;
using Pulse.Domain;

namespace Pulse.Infrastructure.Persistence;

internal sealed class EfAccountStore(PulseDbContext db) : IAccountStore
{
    public async Task AddOrganizationAsync(Organization organization, CancellationToken cancellationToken) =>
        await db.Organizations.AddAsync(organization, cancellationToken);

    public async Task<Organization?> FindOrganizationAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken cancellationToken) =>
        await db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Slug == slug, cancellationToken);

    public async Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken) =>
        await db.Organizations.AnyAsync(o => o.Slug == slug, cancellationToken);

    public Task RemoveOrganizationAsync(Organization organization, CancellationToken cancellationToken)
    {
        db.Organizations.Remove(organization);
        return Task.CompletedTask;
    }

    public async Task AddUserAsync(UserAccount user, CancellationToken cancellationToken) =>
        await db.Users.AddAsync(user, cancellationToken);

    public async Task<UserAccount?> FindUserAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<UserAccount?> FindUserByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

    public async Task<UserAccount?> FindOwnerAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().OrderBy(u => u.CreatedAt).FirstOrDefaultAsync(u => u.OrganizationId == organizationId, cancellationToken);

    public async Task AddSessionAsync(UserSession session, CancellationToken cancellationToken) =>
        await db.Sessions.AddAsync(session, cancellationToken);

    public async Task<UserSession?> FindSessionAsync(string tokenHash, CancellationToken cancellationToken) =>
        await db.Sessions.FirstOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<UserSession>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Sessions.Where(s => s.UserId == userId).ToListAsync(cancellationToken);

    public Task RemoveSessionsAsync(IEnumerable<UserSession> sessions, CancellationToken cancellationToken)
    {
        db.Sessions.RemoveRange(sessions);
        return Task.CompletedTask;
    }

    public async Task<int> PurgeExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessions = await db.Sessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var tokens = await db.EmailTokens.Where(t => t.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        return sessions + tokens;
    }

    public async Task AddEmailTokenAsync(EmailToken token, CancellationToken cancellationToken) =>
        await db.EmailTokens.AddAsync(token, cancellationToken);

    public async Task<EmailToken?> FindEmailTokenAsync(string tokenHash, EmailTokenPurpose purpose, CancellationToken cancellationToken) =>
        await db.EmailTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.Purpose == purpose, cancellationToken);

    public async Task RemoveEmailTokensAsync(Guid userId, EmailTokenPurpose purpose, CancellationToken cancellationToken) =>
        db.EmailTokens.RemoveRange(await db.EmailTokens.Where(t => t.UserId == userId && t.Purpose == purpose).ToListAsync(cancellationToken));

    public async Task AddWebhookAsync(WebhookEndpoint webhook, CancellationToken cancellationToken) =>
        await db.Webhooks.AddAsync(webhook, cancellationToken);

    public async Task<IReadOnlyList<WebhookEndpoint>> ListWebhooksAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await db.Webhooks.AsNoTracking().Where(w => w.OrganizationId == organizationId).OrderBy(w => w.CreatedAt).ToListAsync(cancellationToken);

    public async Task<WebhookEndpoint?> FindWebhookAsync(Guid organizationId, Guid id, CancellationToken cancellationToken) =>
        await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id && w.OrganizationId == organizationId, cancellationToken);

    public Task RemoveWebhookAsync(WebhookEndpoint webhook, CancellationToken cancellationToken)
    {
        db.Webhooks.Remove(webhook);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken) =>
        await db.SaveChangesAsync(cancellationToken);

    public Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChanges.TryAsync(db, cancellationToken);
}
