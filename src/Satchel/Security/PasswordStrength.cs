namespace Satchel.Security;

public readonly record struct StrengthResult(int Score, double Entropy, string Label, string Hint)
{
    public static readonly StrengthResult Empty = new(-1, 0, "", "");
}

/// <summary>
/// Lightweight offline strength estimate: character-pool entropy with penalties for
/// common passwords, keyboard runs, sequences, repeats, years and dictionary words.
/// </summary>
public static class PasswordStrength
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456", "123456789", "12345678", "password", "qwerty", "123123", "111111", "abc123", "1234567",
        "password1", "12345", "1234567890", "000000", "iloveyou", "1234", "qwerty123", "dragon", "monkey",
        "letmein", "football", "baseball", "welcome", "admin", "login", "master", "sunshine", "princess",
        "shadow", "superman", "michael", "trustno1", "starwars", "passw0rd", "p@ssw0rd", "p@ssword",
        "qwertyuiop", "asdfghjkl", "zxcvbnm", "654321", "666666", "121212", "7777777", "secret", "hello",
        "freedom", "whatever", "charlie", "jordan", "hunter", "ranger", "buster", "soccer", "hockey",
        "killer", "computer", "internet", "samsung", "google", "apple", "linkedin", "facebook", "pokemon",
        "batman", "flower", "cheese", "summer", "winter", "spring", "autumn", "love", "family", "hello123",
        "admin123", "root", "toor", "changeme", "default", "guest", "test", "test123", "qazwsx", "1q2w3e4r",
        "1qaz2wsx", "zaq12wsx", "aa123456", "abcd1234", "q1w2e3r4", "password123", "welcome1", "letmein1"
    };

    private static readonly string[] Rows = ["qwertyuiop", "asdfghjkl", "zxcvbnm", "1234567890", "abcdefghijklmnopqrstuvwxyz"];

    public static StrengthResult Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password)) return StrengthResult.Empty;

        if (Common.Contains(password) || Common.Contains(StripDigitsAndSymbols(password)) && password.Length < 12)
            return new StrengthResult(0, 4, "Very weak", "This is one of the most common passwords.");

        int pool = 0;
        bool lower = password.Any(char.IsLower), upper = password.Any(char.IsUpper), digit = password.Any(char.IsDigit);
        bool symbol = password.Any(c => !char.IsLetterOrDigit(c) && c < 128), other = password.Any(c => c >= 128);
        if (lower) pool += 26;
        if (upper) pool += 26;
        if (digit) pool += 10;
        if (symbol) pool += 33;
        if (other) pool += 100;

        double entropy = password.Length * Math.Log2(Math.Max(pool, 2));
        string hint = "";

        // Repeated characters ("aaaa").
        int repeats = 0;
        for (int i = 1; i < password.Length; i++) if (password[i] == password[i - 1]) repeats++;
        if (repeats > 0) { entropy -= repeats * Math.Log2(Math.Max(pool, 2)) * 0.75; hint = "Avoid repeated characters."; }

        // Sequences / keyboard runs of 3+ ("abcd", "4321", "qwer").
        var lowerPw = password.ToLowerInvariant();
        int sequenceChars = 0;
        foreach (var row in Rows)
        {
            for (int len = Math.Min(row.Length, lowerPw.Length); len >= 3; len--)
            {
                for (int start = 0; start + len <= row.Length; start++)
                {
                    var seq = row.Substring(start, len);
                    var rev = new string(seq.Reverse().ToArray());
                    if (lowerPw.Contains(seq) || lowerPw.Contains(rev)) { sequenceChars = Math.Max(sequenceChars, len); }
                }
                if (sequenceChars > 0) break;
            }
        }
        if (sequenceChars > 0) { entropy -= sequenceChars * Math.Log2(Math.Max(pool, 2)) * 0.8; hint = "Avoid sequences like 'abcd' or '1234'."; }

        // Years.
        for (int i = 0; i + 4 <= password.Length; i++)
        {
            if (int.TryParse(password.AsSpan(i, 4), out var year) && year is >= 1900 and <= 2099)
            {
                entropy -= 8;
                hint = "Years and dates are easy to guess.";
                break;
            }
        }

        // Dictionary words (5+ letters).
        foreach (var token in SplitLetters(lowerPw))
        {
            if (token.Length >= 5 && (PasswordGenerator.IsDictionaryWord(token) || Common.Contains(token)))
            {
                entropy -= token.Length * Math.Log2(26) - Math.Log2(1000);
                if (string.IsNullOrEmpty(hint)) hint = "Common words are guessable; combine several or add randomness.";
            }
        }

        if (password.Length < 8) { entropy = Math.Min(entropy, 25); hint = "Use at least 12 characters."; }
        entropy = Math.Max(entropy, 1);

        int score = entropy switch
        {
            < 28 => 0,
            < 40 => 1,
            < 60 => 2,
            < 80 => 3,
            _ => 4
        };
        string label = score switch
        {
            0 => "Very weak",
            1 => "Weak",
            2 => "Fair",
            3 => "Strong",
            _ => "Very strong"
        };
        if (score >= 3) hint = "";
        else if (string.IsNullOrEmpty(hint)) hint = password.Length < 14 ? "Make it longer — length matters most." : "Mix in digits and symbols.";

        return new StrengthResult(score, entropy, label, hint);
    }

    private static string StripDigitsAndSymbols(string s) => new(s.Where(char.IsLetter).ToArray());

    private static IEnumerable<string> SplitLetters(string s)
    {
        var current = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetter(c)) current.Append(c);
            else if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
