using Microsoft.Extensions.DependencyInjection;
using Pulse.Application;
using Pulse.Application.Accounts;
using Pulse.Contracts;
using Pulse.Domain;

namespace Pulse.Tests;

public class AccountServiceTests
{
    private const string Password = "correct horse battery";

    private static SignUpRequest Request(string email = "ana@example.com", string slug = "ana", string password = Password) =>
        new(email, password, "Ana SARL", slug);

    private static async Task<AuthResponse> SignUpAsync(TestApp app, SignUpRequest? request = null)
    {
        await using var scope = app.NewScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountService>().SignUpAsync(request ?? Request(), CancellationToken.None);
        Assert.True(result.IsSuccess, string.Join(" ", result.Errors?.SelectMany(e => e.Value) ?? []));
        return result.Value!;
    }

    private static async Task<Result<AuthResponse>> TrySignUpAsync(TestApp app, SignUpRequest request)
    {
        await using var scope = app.NewScope();
        return await scope.ServiceProvider.GetRequiredService<AccountService>().SignUpAsync(request, CancellationToken.None);
    }

    private static async Task<Result<AuthResponse>> LoginAsync(TestApp app, string email = "ana@example.com", string password = Password)
    {
        await using var scope = app.NewScope();
        return await scope.ServiceProvider.GetRequiredService<AccountService>().LoginAsync(new LoginRequest(email, password), CancellationToken.None);
    }

    private static async Task<AuthenticatedUser?> WhoAsync(TestApp app, string? token)
    {
        await using var scope = app.NewScope();
        return await scope.ServiceProvider.GetRequiredService<AccountService>().AuthenticateAsync(token, CancellationToken.None);
    }

    private static async Task<T> WithAccountsAsync<T>(TestApp app, Func<AccountService, Task<T>> action)
    {
        await using var scope = app.NewScope();
        return await action(scope.ServiceProvider.GetRequiredService<AccountService>());
    }

    // ------------------------------------------------------------ sign-up

    [Fact]
    public async Task Signing_up_creates_the_account_its_organization_and_a_session()
    {
        await using var app = await TestApp.CreateAsync();

        var auth = await SignUpAsync(app);

        Assert.Equal("ana@example.com", auth.Account.Email);
        Assert.Equal("Ana SARL", auth.Account.OrganizationName);
        Assert.Equal("ana", auth.Account.Slug);
        Assert.Equal(PlanTier.Free, auth.Account.Plan);
        Assert.False(auth.Account.EmailVerified);
        Assert.Equal(0, auth.Account.Usage.ServicesUsed);
        Assert.Equal(5, auth.Account.Usage.ServicesLimit);
        Assert.NotNull(await WhoAsync(app, auth.Token));
        Assert.True(auth.ExpiresAt > app.Clock.GetUtcNow().AddDays(6));
    }

    [Fact]
    public async Task Signing_up_sends_a_confirmation_link_to_the_new_address()
    {
        await using var app = await TestApp.CreateAsync();

        await SignUpAsync(app);

        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("ana@example.com", mail.To);
        Assert.Contains("/verify-email?token=", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mail_server_that_fails_does_not_prevent_signing_up()
    {
        await using var app = await TestApp.CreateAsync();
        app.Mail.Failing = true;

        var auth = await SignUpAsync(app);

        Assert.False(auth.Account.EmailVerified);
    }

    [Fact]
    public async Task With_automatic_verification_the_address_is_confirmed_and_no_mail_is_sent()
    {
        await using var app = await TestApp.CreateAsync(configureAccounts: o => o.AutoVerifyEmail = true);

        var auth = await SignUpAsync(app);

        Assert.True(auth.Account.EmailVerified);
        Assert.Empty(app.Mail.Sent);
    }

    [Fact]
    public async Task The_address_is_stored_in_lower_case_and_a_second_account_with_it_is_refused_whatever_the_case()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app, Request(email: "  Ana@Example.COM "));

        var again = await TrySignUpAsync(app, Request(email: "ana@example.com", slug: "other"));

        Assert.False(again.IsSuccess);
        Assert.Contains(ApiMessages.EmailTaken, again.Errors!["email"]);
        Assert.NotNull((await LoginAsync(app, "ANA@example.com")).Value);
    }

    [Theory]
    [InlineData("", "email")]
    [InlineData("not-an-address", "email")]
    [InlineData("ana@localhost", "email")]
    [InlineData("Ana <ana@example.com>", "email")]
    [InlineData("a b@example.com", "email")]
    public async Task An_invalid_address_is_refused(string email, string field)
    {
        await using var app = await TestApp.CreateAsync();

        var result = await TrySignUpAsync(app, Request(email: email));

        Assert.False(result.IsSuccess);
        Assert.Contains(field, result.Errors!.Keys);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("123456789")]
    [InlineData("")]
    public async Task A_password_shorter_than_ten_characters_is_refused(string password)
    {
        await using var app = await TestApp.CreateAsync();

        var result = await TrySignUpAsync(app, Request(password: password));

        Assert.Contains(ApiMessages.PasswordTooShort, result.Errors!["password"]);
    }

    [Fact]
    public async Task A_password_that_is_the_address_or_is_enormous_is_refused()
    {
        await using var app = await TestApp.CreateAsync();

        Assert.Contains(ApiMessages.PasswordIsEmail, (await TrySignUpAsync(app, Request(email: "ana.dupont@example.com", password: "ana.dupont@example.com"))).Errors!["password"]);
        Assert.Contains(ApiMessages.PasswordTooLong, (await TrySignUpAsync(app, Request(password: new string('x', 129)))).Errors!["password"]);
    }

    [Theory]
    [InlineData("ab", ApiMessages.SlugInvalid)]
    [InlineData("Ana", ApiMessages.SlugInvalid)]
    [InlineData("ana_corp", ApiMessages.SlugInvalid)]
    [InlineData("-ana", ApiMessages.SlugInvalid)]
    [InlineData("ana-", ApiMessages.SlugInvalid)]
    [InlineData("ana corp", ApiMessages.SlugInvalid)]
    [InlineData("ana/../admin", ApiMessages.SlugInvalid)]
    [InlineData("admin", ApiMessages.SlugReserved)]
    [InlineData("demo", ApiMessages.SlugReserved)]
    [InlineData("api", ApiMessages.SlugReserved)]
    public async Task A_page_address_that_is_malformed_or_reserved_is_refused(string slug, string message)
    {
        await using var app = await TestApp.CreateAsync();

        var result = await TrySignUpAsync(app, Request(slug: slug));

        Assert.Contains(message, result.Errors!["slug"]);
    }

    [Fact]
    public async Task A_page_address_already_taken_is_refused()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);

        var result = await TrySignUpAsync(app, Request(email: "bob@example.com"));

        Assert.Contains(ApiMessages.SlugTaken, result.Errors!["slug"]);
    }

    [Fact]
    public async Task Every_problem_with_a_sign_up_is_reported_at_once()
    {
        await using var app = await TestApp.CreateAsync();

        var result = await TrySignUpAsync(app, new SignUpRequest("nope", "short", " ", "A"));

        Assert.Equal(["email", "organizationName", "password", "slug"], result.Errors!.Keys.Order(StringComparer.Ordinal));
    }

    // ------------------------------------------------------------ login

    [Fact]
    public async Task The_right_password_gives_a_new_session()
    {
        await using var app = await TestApp.CreateAsync();
        var first = await SignUpAsync(app);

        var login = await LoginAsync(app);

        Assert.True(login.IsSuccess);
        Assert.NotEqual(first.Token, login.Value!.Token);
        Assert.NotNull(await WhoAsync(app, login.Value.Token));
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_address_give_exactly_the_same_answer()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);

        var wrongPassword = await LoginAsync(app, password: "not the password");
        var unknownAddress = await LoginAsync(app, "nobody@example.com");

        Assert.True(wrongPassword.IsUnauthenticated);
        Assert.True(unknownAddress.IsUnauthenticated);
        Assert.Equal(wrongPassword.Errors![Result.CredentialsKey], unknownAddress.Errors![Result.CredentialsKey]);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_for_fifteen_minutes_even_for_the_right_password()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);

        for (var i = 0; i < UserAccount.MaxFailedLogins; i++)
        {
            Assert.False((await LoginAsync(app, password: "wrong password " + i)).IsSuccess);
        }

        Assert.False((await LoginAsync(app)).IsSuccess); // locked: the right password does not help
        app.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.False((await LoginAsync(app)).IsSuccess);
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True((await LoginAsync(app)).IsSuccess);
    }

    [Fact]
    public async Task A_successful_login_resets_the_count_of_failures()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < UserAccount.MaxFailedLogins - 1; i++)
            {
                await LoginAsync(app, password: "wrong password");
            }

            Assert.True((await LoginAsync(app)).IsSuccess); // never reaches the fifth in a row
        }
    }

    // ------------------------------------------------------------ sessions

    [Fact]
    public async Task Only_the_hash_of_a_session_token_is_stored()
    {
        await using var app = await TestApp.CreateAsync();
        var auth = await SignUpAsync(app);

        await using var scope = app.NewScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var user = await accounts.FindUserByEmailAsync("ana@example.com", CancellationToken.None);
        var session = Assert.Single(await accounts.ListSessionsAsync(user!.Id, CancellationToken.None));

        Assert.NotEqual(auth.Token, session.TokenHash);
        Assert.Equal(Tokens.Hash(auth.Token), session.TokenHash);
        Assert.Equal(64, session.TokenHash.Length);
        Assert.DoesNotContain(auth.Token, session.TokenHash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task An_unknown_token_authenticates_nobody(string? token)
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);

        Assert.Null(await WhoAsync(app, token));
    }

    [Fact]
    public async Task A_token_stops_working_after_its_lifetime()
    {
        await using var app = await TestApp.CreateAsync(configureAccounts: o => o.SessionDays = 7);
        var auth = await SignUpAsync(app);

        app.Clock.Advance(TimeSpan.FromDays(6));
        Assert.NotNull(await WhoAsync(app, auth.Token));
        app.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Null(await WhoAsync(app, auth.Token));
    }

    [Fact]
    public async Task Logging_out_closes_that_session_only()
    {
        await using var app = await TestApp.CreateAsync();
        var phone = await SignUpAsync(app);
        var laptop = (await LoginAsync(app)).Value!;

        await WithAccountsAsync(app, async a => { await a.LogoutAsync(phone.Token, CancellationToken.None); return 0; });

        Assert.Null(await WhoAsync(app, phone.Token));
        Assert.NotNull(await WhoAsync(app, laptop.Token));
    }

    [Fact]
    public async Task A_person_keeps_at_most_ten_sessions_and_the_oldest_ones_are_closed_first()
    {
        await using var app = await TestApp.CreateAsync();
        var first = await SignUpAsync(app);

        var latest = first;
        for (var i = 0; i < 12; i++)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(1));
            latest = (await LoginAsync(app)).Value!;
        }

        Assert.Null(await WhoAsync(app, first.Token));
        Assert.NotNull(await WhoAsync(app, latest.Token));
        await using var scope = app.NewScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var user = await accounts.FindUserByEmailAsync("ana@example.com", CancellationToken.None);
        Assert.Equal(10, (await accounts.ListSessionsAsync(user!.Id, CancellationToken.None)).Count);
    }

    // ------------------------------------------------------------ confirming the address

    [Fact]
    public async Task The_confirmation_link_confirms_the_address_once()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);
        var token = app.Mail.LastTokenFor("ana@example.com")!;

        var first = await WithAccountsAsync(app, a => a.VerifyEmailAsync(token, CancellationToken.None));
        var again = await WithAccountsAsync(app, a => a.VerifyEmailAsync(token, CancellationToken.None));

        Assert.True(first.IsSuccess);
        Assert.False(again.IsSuccess);
        var account = await WithAccountsAsync(app, async a => await a.GetAccountAsync((await WhoAsync(app, (await LoginAsync(app)).Value!.Token))!.UserId, CancellationToken.None));
        Assert.True(account!.EmailVerified);
    }

    [Fact]
    public async Task A_confirmation_link_expires_after_a_day()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);
        var token = app.Mail.LastTokenFor("ana@example.com")!;

        app.Clock.Advance(TimeSpan.FromHours(25));

        Assert.False((await WithAccountsAsync(app, a => a.VerifyEmailAsync(token, CancellationToken.None))).IsSuccess);
    }

    [Fact]
    public async Task A_password_reset_link_cannot_confirm_an_address_and_a_made_up_token_confirms_nothing()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);
        await WithAccountsAsync(app, async a => { await a.ForgotPasswordAsync("ana@example.com", CancellationToken.None); return 0; });
        var resetToken = app.Mail.LastTokenFor("ana@example.com")!;

        Assert.False((await WithAccountsAsync(app, a => a.VerifyEmailAsync(resetToken, CancellationToken.None))).IsSuccess);
        Assert.False((await WithAccountsAsync(app, a => a.VerifyEmailAsync("made-up", CancellationToken.None))).IsSuccess);
        Assert.False((await WithAccountsAsync(app, a => a.VerifyEmailAsync(null, CancellationToken.None))).IsSuccess);
    }

    [Fact]
    public async Task A_new_confirmation_link_replaces_the_previous_one()
    {
        await using var app = await TestApp.CreateAsync();
        var auth = await SignUpAsync(app);
        var old = app.Mail.LastTokenFor("ana@example.com")!;

        var resent = await WithAccountsAsync(app, async a => await a.ResendVerificationAsync((await WhoAsync(app, auth.Token))!.UserId, CancellationToken.None));
        var fresh = app.Mail.LastTokenFor("ana@example.com")!;

        Assert.True(resent.IsSuccess);
        Assert.NotEqual(old, fresh);
        Assert.False((await WithAccountsAsync(app, a => a.VerifyEmailAsync(old, CancellationToken.None))).IsSuccess);
        Assert.True((await WithAccountsAsync(app, a => a.VerifyEmailAsync(fresh, CancellationToken.None))).IsSuccess);
    }

    [Fact]
    public async Task Asking_for_a_new_link_when_the_server_cannot_send_mail_says_so()
    {
        await using var app = await TestApp.CreateAsync();
        var auth = await SignUpAsync(app);
        app.Mail.IsAvailable = false;

        var result = await WithAccountsAsync(app, async a => await a.ResendVerificationAsync((await WhoAsync(app, auth.Token))!.UserId, CancellationToken.None));

        Assert.Contains(ApiMessages.MailUnavailable, result.Errors!["email"]);
    }

    // ------------------------------------------------------------ forgotten password

    [Fact]
    public async Task Forgetting_the_password_of_an_unknown_address_sends_nothing_and_reveals_nothing()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);
        var before = app.Mail.Sent.Count;

        await WithAccountsAsync(app, async a => { await a.ForgotPasswordAsync("nobody@example.com", CancellationToken.None); return 0; });

        Assert.Equal(before, app.Mail.Sent.Count);
    }

    [Fact]
    public async Task A_reset_link_sets_a_new_password_closes_every_session_and_works_once()
    {
        await using var app = await TestApp.CreateAsync();
        var auth = await SignUpAsync(app);
        await WithAccountsAsync(app, async a => { await a.ForgotPasswordAsync("ANA@example.com", CancellationToken.None); return 0; });
        var token = app.Mail.LastTokenFor("ana@example.com")!;

        var reset = await WithAccountsAsync(app, a => a.ResetPasswordAsync(new ResetPasswordRequest(token, "a brand new password"), CancellationToken.None));

        Assert.True(reset.IsSuccess);
        Assert.Null(await WhoAsync(app, auth.Token)); // whoever was signed in is signed out
        Assert.False((await LoginAsync(app, password: Password)).IsSuccess);
        Assert.True((await LoginAsync(app, password: "a brand new password")).IsSuccess);
        Assert.False((await WithAccountsAsync(app, a => a.ResetPasswordAsync(new ResetPasswordRequest(token, "yet another password"), CancellationToken.None))).IsSuccess);
    }

    [Fact]
    public async Task A_weak_new_password_is_refused_and_the_link_stays_usable()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);
        await WithAccountsAsync(app, async a => { await a.ForgotPasswordAsync("ana@example.com", CancellationToken.None); return 0; });
        var token = app.Mail.LastTokenFor("ana@example.com")!;

        var weak = await WithAccountsAsync(app, a => a.ResetPasswordAsync(new ResetPasswordRequest(token, "short"), CancellationToken.None));
        var strong = await WithAccountsAsync(app, a => a.ResetPasswordAsync(new ResetPasswordRequest(token, "long enough password"), CancellationToken.None));

        Assert.Contains(ApiMessages.PasswordTooShort, weak.Errors!["password"]);
        Assert.True(strong.IsSuccess);
    }

    [Fact]
    public async Task A_reset_link_expires_after_an_hour()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);
        await WithAccountsAsync(app, async a => { await a.ForgotPasswordAsync("ana@example.com", CancellationToken.None); return 0; });
        var token = app.Mail.LastTokenFor("ana@example.com")!;

        app.Clock.Advance(TimeSpan.FromMinutes(61));

        Assert.False((await WithAccountsAsync(app, a => a.ResetPasswordAsync(new ResetPasswordRequest(token, "long enough password"), CancellationToken.None))).IsSuccess);
    }

    [Fact]
    public async Task A_reset_unlocks_a_locked_account()
    {
        await using var app = await TestApp.CreateAsync();
        await SignUpAsync(app);
        for (var i = 0; i < UserAccount.MaxFailedLogins; i++)
        {
            await LoginAsync(app, password: "wrong password");
        }

        await WithAccountsAsync(app, async a => { await a.ForgotPasswordAsync("ana@example.com", CancellationToken.None); return 0; });
        var token = app.Mail.LastTokenFor("ana@example.com")!;
        await WithAccountsAsync(app, a => a.ResetPasswordAsync(new ResetPasswordRequest(token, "long enough password"), CancellationToken.None));

        Assert.True((await LoginAsync(app, password: "long enough password")).IsSuccess);
    }

    // ------------------------------------------------------------ changing the password and the account

    [Fact]
    public async Task Changing_the_password_closes_the_other_sessions_and_keeps_the_current_one()
    {
        await using var app = await TestApp.CreateAsync();
        var phone = await SignUpAsync(app);
        var laptop = (await LoginAsync(app)).Value!;
        var who = (await WhoAsync(app, laptop.Token))!;

        var result = await WithAccountsAsync(app, a => a.ChangePasswordAsync(who, new ChangePasswordRequest(Password, "my new password!"), CancellationToken.None));

        Assert.True(result.IsSuccess);
        Assert.Null(await WhoAsync(app, phone.Token));
        Assert.NotNull(await WhoAsync(app, laptop.Token));
        Assert.True((await LoginAsync(app, password: "my new password!")).IsSuccess);
        Assert.False((await LoginAsync(app, password: Password)).IsSuccess);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one()
    {
        await using var app = await TestApp.CreateAsync();
        var auth = await SignUpAsync(app);
        var who = (await WhoAsync(app, auth.Token))!;

        var result = await WithAccountsAsync(app, a => a.ChangePasswordAsync(who, new ChangePasswordRequest("not the password", "my new password!"), CancellationToken.None));

        Assert.Contains(ApiMessages.WrongCurrentPassword, result.Errors!["currentPassword"]);
        Assert.True((await LoginAsync(app)).IsSuccess);
    }

    [Fact]
    public async Task The_organization_can_be_renamed_but_keeps_its_page_address()
    {
        await using var app = await TestApp.CreateAsync();
        var auth = await SignUpAsync(app);
        var who = (await WhoAsync(app, auth.Token))!;

        var renamed = await WithAccountsAsync(app, a => a.UpdateOrganizationAsync(who.UserId, new UpdateOrganizationRequest("  Nouveau nom  "), CancellationToken.None));
        var empty = await WithAccountsAsync(app, a => a.UpdateOrganizationAsync(who.UserId, new UpdateOrganizationRequest("   "), CancellationToken.None));

        Assert.Equal("Nouveau nom", renamed.Value!.OrganizationName);
        Assert.Equal("ana", renamed.Value.Slug);
        Assert.False(empty.IsSuccess);
    }

    [Fact]
    public async Task E_mail_alerts_can_be_switched_off_and_on()
    {
        await using var app = await TestApp.CreateAsync();
        var who = (await WhoAsync(app, (await SignUpAsync(app)).Token))!;

        var off = await WithAccountsAsync(app, a => a.UpdateNotificationsAsync(who.UserId, new UpdateNotificationsRequest(false), CancellationToken.None));
        var on = await WithAccountsAsync(app, a => a.UpdateNotificationsAsync(who.UserId, new UpdateNotificationsRequest(true), CancellationToken.None));

        Assert.False(off.Value!.EmailAlerts);
        Assert.True(on.Value!.EmailAlerts);
    }

    // ------------------------------------------------------------ plans

    [Fact]
    public async Task The_plan_can_be_changed_when_the_server_allows_it_and_the_limits_follow()
    {
        await using var app = await TestApp.CreateAsync();
        var who = (await WhoAsync(app, (await SignUpAsync(app)).Token))!;

        var pro = await WithAccountsAsync(app, a => a.ChangePlanAsync(who.UserId, new ChangePlanRequest(PlanTier.Pro), CancellationToken.None));

        Assert.Equal(PlanTier.Pro, pro.Value!.Plan);
        Assert.Equal(50, pro.Value.Usage.ServicesLimit);
        Assert.Equal(30, pro.Value.Usage.MinIntervalSeconds);
    }

    [Fact]
    public async Task Changing_plan_is_refused_when_the_server_does_not_allow_it()
    {
        await using var app = await TestApp.CreateAsync(configureAccounts: o => o.AllowSelfServicePlanChange = false);
        var who = (await WhoAsync(app, (await SignUpAsync(app)).Token))!;

        var result = await WithAccountsAsync(app, a => a.ChangePlanAsync(who.UserId, new ChangePlanRequest(PlanTier.Pro), CancellationToken.None));

        Assert.Contains(ApiMessages.PlanChangeDisabled, result.Errors!["plan"]);
    }

    [Fact]
    public async Task Going_back_to_the_free_plan_is_refused_while_the_account_is_over_its_limits()
    {
        await using var app = await TestApp.CreateAsync();
        var who = (await WhoAsync(app, (await SignUpAsync(app)).Token))!;
        await WithAccountsAsync(app, a => a.ChangePlanAsync(who.UserId, new ChangePlanRequest(PlanTier.Pro), CancellationToken.None));
        for (var i = 0; i < 6; i++)
        {
            await app.AddServiceAsync(who.OrganizationId, "S" + i, intervalSeconds: 300);
        }

        var result = await WithAccountsAsync(app, a => a.ChangePlanAsync(who.UserId, new ChangePlanRequest(PlanTier.Free), CancellationToken.None));

        Assert.Contains("The Free plan allows at most 5 services.", result.Errors!["plan"]);
    }

    [Fact]
    public async Task Going_back_to_the_free_plan_is_refused_while_a_service_is_checked_too_often()
    {
        await using var app = await TestApp.CreateAsync();
        var who = (await WhoAsync(app, (await SignUpAsync(app)).Token))!;
        await WithAccountsAsync(app, a => a.ChangePlanAsync(who.UserId, new ChangePlanRequest(PlanTier.Pro), CancellationToken.None));
        await app.AddServiceAsync(who.OrganizationId, intervalSeconds: 30);

        var result = await WithAccountsAsync(app, a => a.ChangePlanAsync(who.UserId, new ChangePlanRequest(PlanTier.Free), CancellationToken.None));

        Assert.Contains("The Free plan does not allow checks more often than every 300 seconds.", result.Errors!["plan"]);
    }

    // ------------------------------------------------------------ deleting the account

    [Fact]
    public async Task Deleting_the_account_needs_the_password_and_changes_nothing_without_it()
    {
        await using var app = await TestApp.CreateAsync();
        var auth = await SignUpAsync(app);
        var who = (await WhoAsync(app, auth.Token))!;

        var result = await WithAccountsAsync(app, a => a.DeleteAccountAsync(who.UserId, new DeleteAccountRequest("not the password"), CancellationToken.None));

        Assert.Contains(ApiMessages.WrongPassword, result.Errors!["password"]);
        Assert.NotNull(await WhoAsync(app, auth.Token));
    }

    [Fact]
    public async Task Deleting_the_account_erases_everything_it_owns_and_nothing_else()
    {
        await using var app = await TestApp.CreateAsync();
        var auth = await SignUpAsync(app);
        var who = (await WhoAsync(app, auth.Token))!;
        var mine = await app.AddServiceAsync(who.OrganizationId, "Mine");
        var other = await app.AddOrganizationAsync("other");
        var theirs = await app.AddServiceAsync(other.Id, "Theirs");
        await using (var scope = app.NewScope())
        {
            await scope.ServiceProvider.GetRequiredService<CheckRunner>().RunDueAsync(CancellationToken.None); // history for both
        }

        var result = await WithAccountsAsync(app, a => a.DeleteAccountAsync(who.UserId, new DeleteAccountRequest(Password), CancellationToken.None));

        Assert.True(result.IsSuccess);
        await using var verify = app.NewScope();
        var store = verify.ServiceProvider.GetRequiredService<IPulseStore>();
        var accounts = verify.ServiceProvider.GetRequiredService<IAccountStore>();
        Assert.Null(await WhoAsync(app, auth.Token));
        Assert.Null(await accounts.FindUserByEmailAsync("ana@example.com", CancellationToken.None));
        Assert.False(await accounts.SlugExistsAsync("ana", CancellationToken.None));
        Assert.Null(await store.FindServiceAsync(who.OrganizationId, mine.Id, CancellationToken.None));
        Assert.Empty(await store.ListRecentChecksAsync(mine.Id, 10, CancellationToken.None));
        Assert.NotNull(await store.FindServiceAsync(other.Id, theirs.Id, CancellationToken.None));
        Assert.NotEmpty(await store.ListRecentChecksAsync(theirs.Id, 10, CancellationToken.None));
        Assert.False((await LoginAsync(app)).IsSuccess);
    }
}
