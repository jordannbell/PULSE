using System.ComponentModel.DataAnnotations;

namespace Pulse.Application.Alerts;

/// <summary>
/// Settings of the "Pulse:Alerts" section: how hard Pulse tries to deliver an alert. The channels
/// themselves (webhooks, the owner's e-mail address) belong to each organization and live in the database.
/// </summary>
public sealed class AlertOptions
{
    public const string SectionName = "Pulse:Alerts";

    /// <summary>Tries per alert and per channel before giving up.</summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Wait before the second try; it doubles after each failure.</summary>
    [Range(0, 3600)]
    public int RetryDelaySeconds { get; set; } = 5;
}
