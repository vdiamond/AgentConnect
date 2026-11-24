using System.Security.Cryptography;
using System.Text;

namespace AgentConnect;

/// <summary>
/// A newly issued token and the values stored on the Member. The plain token is not stored.
/// </summary>
/// <param name="Token">The opaque token returned to the member.</param>
/// <param name="Hash">The hash of the secret.</param>
/// <param name="Encrypted">The encrypted token.</param>
public sealed record IssuedToken(string Token, string Hash, string Encrypted);

/// <summary>
/// Issues tokens, checks the secret hash, and encrypts the token for resend. FR-5 and FR-47.
/// </summary>
public sealed class TokenService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TokenService"/> class.
    /// </summary>
    /// <param name="encryptionKeyBase64">32 bytes, base64. The key is not stored in Cosmos DB.</param>
    public TokenService(string encryptionKeyBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKeyBase64);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encryptionKeyBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("TokenEncryptionKey must be base64.", exception);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException("TokenEncryptionKey must decode to 32 bytes.");
        }

        _key = key;
        ContinuationTokens.Configure(encryptionKeyBase64);
    }

    /// <summary>
    /// Issues a token that carries the tenant id, the member id, and a random secret.
    /// </summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="memberId">The member id.</param>
    /// <returns>The token and the stored forms.</returns>
    public IssuedToken Issue(string tenantId, string memberId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = tenantId + "." + memberId + "." + secret;
        return new IssuedToken(token, Hash(secret), Encrypt(token));
    }

    /// <summary>
    /// Reads the tenant id, member id, and secret from a token.
    /// </summary>
    /// <param name="token">The bearer token.</param>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="memberId">The member id.</param>
    /// <param name="secret">The secret.</param>
    /// <returns><see langword="false"/> when the token is not in the issued form.</returns>
    public bool TryParse(string? token, out string tenantId, out string memberId, out string secret)
    {
        tenantId = "";
        memberId = "";
        secret = "";
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
        {
            return false;
        }

        tenantId = parts[0];
        memberId = parts[1];
        secret = parts[2];
        return true;
    }

    /// <summary>
    /// Compares a secret with a stored hash in constant time.
    /// </summary>
    /// <param name="secret">The secret from the token.</param>
    /// <param name="storedHash">The stored hash.</param>
    /// <returns><see langword="true"/> when they match.</returns>
    public bool HashMatches(string secret, string storedHash)
    {
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Decrypts a stored token so it can be emailed again.
    /// </summary>
    /// <param name="encrypted">The stored ciphertext.</param>
    /// <returns>The token.</returns>
    public string Decrypt(string encrypted)
    {
        var payload = Convert.FromBase64String(encrypted);
        if (payload.Length < 1 + 12 + 16 || payload[0] != 1)
        {
            throw new CryptographicException("The stored token cannot be decrypted.");
        }

        var nonce = payload.AsSpan(1, 12);
        var tag = payload.AsSpan(13, 16);
        var cipher = payload.AsSpan(29);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// Compares two admin keys without leaking the secret into a log.
    /// </summary>
    /// <param name="provided">The header value.</param>
    /// <param name="expected">The configured key.</param>
    /// <returns><see langword="true"/> when they match.</returns>
    public static bool AdminKeyMatches(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var left = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    #region private

    private readonly byte[] _key;

    private static string Hash(string secret)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    private string Encrypt(string token)
    {
        var plain = Encoding.UTF8.GetBytes(token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var payload = new byte[1 + nonce.Length + tag.Length + cipher.Length];
        payload[0] = 1;
        nonce.CopyTo(payload.AsSpan(1));
        tag.CopyTo(payload.AsSpan(13));
        cipher.CopyTo(payload.AsSpan(29));
        return Convert.ToBase64String(payload);
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    #endregion
}
