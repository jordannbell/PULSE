using System.Text.Json.Serialization;

namespace Pulse.Contracts;

// The wire format shared by the API and the web app. Enums travel as strings ("Up", "PartialOutage").

// ---------------------------------------------------------------- monitoring

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceStatus
{
    Unknown,
    Up,
    Down,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverallStatus
{
    Unknown,
    Operational,
    PartialOutage,
    MajorOutage,
}

/// <summary>How one UTC day went for a service, from its incidents.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DayLevel
{
    /// <summary>The service did not exist yet.</summary>
    NoData,
    Operational,

    /// <summary>Incidents, but less than half an hour of downtime in total.</summary>
    Degraded,

    /// <summary>Half an hour of downtime or more.</summary>
    Outage,
}

public sealed record DayDto(DateOnly Date, DayLevel Level, int Incidents, int DowntimeMinutes);

public sealed record CreateServiceRequest(
    string Name,
    string Url,
    int IntervalSeconds = 300,
    int TimeoutSeconds = 10,
    int ExpectedStatusCode = 200,
    bool IsPublic = true);

/// <summary>What an owner may change on a service. The address cannot change: add a new service instead.</summary>
public sealed record UpdateServiceRequest(
    string Name,
    int IntervalSeconds,
    int TimeoutSeconds,
    int ExpectedStatusCode,
    bool IsPublic);

public sealed record ServiceDto(
    Guid Id,
    string Name,
    string Url,
    int IntervalSeconds,
    int TimeoutSeconds,
    int ExpectedStatusCode,
    ServiceStatus Status,
    int ConsecutiveFailures,
    DateTimeOffset? LastCheckedAt,
    double? UptimePercent = null,
    IReadOnlyList<DayDto>? History = null,
    bool IsPublic = true);

public sealed record CheckDto(
    DateTimeOffset CheckedAt,
    bool Success,
    int? StatusCode,
    int LatencyMs,
    string? Error);

/// <summary>What a visitor of a public status page may see. The target URL is never part of it.</summary>
public sealed record PublicServiceDto(
    string Name,
    ServiceStatus Status,
    double? UptimePercent,
    IReadOnlyList<DayDto> History);

public sealed record IncidentDto(
    string ServiceName,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt,
    string Cause);

public sealed record PublicStatusDto(
    string OrganizationName,
    string Slug,
    OverallStatus Overall,
    DateTimeOffset GeneratedAt,
    int UptimeWindowDays,
    IReadOnlyList<PublicServiceDto> Services,
    IReadOnlyList<IncidentDto> Incidents,
    bool IsDemo = false);

// ---------------------------------------------------------------- accounts

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanTier
{
    Free,
    Pro,
}

public sealed record SignUpRequest(string Email, string Password, string OrganizationName, string Slug);

public sealed record LoginRequest(string Email, string Password);

public sealed record TokenRequest(string Token);

public sealed record EmailRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record DeleteAccountRequest(string Password);

public sealed record ChangePlanRequest(PlanTier Plan);

public sealed record UpdateOrganizationRequest(string Name);

public sealed record UpdateNotificationsRequest(bool EmailAlerts);

/// <summary>What the plan allows and how much of it is used.</summary>
public sealed record UsageDto(
    int ServicesUsed,
    int ServicesLimit,
    int WebhooksUsed,
    int WebhooksLimit,
    int MinIntervalSeconds);

public sealed record AccountDto(
    string Email,
    bool EmailVerified,
    string OrganizationName,
    string Slug,
    PlanTier Plan,
    bool EmailAlerts,
    UsageDto Usage,
    bool MailAvailable,
    bool CanChangePlan);

/// <summary>The result of a sign-up or a login. The token is shown once: keep it, send it as a Bearer token.</summary>
public sealed record AuthResponse(string Token, DateTimeOffset ExpiresAt, AccountDto Account);

// ---------------------------------------------------------------- alerts

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertFormat
{
    Json,
    Slack,
    Discord,
}

public sealed record CreateWebhookRequest(string Url, AlertFormat Format = AlertFormat.Json);

public sealed record WebhookDto(Guid Id, string Url, AlertFormat Format, DateTimeOffset CreatedAt);

/// <summary>The secret that signs the requests. Returned once, when the webhook is created, and never again.</summary>
public sealed record CreatedWebhookDto(WebhookDto Webhook, string Secret);

/// <summary>A way the organization receives alerts. Only its name: addresses and secrets stay out.</summary>
public sealed record AlertChannelDto(string Name);

public sealed record AlertTestResultDto(string Channel, bool Success, string? Error);
