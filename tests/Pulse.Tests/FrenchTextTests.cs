using Pulse.Contracts;

namespace Pulse.Tests;

public class FrenchTextTests
{
    [Theory]
    [InlineData("Expected status 200, got 503.", "Code 503 reçu, 200 attendu.")]
    [InlineData("No answer within 10 s.", "Aucune réponse en 10 s.")]
    [InlineData("The server could not be reached.", "Serveur injoignable.")]
    [InlineData("The host name cannot be resolved.", "Nom de domaine introuvable.")]
    [InlineData("The TLS handshake failed.", "Échec de la connexion sécurisée (TLS).")]
    [InlineData("The address does not point to a public server.", "L'adresse ne pointe pas vers un serveur public.")]
    [InlineData("The request failed.", "La requête a échoué.")]
    [InlineData("The check could not run.", "Le contrôle n'a pas pu s'exécuter.")]
    public void A_failure_cause_is_given_in_french(string english, string french)
    {
        Assert.Equal(french, FrenchText.Cause(english));
    }

    [Theory]
    [InlineData("Enter a full http:// or https:// address.", "Saisissez une adresse complète, en http:// ou https://.")]
    [InlineData("The address must point to a public server.", "L'adresse doit pointer vers un serveur public.")]
    [InlineData("Only ports 80, 443, 8080 and 8443 are allowed.", "Seuls les ports 80, 443, 8080 et 8443 sont autorisés.")]
    [InlineData("The name must have 1 to 100 characters.", "Le nom doit comporter de 1 à 100 caractères.")]
    [InlineData("The interval must be between 30 and 86400 seconds.", "La fréquence doit être comprise entre 30 s et 24 h.")]
    [InlineData("The timeout must be between 1 and 30 seconds, and not longer than the interval.", "Le délai doit être compris entre 1 et 30 secondes, sans dépasser la fréquence.")]
    [InlineData("The expected status code must be between 100 and 599.", "Le code attendu doit être compris entre 100 et 599.")]
    [InlineData("The Free plan allows at most 5 services.", "La formule Gratuite autorise 5 services au maximum.")]
    [InlineData("The Pro plan allows at most 5 webhooks.", "La formule Pro autorise 5 webhooks au maximum.")]
    [InlineData("The Free plan does not allow checks more often than every 300 seconds.", "La formule Gratuite n'autorise pas de contrôle plus fréquent que toutes les 5 min.")]
    public void A_refusal_reason_is_given_in_french(string english, string french)
    {
        Assert.Equal(french, FrenchText.Validation(english));
    }

    [Fact]
    public void Every_message_the_application_can_produce_for_a_new_service_is_translated()
    {
        // The messages written in ServiceCatalog and TargetValidator: none may slip through in English.
        string[] produced =
        [
            "The name must have 1 to 100 characters.",
            "The address must have 1 to 2048 characters.",
            "The interval must be between 30 and 86400 seconds.",
            "The timeout must be between 1 and 30 seconds, and not longer than the interval.",
            "The expected status code must be between 100 and 599.",
            "Enter a full http:// or https:// address.",
            "The address must not contain a user name or password.",
            "Only ports 80, 443, 8080 and 8443 are allowed.",
            "The host name cannot be resolved.",
            "The address must point to a public server.",
            "The Free plan allows at most 5 services.",
            "The Pro plan allows at most 5 webhooks.",
            "The Free plan does not allow checks more often than every 300 seconds.",
        ];

        Assert.All(produced, message => Assert.NotEqual(message, FrenchText.Validation(message)));
    }

    [Theory]
    [InlineData("Something nobody planned for.")]
    [InlineData("Un message déjà en français.")]
    public void An_unknown_message_is_left_as_it_is_never_hidden(string message)
    {
        Assert.Equal(message, FrenchText.Cause(message));
        Assert.Equal(message, FrenchText.Validation(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_in_gives_nothing_out(string? message)
    {
        Assert.Equal(string.Empty, FrenchText.Cause(message));
        Assert.Equal(string.Empty, FrenchText.Validation(message));
    }

    [Fact]
    public void Every_fixed_api_message_has_a_french_version()
    {
        // ApiMessages holds the fixed messages of the API: a new one cannot be added without a translation.
        var messages = typeof(ApiMessages).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(messages);
        Assert.All(messages, message => Assert.NotEqual(message, FrenchText.Validation(message)));
    }
}
