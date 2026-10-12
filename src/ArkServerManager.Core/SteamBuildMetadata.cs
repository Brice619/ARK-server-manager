using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ArkServerManager.Core;

public sealed record SteamBuild(uint AppId, ulong BuildId, string Name, string Platforms);

public static partial class SteamBuildMetadata
{
    public static SteamBuild ReadAvailable(string output, uint appId)
    {
        if (output.Length > 16 * 1024 * 1024) throw new InvalidDataException("Steam metadata is too large.");
        var match = Regex.Match(output, "(?m)^\\s*\"" + appId.ToString(CultureInfo.InvariantCulture) + "\"\\s*\\{", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("Steam did not return metadata for the requested application.");
        var parser = new KeyValuesParser(output[match.Index..]);
        var root = parser.Root();
        var build = root.At("depots", "branches", "public", "buildid").Value;
        if (!ulong.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)
            throw new InvalidDataException("The public server build ID is invalid.");
        return new(appId, number, root.At("common", "name").Value!, root.At("common", "oslist").Value!);
    }

    public static ulong ReadInstalled(string manifest, uint appId)
    {
        var root = new KeyValuesParser(manifest).Root();
        if (root.Name != "AppState" || root.At("appid").Value != appId.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("This installation manifest belongs to another application.");
        if (!ulong.TryParse(root.At("buildid").Value, NumberStyles.None, CultureInfo.InvariantCulture, out var build) || build == 0)
            throw new InvalidDataException("The installed build ID is invalid.");
        return build;
    }

    private sealed record Node(string Name, string? Value, List<Node> Children)
    {
        public Node At(params string[] path)
        {
            var current = this;
            foreach (var part in path)
            {
                var found = current.Children.Where(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (found.Length != 1) throw new InvalidDataException("Missing or ambiguous Steam metadata field: " + part);
                current = found[0];
            }
            return current;
        }
    }

    private sealed class KeyValuesParser(string text)
    {
        private int position;
        public Node Root()
        {
            if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("Steam metadata is too large.");
            var name = Token();
            if (name is null or "{" or "}") throw new InvalidDataException("Missing Steam metadata root.");
            if (Token() != "{") throw new InvalidDataException("Missing Steam metadata block.");
            return new(name, null, Block(0));
        }

        private List<Node> Block(int depth)
        {
            if (depth > 64) throw new InvalidDataException("Steam metadata nesting is too deep.");
            var nodes = new List<Node>();
            while (true)
            {
                var name = Token() ?? throw new InvalidDataException("Truncated Steam metadata.");
                if (name == "}") return nodes;
                if (name == "{") throw new InvalidDataException("Unexpected Steam metadata block.");
                var value = Token() ?? throw new InvalidDataException("Missing Steam metadata value.");
                if (value == "{") nodes.Add(new(name, null, Block(depth + 1)));
                else if (value == "}") throw new InvalidDataException("Missing Steam metadata value.");
                else nodes.Add(new(name, value, []));
            }
        }

        private string? Token()
        {
            while (position < text.Length)
            {
                if (char.IsWhiteSpace(text[position])) { position++; continue; }
                if (position + 1 < text.Length && text[position] == '/' && text[position + 1] == '/')
                { while (position < text.Length && text[position] != '\n') position++; continue; }
                break;
            }
            if (position == text.Length) return null;
            var first = text[position++];
            if (first is '{' or '}') return first.ToString();
            if (first != '"') throw new InvalidDataException("Unsupported Steam metadata token.");
            var value = new StringBuilder();
            while (position < text.Length)
            {
                var character = text[position++];
                if (character == '"') return value.ToString();
                if (character == '\\' && position < text.Length)
                {
                    var escaped = text[position++];
                    if (escaped is '\\' or '"') value.Append(escaped);
                    else { value.Append('\\'); value.Append(escaped); }
                }
                else value.Append(character);
            }
            throw new InvalidDataException("Unterminated Steam metadata string.");
        }
    }
}
