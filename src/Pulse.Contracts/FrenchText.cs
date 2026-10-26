using System.Globalization;
using System.Text.RegularExpressions;

namespace Pulse.Contracts;

/// <summary>
/// The API speaks English (stable messages for any client); the screens and e-mails speak French. This
/// turns the API's own messages into French. A message it does not know is returned unchanged, so a new
/// message is never hidden, only left in English until it is added here.
/// </summary>
public static partial class FrenchText
{
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
    {
        // Why a check failed
        ["The server could not be reached."] = "Serveur injoignable.",
        ["The host name cannot be resolved."] = "Nom de domaine introuvable.",
        ["The TLS handshake failed."] = "Échec de la connexion sécurisée (TLS).",
        ["The address does not point to a public server."] = "L'adresse ne pointe pas vers un serveur public.",
        ["The request failed."] = "La requête a échoué.",
        ["The check could not run."] = "Le contrôle n'a pas pu s'exécuter.",

        // Why a new service or webhook was refused
        ["Enter a full http:// or https:// address."] = "Saisissez une adresse complète, en http:// ou https://.",
        ["The address must not contain a user name or password."] = "L'adresse ne doit contenir ni nom d'utilisateur ni mot de passe.",
        ["Only ports 80, 443, 8080 and 8443 are allowed."] = "Seuls les ports 80, 443, 8080 et 8443 sont autorisés.",
        ["The address must point to a public server."] = "L'adresse doit pointer vers un serveur public.",
        ["The expected status code must be between 100 and 599."] = "Le code attendu doit être compris entre 100 et 599.",

        // Accounts
        [ApiMessages.InvalidEmail] = "Saisissez une adresse e-mail valide.",
        [ApiMessages.EmailTaken] = "Un compte existe déjà avec cette adresse e-mail.",
        [ApiMessages.PasswordTooShort] = "Le mot de passe doit comporter au moins 10 caractères.",
        [ApiMessages.PasswordTooLong] = "Le mot de passe ne doit pas dépasser 128 caractères.",
        [ApiMessages.PasswordIsEmail] = "Le mot de passe ne doit pas être l'adresse e-mail.",
        [ApiMessages.OrganizationNameLength] = "Le nom de l'organisation doit comporter de 1 à 80 caractères.",
        [ApiMessages.SlugInvalid] = "L'adresse de la page doit comporter de 3 à 40 lettres minuscules, chiffres ou tirets, et commencer et finir par une lettre ou un chiffre.",
        [ApiMessages.SlugReserved] = "Cette adresse est réservée. Choisissez-en une autre.",
        [ApiMessages.SlugTaken] = "Cette adresse est déjà prise. Choisissez-en une autre.",
        [ApiMessages.BadCredentials] = "Adresse e-mail ou mot de passe incorrect, ou compte verrouillé pour quelques minutes.",
        [ApiMessages.InvalidLink] = "Ce lien n'est pas valide ou a expiré.",
        [ApiMessages.WrongCurrentPassword] = "Le mot de passe actuel est incorrect.",
        [ApiMessages.WrongPassword] = "Le mot de passe est incorrect.",
        [ApiMessages.MailUnavailable] = "Ce serveur ne peut pas envoyer d'e-mails.",
        [ApiMessages.PlanChangeDisabled] = "Le changement de formule n'est pas possible sur ce serveur.",
        [ApiMessages.WebhookUrlLength] = "L'adresse doit comporter de 1 à 2048 caractères.",
    };

    /// <summary>The reason an incident or a check gives, in French.</summary>
    public static string Cause(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        if (ExpectedStatus().Match(message) is { Success: true } status)
        {
            return $"Code {status.Groups[2].Value} reçu, {status.Groups[1].Value} attendu.";
        }

        if (NoAnswer().Match(message) is { Success: true } timeout)
        {
            return $"Aucune réponse en {timeout.Groups[1].Value} s.";
        }

        return Exact.GetValueOrDefault(message, message);
    }

    /// <summary>A field error returned when something is refused, in French.</summary>
    public static string Validation(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        if (NameLength().Match(message) is { Success: true } name)
        {
            return $"Le nom doit comporter de 1 à {name.Groups[1].Value} caractères.";
        }

        if (AddressLength().Match(message) is { Success: true } address)
        {
            return $"L'adresse doit comporter de 1 à {address.Groups[1].Value} caractères.";
        }

        if (Interval().Match(message) is { Success: true } interval)
        {
            return $"La fréquence doit être comprise entre {Seconds(interval.Groups[1].Value)} et {Seconds(interval.Groups[2].Value)}.";
        }

        if (Timeout().Match(message) is { Success: true } timeout)
        {
            return $"Le délai doit être compris entre 1 et {timeout.Groups[1].Value} secondes, sans dépasser la fréquence.";
        }

        if (PlanServices().Match(message) is { Success: true } services)
        {
            return $"La formule {PlanName(services.Groups[1].Value)} autorise {services.Groups[2].Value} services au maximum.";
        }

        if (PlanWebhooks().Match(message) is { Success: true } webhooks)
        {
            return $"La formule {PlanName(webhooks.Groups[1].Value)} autorise {webhooks.Groups[2].Value} webhooks au maximum.";
        }

        if (PlanInterval().Match(message) is { Success: true } planInterval)
        {
            return $"La formule {PlanName(planInterval.Groups[1].Value)} n'autorise pas de contrôle plus fréquent que toutes les {Seconds(planInterval.Groups[2].Value)}.";
        }

        return Cause(message);
    }

    private static string PlanName(string plan) => plan == "Free" ? "Gratuite" : plan;

    private static string Seconds(string value)
    {
        var seconds = int.Parse(value, CultureInfo.InvariantCulture);
        if (seconds >= 3600 && seconds % 3600 == 0)
        {
            return $"{seconds / 3600} h";
        }

        return seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";
    }

    [GeneratedRegex(@"^Expected status (\d+), got (\d+)\.$")]
    private static partial Regex ExpectedStatus();

    [GeneratedRegex(@"^No answer within (\d+) s\.$")]
    private static partial Regex NoAnswer();

    [GeneratedRegex(@"^The name must have 1 to (\d+) characters\.$")]
    private static partial Regex NameLength();

    [GeneratedRegex(@"^The address must have 1 to (\d+) characters\.$")]
    private static partial Regex AddressLength();

    [GeneratedRegex(@"^The interval must be between (\d+) and (\d+) seconds\.$")]
    private static partial Regex Interval();

    [GeneratedRegex(@"^The timeout must be between 1 and (\d+) seconds, and not longer than the interval\.$")]
    private static partial Regex Timeout();

    [GeneratedRegex(@"^The (\w+) plan allows at most (\d+) services\.$")]
    private static partial Regex PlanServices();

    [GeneratedRegex(@"^The (\w+) plan allows at most (\d+) webhooks\.$")]
    private static partial Regex PlanWebhooks();

    [GeneratedRegex(@"^The (\w+) plan does not allow checks more often than every (\d+) seconds\.$")]
    private static partial Regex PlanInterval();
}
