using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pulse.Infrastructure.Persistence;

namespace Pulse.Tests;

/// <summary>Upgrading a database made before accounts existed must lose nothing and leave no service without an owner.</summary>
public sealed class MigrationTests : IAsyncLifetime
{
    private SqliteConnection _keepAlive = null!;
    private string _connectionString = null!;

    public async ValueTask InitializeAsync()
    {
        _connectionString = $"Data Source=migration-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
    }

    public async ValueTask DisposeAsync() => await _keepAlive.DisposeAsync();

    private PulseDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(_connectionString).Options);

    [Fact]
    public async Task Services_that_existed_before_accounts_move_to_a_historical_public_organization()
    {
        await using (var old = NewContext())
        {
            await old.GetService<IMigrator>().MigrateAsync("InitialCreate");

            // A service and one of its checks, written the way the first version wrote them.
            await old.Database.ExecuteSqlRawAsync(
                "INSERT INTO Services (Id, Name, Url, IntervalSeconds, TimeoutSeconds, ExpectedStatusCode, State, ConsecutiveFailures, LastCheckedAt, CreatedAt) " +
                "VALUES ('AAAAAAAA-0000-0000-0000-000000000001', 'Old site', 'https://old.example/', 60, 10, 200, 'Up', 0, NULL, 0)");
            await old.Database.ExecuteSqlRawAsync(
                "INSERT INTO Checks (Id, ServiceId, CheckedAt, Success, StatusCode, LatencyMs, Error) " +
                "VALUES ('BBBBBBBB-0000-0000-0000-000000000001', 'AAAAAAAA-0000-0000-0000-000000000001', 0, 1, 200, 40, NULL)");
        }

        await using (var upgraded = NewContext())
        {
            await upgraded.Database.MigrateAsync();

            var organization = await upgraded.Organizations.SingleAsync();
            Assert.Equal("historique", organization.Slug);
            var service = await upgraded.Services.SingleAsync();
            Assert.Equal("Old site", service.Name);
            Assert.Equal(organization.Id, service.OrganizationId);
            Assert.True(service.IsPublic);
            Assert.Single(await upgraded.Checks.ToListAsync());
            Assert.Empty(await upgraded.Users.ToListAsync()); // nobody can sign in to it: the owner never existed
        }
    }

    [Fact]
    public async Task A_database_with_nothing_in_it_gets_no_historical_organization()
    {
        await using var db = NewContext();

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Organizations.ToListAsync());
    }

    [Fact]
    public async Task Deleting_an_organization_removes_what_it_owns_but_keeps_other_organizations()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        var one = Pulse.Domain.Organization.Create("One", "one", DateTimeOffset.UtcNow);
        var two = Pulse.Domain.Organization.Create("Two", "two", DateTimeOffset.UtcNow);
        db.Organizations.AddRange(one, two);
        db.Services.AddRange(
            Pulse.Domain.MonitoredService.Create(one.Id, "A", "https://a.example/", 300, 10, 200, true, DateTimeOffset.UtcNow),
            Pulse.Domain.MonitoredService.Create(two.Id, "B", "https://b.example/", 300, 10, 200, true, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        db.Organizations.Remove(one);
        await db.SaveChangesAsync();

        Assert.Equal(["B"], await db.Services.Select(s => s.Name).ToListAsync());
    }

    [Fact]
    public async Task The_database_itself_refuses_two_accounts_with_the_same_address_or_two_pages_with_the_same_name()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        var one = Pulse.Domain.Organization.Create("One", "same", DateTimeOffset.UtcNow);
        db.Organizations.Add(one);
        await db.SaveChangesAsync();

        db.Organizations.Add(Pulse.Domain.Organization.Create("Two", "same", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.Users.Add(Pulse.Domain.UserAccount.Create(one.Id, "ana@example.com", "hash", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        db.Users.Add(Pulse.Domain.UserAccount.Create(one.Id, "ana@example.com", "hash", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
