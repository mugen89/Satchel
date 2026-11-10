using System.Security.Cryptography;
using System.Web;

namespace Satchel.Security;

public sealed class TotpConfig
{
    public required byte[] Secret { get; init; }
    public int Digits { get; init; } = 6;
    public int Period { get; init; } = 30;
    public string Algorithm { get; init; } = "SHA1";
    public string? Issuer { get; init; }
    public string? Account { get; init; }
}

/// <summary>RFC 6238 time-based one-time passwords (Google Authenticator compatible).</summary>
public static class Totp
{
    public static TotpConfig? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();

        if (input.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return null;
            if (!string.Equals(uri.Host, "totp", StringComparison.OrdinalIgnoreCase)) return null;
            var query = HttpUtility.ParseQueryString(uri.Query);
            var secret = query["secret"];
            if (secret == null || !Base32.TryDecode(secret, out var bytes)) return null;

            var label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            string? issuer = query["issuer"], account = label;
            var colon = label.IndexOf(':');
            if (colon >= 0)
            {
                issuer ??= label[..colon];
                account = label[(colon + 1)..].Trim();
            }

            int digits = int.TryParse(query["digits"], out var d) && d is >= 6 and <= 8 ? d : 6;
            int period = int.TryParse(query["period"], out var p) && p is > 0 and <= 300 ? p : 30;
            var algorithm = (query["algorithm"] ?? "SHA1").ToUpperInvariant();
            if (algorithm is not ("SHA1" or "SHA256" or "SHA512")) algorithm = "SHA1";

            return new TotpConfig { Secret = bytes, Digits = digits, Period = period, Algorithm = algorithm, Issuer = issuer, Account = account };
        }

        return Base32.TryDecode(input, out var raw) ? new TotpConfig { Secret = raw } : null;
    }

    public static bool IsValid(string? input) => Parse(input) != null;

    public static string Compute(TotpConfig config, DateTimeOffset time)
    {
        long counter = time.ToUnixTimeSeconds() / config.Period;
        Span<byte> message = stackalloc byte[8];
        for (int i = 7; i >= 0; i--) { message[i] = (byte)(counter & 0xFF); counter >>= 8; }

        byte[] hash = config.Algorithm switch
        {
            "SHA256" => HMACSHA256.HashData(config.Secret, message),
            "SHA512" => HMACSHA512.HashData(config.Secret, message),
            _ => HMACSHA1.HashData(config.Secret, message)
        };

        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        int modulo = 1;
        for (int i = 0; i < config.Digits; i++) modulo *= 10;
        return (binary % modulo).ToString(new string('0', config.Digits));
    }

    public static int SecondsRemaining(TotpConfig config, DateTimeOffset time) =>
        config.Period - (int)(time.ToUnixTimeSeconds() % config.Period);

    /// <summary>"123456" → "123 456".</summary>
    public static string Pretty(string code) => code.Length switch
    {
        6 => $"{code[..3]} {code[3..]}",
        8 => $"{code[..4]} {code[4..]}",
        _ => code
    };
}
