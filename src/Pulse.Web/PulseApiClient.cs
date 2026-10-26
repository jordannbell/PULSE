using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Pulse.Contracts;

namespace Pulse.Web;

public enum ApiStatus
{
    Ok,

    /// <summary>The session is no longer valid (or the credentials are wrong).</summary>
    Unauthorized,

    /// <summary>The request was refused; <see cref="ApiResult{T}.Errors"/> says why, field by field.</summary>
    Invalid,
    NotFound,
    TooManyRequests,
    Unavailable,
}

public sealed record ApiResult<T>(ApiStatus Status, T? Value = default, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public bool IsOk => Status == ApiStatus.Ok;

    /// <summary>The first error message of a field, in French, or null.</summary>
    public string? Error(string field) =>
        Errors is not null && Errors.TryGetValue(field, out var messages) && messages.Length > 0
            ? FrenchText.Validation(messages[0])
            : null;

    /// <summary>Every error, in French, whatever its field. For forms that have one general message line.</summary>
    public IEnumerable<string> AllErrors() =>
        (Errors?.Values.SelectMany(m => m) ?? []).Select(FrenchText.Validation);
}

/// <summary>
/// The web site's only way to reach the data: calls to the Pulse API. The signed-in person's token is added
/// to every call that needs one; it never reaches the browser.
/// </summary>
public sealed class PulseApiClient(HttpClient http, ApiSession session)
{
    private const string Base = "api/v1/";

    // ------------------------------------------------------------ public

    public Task<ApiResult<PublicStatusDto>> GetPublicStatusAsync(string slug, CancellationToken ct) =>
        SendAsync<PublicStatusDto>(HttpMethod.Get, $"{Base}public/{Uri.EscapeDataString(slug)}/status", null, null, ct);

    public Task<ApiResult<AuthResponse>> SignUpAsync(SignUpRequest request, CancellationToken ct) =>
        SendAsync<AuthResponse>(HttpMethod.Post, Base + "auth/signup", null, request, ct);

    public Task<ApiResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct) =>
        SendAsync<AuthResponse>(HttpMethod.Post, Base + "auth/login", null, request, ct);

    public Task<ApiResult<object>> VerifyEmailAsync(string token, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, Base + "auth/verify-email", null, new TokenRequest(token), ct);

    public Task<ApiResult<object>> ForgotPasswordAsync(string email, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, Base + "auth/forgot-password", null, new EmailRequest(email), ct);

    public Task<ApiResult<object>> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, Base + "auth/reset-password", null, request, ct);

    /// <summary>Closes a session. The token is given because this is called outside a live page (sign-out request).</summary>
    public Task<ApiResult<object>> LogoutAsync(string token, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, Base + "auth/logout", token, null, ct);

    // ------------------------------------------------------------ the signed-in person

    public Task<ApiResult<object>> ResendVerificationAsync(CancellationToken ct) =>
        SignedInAsync<object>(HttpMethod.Post, "auth/resend-verification", null, ct);

    public Task<ApiResult<AccountDto>> GetAccountAsync(CancellationToken ct) =>
        SignedInAsync<AccountDto>(HttpMethod.Get, "account", null, ct);

    public Task<ApiResult<AccountDto>> UpdateOrganizationAsync(string name, CancellationToken ct) =>
        SignedInAsync<AccountDto>(HttpMethod.Put, "account/organization", new UpdateOrganizationRequest(name), ct);

    public Task<ApiResult<AccountDto>> UpdateNotificationsAsync(bool emailAlerts, CancellationToken ct) =>
        SignedInAsync<AccountDto>(HttpMethod.Put, "account/notifications", new UpdateNotificationsRequest(emailAlerts), ct);

    public Task<ApiResult<AccountDto>> ChangePlanAsync(PlanTier plan, CancellationToken ct) =>
        SignedInAsync<AccountDto>(HttpMethod.Put, "account/plan", new ChangePlanRequest(plan), ct);

    public Task<ApiResult<object>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct) =>
        SignedInAsync<object>(HttpMethod.Post, "account/password", request, ct);

    public Task<ApiResult<object>> DeleteAccountAsync(string password, CancellationToken ct) =>
        SignedInAsync<object>(HttpMethod.Post, "account/delete", new DeleteAccountRequest(password), ct);

    public Task<ApiResult<IReadOnlyList<ServiceDto>>> ListServicesAsync(CancellationToken ct) =>
        SignedInAsync<IReadOnlyList<ServiceDto>>(HttpMethod.Get, "services", null, ct);

    public Task<ApiResult<ServiceDto>> CreateServiceAsync(CreateServiceRequest request, CancellationToken ct) =>
        SignedInAsync<ServiceDto>(HttpMethod.Post, "services", request, ct);

    public Task<ApiResult<ServiceDto>> UpdateServiceAsync(Guid id, UpdateServiceRequest request, CancellationToken ct) =>
        SignedInAsync<ServiceDto>(HttpMethod.Put, $"services/{id}", request, ct);

    public Task<ApiResult<object>> DeleteServiceAsync(Guid id, CancellationToken ct) =>
        SignedInAsync<object>(HttpMethod.Delete, $"services/{id}", null, ct);

    public Task<ApiResult<IReadOnlyList<CheckDto>>> ListChecksAsync(Guid id, int limit, CancellationToken ct) =>
        SignedInAsync<IReadOnlyList<CheckDto>>(HttpMethod.Get, $"services/{id}/checks?limit={limit}", null, ct);

    public Task<ApiResult<IReadOnlyList<WebhookDto>>> ListWebhooksAsync(CancellationToken ct) =>
        SignedInAsync<IReadOnlyList<WebhookDto>>(HttpMethod.Get, "webhooks", null, ct);

    public Task<ApiResult<CreatedWebhookDto>> CreateWebhookAsync(CreateWebhookRequest request, CancellationToken ct) =>
        SignedInAsync<CreatedWebhookDto>(HttpMethod.Post, "webhooks", request, ct);

    public Task<ApiResult<object>> DeleteWebhookAsync(Guid id, CancellationToken ct) =>
        SignedInAsync<object>(HttpMethod.Delete, $"webhooks/{id}", null, ct);

    public Task<ApiResult<IReadOnlyList<AlertChannelDto>>> ListAlertChannelsAsync(CancellationToken ct) =>
        SignedInAsync<IReadOnlyList<AlertChannelDto>>(HttpMethod.Get, "alerts", null, ct);

    public Task<ApiResult<IReadOnlyList<AlertTestResultDto>>> TestAlertsAsync(CancellationToken ct) =>
        SignedInAsync<IReadOnlyList<AlertTestResultDto>>(HttpMethod.Post, "alerts/test", null, ct);

    // ------------------------------------------------------------ plumbing

    private async Task<ApiResult<T>> SignedInAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = await session.TokenAsync();
        return token is null
            ? new ApiResult<T>(ApiStatus.Unauthorized)
            : await SendAsync<T>(method, Base + path, token, body, ct);
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new ApiResult<T>(ApiStatus.Unauthorized, Errors: await ReadErrorsAsync(response, ct)),
                HttpStatusCode.NotFound => new ApiResult<T>(ApiStatus.NotFound),
                HttpStatusCode.TooManyRequests => new ApiResult<T>(ApiStatus.TooManyRequests),
                HttpStatusCode.BadRequest => new ApiResult<T>(ApiStatus.Invalid, Errors: await ReadErrorsAsync(response, ct)),
                _ when response.IsSuccessStatusCode => new ApiResult<T>(
                    ApiStatus.Ok,
                    typeof(T) == typeof(object) || response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted
                        ? default
                        : await response.Content.ReadFromJsonAsync<T>(ct)),
                _ => new ApiResult<T>(ApiStatus.Unavailable),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new ApiResult<T>(ApiStatus.Unavailable);
        }
    }

    private static async Task<IReadOnlyDictionary<string, string[]>> ReadErrorsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(ct);
            if (problem is { Errors.Count: > 0 })
            {
                return new Dictionary<string, string[]>(problem.Errors);
            }

            // A 401 answer carries a plain problem (title and detail) instead of field errors.
            return problem?.Detail is { } detail
                ? new Dictionary<string, string[]> { ["credentials"] = [detail] }
                : new Dictionary<string, string[]>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, string[]>();
        }
    }
}
