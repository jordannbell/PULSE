using System.Globalization;
using System.Text;
using Pulse.Application.Alerts;
using Pulse.Contracts;

namespace Pulse.Infrastructure.Alerts;

/// <summary>The words of an alert. French, because the administrator reads it; times are always UTC.</summary>
internal static class AlertFormatter
{
    public static string EventName(AlertKind kind) =>
        kind switch
        {
            AlertKind.IncidentOpened => "incident.opened",
            AlertKind.IncidentResolved => "incident.resolved",
            _ => "test",
        };

    public static string Subject(AlertEvent alert) =>
        alert.Kind switch
        {
            AlertKind.IncidentOpened => $"[Pulse] {alert.ServiceName} est en panne",
            AlertKind.IncidentResolved => $"[Pulse] {alert.ServiceName} fonctionne de nouveau",
            _ => "[Pulse] Message de test",
        };

    /// <summary>A short sentence, used by chat webhooks and as the first line of an e-mail.</summary>
    public static string Headline(AlertEvent alert) =>
        alert.Kind switch
        {
            AlertKind.IncidentOpened => $"🔴 {alert.ServiceName} est en panne : {FrenchText.Cause(alert.Cause)}",
            AlertKind.IncidentResolved => $"🟢 {alert.ServiceName} fonctionne de nouveau (panne de {FormatDuration(alert.Duration)}).",
            _ => "Message de test envoyé depuis l'administration de Pulse. Ce canal fonctionne.",
        };

    public static string Body(AlertEvent alert)
    {
        var text = new StringBuilder();
        text.AppendLine(Headline(alert));
        if (alert.Kind != AlertKind.Test)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Service : {alert.ServiceName}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Adresse : {alert.ServiceUrl}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Début   : {alert.IncidentStartedAt:dd/MM/yyyy HH:mm:ss} UTC");
            if (alert.IncidentResolvedAt is { } end)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Fin     : {end:dd/MM/yyyy HH:mm:ss} UTC");
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"Cause   : {FrenchText.Cause(alert.Cause)}");
        }

        return text.ToString();
    }

    internal static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } span)
        {
            return "durée inconnue";
        }

        if (span.TotalSeconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds} s");
        }

        if (span.TotalHours < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} min {span.Seconds} s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h {span.Minutes} min");
    }
}
