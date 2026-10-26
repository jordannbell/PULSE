using System.ComponentModel.DataAnnotations;

namespace Pulse.Application;

/// <summary>Settings read from the "Pulse" configuration section. Invalid values stop the app at startup.</summary>
public sealed class PulseOptions
{
    public const string SectionName = "Pulse";

    /// <summary>Failed checks in a row before a service is declared down.</summary>
    [Range(1, 10)]
    public int FailureThreshold { get; set; } = 3;

    [Range(1, 50)]
    public int MaxParallelProbes { get; set; } = 10;

    /// <summary>How long check results are kept, and the window used for the uptime figure.</summary>
    [Range(1, 365)]
    public int RetentionDays { get; set; } = 90;

    [Range(1, 60)]
    public int SchedulerTickSeconds { get; set; } = 5;

    /// <summary>Requests one client address may send to the API per minute.</summary>
    [Range(10, 100_000)]
    public int RateLimitPerMinute { get; set; } = 120;

    /// <summary>Sign-up, login and password requests one client address may send per minute.</summary>
    [Range(3, 100_000)]
    public int AuthRateLimitPerMinute { get; set; } = 10;

    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>
    /// Fills an empty database with an example organization (public page <c>/s/demo</c>) and 90 days of
    /// invented history. The page says so. For demonstrations only.
    /// </summary>
    public bool SeedDemoData { get; set; }

    /// <summary>Lets monitors and webhooks target private addresses and any port. For local demos only.</summary>
    public bool AllowPrivateTargets { get; set; }

    /// <summary>
    /// Believe the X-Forwarded-For and X-Forwarded-Proto headers. Turn on only behind a reverse proxy you
    /// control: otherwise anyone can claim to be any address and slip past the rate limits.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }
}
