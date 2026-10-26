using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Pulse.Application.Accounts;

/// <summary>Settings of the "Pulse:Accounts" section.</summary>
public sealed class AccountOptions
{
    public const string SectionName = "Pulse:Accounts";

    /// <summary>How long a login lasts before the person has to sign in again.</summary>
    [Range(1, 90)]
    public int SessionDays { get; set; } = 7;

    /// <summary>
    /// Treat every address as confirmed without sending a link. For development and private installations
    /// with no mail server. With this off, only a confirmed address receives alert e-mails.
    /// </summary>
    public bool AutoVerifyEmail { get; set; }

    /// <summary>
    /// Write the e-mails Pulse would send (confirmation and password-reset links) to the log instead of, or
    /// when there is no, mail server. The links are secrets: development only.
    /// </summary>
    public bool LogEmailsToConsole { get; set; }

    /// <summary>Let an account switch between plans by itself. There is no payment yet, so this is on.</summary>
    public bool AllowSelfServicePlanChange { get; set; } = true;

    /// <summary>
    /// Base64 of 32 random bytes. Encrypts the webhook secrets stored in the database. Keep it out of the
    /// repository and do not lose it: without it the stored secrets cannot be read.
    /// </summary>
    [Required]
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>Public address of the web site, used to build the links in e-mails.</summary>
    [Required]
    public string WebBaseUrl { get; set; } = "http://localhost:5090";
}

public sealed class AccountOptionsValidator : IValidateOptions<AccountOptions>
{
    public const int KeyBytes = 32;

    public ValidateOptionsResult Validate(string? name, AccountOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.SessionDays is < 1 or > 90)
        {
            failures.Add("Pulse:Accounts:SessionDays must be between 1 and 90.");
        }

        if (!IsValidKey(options.EncryptionKey))
        {
            failures.Add($"Pulse:Accounts:EncryptionKey must be the base64 of {KeyBytes} random bytes (for example: openssl rand -base64 32).");
        }

        if (!Uri.TryCreate(options.WebBaseUrl, UriKind.Absolute, out var web)
            || (web.Scheme != Uri.UriSchemeHttp && web.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("Pulse:Accounts:WebBaseUrl must be a full http(s) address.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[KeyBytes + 4];
        return Convert.TryFromBase64String(key, buffer, out var written) && written == KeyBytes;
    }
}

public enum SmtpSecurity
{
    None,
    StartTls,
    Tls,
}

/// <summary>Settings of the "Pulse:Mail" section: the server that sends confirmation links and alert e-mails.</summary>
public sealed class MailOptions
{
    public const string SectionName = "Pulse:Mail";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

public sealed class MailOptionsValidator : IValidateOptions<MailOptions>
{
    public ValidateOptionsResult Validate(string? name, MailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (options.Port is < 1 or > 65535)
        {
            failures.Add("Pulse:Mail:Port must be between 1 and 65535.");
        }

        if (!MailAddress.TryCreate(options.From, out _))
        {
            failures.Add("Pulse:Mail:From must be a valid e-mail address.");
        }

        if (!string.IsNullOrEmpty(options.User) && string.IsNullOrEmpty(options.Password))
        {
            failures.Add("Pulse:Mail:Password is required when Pulse:Mail:User is set.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
