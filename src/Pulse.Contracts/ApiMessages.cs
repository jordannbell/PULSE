namespace Pulse.Contracts;

/// <summary>
/// The fixed messages the API gives when it refuses something. They are English for any client; the web
/// site shows them in French through <see cref="FrenchText"/>. A test checks that each one has a French
/// version, so a new message cannot be added and forgotten.
/// </summary>
public static class ApiMessages
{
    public const string InvalidEmail = "Enter a valid e-mail address.";
    public const string EmailTaken = "An account already exists with this e-mail address.";
    public const string PasswordTooShort = "The password must have at least 10 characters.";
    public const string PasswordTooLong = "The password must have at most 128 characters.";
    public const string PasswordIsEmail = "The password must not be the e-mail address.";
    public const string OrganizationNameLength = "The organization name must have 1 to 80 characters.";
    public const string SlugInvalid = "The address name must have 3 to 40 lowercase letters, digits or hyphens, and start and end with a letter or a digit.";
    public const string SlugReserved = "This address name is reserved.";
    public const string SlugTaken = "This address name is already taken.";
    public const string BadCredentials = "The e-mail address or the password is wrong, or the account is locked for a few minutes.";
    public const string InvalidLink = "This link is not valid or has expired.";
    public const string WrongCurrentPassword = "The current password is wrong.";
    public const string WrongPassword = "The password is wrong.";
    public const string MailUnavailable = "This server cannot send e-mails.";
    public const string PlanChangeDisabled = "Changing plan is not possible on this server.";
    public const string WebhookUrlLength = "The address must have 1 to 2048 characters.";
}
