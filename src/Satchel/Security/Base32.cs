namespace Satchel.Security;

/// <summary>RFC 4648 base32 decoding (used for TOTP secrets).</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static bool TryDecode(string input, out byte[] bytes)
    {
        bytes = [];
        var clean = new string(input.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '=').ToArray()).ToUpperInvariant();
        if (clean.Length == 0) return false;

        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in clean)
        {
            int value = Alphabet.IndexOf(c);
            if (value < 0) return false;
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)(buffer >> (bitsLeft - 8)));
                bitsLeft -= 8;
            }
            buffer &= (1 << bitsLeft) - 1;
        }
        bytes = output.ToArray();
        return bytes.Length > 0;
    }
}
