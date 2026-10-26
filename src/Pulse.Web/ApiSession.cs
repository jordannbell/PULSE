using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Pulse.Contracts;

namespace Pulse.Web;

/// <summary>
/// Who is signed in, as far as this site is concerned. The browser holds only an encrypted cookie; the
/// API token lives inside it (a claim) and is read here, on the server, to call the API.
/// </summary>
public sealed class ApiSession(AuthenticationStateProvider authentication)
{
    public const string TokenClaim = "pulse:api-token";
    public const string SlugClaim = "pulse:slug";
    public const string OrganizationClaim = "pulse:organization";

    public async Task<string?> TokenAsync()
    {
        var state = await authentication.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(TokenClaim);
    }

    /// <summary>The claims stored in the cookie after a sign-in.</summary>
    public static ClaimsPrincipal PrincipalFor(AuthResponse auth)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, auth.Account.Email),
                new Claim(TokenClaim, auth.Token),
                new Claim(SlugClaim, auth.Account.Slug),
                new Claim(OrganizationClaim, auth.Account.OrganizationName),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Signs the person in: a cookie that lasts as long as the API session.</summary>
    public static Task SignInAsync(HttpContext http, AuthResponse auth) =>
        http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            PrincipalFor(auth),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = auth.ExpiresAt });

    /// <summary>Only addresses inside this site are followed after a sign-in: never an address from the query string.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
        && !returnUrl.Contains('\\', StringComparison.Ordinal)
            ? returnUrl
            : "/app";
}
