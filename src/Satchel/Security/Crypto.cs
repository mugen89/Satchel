using System.Security.Cryptography;
using System.Text;

namespace Satchel.Security;

/// <summary>
/// AES-256-GCM encryption for everything stored in the database.
/// The data key only exists in memory while the app is unlocked.
/// Layout of an encrypted value: [version:1][nonce:12][tag:16][ciphertext:n].
/// </summary>
public static class Crypto
{
    private const byte FormatVersion = 1;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int KeySize = 32;

    private static readonly object Gate = new();
    private static byte[]? _key;
    private static AesGcm? _aes;

    public static bool IsUnlocked
    {
        get { lock (Gate) return _key != null; }
    }

    public static void SetKey(byte[] key)
    {
        if (key.Length != KeySize) throw new ArgumentException("Invalid key length.", nameof(key));
        lock (Gate)
        {
            ClearInternal();
            _key = (byte[])key.Clone();
            _aes = new AesGcm(_key, TagSize);
        }
    }

    public static void Clear()
    {
        lock (Gate) ClearInternal();
    }

    private static void ClearInternal()
    {
        _aes?.Dispose();
        _aes = null;
        if (_key != null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }

    public static string EncryptString(string plain) =>
        Convert.ToBase64String(EncryptBytes(Encoding.UTF8.GetBytes(plain)));

    public static string DecryptString(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        return Encoding.UTF8.GetString(DecryptBytes(Convert.FromBase64String(cipher)));
    }

    public static byte[] EncryptBytes(byte[] plain)
    {
        lock (Gate)
        {
            var aes = _aes ?? throw new InvalidOperationException("The vault is locked.");
            return Seal(aes, plain, null);
        }
    }

    public static byte[] DecryptBytes(byte[] data)
    {
        if (data.Length == 0) return data;
        lock (Gate)
        {
            var aes = _aes ?? throw new InvalidOperationException("The vault is locked.");
            return Open(aes, data, null);
        }
    }

    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> plain, byte[]? aad)
    {
        using var aes = new AesGcm(key, TagSize);
        return Seal(aes, plain, aad);
    }

    public static byte[] Open(byte[] key, ReadOnlySpan<byte> data, byte[]? aad)
    {
        using var aes = new AesGcm(key, TagSize);
        return Open(aes, data, aad);
    }

    private static byte[] Seal(AesGcm aes, ReadOnlySpan<byte> plain, byte[]? aad)
    {
        var output = new byte[1 + NonceSize + TagSize + plain.Length];
        output[0] = FormatVersion;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        var tag = output.AsSpan(1 + NonceSize, TagSize);
        var cipher = output.AsSpan(1 + NonceSize + TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, aad);
        return output;
    }

    private static byte[] Open(AesGcm aes, ReadOnlySpan<byte> data, byte[]? aad)
    {
        if (data.Length < 1 + NonceSize + TagSize || data[0] != FormatVersion)
            throw new CryptographicException("Unsupported or corrupted encrypted value.");
        var nonce = data.Slice(1, NonceSize);
        var tag = data.Slice(1 + NonceSize, TagSize);
        var cipher = data.Slice(1 + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        aes.Decrypt(nonce, cipher, tag, plain, aad);
        return plain;
    }
}
