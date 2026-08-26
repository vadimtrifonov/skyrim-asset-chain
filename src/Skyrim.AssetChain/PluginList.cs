using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace Skyrim.AssetChain;

internal static class PluginList
{
    internal static IReadOnlyList<ActivePlugin> ReadActive(
        GameKind game,
        string pluginsPath,
        SourceFileSystem sourceFiles)
    {
        var explicitPlugins = ReadProfileListings(pluginsPath);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var release = game == GameKind.SkyrimSE ? SkyrimRelease.SkyrimSE : SkyrimRelease.SkyrimVR;
        foreach (var modKey in Implicits.Listings.Skyrim(release))
        {
            AddUnique(names, seen, modKey.FileName);
        }

        var creationClubFile = sourceFiles.ResolveGameFiles("Skyrim.ccc").LastOrDefault();
        if (creationClubFile is not null)
        {
            foreach (var name in ReadCreationClubListings(creationClubFile.Path))
            {
                if (sourceFiles.ResolveDataFiles(name).Count != 0)
                {
                    AddUnique(names, seen, name);
                }
            }
        }

        foreach (var name in explicitPlugins)
        {
            AddUnique(names, seen, name);
        }

        var active = new List<ActivePlugin>(names.Count);
        for (var index = 0; index < names.Count; index++)
        {
            var name = names[index];
            var provider = sourceFiles.ResolveDataFiles(name).LastOrDefault();
            if (provider is null)
            {
                throw new FileNotFoundException(
                    $"Active plugin '{name}' cannot be resolved through the selected MO2 profile.");
            }

            active.Add(new ActivePlugin(name, index, provider));
        }

        return active;
    }

    internal static IReadOnlyList<string> ReadLoadOrder(string path)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(path);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var raw = lines[lineIndex];
            var name = raw.Trim();
            if (name.Length == 0 || name.StartsWith('#'))
            {
                continue;
            }

            if (name.StartsWith('*'))
            {
                name = name[1..].Trim();
            }

            ValidatePluginName(path, lineIndex + 1, raw, name);
            if (!seen.Add(name))
            {
                throw MalformedEntry(path, lineIndex + 1, raw, "duplicate load-order plugin");
            }

            names.Add(name);
        }

        return names;
    }

    private static IReadOnlyList<string> ReadProfileListings(string path)
    {
        var active = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(path);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var raw = lines[lineIndex];
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var enabled = line.StartsWith('*');
            var name = enabled ? line[1..].Trim() : line;
            if (name.EndsWith(".ghost", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^".ghost".Length];
                enabled = false;
            }

            ValidatePluginName(path, lineIndex + 1, raw, name);
            if (!seen.Add(name))
            {
                throw MalformedEntry(path, lineIndex + 1, raw, "duplicate plugin");
            }

            if (enabled)
            {
                active.Add(name);
            }
        }

        return active;
    }

    private static IReadOnlyList<string> ReadCreationClubListings(string path)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(path);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var raw = lines[lineIndex];
            var name = raw.Trim();
            if (name.Length == 0 || name.StartsWith('#'))
            {
                continue;
            }

            ValidatePluginName(path, lineIndex + 1, raw, name);
            if (!seen.Add(name))
            {
                throw MalformedEntry(path, lineIndex + 1, raw, "duplicate Creation Club plugin");
            }

            names.Add(name);
        }

        return names;
    }

    private static void AddUnique(
        ICollection<string> names,
        ISet<string> seen,
        string name)
    {
        if (seen.Add(name))
        {
            names.Add(name);
        }
    }

    private static void ValidatePluginName(string path, int line, string raw, string name)
    {
        var extension = Path.GetExtension(name);
        var validExtension = extension.Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".esl", StringComparison.OrdinalIgnoreCase);
        if (name.Length == 0 ||
            !Path.GetFileName(name).Equals(name, StringComparison.Ordinal) ||
            !validExtension)
        {
            throw MalformedEntry(path, line, raw, "invalid plugin filename");
        }
    }

    private static InvalidOperationException MalformedEntry(
        string path,
        int line,
        string raw,
        string reason) =>
        new($"Malformed profile entry in {path}, line {line}: {reason}: {raw}");
}

internal sealed record ActivePlugin(string Name, int LoadOrderIndex, PhysicalSourceFile Provider);
