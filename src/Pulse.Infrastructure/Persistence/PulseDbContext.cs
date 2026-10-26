using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pulse.Domain;

namespace Pulse.Infrastructure.Persistence;

public sealed class PulseDbContext(DbContextOptions<PulseDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();

    public DbSet<WebhookEndpoint> Webhooks => Set<WebhookEndpoint>();

    public DbSet<MonitoredService> Services => Set<MonitoredService>();

    public DbSet<CheckResult> Checks => Set<CheckResult>();

    public DbSet<Incident> Incidents => Set<Incident>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot compare or sort its default text form of DateTimeOffset in queries.
        // All dates are stored in UTC, so the binary form keeps the order.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Deleting an organization deletes everything it owns, in one step: that is how an account is erased.
        modelBuilder.Entity<Organization>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Name).HasMaxLength(Organization.NameMaxLength).IsRequired();
            entity.Property(o => o.Slug).HasMaxLength(Organization.SlugMaxLength).IsRequired();
            entity.Property(o => o.Plan).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(o => o.Slug).IsUnique();
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Email).HasMaxLength(UserAccount.EmailMaxLength).IsRequired();
            entity.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasIndex(u => u.OrganizationId);
            entity.HasOne<Organization>().WithMany().HasForeignKey(u => u.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(s => s.TokenHash).IsUnique();
            entity.HasIndex(s => s.UserId);
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailToken>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(t => t.Purpose).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => new { t.UserId, t.Purpose });
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebhookEndpoint>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Url).HasMaxLength(WebhookEndpoint.UrlMaxLength).IsRequired();
            entity.Property(w => w.SecretCipher).HasMaxLength(512).IsRequired();
            entity.Property(w => w.Format).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(w => w.OrganizationId);
            entity.HasOne<Organization>().WithMany().HasForeignKey(w => w.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MonitoredService>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Name).HasMaxLength(MonitoredService.NameMaxLength).IsRequired();
            entity.Property(s => s.Url).HasMaxLength(MonitoredService.UrlMaxLength).IsRequired();
            entity.Property(s => s.State).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(s => s.OrganizationId);
            entity.HasOne<Organization>().WithMany().HasForeignKey(s => s.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CheckResult>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Error).HasMaxLength(CheckResult.ErrorMaxLength);
            entity.HasIndex(c => new { c.ServiceId, c.CheckedAt });
            entity.HasIndex(c => c.CheckedAt);
            entity.HasOne<MonitoredService>().WithMany().HasForeignKey(c => c.ServiceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Incident>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Cause).HasMaxLength(Incident.CauseMaxLength).IsRequired();
            entity.HasIndex(i => new { i.ServiceId, i.ResolvedAt });
            entity.HasOne<MonitoredService>().WithMany().HasForeignKey(i => i.ServiceId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
