using System.Security.Cryptography;
using System.Text;

namespace Satchel.Security;

/// <summary>Persisted (non-secret) key material. The data key is stored only in wrapped form.</summary>
public sealed class KeyMaterial
{
    public int Version { get; set; } = 1;
    public string Kdf { get; set; } = "PBKDF2-SHA256";
    public int Iterations { get; set; }
    public string Salt { get; set; } = "";
    public string WrappedKey { get; set; } = "";
    public int RecoveryIterations { get; set; }
    public string RecoverySalt { get; set; } = "";
    public string RecoveryWrappedKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime PasswordChangedAt { get; set; }
}

/// <summary>
/// Envelope encryption: a random 256-bit data key encrypts the content; the data key itself is
/// wrapped twice — once with a key derived from the master password and once with a key derived
/// from the recovery key. Changing the password only re-wraps the data key.
/// </summary>
public static class KeyManager
{
    public const int PasswordIterations = 600_000;
    public const int RecoveryIterations = 210_000;
    private static readonly byte[] PasswordAad = Encoding.ASCII.GetBytes("satchel:dek:password:v1");
    private static readonly byte[] RecoveryAad = Encoding.ASCII.GetBytes("satchel:dek:recovery:v1");
    private const string RecoveryAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32

    public static byte[] Derive(string secret, byte[] salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(secret.Normalize(NormalizationForm.FormC));
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, Crypto.KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static (KeyMaterial Material, byte[] DataKey, string RecoveryKey) Create(string password)
    {
        var dataKey = RandomNumberGenerator.GetBytes(Crypto.KeySize);
        var material = new KeyMaterial
        {
            Iterations = PasswordIterations,
            RecoveryIterations = RecoveryIterations,
            CreatedAt = DateTime.Now,
            PasswordChangedAt = DateTime.Now
        };
        WrapWithPassword(material, dataKey, password);
        var recovery = GenerateRecoveryKey();
        WrapWithRecovery(material, dataKey, recovery);
        return (material, dataKey, recovery);
    }

    public static void WrapWithPassword(KeyMaterial material, byte[] dataKey, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        if (material.Iterations <= 0) material.Iterations = PasswordIterations;
        var kek = Derive(password, salt, material.Iterations);
        try
        {
            material.Salt = Convert.ToBase64String(salt);
            material.WrappedKey = Convert.ToBase64String(Crypto.Seal(kek, dataKey, PasswordAad));
            material.PasswordChangedAt = DateTime.Now;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public static void WrapWithRecovery(KeyMaterial material, byte[] dataKey, string recoveryKey)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        if (material.RecoveryIterations <= 0) material.RecoveryIterations = RecoveryIterations;
        var kek = Derive(NormalizeRecoveryKey(recoveryKey), salt, material.RecoveryIterations);
        try
        {
            material.RecoverySalt = Convert.ToBase64String(salt);
            material.RecoveryWrappedKey = Convert.ToBase64String(Crypto.Seal(kek, dataKey, RecoveryAad));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public static byte[]? TryUnwrapWithPassword(KeyMaterial material, string password) =>
        TryUnwrap(password, material.Salt, material.Iterations, material.WrappedKey, PasswordAad);

    public static byte[]? TryUnwrapWithRecovery(KeyMaterial material, string recoveryKey) =>
        TryUnwrap(NormalizeRecoveryKey(recoveryKey), material.RecoverySalt, material.RecoveryIterations, material.RecoveryWrappedKey, RecoveryAad);

    private static byte[]? TryUnwrap(string secret, string salt, int iterations, string wrapped, byte[] aad)
    {
        if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(wrapped)) return null;
        byte[] kek = Derive(secret, Convert.FromBase64String(salt), iterations);
        try
        {
            return Crypto.Open(kek, Convert.FromBase64String(wrapped), aad);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>25 characters of Crockford base32 (125 bits), shown as 5 groups of 5.</summary>
    public static string GenerateRecoveryKey()
    {
        var sb = new StringBuilder(29);
        for (int i = 0; i < 25; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)]);
        }
        return sb.ToString();
    }

    public static string NormalizeRecoveryKey(string input)
    {
        var sb = new StringBuilder(25);
        foreach (var raw in input.ToUpperInvariant())
        {
            var ch = raw switch { 'O' => '0', 'I' => '1', 'L' => '1', _ => raw };
            if (RecoveryAlphabet.Contains(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }
}
