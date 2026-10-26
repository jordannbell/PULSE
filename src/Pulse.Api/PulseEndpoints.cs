using Pulse.Application;
using Pulse.Application.Accounts;
using Pulse.Application.Alerts;
using Pulse.Contracts;

namespace Pulse.Api;

internal static class PulseEndpoints
{
    public const string AuthRateLimit = "auth";

    public static void MapPulseEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        var api = app.MapGroup("/api/v1");

        MapPublic(api);
        MapAuth(api);

        // Everything below needs a signed-in session, and only ever sees the caller's own organization.
        var signedIn = api.MapGroup(string.Empty).RequireAuthorization();
        MapAccount(signedIn);
        MapServices(signedIn);
        MapWebhooks(signedIn);
        MapAlerts(signedIn);
    }

    private static void MapPublic(RouteGroupBuilder api)
    {
        // What a visitor of an organization's public page sees. It never exposes the monitored addresses.
        api.MapGet("/public/{slug}/status", async (string slug, StatusQueries status, CancellationToken ct) =>
            await status.GetPublicStatusAsync(slug, ct) is { } page ? Results.Ok(page) : Results.NotFound());
    }

    private static void MapAuth(RouteGroupBuilder api)
    {
        // Stricter rate limit: these are the doors a password guesser would try.
        var auth = api.MapGroup("/auth").RequireRateLimiting(AuthRateLimit);

        auth.MapPost("/signup", async (SignUpRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var result = await accounts.SignUpAsync(request, ct);
            return result.IsSuccess ? Results.Created("/api/v1/account", result.Value) : Problem(result);
        });

        auth.MapPost("/login", async (LoginRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var result = await accounts.LoginAsync(request, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problem(result);
        });

        auth.MapPost("/verify-email", async (TokenRequest request, AccountService accounts, CancellationToken ct) =>
            Answer(await accounts.VerifyEmailAsync(request.Token, ct)));

        // The same answer whether or not the address has an account: it must not reveal who is registered.
        auth.MapPost("/forgot-password", async (EmailRequest request, AccountService accounts, CancellationToken ct) =>
        {
            await accounts.ForgotPasswordAsync(request.Email, ct);
            return Results.Accepted();
        });

        auth.MapPost("/reset-password", async (ResetPasswordRequest request, AccountService accounts, CancellationToken ct) =>
            Answer(await accounts.ResetPasswordAsync(request, ct)));

        auth.MapPost("/logout", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            await accounts.LogoutAsync(BearerToken(http), ct);
            return Results.NoContent();
        }).RequireAuthorization();

        auth.MapPost("/resend-verification", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
            Answer(await accounts.ResendVerificationAsync(User(http).UserId, ct)))
            .RequireAuthorization();
    }

    private static void MapAccount(RouteGroupBuilder api)
    {
        var account = api.MapGroup("/account");

        account.MapGet("/", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
            await accounts.GetAccountAsync(User(http).UserId, ct) is { } dto ? Results.Ok(dto) : Results.Unauthorized());

        account.MapPut("/organization", async (UpdateOrganizationRequest request, HttpContext http, AccountService accounts, CancellationToken ct) =>
            Answer(await accounts.UpdateOrganizationAsync(User(http).UserId, request, ct)));

        account.MapPut("/notifications", async (UpdateNotificationsRequest request, HttpContext http, AccountService accounts, CancellationToken ct) =>
            Answer(await accounts.UpdateNotificationsAsync(User(http).UserId, request, ct)));

        account.MapPut("/plan", async (ChangePlanRequest request, HttpContext http, AccountService accounts, CancellationToken ct) =>
            Answer(await accounts.ChangePlanAsync(User(http).UserId, request, ct)));

        account.MapPost("/password", async (ChangePasswordRequest request, HttpContext http, AccountService accounts, CancellationToken ct) =>
            Answer(await accounts.ChangePasswordAsync(User(http), request, ct)))
            .RequireRateLimiting(AuthRateLimit);

        // A POST rather than a DELETE with a body: some clients and proxies drop the body of a DELETE.
        account.MapPost("/delete", async (DeleteAccountRequest request, HttpContext http, AccountService accounts, CancellationToken ct) =>
            Answer(await accounts.DeleteAccountAsync(User(http).UserId, request, ct)))
            .RequireRateLimiting(AuthRateLimit);
    }

    private static void MapServices(RouteGroupBuilder api)
    {
        var services = api.MapGroup("/services");

        services.MapGet("/", async (HttpContext http, ServiceCatalog catalog, CancellationToken ct) =>
            Results.Ok(await catalog.ListAsync(User(http).OrganizationId, ct)));

        services.MapGet("/{id:guid}", async (Guid id, HttpContext http, ServiceCatalog catalog, CancellationToken ct) =>
            await catalog.GetAsync(User(http).OrganizationId, id, ct) is { } service ? Results.Ok(service) : Results.NotFound());

        services.MapPost("/", async (CreateServiceRequest request, HttpContext http, ServiceCatalog catalog, CancellationToken ct) =>
        {
            var result = await catalog.CreateAsync(User(http).OrganizationId, request, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/services/{result.Value!.Id}", result.Value)
                : Problem(result);
        });

        services.MapPut("/{id:guid}", async (Guid id, UpdateServiceRequest request, HttpContext http, ServiceCatalog catalog, CancellationToken ct) =>
            await catalog.UpdateAsync(User(http).OrganizationId, id, request, ct) is { } result ? Answer(result) : Results.NotFound());

        services.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ServiceCatalog catalog, CancellationToken ct) =>
            await catalog.DeleteAsync(User(http).OrganizationId, id, ct) ? Results.NoContent() : Results.NotFound());

        services.MapGet("/{id:guid}/checks", async (Guid id, int? limit, HttpContext http, ServiceCatalog catalog, CancellationToken ct) =>
            await catalog.RecentChecksAsync(User(http).OrganizationId, id, limit ?? 50, ct) is { } checks ? Results.Ok(checks) : Results.NotFound());
    }

    private static void MapWebhooks(RouteGroupBuilder api)
    {
        var webhooks = api.MapGroup("/webhooks");

        webhooks.MapGet("/", async (HttpContext http, WebhookService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(User(http).OrganizationId, ct)));

        webhooks.MapPost("/", async (CreateWebhookRequest request, HttpContext http, WebhookService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(User(http).OrganizationId, request, ct);
            return result.IsSuccess ? Results.Created($"/api/v1/webhooks/{result.Value!.Webhook.Id}", result.Value) : Problem(result);
        });

        webhooks.MapDelete("/{id:guid}", async (Guid id, HttpContext http, WebhookService service, CancellationToken ct) =>
            await service.DeleteAsync(User(http).OrganizationId, id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private static void MapAlerts(RouteGroupBuilder api)
    {
        // Which alert channels the caller's organization has, and a way to check that they work.
        var alerts = api.MapGroup("/alerts");
        alerts.MapGet("/", async (HttpContext http, AlertTester tester, CancellationToken ct) =>
            Results.Ok(await tester.ListAsync(User(http).OrganizationId, ct)));
        alerts.MapPost("/test", async (HttpContext http, AlertTester tester, CancellationToken ct) =>
            Results.Ok(await tester.TestAsync(User(http).OrganizationId, ct)));
    }

    private static AuthenticatedUser User(HttpContext http) => SessionAuthenticationHandler.ToUser(http.User);

    private static string? BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }

    /// <summary>A result with a value becomes 200 and the value; an error becomes the matching problem.</summary>
    private static IResult Answer<T>(Result<T> result) =>
        result.IsSuccess ? (result.Value is bool ? Results.NoContent() : Results.Ok(result.Value)) : Problem(result);

    private static IResult Problem<T>(Result<T> result) =>
        result.IsUnauthenticated
            ? Results.Problem(title: "Unauthorized", detail: ApiMessages.BadCredentials, statusCode: StatusCodes.Status401Unauthorized)
            : Results.ValidationProblem(result.Errors!.ToDictionary(e => e.Key, e => e.Value));
}
