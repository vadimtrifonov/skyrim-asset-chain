namespace Skyrim.AssetChain;

internal static class Mo2ModList
{
    internal static IReadOnlyList<EnabledMod> ReadEnabled(string modlistPath, string modsFolder)
    {
        var enabled = new List<EnabledMod>();
        var seenManaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(modlistPath);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var raw = lines[lineIndex];
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var marker = line[0];
            if (marker is not ('+' or '-' or '*'))
            {
                throw MalformedEntry(modlistPath, lineIndex + 1, raw, "unknown mod marker");
            }

            var name = line[1..].Trim();
            if (name.Length == 0)
            {
                throw MalformedEntry(modlistPath, lineIndex + 1, raw, "missing mod name");
            }

            if (marker == '*' || name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seenManaged.Add(name))
            {
                throw MalformedEntry(modlistPath, lineIndex + 1, raw, "duplicate managed mod");
            }

            if (marker == '-')
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(modsFolder, name));
            var modsPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsFolder)) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(modsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw MalformedEntry(modlistPath, lineIndex + 1, raw, "mod name resolves outside the mods directory");
            }

            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(
                    $"Enabled mod '{name}' from {modlistPath}, line {lineIndex + 1}, has no directory: {path}");
            }

            enabled.Add(new EnabledMod(name, path, enabled.Count));
        }

        return enabled;
    }

    private static InvalidOperationException MalformedEntry(
        string path,
        int line,
        string raw,
        string reason) =>
        new($"Malformed profile entry in {path}, line {line}: {reason}: {raw}");
}

internal sealed record EnabledMod(string Name, string Path, int ModlistIndex);
