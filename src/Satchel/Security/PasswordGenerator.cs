using System.IO;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Security;

public partial class PasswordOptions : ObservableObject
{
    [ObservableProperty] private bool _usePassphrase;
    [ObservableProperty] private int _length = 20;
    [ObservableProperty] private bool _uppercase = true;
    [ObservableProperty] private bool _lowercase = true;
    [ObservableProperty] private bool _digits = true;
    [ObservableProperty] private bool _symbols = true;
    [ObservableProperty] private bool _avoidAmbiguous = true;
    [ObservableProperty] private int _wordCount = 5;
    [ObservableProperty] private string _separator = "-";
    [ObservableProperty] private bool _capitalize = true;
    [ObservableProperty] private bool _includeNumber = true;
}

/// <summary>Cryptographically secure password and passphrase generator.</summary>
public static class PasswordGenerator
{
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    private const string Symbols = "!@#$%^&*()-_=+[]{};:,.?/~";
    private const string Ambiguous = "Il1O0o|`'\"";

    private static readonly Lazy<string[]> Words = new(LoadWords);

    public static int WordListSize => Words.Value.Length;

    public static string Generate(PasswordOptions o) => o.UsePassphrase ? GeneratePassphrase(o) : GeneratePassword(o);

    public static string GeneratePassword(PasswordOptions o)
    {
        var pools = new List<string>();
        if (o.Uppercase) pools.Add(Upper);
        if (o.Lowercase) pools.Add(Lower);
        if (o.Digits) pools.Add(Digits);
        if (o.Symbols) pools.Add(Symbols);
        if (pools.Count == 0) pools.Add(Lower);

        if (o.AvoidAmbiguous)
            pools = pools.Select(p => new string(p.Where(c => !Ambiguous.Contains(c)).ToArray())).ToList();

        int length = Math.Clamp(o.Length, 4, 128);
        var all = string.Concat(pools);
        var chars = new List<char>(length);
        foreach (var pool in pools) chars.Add(pool[RandomNumberGenerator.GetInt32(pool.Length)]);
        while (chars.Count < length) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        // Fisher–Yates shuffle so the guaranteed characters are not always first.
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.Take(length).ToArray());
    }

    public static string GeneratePassphrase(PasswordOptions o)
    {
        var list = Words.Value;
        int count = Math.Clamp(o.WordCount, 3, 12);
        var words = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var w = list[RandomNumberGenerator.GetInt32(list.Length)];
            if (o.Capitalize) w = char.ToUpperInvariant(w[0]) + w[1..];
            words.Add(w);
        }
        if (o.IncludeNumber)
        {
            int idx = RandomNumberGenerator.GetInt32(words.Count);
            words[idx] += RandomNumberGenerator.GetInt32(10, 100).ToString();
        }
        return string.Join(o.Separator ?? "", words);
    }

    /// <summary>Theoretical entropy of the generator's output for the given options.</summary>
    public static double Entropy(PasswordOptions o)
    {
        if (o.UsePassphrase)
            return Math.Clamp(o.WordCount, 3, 12) * Math.Log2(Math.Max(2, WordListSize)) + (o.IncludeNumber ? Math.Log2(90 * o.WordCount) : 0);
        int pool = (o.Uppercase ? 26 : 0) + (o.Lowercase ? 26 : 0) + (o.Digits ? 10 : 0) + (o.Symbols ? Symbols.Length : 0);
        if (o.AvoidAmbiguous) pool -= 7;
        return Math.Clamp(o.Length, 4, 128) * Math.Log2(Math.Max(2, pool));
    }

    internal static bool IsDictionaryWord(string token) => Words.Value.Contains(token, StringComparer.OrdinalIgnoreCase);

    private static string[] LoadWords()
    {
        try
        {
            using var stream = typeof(PasswordGenerator).Assembly.GetManifestResourceStream("Satchel.Resources.wordlist.txt");
            if (stream == null) return Fallback;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var words = reader.ReadToEnd()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim().ToLowerInvariant())
                .Where(w => w.Length >= 3 && w.All(char.IsLetter))
                .Distinct()
                .ToArray();
            return words.Length > 100 ? words : Fallback;
        }
        catch
        {
            return Fallback;
        }
    }

    private static readonly string[] Fallback =
        "apple river stone cloud maple tiger ocean candle forest silver rocket meadow pepper harbor willow ember".Split(' ');
}
