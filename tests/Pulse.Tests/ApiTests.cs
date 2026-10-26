using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Pulse.Contracts;

namespace Pulse.Tests;

/// <summary>The whole application started in memory and driven through HTTP, like a real client.</summary>
public class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Api = "/api/v1";

    private static CreateServiceRequest NewService(string name = "Shop", int interval = 300) =>
        new(name, "https://shop.test/health", interval, 10, 200);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>())!;

    private static async Task<HttpValidationProblemDetails> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>())!;

    private static async Task<string> ProblemFieldsAsync(HttpResponseMessage response)
    {
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return $"{json.RootElement.GetProperty("status")}|{json.RootElement.GetProperty("title")}|{json.RootElement.GetProperty("detail")}";
    }

    // ------------------------------------------------------------ open and closed doors

    [Fact]
    public async Task Health_is_open_to_everyone_and_responses_carry_the_security_headers()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
    }

    [Theory]
    [InlineData("GET", "/account")]
    [InlineData("PUT", "/account/organization")]
    [InlineData("PUT", "/account/notifications")]
    [InlineData("PUT", "/account/plan")]
    [InlineData("POST", "/account/password")]
    [InlineData("POST", "/account/delete")]
    [InlineData("GET", "/services")]
    [InlineData("POST", "/services")]
    [InlineData("GET", "/webhooks")]
    [InlineData("POST", "/webhooks")]
    [InlineData("GET", "/alerts")]
    [InlineData("POST", "/alerts/test")]
    [InlineData("POST", "/auth/logout")]
    [InlineData("POST", "/auth/resend-verification")]
    public async Task Everything_behind_the_sign_in_refuses_a_visitor_with_no_token(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), Api + path);

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bearer not-a-real-token")]
    [InlineData("Bearer ")]
    [InlineData("Bearer")]
    [InlineData("Basic YW5hOnBhc3N3b3Jk")]
    [InlineData("Token abc")]
    public async Task A_made_up_or_badly_formed_credential_is_refused(string header)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Api + "/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Api + "/services")).StatusCode);
    }

    [Fact]
    public async Task A_token_of_the_right_length_that_was_never_issued_is_refused()
    {
        var client = factory.ClientFor(new string('A', 43));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Api + "/account")).StatusCode);
    }

    // ------------------------------------------------------------ the journey of a customer

    [Fact]
    public async Task A_customer_signs_up_adds_a_service_and_shares_a_public_page()
    {
        var ana = await factory.SignUpAsync(name: "Ana SARL");
        Assert.Equal(PlanTier.Free, ana.Account.Plan);
        Assert.Equal(5, ana.Account.Usage.ServicesLimit);

        var created = await ana.Client.PostAsJsonAsync(Api + "/services", NewService("Boutique"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var service = await ReadAsync<ServiceDto>(created);
        Assert.Equal(90, service.History!.Count);

        var list = await ReadAsync<List<ServiceDto>>(await ana.Client.GetAsync(Api + "/services"));
        Assert.Equal(["Boutique"], list.Select(s => s.Name));

        // The public page needs no sign-in, names the organization, and never shows the address.
        var page = await factory.CreateClient().GetAsync($"{Api}/public/{ana.Slug}/status");
        var body = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Boutique", body, StringComparison.Ordinal);
        Assert.Contains("Ana SARL", body, StringComparison.Ordinal);
        Assert.DoesNotContain("shop.test", body, StringComparison.Ordinal);

        // The account shows the usage.
        Assert.Equal(1, (await ReadAsync<AccountDto>(await ana.Client.GetAsync(Api + "/account"))).Usage.ServicesUsed);
    }

    [Fact]
    public async Task A_private_service_stays_off_the_public_page_until_it_is_made_public()
    {
        var ana = await factory.SignUpAsync();
        var service = await ReadAsync<ServiceDto>(await ana.Client.PostAsJsonAsync(Api + "/services", NewService("Internal") with { IsPublic = false }));
        var url = $"{Api}/public/{ana.Slug}/status";

        Assert.DoesNotContain("Internal", await factory.CreateClient().GetStringAsync(url), StringComparison.Ordinal);

        var update = new UpdateServiceRequest("Internal", 300, 10, 200, IsPublic: true);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.PutAsJsonAsync($"{Api}/services/{service.Id}", update)).StatusCode);

        Assert.Contains("Internal", await factory.CreateClient().GetStringAsync(url), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_can_be_renamed_checked_and_deleted()
    {
        var ana = await factory.SignUpAsync();
        var service = await ReadAsync<ServiceDto>(await ana.Client.PostAsJsonAsync(Api + "/services", NewService()));

        var renamed = await ana.Client.PutAsJsonAsync($"{Api}/services/{service.Id}", new UpdateServiceRequest("Renamed", 600, 5, 204, true));
        Assert.Equal("Renamed", (await ReadAsync<ServiceDto>(renamed)).Name);
        Assert.Equal(HttpStatusCode.OK, (await ana.Client.GetAsync($"{Api}/services/{service.Id}/checks")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Api}/services/{service.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ana.Client.GetAsync($"{Api}/services/{service.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ana.Client.DeleteAsync($"{Api}/services/{service.Id}")).StatusCode);
    }

    [Fact]
    public async Task Logging_out_makes_the_token_useless_and_logging_in_gives_a_new_one()
    {
        var ana = await factory.SignUpAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.PostAsync(Api + "/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ana.Client.GetAsync(Api + "/account")).StatusCode);

        var login = await factory.CreateClient().PostAsJsonAsync(Api + "/auth/login", new LoginRequest(ana.Email, ApiFactory.Password));
        var auth = await ReadAsync<AuthResponse>(login);
        Assert.NotEqual(ana.Token, auth.Token);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(auth.Token).GetAsync(Api + "/account")).StatusCode);
    }

    // ------------------------------------------------------------ one customer against another

    [Fact]
    public async Task One_customer_cannot_see_or_touch_the_services_of_another_by_guessing_the_id()
    {
        var ana = await factory.SignUpAsync();
        var bob = await factory.SignUpAsync();
        var anas = await ReadAsync<ServiceDto>(await ana.Client.PostAsJsonAsync(Api + "/services", NewService("Ana's")));
        var update = new UpdateServiceRequest("Hijacked", 300, 10, 200, false);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.GetAsync($"{Api}/services/{anas.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.PutAsJsonAsync($"{Api}/services/{anas.Id}", update)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.DeleteAsync($"{Api}/services/{anas.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.GetAsync($"{Api}/services/{anas.Id}/checks")).StatusCode);
        Assert.Empty(await ReadAsync<List<ServiceDto>>(await bob.Client.GetAsync(Api + "/services")));

        var intact = await ReadAsync<ServiceDto>(await ana.Client.GetAsync($"{Api}/services/{anas.Id}"));
        Assert.Equal("Ana's", intact.Name);
        Assert.True(intact.IsPublic);
    }

    [Fact]
    public async Task A_request_cannot_choose_which_organization_a_service_is_created_in()
    {
        var ana = await factory.SignUpAsync();
        var bob = await factory.SignUpAsync();
        var bobsOrganization = (await ReadAsync<AccountDto>(await bob.Client.GetAsync(Api + "/account"))).Slug;

        // Extra fields in the body, as a mass-assignment attempt: they are ignored.
        const string body = """{ "name": "Smuggled", "url": "https://shop.test/x", "intervalSeconds": 300, "timeoutSeconds": 10, "expectedStatusCode": 200, "organizationId": "00000000-0000-0000-0000-000000000000", "slug": "x" }""";
        var response = await ana.Client.PostAsync(Api + "/services", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(await ReadAsync<List<ServiceDto>>(await ana.Client.GetAsync(Api + "/services")));
        Assert.Empty(await ReadAsync<List<ServiceDto>>(await bob.Client.GetAsync(Api + "/services")));
        Assert.DoesNotContain("Smuggled", await factory.CreateClient().GetStringAsync($"{Api}/public/{bobsOrganization}/status"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_customer_cannot_see_or_delete_the_webhooks_of_another()
    {
        var ana = await factory.SignUpAsync();
        var bob = await factory.SignUpAsync();
        var created = await ReadAsync<CreatedWebhookDto>(await ana.Client.PostAsJsonAsync(Api + "/webhooks", new CreateWebhookRequest("https://hooks.test/pulse")));

        Assert.Empty(await ReadAsync<List<WebhookDto>>(await bob.Client.GetAsync(Api + "/webhooks")));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.DeleteAsync($"{Api}/webhooks/{created.Webhook.Id}")).StatusCode);
        Assert.Single(await ReadAsync<List<WebhookDto>>(await ana.Client.GetAsync(Api + "/webhooks")));
    }

    [Fact]
    public async Task Each_account_has_its_own_plan_usage_and_alert_channels()
    {
        var ana = await factory.SignUpAsync();
        var bob = await factory.SignUpAsync();
        await ana.Client.PostAsJsonAsync(Api + "/webhooks", new CreateWebhookRequest("https://hooks.test/pulse"));

        Assert.Single(await ReadAsync<List<AlertChannelDto>>(await ana.Client.GetAsync(Api + "/alerts")));
        Assert.Empty(await ReadAsync<List<AlertChannelDto>>(await bob.Client.GetAsync(Api + "/alerts")));
    }

    [Fact]
    public async Task A_public_page_is_found_only_for_a_real_page_address_and_never_fails_on_odd_ones()
    {
        var ana = await factory.SignUpAsync();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Api}/public/{ana.Slug}/status")).StatusCode);
        foreach (var odd in new[] { "nobody", "%27%20OR%20%271%27%3D%271", "..%2F..%2Fetc%2Fpasswd", new string('a', 500), "ANA" })
        {
            var response = await client.GetAsync($"{Api}/public/{odd}/status");
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{odd} gave {(int)response.StatusCode}");
        }
    }

    // ------------------------------------------------------------ sign-up, sign-in and the account

    [Fact]
    public async Task Signing_up_with_bad_values_reports_every_field_in_a_problem_document()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(Api + "/auth/signup", new SignUpRequest("nope", "short", " ", "A"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(["email", "organizationName", "password", "slug"], problem.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_second_account_with_the_same_address_is_refused_whatever_the_case()
    {
        var ana = await factory.SignUpAsync();

        var response = await factory.CreateClient().PostAsJsonAsync(Api + "/auth/signup", new SignUpRequest(ana.Email.ToUpperInvariant(), ApiFactory.Password, "Other", "other-" + Guid.NewGuid().ToString("N")[..8]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("email", (await ProblemAsync(response)).Errors.Keys);
    }

    [Fact]
    public async Task Demo_and_other_reserved_page_names_cannot_be_taken()
    {
        foreach (var slug in new[] { "demo", "admin", "api", "status" })
        {
            var response = await factory.CreateClient().PostAsJsonAsync(Api + "/auth/signup", new SignUpRequest(ApiFactory.NewEmail(), ApiFactory.Password, "X", slug));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_address_look_the_same_from_outside()
    {
        var ana = await factory.SignUpAsync();
        var client = factory.CreateClient();

        var wrong = await client.PostAsJsonAsync(Api + "/auth/login", new LoginRequest(ana.Email, "not the password"));
        var unknown = await client.PostAsJsonAsync(Api + "/auth/login", new LoginRequest("nobody@example.com", "not the password"));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        // Same title and detail (only the per-request trace id differs).\n        var wrongBody = await ProblemFieldsAsync(wrong);\n        Assert.Equal(wrongBody, await ProblemFieldsAsync(unknown));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_against_the_right_one()
    {
        var ana = await factory.SignUpAsync();
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync(Api + "/auth/login", new LoginRequest(ana.Email, "wrong password " + i));
        }

        var locked = await client.PostAsJsonAsync(Api + "/auth/login", new LoginRequest(ana.Email, ApiFactory.Password));

        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [Fact]
    public async Task The_account_never_exposes_a_password_hash_a_token_or_a_secret()
    {
        var ana = await factory.SignUpAsync();
        await ana.Client.PostAsJsonAsync(Api + "/webhooks", new CreateWebhookRequest("https://hooks.test/pulse"));

        foreach (var path in new[] { "/account", "/services", "/webhooks", "/alerts" })
        {
            var body = await ana.Client.GetStringAsync(Api + path);
            Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("whsec_", body, StringComparison.Ordinal);
            Assert.DoesNotContain(ana.Token, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_address_is_confirmed_by_the_link_sent_by_e_mail_and_the_link_works_once()
    {
        var ana = await factory.SignUpAsync();
        var token = factory.Mail.LastTokenFor(ana.Email)!;
        Assert.False((await ReadAsync<AccountDto>(await ana.Client.GetAsync(Api + "/account"))).EmailVerified);

        var first = await factory.CreateClient().PostAsJsonAsync(Api + "/auth/verify-email", new TokenRequest(token));
        var second = await factory.CreateClient().PostAsJsonAsync(Api + "/auth/verify-email", new TokenRequest(token));

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.True((await ReadAsync<AccountDto>(await ana.Client.GetAsync(Api + "/account"))).EmailVerified);
    }

    [Fact]
    public async Task Forgetting_a_password_answers_the_same_for_a_known_and_an_unknown_address_and_the_link_resets_it()
    {
        var ana = await factory.SignUpAsync();
        var client = factory.CreateClient();

        var known = await client.PostAsJsonAsync(Api + "/auth/forgot-password", new EmailRequest(ana.Email));
        var unknown = await client.PostAsJsonAsync(Api + "/auth/forgot-password", new EmailRequest("nobody@example.com"));
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync()); // both empty

        var token = factory.Mail.LastTokenFor(ana.Email)!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(Api + "/auth/reset-password", new ResetPasswordRequest(token, "a brand new password"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ana.Client.GetAsync(Api + "/account")).StatusCode); // old session closed
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Api + "/auth/login", new LoginRequest(ana.Email, "a brand new password"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Api + "/auth/reset-password", new ResetPasswordRequest(token, "yet another password"))).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_closes_the_other_sessions_only()
    {
        var ana = await factory.SignUpAsync();
        var other = await ReadAsync<AuthResponse>(await factory.CreateClient().PostAsJsonAsync(Api + "/auth/login", new LoginRequest(ana.Email, ApiFactory.Password)));
        var otherClient = factory.ClientFor(other.Token);

        var change = await otherClient.PostAsJsonAsync(Api + "/account/password", new ChangePasswordRequest(ApiFactory.Password, "my new password!"));

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync(Api + "/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ana.Client.GetAsync(Api + "/account")).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_account_needs_the_password_and_then_erases_the_public_page_too()
    {
        var ana = await factory.SignUpAsync();
        await ana.Client.PostAsJsonAsync(Api + "/services", NewService());

        var refused = await ana.Client.PostAsJsonAsync(Api + "/account/delete", new DeleteAccountRequest("not the password"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync($"{Api}/public/{ana.Slug}/status")).StatusCode);

        var deleted = await ana.Client.PostAsJsonAsync(Api + "/account/delete", new DeleteAccountRequest(ApiFactory.Password));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ana.Client.GetAsync(Api + "/account")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"{Api}/public/{ana.Slug}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(Api + "/auth/login", new LoginRequest(ana.Email, ApiFactory.Password))).StatusCode);
    }

    // ------------------------------------------------------------ plans

    [Fact]
    public async Task The_free_plan_refuses_a_sixth_service_and_fast_checks_until_the_plan_is_changed()
    {
        var ana = await factory.SignUpAsync();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await ana.Client.PostAsJsonAsync(Api + "/services", NewService("S" + i))).StatusCode);
        }

        var sixth = await ana.Client.PostAsJsonAsync(Api + "/services", NewService("S5"));
        var fast = await ana.Client.PostAsJsonAsync(Api + "/services", NewService("Fast", interval: 30));
        Assert.Equal(HttpStatusCode.BadRequest, sixth.StatusCode);
        Assert.Contains("service", (await ProblemAsync(sixth)).Errors.Keys);
        Assert.Contains("intervalSeconds", (await ProblemAsync(fast)).Errors.Keys);

        var pro = await ana.Client.PutAsJsonAsync(Api + "/account/plan", new ChangePlanRequest(PlanTier.Pro));
        Assert.Equal(PlanTier.Pro, (await ReadAsync<AccountDto>(pro)).Plan);
        Assert.Equal(HttpStatusCode.Created, (await ana.Client.PostAsJsonAsync(Api + "/services", NewService("S5"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ana.Client.PostAsJsonAsync(Api + "/services", NewService("Fast", interval: 30))).StatusCode);
    }

    [Fact]
    public async Task The_free_plan_allows_one_webhook()
    {
        var ana = await factory.SignUpAsync();

        var first = await ana.Client.PostAsJsonAsync(Api + "/webhooks", new CreateWebhookRequest("https://hooks.test/a"));
        var second = await ana.Client.PostAsJsonAsync(Api + "/webhooks", new CreateWebhookRequest("https://hooks.test/b"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    // ------------------------------------------------------------ webhooks

    [Fact]
    public async Task A_webhook_is_created_with_a_secret_shown_once_and_can_be_deleted()
    {
        var ana = await factory.SignUpAsync();

        var created = await ReadAsync<CreatedWebhookDto>(await ana.Client.PostAsJsonAsync(Api + "/webhooks", new CreateWebhookRequest("https://hooks.test/pulse", AlertFormat.Discord)));

        Assert.StartsWith("whsec_", created.Secret, StringComparison.Ordinal);
        Assert.Equal(AlertFormat.Discord, created.Webhook.Format);
        var listed = await ana.Client.GetStringAsync(Api + "/webhooks");
        Assert.DoesNotContain(created.Secret, listed, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Api}/webhooks/{created.Webhook.Id}")).StatusCode);
        Assert.Empty(await ReadAsync<List<WebhookDto>>(await ana.Client.GetAsync(Api + "/webhooks")));
    }

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://intranet.test/hook")]
    [InlineData("https://hooks.test:22/hook")]
    [InlineData("ftp://hooks.test/hook")]
    [InlineData("https://user:secret@hooks.test/hook")]
    [InlineData("not a url")]
    public async Task A_webhook_pointing_inside_the_network_is_refused_with_a_field_error(string url)
    {
        var ana = await factory.SignUpAsync();

        var response = await ana.Client.PostAsJsonAsync(Api + "/webhooks", new CreateWebhookRequest(url));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("url", (await ProblemAsync(response)).Errors.Keys);
    }

    [Fact]
    public async Task A_service_pointing_inside_the_network_is_refused_with_a_field_error()
    {
        var ana = await factory.SignUpAsync();

        foreach (var url in new[] { "http://127.0.0.1/", "http://169.254.169.254/latest/meta-data/", "http://intranet.test/", "https://shop.test:6379/", "file:///etc/passwd" })
        {
            var response = await ana.Client.PostAsJsonAsync(Api + "/services", NewService() with { Url = url });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("url", (await ProblemAsync(response)).Errors.Keys);
        }
    }

    [Fact]
    public async Task The_test_alert_reports_nothing_to_send_to_when_no_channel_is_set_up()
    {
        var ana = await factory.SignUpAsync();

        var response = await ana.Client.PostAsync(Api + "/alerts/test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadAsync<List<AlertTestResultDto>>(response));
    }

    // ------------------------------------------------------------ malformed requests

    [Fact]
    public async Task Malformed_json_is_a_client_error_not_a_crash()
    {
        var ana = await factory.SignUpAsync();

        var response = await ana.Client.PostAsync(Api + "/services", new StringContent("{ not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_content_type_is_refused_and_an_enormous_name_is_a_validation_error()
    {
        var ana = await factory.SignUpAsync();

        var wrongType = await ana.Client.PostAsync(Api + "/services", new StringContent("name=x", Encoding.UTF8, "text/plain"));
        var huge = await ana.Client.PostAsJsonAsync(Api + "/services", NewService(new string('x', 2_000_000)));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.True(huge.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_service_id_that_is_not_a_guid_is_not_found()
    {
        var ana = await factory.SignUpAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await ana.Client.GetAsync(Api + "/services/abc")).StatusCode);
    }
}

public class StrictAuthApiTests(StrictAuthApiFactory factory) : IClassFixture<StrictAuthApiFactory>
{
    [Fact]
    public async Task Sign_in_attempts_are_limited_per_address_so_guessing_is_slow()
    {
        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 9; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.com", "whatever password"))).StatusCode);
        }

        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task The_limit_on_sign_in_does_not_slow_down_the_rest_of_the_api()
    {
        var client = factory.CreateClient();
        for (var i = 0; i < 8; i++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.com", "whatever password"));
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}

public class DemoApiTests(DemoApiFactory factory) : IClassFixture<DemoApiFactory>
{
    [Fact]
    public async Task The_demonstration_page_exists_is_flagged_and_has_ninety_days_of_history()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/public/demo/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<PublicStatusDto>())!;
        Assert.True(page.IsDemo);
        Assert.Equal(3, page.Services.Count);
        Assert.All(page.Services, s => Assert.Equal(90, s.History.Count));
        Assert.NotEmpty(page.Incidents);
    }

    [Fact]
    public async Task No_one_can_sign_up_as_the_demonstration_organization()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/signup", new SignUpRequest(ApiFactory.NewEmail(), ApiFactory.Password, "Fake demo", "demo"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public class LockedPlansApiTests(LockedPlansApiFactory factory) : IClassFixture<LockedPlansApiFactory>
{
    [Fact]
    public async Task With_automatic_verification_the_address_is_confirmed_from_the_start_and_no_mail_is_sent()
    {
        var ana = await factory.SignUpAsync();

        Assert.True(ana.Account.EmailVerified);
        Assert.Empty(factory.Mail.Sent);
    }

    [Fact]
    public async Task When_the_server_forbids_it_the_plan_cannot_be_changed()
    {
        var ana = await factory.SignUpAsync();

        var response = await ana.Client.PutAsJsonAsync("/api/v1/account/plan", new ChangePlanRequest(PlanTier.Pro));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(ana.Account.CanChangePlan);
    }
}
