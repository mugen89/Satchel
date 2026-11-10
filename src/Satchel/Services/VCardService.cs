using System.Globalization;
using System.Text;
using Satchel.Models;

namespace Satchel.Services;

/// <summary>Basic vCard (.vcf) import/export (versions 2.1–4.0, common fields).</summary>
public static class VCardService
{
    public static List<Contact> Parse(string text)
    {
        var result = new List<Contact>();
        Contact? c = null;
        var emails = new List<ContactEntry>();
        var phones = new List<ContactEntry>();

        foreach (var line in Unfold(text))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var head = line[..colon];
            var value = line[(colon + 1)..];
            var parts = head.Split(';');
            var name = parts[0].ToUpperInvariant();
            if (name.Contains('.')) name = name[(name.IndexOf('.') + 1)..]; // item1.EMAIL
            var type = string.Join(",", parts.Skip(1).Select(p => p.Replace("TYPE=", "", StringComparison.OrdinalIgnoreCase))).ToLowerInvariant();

            switch (name)
            {
                case "BEGIN" when value.Equals("VCARD", StringComparison.OrdinalIgnoreCase):
                    c = new Contact();
                    emails = [];
                    phones = [];
                    break;
                case "END" when c != null:
                    c.Emails = ContactEntry.Serialize(emails);
                    c.Phones = ContactEntry.Serialize(phones);
                    if (!string.IsNullOrWhiteSpace(c.FullName)) result.Add(c);
                    c = null;
                    break;
                case "N" when c != null:
                    var n = value.Split(';');
                    c.LastName = Unescape(n.ElementAtOrDefault(0) ?? "");
                    c.FirstName = Unescape(n.ElementAtOrDefault(1) ?? "");
                    break;
                case "FN" when c != null && string.IsNullOrWhiteSpace(c.FirstName) && string.IsNullOrWhiteSpace(c.LastName):
                    var full = Unescape(value).Trim();
                    var space = full.LastIndexOf(' ');
                    if (space > 0) { c.FirstName = full[..space]; c.LastName = full[(space + 1)..]; }
                    else c.FirstName = full;
                    break;
                case "NICKNAME" when c != null: c.Nickname = Unescape(value); break;
                case "ORG" when c != null: c.Company = Unescape(value.Split(';')[0]); break;
                case "TITLE" when c != null: c.JobTitle = Unescape(value); break;
                case "NOTE" when c != null: c.Notes = Unescape(value); break;
                case "URL" when c != null: c.Website = Unescape(value); break;
                case "EMAIL" when c != null: emails.Add(new ContactEntry { Label = Label(type, "Email"), Value = Unescape(value) }); break;
                case "TEL" when c != null: phones.Add(new ContactEntry { Label = Label(type, "Phone"), Value = Unescape(value) }); break;
                case "ADR" when c != null:
                    var adr = value.Split(';').Select(Unescape).Where(p => !string.IsNullOrWhiteSpace(p));
                    c.Address = string.Join(", ", adr);
                    break;
                case "BDAY" when c != null:
                    var v = value.Replace("-", "");
                    if (DateTime.TryParseExact(v.Length >= 8 ? v[..8] : v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b)) c.Birthday = b;
                    break;
            }
        }
        return result;
    }

    private static string Label(string type, string fallback)
    {
        if (type.Contains("cell") || type.Contains("mobile")) return "Mobile";
        if (type.Contains("work")) return "Work";
        if (type.Contains("home")) return "Home";
        return fallback;
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var lines = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && lines.Count > 0) lines[^1] += line[1..];
            else lines.Add(line);
        }
        return lines;
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\r\n", "\\n").Replace("\n", "\\n");

    public static string Build(IEnumerable<Contact> contacts)
    {
        var sb = new StringBuilder();
        foreach (var c in contacts)
        {
            sb.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");
            sb.Append($"N:{Escape(c.LastName)};{Escape(c.FirstName)};;;\r\n");
            sb.Append($"FN:{Escape(c.FullName)}\r\n");
            if (!string.IsNullOrWhiteSpace(c.Nickname)) sb.Append($"NICKNAME:{Escape(c.Nickname)}\r\n");
            if (!string.IsNullOrWhiteSpace(c.Company)) sb.Append($"ORG:{Escape(c.Company)}\r\n");
            if (!string.IsNullOrWhiteSpace(c.JobTitle)) sb.Append($"TITLE:{Escape(c.JobTitle)}\r\n");
            foreach (var e in ContactEntry.Parse(c.Emails)) sb.Append($"EMAIL;TYPE={e.Label.ToUpperInvariant()}:{Escape(e.Value)}\r\n");
            foreach (var p in ContactEntry.Parse(c.Phones)) sb.Append($"TEL;TYPE={(p.Label.Equals("Mobile", StringComparison.OrdinalIgnoreCase) ? "CELL" : p.Label.ToUpperInvariant())}:{Escape(p.Value)}\r\n");
            if (!string.IsNullOrWhiteSpace(c.Address)) sb.Append($"ADR:;;{Escape(c.Address)};;;;\r\n");
            if (c.Birthday.HasValue) sb.Append($"BDAY:{c.Birthday.Value:yyyy-MM-dd}\r\n");
            if (!string.IsNullOrWhiteSpace(c.Website)) sb.Append($"URL:{Escape(c.Website)}\r\n");
            if (!string.IsNullOrWhiteSpace(c.Notes)) sb.Append($"NOTE:{Escape(c.Notes)}\r\n");
            sb.Append("END:VCARD\r\n");
        }
        return sb.ToString();
    }
}
