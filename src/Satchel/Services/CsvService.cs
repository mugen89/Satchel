using System.Text;
using Satchel.Models;

namespace Satchel.Services;

public sealed record ImportedLogin(string Name, string Url, string Username, string Password, string Notes, string Totp, string Folder);

/// <summary>CSV import/export for passwords (Chrome, Edge, Firefox, Bitwarden, 1Password, generic).</summary>
public static class CsvService
{
    public static List<string[]> ReadRows(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Any(f => f.Length > 0)) rows.Add(row.ToArray());
                    row.Clear();
                    break;
                default: field.Append(c); break;
            }
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Length > 0)) rows.Add(row.ToArray());
        return rows;
    }

    public static List<ImportedLogin> ParseLogins(string text)
    {
        var rows = ReadRows(text.TrimStart('﻿'));
        if (rows.Count < 2) return [];
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();

        int Find(params string[] names)
        {
            foreach (var n in names)
            {
                int exact = Array.IndexOf(header, n);
                if (exact >= 0) return exact;
            }
            foreach (var n in names)
            {
                int partial = Array.FindIndex(header, h => h.Contains(n));
                if (partial >= 0) return partial;
            }
            return -1;
        }

        int name = Find("name", "title", "account");
        int url = Find("login_uri", "url", "website", "uri", "web site", "address");
        int user = Find("login_username", "username", "user name", "user", "login", "email");
        int pass = Find("login_password", "password", "pass");
        int notes = Find("notes", "note", "extra", "comments");
        int totp = Find("login_totp", "totp", "otpauth", "otp", "one-time");
        int folder = Find("folder", "group", "category", "grouping");
        if (pass < 0 && user < 0) return [];

        string Get(string[] r, int i) => i >= 0 && i < r.Length ? r[i].Trim() : "";
        var list = new List<ImportedLogin>();
        foreach (var r in rows.Skip(1))
        {
            var u = Get(r, url);
            var n = Get(r, name);
            if (string.IsNullOrWhiteSpace(n)) n = HostName(u);
            if (string.IsNullOrWhiteSpace(n)) n = "Imported";
            if (Get(r, user).Length == 0 && Get(r, pass).Length == 0 && Get(r, notes).Length == 0) continue;
            list.Add(new ImportedLogin(n, u, Get(r, user), Get(r, pass), Get(r, notes), Get(r, totp), Get(r, folder)));
        }
        return list;
    }

    public static string HostName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var s = url.Contains("://") ? url : "https://" + url;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return "";
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        if (host.Length == 0) return "";
        var first = host.Split('.')[0];
        return first.Length == 0 ? host : char.ToUpperInvariant(first[0]) + first[1..];
    }

    private static string Q(string? s)
    {
        s ??= "";
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static string BuildExport(IEnumerable<VaultProfile> profiles, IEnumerable<VaultAccount> accounts)
    {
        var byId = profiles.ToDictionary(p => p.Id);
        var sb = new StringBuilder();
        sb.AppendLine("profile,category,website,account,kind,username,email,password,url,totp,expires,tags,notes,fields");
        foreach (var a in accounts.OrderBy(a => byId.TryGetValue(a.ProfileId, out var p) ? p.Name : ""))
        {
            byId.TryGetValue(a.ProfileId, out var p);
            var fields = string.Join(" | ", CustomField.Parse(a.Fields).Select(f => $"{f.Label}: {f.Value}"));
            sb.AppendLine(string.Join(',',
                Q(p?.Name), Q(p?.Category), Q(p?.Website), Q(a.Label), Q(VaultAccount.KindName(a.Kind)), Q(a.Username), Q(a.Email), Q(a.Password),
                Q(a.Url), Q(a.TotpSecret), Q(a.ExpiresAt?.ToString("yyyy-MM-dd")), Q(a.Tags), Q(a.Notes), Q(fields)));
        }
        return sb.ToString();
    }
}
