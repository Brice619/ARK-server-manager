namespace ArkServerManager.Core;

public sealed record ConfigurationSyntaxFinding(int Line, string Message);

/// <summary>INI structure checks only; unknown game/mod keys and repeated array values are valid.</summary>
public static class ConfigurationSyntax
{
    public static IReadOnlyList<ConfigurationSyntaxFinding> Check(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 4 * 1024 * 1024) throw new ArgumentException("Configuration exceeds the supported size.");
        var findings = new List<ConfigurationSyntaxFinding>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            string? problem = null;
            if (line.Any(c => char.IsControl(c) && c != '\t')) problem = "Unexpected control character.";
            else if (line.StartsWith('['))
            {
                if (!line.EndsWith(']') || line.Length <= 2 || line[1..^1].IndexOfAny(['[', ']']) >= 0)
                    problem = "Section name must be enclosed in brackets.";
            }
            else if (line.IndexOf('=') <= 0 || string.IsNullOrWhiteSpace(line[..line.IndexOf('=')]))
                problem = "Expected a setting name followed by = and its value.";
            if (problem is not null) findings.Add(new(index + 1, problem));
            if (findings.Count == 100) break;
        }
        return findings;
    }
}
