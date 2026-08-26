namespace Skyrim.AssetChain;

internal sealed class SourceFileSystem
{
    private readonly IReadOnlyList<SourceLayer> _layersWeakToStrong;

    private SourceFileSystem(IReadOnlyList<SourceLayer> layersWeakToStrong)
    {
        _layersWeakToStrong = layersWeakToStrong;
    }

    internal static SourceFileSystem Create(
        string gameRoot,
        IReadOnlyList<EnabledMod> enabledMods,
        string overwriteFolder,
        Mo2SkipRules skipRules)
    {
        var layers = new List<SourceLayer>(enabledMods.Count + 2)
        {
            SourceLayer.CreateGame("Game Data", gameRoot)
        };

        foreach (var mod in enabledMods.Reverse())
        {
            layers.Add(SourceLayer.CreateMod(
                mod.Name,
                mod.Path,
                mod.ModlistIndex,
                required: true,
                skipRules: skipRules));
        }

        layers.Add(SourceLayer.CreateMod(
            "Overwrite",
            overwriteFolder,
            modlistIndex: null,
            required: false,
            skipRules: skipRules));
        return new SourceFileSystem(layers);
    }

    internal IReadOnlyList<PhysicalSourceFile> ResolveDataFiles(string canonicalPath) =>
        ResolveGameFiles($"data/{canonicalPath}");

    internal IReadOnlyList<PhysicalSourceFile> ResolveGameFiles(string canonicalPath)
    {
        var files = new List<PhysicalSourceFile>();
        SourceLayer? strongestLayer = null;
        SourceEntry? strongestEntry = null;

        foreach (var layer in _layersWeakToStrong)
        {
            var entry = layer.FindEntry(canonicalPath);
            if (entry is null)
            {
                continue;
            }

            strongestLayer = layer;
            strongestEntry = entry;
            if (entry.Kind == SourceEntryKind.File)
            {
                files.Add(new PhysicalSourceFile(layer, entry.Path, entry.RelativePath));
            }
        }

        if (strongestEntry?.Kind == SourceEntryKind.Directory && files.Count != 0)
        {
            var maskedFile = files[^1];
            throw new InvalidOperationException(
                $"Unsupported mapped file/directory collision for '{canonicalPath}': " +
                $"directory from '{strongestLayer!.Origin}' masks file from " +
                $"'{maskedFile.Layer.Origin}'. Directory: {strongestEntry.Path}. " +
                $"File: {maskedFile.Path}");
        }

        return files;
    }
}

internal sealed record Mo2SkipRules(
    IReadOnlyList<string> FileSuffixes,
    IReadOnlyList<string> Directories)
{
    internal bool SkipsFile(string fileName) =>
        FileSuffixes.Any(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    internal bool SkipsDirectory(string directoryName) =>
        Directories.Contains(directoryName, StringComparer.OrdinalIgnoreCase);
}

internal sealed class SourceLayer
{
    private readonly IReadOnlyList<MappedSourceDirectory> _directories;

    private SourceLayer(
        string origin,
        int? modlistIndex,
        IReadOnlyList<MappedSourceDirectory> directories)
    {
        Origin = origin;
        ModlistIndex = modlistIndex;
        _directories = directories;
    }

    internal string Origin { get; }
    internal int? ModlistIndex { get; }

    internal static SourceLayer CreateGame(string origin, string gameRoot) =>
        new(
            origin,
            modlistIndex: null,
            [
                MappedSourceDirectory.Create(
                    origin,
                    Path.Combine(gameRoot, "Data"),
                    destinationPrefix: "data",
                    required: true,
                    skipRules: null),
                MappedSourceDirectory.Create(
                    origin,
                    gameRoot,
                    destinationPrefix: string.Empty,
                    required: true,
                    skipRules: null)
            ]);

    // MO2 maps the mod root to Data; Root Builder maps its Root child to the game directory.
    internal static SourceLayer CreateMod(
        string origin,
        string modRoot,
        int? modlistIndex,
        bool required,
        Mo2SkipRules skipRules) =>
        new(
            origin,
            modlistIndex,
            [
                MappedSourceDirectory.Create(
                    origin,
                    modRoot,
                    destinationPrefix: "data",
                    required: required,
                    skipRules: skipRules),
                MappedSourceDirectory.Create(
                    origin,
                    Path.Combine(modRoot, "Root"),
                    destinationPrefix: string.Empty,
                    required: false,
                    skipRules: null)
            ]);

    internal SourceEntry? FindEntry(string canonicalGamePath)
    {
        // A specific mount, such as Data, owns its complete destination subtree.
        foreach (var directory in _directories)
        {
            if (directory.TryMap(canonicalGamePath, out var sourceRelativePath))
            {
                return directory.FindEntry(sourceRelativePath, canonicalGamePath);
            }
        }

        return null;
    }
}

internal sealed class MappedSourceDirectory
{
    private readonly string _origin;
    private readonly string _root;
    private readonly string _destinationPrefix;
    private readonly bool _exists;
    private readonly Mo2SkipRules? _skipRules;
    private readonly Dictionary<string, SourceEntry> _rootEntries;

    private MappedSourceDirectory(
        string origin,
        string root,
        string destinationPrefix,
        bool exists,
        Mo2SkipRules? skipRules)
    {
        _origin = origin;
        _root = root;
        _destinationPrefix = destinationPrefix;
        _exists = exists;
        _skipRules = skipRules;
        _rootEntries = exists
            ? IndexRootEntries()
            : new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
    }

    internal static MappedSourceDirectory Create(
        string origin,
        string root,
        string destinationPrefix,
        bool required,
        Mo2SkipRules? skipRules)
    {
        var exists = Directory.Exists(root);
        if (required && !exists)
        {
            throw new DirectoryNotFoundException($"Source directory for '{origin}' does not exist: {root}");
        }

        if (exists)
        {
            EnsureReadable(origin, root);
        }

        return new MappedSourceDirectory(origin, root, destinationPrefix, exists, skipRules);
    }

    internal bool TryMap(string canonicalGamePath, out string sourceRelativePath)
    {
        if (_destinationPrefix.Length == 0)
        {
            sourceRelativePath = canonicalGamePath;
            return true;
        }

        var prefix = _destinationPrefix + "/";
        if (canonicalGamePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            sourceRelativePath = canonicalGamePath[prefix.Length..];
            return true;
        }

        sourceRelativePath = string.Empty;
        return false;
    }

    internal SourceEntry? FindEntry(string sourceRelativePath, string canonicalGamePath)
    {
        if (!_exists)
        {
            return null;
        }

        var segments = sourceRelativePath.Split('/');
        if (segments.Length == 1)
        {
            return _rootEntries.GetValueOrDefault(segments[0]);
        }

        if (_skipRules is not null && segments[..^1].Any(_skipRules.SkipsDirectory))
        {
            return null;
        }

        try
        {
            var current = _root;
            var actualSegments = new List<string>(segments.Length);
            for (var index = 0; index < segments.Length; index++)
            {
                var isLast = index == segments.Length - 1;
                var matches = Directory.EnumerateFileSystemEntries(current)
                    .Where(path => Path.GetFileName(path).Equals(segments[index], StringComparison.OrdinalIgnoreCase))
                    .Where(path => isLast || Directory.Exists(path))
                    .ToArray();
                if (matches.Length != 1)
                {
                    if (matches.Length == 0)
                    {
                        return null;
                    }

                    throw new InvalidOperationException(
                        $"Source '{_origin}' contains ambiguous case variants for '{canonicalGamePath}'.");
                }

                current = matches[0];
                actualSegments.Add(Path.GetFileName(current));
            }

            return CreateEntry(current, string.Join('/', actualSegments));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Cannot inspect source directory for '{_origin}': {_root}",
                exception);
        }
    }

    private Dictionary<string, SourceEntry> IndexRootEntries()
    {
        try
        {
            var result = new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.TopDirectoryOnly))
            {
                var entry = CreateEntry(path, Path.GetFileName(path));
                if (entry is null)
                {
                    continue;
                }

                if (!result.TryAdd(entry.RelativePath, entry))
                {
                    throw new InvalidOperationException(
                        $"Source '{_origin}' contains ambiguous root entries named '{entry.RelativePath}'.");
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot read source directory for '{_origin}': {_root}", exception);
        }
    }

    private SourceEntry? CreateEntry(string path, string relativePath)
    {
        var kind = File.GetAttributes(path).HasFlag(FileAttributes.Directory)
            ? SourceEntryKind.Directory
            : SourceEntryKind.File;
        var name = Path.GetFileName(path);
        var skipped = kind == SourceEntryKind.Directory
            ? _skipRules?.SkipsDirectory(name) is true
            : _skipRules?.SkipsFile(name) is true;
        if (skipped)
        {
            return null;
        }

        return new SourceEntry(kind, path, relativePath);
    }

    private static void EnsureReadable(string origin, string root)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(root).Take(1).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot read source directory for '{origin}': {root}", exception);
        }
    }
}

internal enum SourceEntryKind
{
    File,
    Directory
}

internal sealed record SourceEntry(SourceEntryKind Kind, string Path, string RelativePath);
internal sealed record PhysicalSourceFile(SourceLayer Layer, string Path, string RelativePath);
