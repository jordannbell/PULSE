using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pulse.Contracts;
using Pulse.Domain;

namespace Pulse.Application.Accounts;

/// <summary>Who is asking, once their token has been checked.</summary>
public sealed record AuthenticatedUser(Guid UserId, Guid OrganizationId, Guid SessionId);

/// <summary>Sign-up, login and everything a person does to their own account.</summary>
public sealed class AccountService(
    IAccountStore accounts,
    IPulseStore pulse,
    IPasswordHasher hasher,
    IMailSender mail,
    TimeProvider clock,
    IOptions<AccountOptions> options,
    ILogger<AccountService> logger)
{
    private const int MaxSessionsPerUser = 10;
    private static readonly TimeSpan VerifyLinkLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan ResetLinkLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    // ------------------------------------------------------------ sign up and in

    public async Task<Result<AuthResponse>> SignUpAsync(SignUpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>();
        var email = UserAccount.NormalizeEmail(request.Email ?? string.Empty);
        var name = request.OrganizationName?.Trim() ?? string.Empty;
        var slug = request.Slug?.Trim() ?? string.Empty;

        if (!EmailPolicy.IsValid(email))
        {
            errors["email"] = [ApiMessages.InvalidEmail];
        }

        if (PasswordPolicy.Check(request.Password, email) is { } passwordProblem)
        {
            errors["password"] = [passwordProblem];
        }

        if (name.Length is 0 or > Organization.NameMaxLength)
        {
            errors["organizationName"] = [ApiMessages.OrganizationNameLength];
        }

        if (!Organization.IsValidSlug(slug))
        {
            errors["slug"] = [ApiMessages.SlugInvalid];
        }
        else if (SlugPolicy.IsReserved(slug))
        {
            errors["slug"] = [ApiMessages.SlugReserved];
        }

        if (errors.Count == 0)
        {
            if (await accounts.FindUserByEmailAsync(email, cancellationToken) is not null)
            {
                errors["email"] = [ApiMessages.EmailTaken];
            }

            if (await accounts.SlugExistsAsync(slug, cancellationToken))
            {
                errors["slug"] = [ApiMessages.SlugTaken];
            }
        }

        if (errors.Count > 0)
        {
            return Result.Invalid<AuthResponse>(errors);
        }

        var now = clock.GetUtcNow();
        var organization = Organization.Create(name, slug, now);
        var user = UserAccount.Create(organization.Id, email, hasher.Hash(request.Password), now);
        if (options.Value.AutoVerifyEmail)
        {
            user.MarkEmailVerified(now);
        }

        await accounts.AddOrganizationAsync(organization, cancellationToken);
        await accounts.AddUserAsync(user, cancellationToken);
        var (session, token) = NewSession(user.Id, now);
        await accounts.AddSessionAsync(session, cancellationToken);

        // Two sign-ups with the same address or name at the same moment: the database's unique index decides.
        if (!await accounts.TrySaveChangesAsync(cancellationToken))
        {
            return Result.Invalid<AuthResponse>("email", ApiMessages.EmailTaken);
        }

        if (!user.IsEmailVerified)
        {
            await SendLinkAsync(user, EmailTokenPurpose.VerifyEmail, cancellationToken);
        }

        logger.LogInformation("Account created for organization {OrganizationId}", organization.Id);
        return Result.Ok(new AuthResponse(token, session.ExpiresAt, await BuildAccountAsync(user, organization, cancellationToken)));
    }

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = clock.GetUtcNow();
        var user = await accounts.FindUserByEmailAsync(UserAccount.NormalizeEmail(request.Email ?? string.Empty), cancellationToken);

        // The same work is done whether or not the account exists, so the answer time does not reveal it.
        var check = hasher.Verify(user?.PasswordHash, request.Password ?? string.Empty);
        if (user is null || user.IsLocked(now))
        {
            return Result.Invalid<AuthResponse>(Result.CredentialsKey, ApiMessages.BadCredentials);
        }

        if (check == PasswordCheck.Wrong)
        {
            user.RecordFailedLogin(now);
            await accounts.SaveChangesAsync(cancellationToken);
            return Result.Invalid<AuthResponse>(Result.CredentialsKey, ApiMessages.BadCredentials);
        }

        user.RecordSuccessfulLogin();
        if (check == PasswordCheck.CorrectNeedsNewHash)
        {
            user.ChangePassword(hasher.Hash(request.Password!));
        }

        var (session, token) = NewSession(user.Id, now);
        await accounts.AddSessionAsync(session, cancellationToken);
        await TrimSessionsAsync(user.Id, now, cancellationToken);
        await accounts.SaveChangesAsync(cancellationToken);

        var organization = await accounts.FindOrganizationAsync(user.OrganizationId, cancellationToken);
        return organization is null
            ? Result.Invalid<AuthResponse>(Result.CredentialsKey, ApiMessages.BadCredentials)
            : Result.Ok(new AuthResponse(token, session.ExpiresAt, await BuildAccountAsync(user, organization, cancellationToken)));
    }

    /// <summary>Who owns this token? Null when it is unknown or expired.</summary>
    public async Task<AuthenticatedUser?> AuthenticateAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return null;
        }

        var session = await accounts.FindSessionAsync(Tokens.Hash(token), cancellationToken);
        if (session is null)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (session.IsExpired(now))
        {
            await accounts.RemoveSessionsAsync([session], cancellationToken);
            await accounts.SaveChangesAsync(cancellationToken);
            return null;
        }

        var user = await accounts.FindUserAsync(session.UserId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        // Writing on every request would be wasteful: note the visit at most once a minute.
        if (now - session.LastSeenAt > TouchEvery)
        {
            session.Touch(now);
            await accounts.SaveChangesAsync(cancellationToken);
        }

        return new AuthenticatedUser(user.Id, user.OrganizationId, session.Id);
    }

    public async Task LogoutAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return;
        }

        if (await accounts.FindSessionAsync(Tokens.Hash(token), cancellationToken) is { } session)
        {
            await accounts.RemoveSessionsAsync([session], cancellationToken);
            await accounts.SaveChangesAsync(cancellationToken);
        }
    }

    // ------------------------------------------------------------ e-mail address and password reset

    public async Task<Result<bool>> VerifyEmailAsync(string? token, CancellationToken cancellationToken)
    {
        var emailToken = await UseTokenAsync(token, EmailTokenPurpose.VerifyEmail, cancellationToken);
        if (emailToken is null || await accounts.FindUserAsync(emailToken.UserId, cancellationToken) is not { } user)
        {
            return Result.Invalid<bool>("token", ApiMessages.InvalidLink);
        }

        var now = clock.GetUtcNow();
        user.MarkEmailVerified(now);
        emailToken.MarkUsed(now);
        await accounts.SaveChangesAsync(cancellationToken);
        return Result.Done();
    }

    public async Task<Result<bool>> ResendVerificationAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await accounts.FindUserAsync(userId, cancellationToken);
        if (user is null || user.IsEmailVerified)
        {
            return Result.Done();
        }

        return await SendLinkAsync(user, EmailTokenPurpose.VerifyEmail, cancellationToken)
            ? Result.Done()
            : Result.Invalid<bool>("email", ApiMessages.MailUnavailable);
    }

    /// <summary>Always succeeds from the caller's point of view: it never says whether the address has an account.</summary>
    public async Task ForgotPasswordAsync(string? email, CancellationToken cancellationToken)
    {
        var user = await accounts.FindUserByEmailAsync(UserAccount.NormalizeEmail(email ?? string.Empty), cancellationToken);
        if (user is not null)
        {
            await SendLinkAsync(user, EmailTokenPurpose.ResetPassword, cancellationToken);
        }
    }

    public async Task<Result<bool>> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var emailToken = await UseTokenAsync(request.Token, EmailTokenPurpose.ResetPassword, cancellationToken);
        if (emailToken is null || await accounts.FindUserAsync(emailToken.UserId, cancellationToken) is not { } user)
        {
            return Result.Invalid<bool>("token", ApiMessages.InvalidLink);
        }

        if (PasswordPolicy.Check(request.NewPassword, user.Email) is { } problem)
        {
            return Result.Invalid<bool>("password", problem);
        }

        user.ChangePassword(hasher.Hash(request.NewPassword));
        emailToken.MarkUsed(clock.GetUtcNow());

        // Whoever knew the old password, or had a session open, is signed out.
        await accounts.RemoveSessionsAsync(await accounts.ListSessionsAsync(user.Id, cancellationToken), cancellationToken);
        await accounts.SaveChangesAsync(cancellationToken);
        return Result.Done();
    }

    public async Task<Result<bool>> ChangePasswordAsync(
        AuthenticatedUser who,
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(request);

        var user = await accounts.FindUserAsync(who.UserId, cancellationToken);
        if (user is null || hasher.Verify(user.PasswordHash, request.CurrentPassword ?? string.Empty) == PasswordCheck.Wrong)
        {
            return Result.Invalid<bool>("currentPassword", ApiMessages.WrongCurrentPassword);
        }

        if (PasswordPolicy.Check(request.NewPassword, user.Email) is { } problem)
        {
            return Result.Invalid<bool>("newPassword", problem);
        }

        user.ChangePassword(hasher.Hash(request.NewPassword));

        // Every other session is closed: the one that made the change stays.
        var others = (await accounts.ListSessionsAsync(user.Id, cancellationToken)).Where(s => s.Id != who.SessionId);
        await accounts.RemoveSessionsAsync(others, cancellationToken);
        await accounts.SaveChangesAsync(cancellationToken);
        return Result.Done();
    }

    // ------------------------------------------------------------ the account itself

    public async Task<AccountDto?> GetAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await accounts.FindUserAsync(userId, cancellationToken);
        var organization = user is null ? null : await accounts.FindOrganizationAsync(user.OrganizationId, cancellationToken);
        return user is null || organization is null ? null : await BuildAccountAsync(user, organization, cancellationToken);
    }

    public async Task<Result<AccountDto>> UpdateOrganizationAsync(
        Guid userId,
        UpdateOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > Organization.NameMaxLength)
        {
            return Result.Invalid<AccountDto>("name", ApiMessages.OrganizationNameLength);
        }

        var (user, organization) = await LoadAsync(userId, cancellationToken);
        if (user is null || organization is null)
        {
            return Result.Invalid<AccountDto>(Result.CredentialsKey, ApiMessages.BadCredentials);
        }

        organization.Rename(name);
        await accounts.SaveChangesAsync(cancellationToken);
        return Result.Ok(await BuildAccountAsync(user, organization, cancellationToken));
    }

    public async Task<Result<AccountDto>> UpdateNotificationsAsync(
        Guid userId,
        UpdateNotificationsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (user, organization) = await LoadAsync(userId, cancellationToken);
        if (user is null || organization is null)
        {
            return Result.Invalid<AccountDto>(Result.CredentialsKey, ApiMessages.BadCredentials);
        }

        organization.SetEmailAlerts(request.EmailAlerts);
        await accounts.SaveChangesAsync(cancellationToken);
        return Result.Ok(await BuildAccountAsync(user, organization, cancellationToken));
    }

    public async Task<Result<AccountDto>> ChangePlanAsync(Guid userId, ChangePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (user, organization) = await LoadAsync(userId, cancellationToken);
        if (user is null || organization is null)
        {
            return Result.Invalid<AccountDto>(Result.CredentialsKey, ApiMessages.BadCredentials);
        }

        if (!options.Value.AllowSelfServicePlanChange)
        {
            return Result.Invalid<AccountDto>("plan", ApiMessages.PlanChangeDisabled);
        }

        var target = request.Plan.ToPlan();
        if (await PlanProblemAsync(organization.Id, target, cancellationToken) is { } problem)
        {
            return Result.Invalid<AccountDto>("plan", problem);
        }

        organization.ChangePlan(target);
        await accounts.SaveChangesAsync(cancellationToken);
        return Result.Ok(await BuildAccountAsync(user, organization, cancellationToken));
    }

    /// <summary>Deletes the account and everything it owns. The password is asked again: this cannot be undone.</summary>
    public async Task<Result<bool>> DeleteAccountAsync(Guid userId, DeleteAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (user, organization) = await LoadAsync(userId, cancellationToken);
        if (user is null || organization is null
            || hasher.Verify(user.PasswordHash, request.Password ?? string.Empty) == PasswordCheck.Wrong)
        {
            return Result.Invalid<bool>("password", ApiMessages.WrongPassword);
        }

        await accounts.RemoveOrganizationAsync(organization, cancellationToken);
        await accounts.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Organization {OrganizationId} deleted at its owner's request", organization.Id);
        return Result.Done();
    }

    // ------------------------------------------------------------ helpers

    private async Task<(UserAccount? User, Organization? Organization)> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await accounts.FindUserAsync(userId, cancellationToken);
        return user is null ? (null, null) : (user, await accounts.FindOrganizationAsync(user.OrganizationId, cancellationToken));
    }

    private (UserSession Session, string Token) NewSession(Guid userId, DateTimeOffset now)
    {
        var token = Tokens.NewToken();
        return (UserSession.Start(userId, Tokens.Hash(token), now, TimeSpan.FromDays(options.Value.SessionDays)), token);
    }

    /// <summary>\n    /// Keeps the number of open sessions per person bounded: the oldest ones are closed first. The session\n    /// just added is not in the list yet (it is not saved), so room is made for it.\n    /// </summary>
    private async Task TrimSessionsAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessions = await accounts.ListSessionsAsync(userId, cancellationToken);
        var surplus = sessions
            .OrderBy(s => s.IsExpired(now) ? 0 : 1)
            .ThenBy(s => s.LastSeenAt)
            .Take(Math.Max(0, sessions.Count - (MaxSessionsPerUser - 1)));
        await accounts.RemoveSessionsAsync(surplus, cancellationToken);
    }

    private async Task<EmailToken?> UseTokenAsync(string? token, EmailTokenPurpose purpose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return null;
        }

        var found = await accounts.FindEmailTokenAsync(Tokens.Hash(token), purpose, cancellationToken);
        return found is not null && found.IsUsable(clock.GetUtcNow()) ? found : null;
    }

    /// <summary>
    /// Issues a one-use link and sends it. Returns false when it could not be sent (no mail server): the
    /// caller decides whether that matters. A failure to send never breaks sign-up.
    /// </summary>
    private async Task<bool> SendLinkAsync(UserAccount user, EmailTokenPurpose purpose, CancellationToken cancellationToken)
    {
        var token = Tokens.NewToken();
        var now = clock.GetUtcNow();
        var verify = purpose == EmailTokenPurpose.VerifyEmail;

        await accounts.RemoveEmailTokensAsync(user.Id, purpose, cancellationToken);
        await accounts.AddEmailTokenAsync(
            EmailToken.Issue(user.Id, purpose, Tokens.Hash(token), now, verify ? VerifyLinkLifetime : ResetLinkLifetime),
            cancellationToken);
        await accounts.SaveChangesAsync(cancellationToken);

        var baseUrl = options.Value.WebBaseUrl.TrimEnd('/');
        var link = verify
            ? $"{baseUrl}/verify-email?token={token}"
            : $"{baseUrl}/reset-password?token={token}";
        var mailToSend = verify
            ? new OutgoingMail(
                user.Email,
                "Confirmez votre adresse e-mail",
                $"Bonjour,\n\nConfirmez votre adresse pour recevoir les alertes de Pulse :\n\n{link}\n\nCe lien est valable 24 heures. Si vous n'êtes pas à l'origine de cette inscription, ignorez ce message.\n")
            : new OutgoingMail(
                user.Email,
                "Réinitialisation de votre mot de passe",
                $"Bonjour,\n\nPour choisir un nouveau mot de passe, ouvrez ce lien :\n\n{link}\n\nIl est valable 1 heure et ne sert qu'une fois. Si vous n'avez rien demandé, ignorez ce message : votre mot de passe reste inchangé.\n");

        if (options.Value.LogEmailsToConsole)
        {
            // Development only: the link is a secret, so this is off by default.
            logger.LogWarning("E-mail for {To}: {Subject} {Link}", mailToSend.To, mailToSend.Subject, link);
        }

        if (!mail.IsAvailable)
        {
            return options.Value.LogEmailsToConsole;
        }

        try
        {
            await mail.SendAsync(mailToSend, cancellationToken);
            return true;
        }
        catch (MailDeliveryException ex)
        {
            logger.LogWarning("A {Purpose} e-mail could not be sent: {Reason}", purpose, ex.Message);
            return false;
        }
    }

    private async Task<string?> PlanProblemAsync(Guid organizationId, Plan target, CancellationToken cancellationToken)
    {
        var limits = PlanPolicy.For(target);
        var services = await pulse.ListServicesAsync(organizationId, publicOnly: false, cancellationToken);
        if (services.Count > limits.MaxServices)
        {
            return $"The {target} plan allows at most {limits.MaxServices} services.";
        }

        if ((await accounts.ListWebhooksAsync(organizationId, cancellationToken)).Count > limits.MaxWebhooks)
        {
            return $"The {target} plan allows at most {limits.MaxWebhooks} webhooks.";
        }

        return services.Any(s => s.IntervalSeconds < limits.MinIntervalSeconds)
            ? $"The {target} plan does not allow checks more often than every {limits.MinIntervalSeconds} seconds."
            : null;
    }

    private async Task<AccountDto> BuildAccountAsync(UserAccount user, Organization organization, CancellationToken cancellationToken)
    {
        var limits = PlanPolicy.For(organization.Plan);
        var services = await pulse.CountServicesAsync(organization.Id, cancellationToken);
        var webhooks = (await accounts.ListWebhooksAsync(organization.Id, cancellationToken)).Count;

        return new AccountDto(
            user.Email,
            user.IsEmailVerified,
            organization.Name,
            organization.Slug,
            organization.Plan.ToTier(),
            organization.EmailAlerts,
            new UsageDto(services, limits.MaxServices, webhooks, limits.MaxWebhooks, limits.MinIntervalSeconds),
            mail.IsAvailable,
            options.Value.AllowSelfServicePlanChange);
    }
}
