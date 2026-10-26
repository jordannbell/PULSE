using System.Globalization;
using Pulse.Contracts;

namespace Pulse.Web;

/// <summary>
/// Dates, durations and numbers written the French way. The app runs without culture data (invariant
/// globalization, smaller and safer), so month names and the decimal comma are written here by hand.
/// All times are UTC and say so.
/// </summary>
public static class Fmt
{
    private const string NoBreakSpace = " ";

    private static readonly string[] Months =
        ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre"];

    private static readonly string[] MonthsShort =
        ["janv.", "févr.", "mars", "avr.", "mai", "juin", "juil.", "août", "sept.", "oct.", "nov.", "déc."];

    /// <summary>"99,95 %" (no trailing zeros, a non-breaking space before the sign).</summary>
    public static string Percent(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',') + NoBreakSpace + "%";

    /// <summary>"22 octobre 2026" ("1er" for the first of the month).</summary>
    public static string LongDate(DateOnly date) =>
        $"{DayNumber(date.Day)} {Months[date.Month - 1]} {date.Year}";

    public static string LongDate(DateTimeOffset moment) => LongDate(DateOnly.FromDateTime(moment.UtcDateTime));

    /// <summary>"22 oct. 14:03 UTC".</summary>
    public static string ShortDateTime(DateTimeOffset moment)
    {
        var utc = moment.UtcDateTime;
        return $"{utc.Day} {MonthsShort[utc.Month - 1]} {Time(moment)}";
    }

    /// <summary>"14:03 UTC".</summary>
    public static string Time(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + NoBreakSpace + "UTC";

    public static string TimeWithSeconds(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + NoBreakSpace + "UTC";

    /// <summary>"45 s", "7 min", "1 h 35 min".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
        {
            return $"{Math.Max(0, (int)span.TotalSeconds)}{NoBreakSpace}s";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes}{NoBreakSpace}min";
        }

        var minutes = span.Minutes;
        return minutes == 0
            ? $"{(int)span.TotalHours}{NoBreakSpace}h"
            : $"{(int)span.TotalHours}{NoBreakSpace}h{NoBreakSpace}{minutes}{NoBreakSpace}min";
    }

    /// <summary>"toutes les 5 min".</summary>
    public static string Every(int seconds) => "toutes les " + Duration(TimeSpan.FromSeconds(seconds));

    /// <summary>"il y a 12 s", "il y a 5 min", "il y a 2 h", "il y a 3 j".</summary>
    public static string Ago(DateTimeOffset moment, DateTimeOffset now)
    {
        var age = now - moment;
        if (age.TotalSeconds < 5)
        {
            return "à l'instant";
        }

        if (age.TotalDays >= 1)
        {
            return $"il y a {(int)age.TotalDays}{NoBreakSpace}j";
        }

        return "il y a " + Duration(age);
    }

    public static string StatusLabel(ServiceStatus status) =>
        status switch
        {
            ServiceStatus.Up => "Opérationnel",
            ServiceStatus.Down => "En panne",
            _ => "En attente",
        };

    public static string StatusCss(ServiceStatus status) =>
        status switch
        {
            ServiceStatus.Up => "up",
            ServiceStatus.Down => "down",
            _ => "unknown",
        };

    /// <summary>What a day of the 90-day strip says when hovered.</summary>
    public static (string Title, string Detail) DayTip(DayDto day) =>
        (LongDate(day.Date), day.Level switch
        {
            DayLevel.NoData => "Pas encore surveillé",
            DayLevel.Operational => "Aucun incident",
            _ => $"{day.Incidents} {(day.Incidents > 1 ? "incidents" : "incident")}, {Duration(TimeSpan.FromMinutes(day.DowntimeMinutes))} d'indisponibilité",
        });

    /// <summary>One sentence for screen readers, since the 90 bars themselves are decoration.</summary>
    public static string StripSummary(IReadOnlyList<DayDto> history)
    {
        var clean = history.Count(d => d.Level == DayLevel.Operational);
        var affected = history.Count(d => d.Level is DayLevel.Degraded or DayLevel.Outage);
        var none = history.Count(d => d.Level == DayLevel.NoData);
        var text = $"Historique sur {history.Count} jours : {clean} sans incident, {affected} avec incident";
        return none > 0 ? $"{text}, {none} sans mesure." : text + ".";
    }

    private static string DayNumber(int day) => day == 1 ? "1er" : day.ToString(CultureInfo.InvariantCulture);
}
