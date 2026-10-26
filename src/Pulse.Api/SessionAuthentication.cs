using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Pulse.Application.Accounts;

namespace Pulse.Api;

/// <summary>
/// Signs a request in from its Bearer token. The token is looked up (as a hash) among the open sessions:
/// signing out, a password change or deleting the account closes it at once, which a signed-token scheme
/// such as JWT could not do.
/// </summary>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccountService accounts) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Session";

    private const string BearerPrefix = "Bearer ";
    private const string OrganizationClaim = "org";
    private const string SessionClaim = "sid";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("Use the Bearer scheme.");
        }

        var who = await accounts.AuthenticateAsync(header[BearerPrefix.Length..].Trim(), Context.RequestAborted);
        if (who is null)
        {
            return AuthenticateResult.Fail("The session is unknown or has expired.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, who.UserId.ToString()),
                new Claim(OrganizationClaim, who.OrganizationId.ToString()),
                new Claim(SessionClaim, who.SessionId.ToString()),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    /// <summary>Reads back what the handler put in the principal.</summary>
    public static AuthenticatedUser ToUser(ClaimsPrincipal principal) =>
        new(
            Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Guid.Parse(principal.FindFirstValue(OrganizationClaim)!),
            Guid.Parse(principal.FindFirstValue(SessionClaim)!));
}
