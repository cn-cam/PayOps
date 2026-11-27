using System.Security.Cryptography;
using System.Text;

namespace PayOps.Core.Security;

/// <summary>敏感配置落库加密：AES-256-GCM，主密钥来自配置，所有副本共享。</summary>
public sealed class SecretProtector
{
    private const string Prefix = "v1:";
    private readonly byte[] _key;

    public SecretProtector(string masterKey)
    {
        if (string.IsNullOrWhiteSpace(masterKey)) throw new ArgumentException("MasterKey 未配置", nameof(masterKey));
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(masterKey));
    }

    public string Protect(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        var output = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, 12);
        cipher.CopyTo(output, 28);
        return Prefix + Convert.ToBase64String(output);
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("未知的密文格式");
        var raw = Convert.FromBase64String(protectedText[Prefix.Length..]);
        var plain = new byte[raw.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}

public static class PasswordHasher
{
    private const int Iterations = 120_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// 业务接入签名（请求与业务通知共用）：
/// sign = hex(HMAC-SHA256(secret, "{METHOD}\n{path}\n{timestamp}\n{nonce}\n{body}"))
/// </summary>
public static class ApiSigner
{
    public const string HeaderAppId = "X-App-Id";
    public const string HeaderTimestamp = "X-Timestamp";
    public const string HeaderNonce = "X-Nonce";
    public const string HeaderSign = "X-Sign";

    public static string Sign(string secret, string method, string pathAndQuery, string timestamp, string nonce, string body)
    {
        var text = $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{timestamp}\n{nonce}\n{body}";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(mac);
    }

    public static bool Verify(string secret, string method, string pathAndQuery, string timestamp, string nonce, string body, string sign)
    {
        var expected = Sign(secret, method, pathAndQuery, timestamp, nonce, body);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes((sign ?? "").Trim().ToLowerInvariant()));
    }

    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
}
