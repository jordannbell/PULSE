using System.Security.Cryptography;
using Pulse.Application;
using Pulse.Application.Accounts;
using Pulse.Infrastructure.Security;

namespace Pulse.Tests;

public class SecurityAdapterTests
{
    private static AesGcmSecretProtector Protector(string? key = null) =>
        new(OptionsFactory.Wrap(new AccountOptions { EncryptionKey = key ?? TestKeys.Encryption }));

    // ------------------------------------------------------------ passwords

    [Fact]
    public void A_password_is_never_stored_and_each_hash_has_its_own_salt()
    {
        var hasher = new Pbkdf2PasswordHasher();

        var one = hasher.Hash("correct horse battery");
        var two = hasher.Hash("correct horse battery");

        Assert.NotEqual(one, two);
        Assert.DoesNotContain("correct horse battery", one, StringComparison.Ordinal);
        Assert.Equal(PasswordCheck.Correct, hasher.Verify(one, "correct horse battery"));
        Assert.Equal(PasswordCheck.Correct, hasher.Verify(two, "correct horse battery"));
    }

    [Theory]
    [InlineData("correct horse batter")]
    [InlineData("Correct horse battery")]
    [InlineData("")]
    [InlineData("correct horse battery ")]
    public void A_different_password_is_wrong(string attempt)
    {
        var hasher = new Pbkdf2PasswordHasher();

        Assert.Equal(PasswordCheck.Wrong, hasher.Verify(hasher.Hash("correct horse battery"), attempt));
    }

    [Fact]
    public void A_missing_hash_is_wrong_but_still_does_the_same_work()
    {
        var hasher = new Pbkdf2PasswordHasher();

        Assert.Equal(PasswordCheck.Wrong, hasher.Verify(null, "anything at all"));
    }

    [Fact]
    public void A_damaged_hash_is_wrong_not_an_error()
    {
        var hasher = new Pbkdf2PasswordHasher();

        Assert.Equal(PasswordCheck.Wrong, hasher.Verify("not a hash", "correct horse battery"));
    }

    // ------------------------------------------------------------ encrypted secrets

    [Fact]
    public void A_secret_survives_the_round_trip_and_is_not_readable_in_storage()
    {
        var protector = Protector();

        var stored = protector.Protect("whsec_0123456789");

        Assert.StartsWith("v1.", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("whsec_0123456789", stored, StringComparison.Ordinal);
        Assert.Equal("whsec_0123456789", protector.Unprotect(stored));
    }

    [Fact]
    public void The_same_secret_encrypts_differently_each_time()
    {
        var protector = Protector();

        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }

    [Fact]
    public void A_secret_cannot_be_read_with_another_key()
    {
        var stored = Protector().Protect("whsec_0123456789");
        var otherKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        Assert.ThrowsAny<CryptographicException>(() => Protector(otherKey).Unprotect(stored));
    }

    [Fact]
    public void A_secret_that_was_altered_is_refused()
    {
        var protector = Protector();
        var stored = protector.Protect("whsec_0123456789");
        var bytes = Convert.FromBase64String(stored["v1.".Length..]);
        bytes[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v1." + Convert.ToBase64String(bytes)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v2.AAAA")]
    [InlineData("plain text")]
    public void An_unknown_or_truncated_format_is_refused(string stored)
    {
        Assert.ThrowsAny<Exception>(() => Protector().Unprotect(stored.Length == 0 ? "v1." : stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("c2hvcnQ=")] // base64 of "short": valid, but not 32 bytes
    public void An_encryption_key_that_is_not_32_bytes_stops_the_application_at_start(string key)
    {
        Assert.Throws<InvalidOperationException>(() => Protector(key));
        Assert.False(AccountOptionsValidator.IsValidKey(key));
    }

    [Fact]
    public void The_options_validator_accepts_a_good_key_and_rejects_the_rest()
    {
        var validator = new AccountOptionsValidator();

        Assert.True(validator.Validate(null, new AccountOptions { EncryptionKey = TestKeys.Encryption }).Succeeded);
        Assert.True(validator.Validate(null, new AccountOptions { EncryptionKey = "nope" }).Failed);
        Assert.True(validator.Validate(null, new AccountOptions { EncryptionKey = TestKeys.Encryption, WebBaseUrl = "ftp://x" }).Failed);
        Assert.True(validator.Validate(null, new AccountOptions { EncryptionKey = TestKeys.Encryption, SessionDays = 0 }).Failed);
    }

    [Fact]
    public void The_mail_validator_requires_a_sender_when_a_host_is_set()
    {
        var validator = new MailOptionsValidator();

        Assert.True(validator.Validate(null, new MailOptions()).Succeeded); // no host: mail simply off
        Assert.True(validator.Validate(null, new MailOptions { Host = "smtp.example", From = "pulse@example.com" }).Succeeded);
        Assert.True(validator.Validate(null, new MailOptions { Host = "smtp.example" }).Failed);
        Assert.True(validator.Validate(null, new MailOptions { Host = "smtp.example", From = "pulse@example.com", Port = 0 }).Failed);
        Assert.True(validator.Validate(null, new MailOptions { Host = "smtp.example", From = "pulse@example.com", User = "u" }).Failed);
    }

    // ------------------------------------------------------------ tokens and policies

    [Fact]
    public void Tokens_are_long_random_and_url_safe_and_their_hash_is_not_the_token()
    {
        var one = Tokens.NewToken();
        var two = Tokens.NewToken();

        Assert.NotEqual(one, two);
        Assert.Equal(43, one.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", one);
        Assert.Equal(Tokens.Hash(one), Tokens.Hash(one));
        Assert.NotEqual(one, Tokens.Hash(one));
        Assert.Equal(64, Tokens.Hash(one).Length);
    }

    [Theory]
    [InlineData("ana@example.com", true)]
    [InlineData("ana.dupont+pulse@mail.example.fr", true)]
    [InlineData("ana@example", false)]
    [InlineData("ana", false)]
    [InlineData("@example.com", false)]
    [InlineData("ana@@example.com", false)]
    [InlineData("Ana Dupont <ana@example.com>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void E_mail_addresses_are_checked(string? email, bool valid)
    {
        Assert.Equal(valid, EmailPolicy.IsValid(email));
    }

    [Theory]
    [InlineData("ana-corp", true)]
    [InlineData("a1b", true)]
    [InlineData("ab", false)]
    [InlineData("Ana", false)]
    [InlineData("ana corp", false)]
    [InlineData("ana--corp", true)]
    [InlineData("-ana", false)]
    [InlineData("ana-", false)]
    [InlineData("éclair", false)]
    public void Page_addresses_are_checked(string slug, bool valid)
    {
        Assert.Equal(valid, Pulse.Domain.Organization.IsValidSlug(slug));
    }
}
