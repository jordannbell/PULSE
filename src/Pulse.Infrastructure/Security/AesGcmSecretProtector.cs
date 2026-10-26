using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Pulse.Application;
using Pulse.Application.Accounts;

namespace Pulse.Infrastructure.Security;

/// <summary>
/// Encrypts the few secrets Pulse must read back (webhook signing keys) with AES-256-GCM. The key comes from
/// the configuration, not from the database: a stolen copy of the database does not open them. Format:
/// "v1." then base64 of nonce (12 bytes), tag (16 bytes) and cipher text. GCM also detects any change.
/// </summary>
internal sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1.";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<AccountOptions> options)
    {
        var key = options.Value.EncryptionKey;
        if (!AccountOptionsValidator.IsValidKey(key))
        {
            throw new InvalidOperationException("Pulse:Accounts:EncryptionKey is missing or is not 32 bytes in base64.");
        }

        _key = Convert.FromBase64String(key);
    }

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        var plain = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plain, cipher, tag);

        return Version + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);
        if (!protectedText.StartsWith(Version, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unknown secret format.");
        }

        var data = Convert.FromBase64String(protectedText[Version.Length..]);
        if (data.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("The stored secret is too short.");
        }

        var nonce = data.AsSpan(0, NonceBytes);
        var tag = data.AsSpan(NonceBytes, TagBytes);
        var cipher = data.AsSpan(NonceBytes + TagBytes);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
