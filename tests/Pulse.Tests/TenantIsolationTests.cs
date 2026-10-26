using Microsoft.Extensions.DependencyInjection;
using Pulse.Application;
using Pulse.Application.Accounts;
using Pulse.Application.Alerts;
using Pulse.Contracts;
using Pulse.Domain;
using Pulse.Infrastructure.Alerts;

namespace Pulse.Tests;

/// <summary>
/// The first risk of a service shared by many customers: one seeing or touching another's data. Every
/// operation is tried with another organization's identifiers, and must find nothing.
/// </summary>
public class TenantIsolationTests
{
    private static async Task<T> WithAsync<T>(TestApp app, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = app.NewScope();
        return await action(scope.ServiceProvider);
    }

    private static CreateServiceRequest NewService(string name = "Shop", int interval = 300) =>
        new(name, "https://shop.test/health", interval, 10, 200);


    // ------------------------------------------------------------ services

    [Fact]
    public async Task An_organization_lists_only_its_own_services()
    {
        await using var app = await TestApp.CreateAsync();
        var a = await app.AddOrganizationAsync();
        var b = await app.AddOrganizationAsync();
        await app.AddServiceAsync(a.Id, "A-one");
        await app.AddServiceAsync(b.Id, "B-one");
        await app.AddServiceAsync(b.Id, "B-two");

        var listA = await WithAsync(app, p => p.GetRequiredService<ServiceCatalog>().ListAsync(a.Id, CancellationToken.None));
        var listB = await WithAsync(app, p => p.GetRequiredService<ServiceCatalog>().ListAsync(b.Id, CancellationToken.None));

        Assert.Equal(["A-one"], listA.Select(s => s.Name));
        Assert.Equal(["B-one", "B-two"], listB.Select(s => s.Name));
    }

    [Fact]
    public async Task Another_organizations_service_cannot_be_read_changed_checked_or_deleted_by_its_id()
    {
        await using var app = await TestApp.CreateAsync();
        var mine = await app.AddOrganizationAsync();
        var theirs = await app.AddOrganizationAsync();
        var target = await app.AddServiceAsync(theirs.Id, "Theirs");
        var update = new UpdateServiceRequest("Hijacked", 300, 10, 200, false);

        await using var scope = app.NewScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ServiceCatalog>();

        Assert.Null(await catalog.GetAsync(mine.Id, target.Id, CancellationToken.None));
        Assert.Null(await catalog.UpdateAsync(mine.Id, target.Id, update, CancellationToken.None));
        Assert.Null(await catalog.RecentChecksAsync(mine.Id, target.Id, 10, CancellationToken.None));
        Assert.False(await catalog.DeleteAsync(mine.Id, target.Id, CancellationToken.None));

        // Still there, still untouched, for its real owner.
        var still = await catalog.GetAsync(theirs.Id, target.Id, CancellationToken.None);
        Assert.Equal("Theirs", still!.Name);
        Assert.True(still.IsPublic);
    }

    [Fact]
    public async Task The_plan_limit_is_counted_per_organization()
    {
        await using var app = await TestApp.CreateAsync();
        var free = await app.AddOrganizationAsync(plan: Plan.Free);
        var other = await app.AddOrganizationAsync(plan: Plan.Free);
        for (var i = 0; i < 5; i++)
        {
            await app.AddServiceAsync(free.Id, "S" + i, intervalSeconds: 300);
        }

        var created = await WithAsync(app, p => p.GetRequiredService<ServiceCatalog>().CreateAsync(other.Id, NewService(), CancellationToken.None));

        Assert.True(created.IsSuccess); // the first organization is "full"; this one is not
    }

    // ------------------------------------------------------------ plans

    [Fact]
    public async Task The_free_plan_allows_five_services_and_checks_no_more_often_than_every_five_minutes()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync(plan: Plan.Free);
        for (var i = 0; i < 5; i++)
        {
            await app.AddServiceAsync(org.Id, "S" + i, intervalSeconds: 300);
        }

        var tooMany = await WithAsync(app, p => p.GetRequiredService<ServiceCatalog>().CreateAsync(org.Id, NewService(), CancellationToken.None));
        var tooOften = await WithAsync(app, p => p.GetRequiredService<ServiceCatalog>().CreateAsync(org.Id, NewService(interval: 60), CancellationToken.None));

        Assert.Contains("The Free plan allows at most 5 services.", tooMany.Errors!.GetValueOrDefault("service") ?? []);
        Assert.Contains("The Free plan does not allow checks more often than every 300 seconds.", tooOften.Errors!["intervalSeconds"]);
    }

    [Fact]
    public async Task The_pro_plan_allows_checks_every_thirty_seconds()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync(plan: Plan.Pro);

        var result = await WithAsync(app, p => p.GetRequiredService<ServiceCatalog>().CreateAsync(org.Id, NewService(interval: 30), CancellationToken.None));

        Assert.True(result.IsSuccess);
        Assert.Equal(30, result.Value!.IntervalSeconds);
    }

    [Fact]
    public async Task An_update_cannot_get_around_the_plan_limits()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync(plan: Plan.Free);
        var service = await app.AddServiceAsync(org.Id, intervalSeconds: 300);

        var result = await WithAsync(app, p => p.GetRequiredService<ServiceCatalog>().UpdateAsync(org.Id, service.Id, new UpdateServiceRequest("Site", 30, 10, 200, true), CancellationToken.None));

        Assert.False(result!.IsSuccess);
        Assert.Contains("intervalSeconds", result.Errors!.Keys);
    }

    [Fact]
    public async Task A_service_can_be_made_private_and_renamed()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync(plan: Plan.Pro);
        var service = await app.AddServiceAsync(org.Id, "Old", intervalSeconds: 300);

        var result = await WithAsync(app, p => p.GetRequiredService<ServiceCatalog>().UpdateAsync(org.Id, service.Id, new UpdateServiceRequest(" New ", 600, 5, 204, false), CancellationToken.None));

        var dto = result!.Value!;
        Assert.Equal("New", dto.Name);
        Assert.Equal(600, dto.IntervalSeconds);
        Assert.Equal(204, dto.ExpectedStatusCode);
        Assert.False(dto.IsPublic);
    }

    // ------------------------------------------------------------ public pages

    [Fact]
    public async Task A_public_page_shows_only_its_organizations_public_services_and_its_incidents()
    {
        await using var app = await TestApp.CreateAsync();
        var a = await app.AddOrganizationAsync("alpha");
        var b = await app.AddOrganizationAsync("beta");
        await app.AddServiceAsync(a.Id, "Alpha public");
        await app.AddServiceAsync(a.Id, "Alpha private", isPublic: false);
        await app.AddServiceAsync(b.Id, "Beta public");

        var page = await WithAsync(app, p => p.GetRequiredService<StatusQueries>().GetPublicStatusAsync("alpha", CancellationToken.None));

        Assert.Equal("alpha", page!.Slug);
        Assert.Equal(["Alpha public"], page.Services.Select(s => s.Name));
        Assert.False(page.IsDemo);
    }

    [Fact]
    public async Task An_unknown_page_is_not_found_and_so_is_a_slug_that_only_looks_similar()
    {
        await using var app = await TestApp.CreateAsync();
        await app.AddOrganizationAsync("alpha");

        await using var scope = app.NewScope();
        var status = scope.ServiceProvider.GetRequiredService<StatusQueries>();

        Assert.Null(await status.GetPublicStatusAsync("nobody", CancellationToken.None));
        Assert.Null(await status.GetPublicStatusAsync("alph", CancellationToken.None));
        Assert.Null(await status.GetPublicStatusAsync("ALPHA", CancellationToken.None));
        Assert.Null(await status.GetPublicStatusAsync("alpha' OR '1'='1", CancellationToken.None));
        Assert.Null(await status.GetPublicStatusAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task The_incidents_on_a_page_are_those_of_its_own_services()
    {
        await using var app = await TestApp.CreateAsync(o => o.FailureThreshold = 1);
        var a = await app.AddOrganizationAsync("alpha");
        var b = await app.AddOrganizationAsync("beta");
        await app.AddServiceAsync(a.Id, "Alpha site");
        await app.AddServiceAsync(b.Id, "Beta site");
        app.Probe.Succeeds = false;
        await WithAsync(app, async p => await p.GetRequiredService<CheckRunner>().RunDueAsync(CancellationToken.None));

        var alpha = await WithAsync(app, p => p.GetRequiredService<StatusQueries>().GetPublicStatusAsync("alpha", CancellationToken.None));

        Assert.Equal(["Alpha site"], alpha!.Incidents.Select(i => i.ServiceName));
        Assert.Equal(OverallStatus.MajorOutage, alpha.Overall);
    }

    [Fact]
    public async Task The_demonstration_organization_is_flagged_as_such()
    {
        await using var app = await TestApp.CreateAsync();
        await app.AddOrganizationAsync(DemoOrganization.Slug);

        var page = await WithAsync(app, p => p.GetRequiredService<StatusQueries>().GetPublicStatusAsync(DemoOrganization.Slug, CancellationToken.None));

        Assert.True(page!.IsDemo);
    }

    // ------------------------------------------------------------ webhooks


    [Fact]
    public async Task A_webhook_to_a_private_address_is_refused()
    {
        await using var app = await TestApp.CreateAsync();
        var org = await app.AddOrganizationAsync();

        foreach (var url in new[] { "http://127.0.0.1/hook", "http://169.254.169.254/latest/meta-data/", "http://10.0.0.5/hook", "http://localhost/hook", "ftp://hooks.example/x", "not a url" })
        {
            var result = await WithAsync(app, p => p.GetRequiredService<WebhookService>().CreateAsync(org.Id, new CreateWebhookRequest(url), CancellationToken.None));
            Assert.False(result.IsSuccess);
            Assert.Contains("url", result.Errors!.Keys);
        }
    }

    [Fact]
    public async Task Another_organizations_webhook_cannot_be_deleted_or_even_listed()
    {
        await using var app = await TestApp.CreateAsync(o => o.AllowPrivateTargets = true);
        var mine = await app.AddOrganizationAsync();
        var theirs = await app.AddOrganizationAsync();
        var created = await WithAsync(app, p => p.GetRequiredService<WebhookService>().CreateAsync(theirs.Id, new CreateWebhookRequest("http://127.0.0.1:9/hook"), CancellationToken.None));
        var id = created.Value!.Webhook.Id;

        Assert.Empty(await WithAsync(app, p => p.GetRequiredService<WebhookService>().ListAsync(mine.Id, CancellationToken.None)));
        Assert.False(await WithAsync(app, p => p.GetRequiredService<WebhookService>().DeleteAsync(mine.Id, id, CancellationToken.None)));
        Assert.Single(await WithAsync(app, p => p.GetRequiredService<WebhookService>().ListAsync(theirs.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task The_secret_is_shown_once_and_stored_only_encrypted()
    {
        await using var app = await TestApp.CreateAsync(o => o.AllowPrivateTargets = true);
        var org = await app.AddOrganizationAsync();

        var created = await WithAsync(app, p => p.GetRequiredService<WebhookService>().CreateAsync(org.Id, new CreateWebhookRequest("http://127.0.0.1:9/hook", AlertFormat.Slack), CancellationToken.None));

        var secret = created.Value!.Secret;
        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);
        await using var scope = app.NewScope();
        var stored = Assert.Single(await scope.ServiceProvider.GetRequiredService<IAccountStore>().ListWebhooksAsync(org.Id, CancellationToken.None));
        Assert.DoesNotContain(secret, stored.SecretCipher, StringComparison.Ordinal);
        Assert.Equal(WebhookFormat.Slack, stored.Format);
        Assert.Equal(secret, scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(stored.SecretCipher));
        // Nothing the API gives back later contains it.
        var listed = await scope.ServiceProvider.GetRequiredService<WebhookService>().ListAsync(org.Id, CancellationToken.None);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(listed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_number_of_webhooks_follows_the_plan()
    {
        await using var app = await TestApp.CreateAsync(o => o.AllowPrivateTargets = true);
        var free = await app.AddOrganizationAsync(plan: Plan.Free);

        var first = await WithAsync(app, p => p.GetRequiredService<WebhookService>().CreateAsync(free.Id, new CreateWebhookRequest("http://127.0.0.1:9/a"), CancellationToken.None));
        var second = await WithAsync(app, p => p.GetRequiredService<WebhookService>().CreateAsync(free.Id, new CreateWebhookRequest("http://127.0.0.1:9/b"), CancellationToken.None));

        Assert.True(first.IsSuccess);
        Assert.Contains("The Free plan allows at most 1 webhooks.", second.Errors!["webhook"]);
    }

    // ------------------------------------------------------------ who receives which alert

    private static async Task<IReadOnlyList<string>> ChannelNamesAsync(TestApp app, Guid organizationId) =>
        await WithAsync(app, async p => (IReadOnlyList<string>)[.. (await p.GetRequiredService<IAlertChannelFactory>().CreateAsync(organizationId, CancellationToken.None)).Select(c => c.Name)]);

    private static async Task<(Organization Org, UserAccount User)> OrganizationWithOwnerAsync(TestApp app, string email, bool verified)
    {
        var org = await app.AddOrganizationAsync();
        await using var scope = app.NewScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var user = UserAccount.Create(org.Id, email, "hash", app.Clock.GetUtcNow());
        if (verified)
        {
            user.MarkEmailVerified(app.Clock.GetUtcNow());
        }

        await accounts.AddUserAsync(user, CancellationToken.None);
        await accounts.SaveChangesAsync(CancellationToken.None);
        return (org, user);
    }

    [Fact]
    public async Task Alerts_go_by_e_mail_to_a_confirmed_owner_and_to_each_webhook_of_that_organization_only()
    {
        await using var app = await TestApp.CreateAsync(o => o.AllowPrivateTargets = true);
        var (mine, _) = await OrganizationWithOwnerAsync(app, "ana@example.com", verified: true);
        var (theirs, _) = await OrganizationWithOwnerAsync(app, "bob@example.com", verified: true);
        await WithAsync(app, p => p.GetRequiredService<WebhookService>().CreateAsync(mine.Id, new CreateWebhookRequest("http://127.0.0.1:9/mine"), CancellationToken.None));
        await WithAsync(app, p => p.GetRequiredService<WebhookService>().CreateAsync(theirs.Id, new CreateWebhookRequest("http://127.0.0.1:9/theirs"), CancellationToken.None));

        var names = await ChannelNamesAsync(app, mine.Id);

        Assert.Equal(["E-mail", "Webhook (127.0.0.1)"], names);
        await using var scope = app.NewScope();
        var channels = await scope.ServiceProvider.GetRequiredService<IAlertChannelFactory>().CreateAsync(mine.Id, CancellationToken.None);
        Assert.Equal(2, channels.Count);

        await channels[0].SendAsync(Alerts.Opened(organizationId: mine.Id), CancellationToken.None);
        Assert.Equal(["ana@example.com"], app.Mail.Sent.Select(m => m.To)); // never to bob
    }

    [Fact]
    public async Task An_unconfirmed_address_receives_no_alert_mail()
    {
        await using var app = await TestApp.CreateAsync();
        var (org, _) = await OrganizationWithOwnerAsync(app, "ana@example.com", verified: false);

        Assert.Empty(await ChannelNamesAsync(app, org.Id));
    }

    [Fact]
    public async Task Switching_e_mail_alerts_off_or_having_no_mail_server_removes_the_e_mail_channel()
    {
        await using var app = await TestApp.CreateAsync();
        var (org, user) = await OrganizationWithOwnerAsync(app, "ana@example.com", verified: true);
        Assert.Equal(["E-mail"], await ChannelNamesAsync(app, org.Id));

        app.Mail.IsAvailable = false;
        Assert.Empty(await ChannelNamesAsync(app, org.Id));

        app.Mail.IsAvailable = true;
        await WithAsync(app, async p => await p.GetRequiredService<AccountService>().UpdateNotificationsAsync(user.Id, new UpdateNotificationsRequest(false), CancellationToken.None));
        Assert.Empty(await ChannelNamesAsync(app, org.Id));
    }

    [Fact]
    public async Task A_webhook_whose_secret_can_no_longer_be_read_is_skipped_not_fatal()
    {
        await using var app = await TestApp.CreateAsync(o => o.AllowPrivateTargets = true);
        var org = await app.AddOrganizationAsync();
        await using (var scope = app.NewScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.AddWebhookAsync(WebhookEndpoint.Create(org.Id, "http://127.0.0.1:9/broken", "v1.AAAA", WebhookFormat.Json, app.Clock.GetUtcNow()), CancellationToken.None);
            await accounts.SaveChangesAsync(CancellationToken.None);
        }

        await WithAsync(app, p => p.GetRequiredService<WebhookService>().CreateAsync(org.Id, new CreateWebhookRequest("http://127.0.0.1:9/good"), CancellationToken.None));
        var names = await ChannelNamesAsync(app, org.Id);

        Assert.Single(names); // the good one only
    }

    [Fact]
    public async Task The_test_message_goes_to_the_callers_channels_and_reports_each_failure()
    {
        await using var app = await TestApp.CreateAsync(o => o.AllowPrivateTargets = true);
        var (org, _) = await OrganizationWithOwnerAsync(app, "ana@example.com", verified: true);
        await WithAsync(app, p => p.GetRequiredService<WebhookService>().CreateAsync(org.Id, new CreateWebhookRequest("http://127.0.0.1:1/down"), CancellationToken.None));

        var results = await WithAsync(app, p => p.GetRequiredService<AlertTester>().TestAsync(org.Id, CancellationToken.None));

        Assert.True(results.Single(r => r.Channel == "E-mail").Success);
        var webhook = results.Single(r => r.Channel.StartsWith("Webhook", StringComparison.Ordinal));
        Assert.False(webhook.Success);
        Assert.Contains("could not be reached", webhook.Error, StringComparison.Ordinal);
        Assert.Single(app.Mail.Sent);
    }

    [Fact]
    public async Task A_resolving_public_address_is_accepted_as_a_webhook_in_production_mode()
    {
        await using var app = await TestApp.CreateAsync(); // private targets NOT allowed
        var org = await app.AddOrganizationAsync();

        var result = await WithAsync(app, p => p.GetRequiredService<WebhookService>().CreateAsync(org.Id, new CreateWebhookRequest("https://shop.test/hook"), CancellationToken.None));

        Assert.True(result.IsSuccess);
    }
}
