namespace ArkServerManager.Core;

public sealed record IniEntry(string Section, string Key, string Value);

/// <summary>Edits selected keys without discarding unknown settings, comments, or repeated entries.</summary>
public sealed class IniDocument
{
    private readonly List<string> lines;
    private readonly string newline;
    private readonly bool trailingNewline;

    public IniDocument(string text)
    {
        newline = text.Contains("\r\n") ? "\r\n" : "\n";
        trailingNewline = text.EndsWith('\n');
        lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (trailingNewline) lines.RemoveAt(lines.Count - 1);
    }

    public IReadOnlyList<string> GetValues(string section, string key)
    {
        var active = "";
        var values = new List<string>();
        foreach (var line in lines)
        {
            if (TrySection(line, out var name)) { active = name; continue; }
            if (string.Equals(active, section, StringComparison.OrdinalIgnoreCase) && TryKey(line, out var found, out var value)
                && string.Equals(found, key, StringComparison.OrdinalIgnoreCase)) values.Add(value);
        }
        return values;
    }

    public IReadOnlyList<IniEntry> Entries()
    {
        var active = ""; var result = new List<IniEntry>();
        foreach (var line in lines)
        {
            if (TrySection(line, out var section)) { active = section; continue; }
            if (active.Length > 0 && TryKey(line, out var key, out var value)) result.Add(new(active, key, value));
        }
        return result;
    }

    public void SetValues(string section, string key, IEnumerable<string> values)
    {
        ValidateToken(section, nameof(section));
        ValidateToken(key, nameof(key));
        var replacement = values.Select(v =>
        {
            if (v.Contains('\n') || v.Contains('\r')) throw new ArgumentException("A setting value must fit on one line.");
            return key + "=" + v;
        }).ToList();
        var active = "";
        var matches = new List<int>();
        var lastSectionEnd = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (TrySection(lines[i], out var name)) active = name;
            if (!string.Equals(active, section, StringComparison.OrdinalIgnoreCase)) continue;
            lastSectionEnd = i + 1;
            if (TryKey(lines[i], out var found, out _) && string.Equals(found, key, StringComparison.OrdinalIgnoreCase)) matches.Add(i);
        }
        if (matches.Count > 0)
        {
            var insert = matches[0];
            foreach (var index in matches.AsEnumerable().Reverse()) lines.RemoveAt(index);
            lines.InsertRange(insert, replacement);
        }
        else if (lastSectionEnd >= 0) lines.InsertRange(lastSectionEnd, replacement);
        else
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            lines.Add("[" + section + "]");
            lines.AddRange(replacement);
        }
    }

    public override string ToString() => string.Join(newline, lines) + (trailingNewline ? newline : "");

    public void RemoveKeys(IEnumerable<string> keys)
    {
        var names = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        lines.RemoveAll(line => TryKey(line, out var key, out _) && names.Contains(key));
    }

    private static void ValidateToken(string value, string argument)
    {
        var brackets=value.IndexOfAny(['[',']'])>=0;
        var indexedKey=argument=="key"&&System.Text.RegularExpressions.Regex.IsMatch(value,@"^[^\[\]\r\n=]+(?:\[[0-9]+\])+$");
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('=') || brackets&&!indexedKey || argument=="key"&&(value.TrimStart().StartsWith(';')||value.TrimStart().StartsWith('#')))
            throw new ArgumentException("Invalid configuration name.", argument);
    }

    private static bool TrySection(string line, out string section)
    {
        var trimmed = line.Trim();
        section = trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1] : "";
        return section.Length > 0;
    }

    private static bool TryKey(string line, out string key, out string value)
    {
        key = value = "";
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) return false;
        var separator = line.IndexOf('=');
        if (separator <= 0) return false;
        key = line[..separator].Trim();
        value = line[(separator + 1)..];
        return key.Length > 0;
    }
}
