using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace Skyrim.AssetChain;

internal sealed class Mo2Profile
{
    private static readonly string[] DefaultArchiveList =
    [
        "Skyrim - Misc.bsa",
        "Skyrim - Shaders.bsa",
        "Skyrim - Interface.bsa",
        "Skyrim - Animations.bsa",
        "Skyrim - Meshes0.bsa",
        "Skyrim - Meshes1.bsa",
        "Skyrim - Sounds.bsa"
    ];

    private static readonly string[] DefaultArchiveList2 =
    [
        "Skyrim - Voices_en0.bsa",
        "Skyrim - Textures0.bsa",
        "Skyrim - Textures1.bsa",
        "Skyrim - Textures2.bsa",
        "Skyrim - Textures3.bsa",
        "Skyrim - Textures4.bsa",
        "Skyrim - Textures5.bsa",
        "Skyrim - Textures6.bsa",
        "Skyrim - Textures7.bsa",
        "Skyrim - Textures8.bsa",
        "Skyrim - Patch.bsa"
    ];

    private Mo2Profile(
        GameKind game,
        string instanceRoot,
        string gameRoot,
        string dataFolder,
        string profileFolder,
        IReadOnlyList<SourceLayer> layersWeakToStrong,
        IReadOnlyList<ActivePlugin> activePlugins,
        IReadOnlyList<string> loadOrderValidation,
        ArchiveIniSettings archiveSettings)
    {
        Game = game;
        InstanceRoot = instanceRoot;
        GameRoot = gameRoot;
        DataFolder = dataFolder;
        ProfileFolder = profileFolder;
        LayersWeakToStrong = layersWeakToStrong;
        ActivePlugins = activePlugins;
        LoadOrderValidation = loadOrderValidation;
        ArchiveSettings = archiveSettings;
    }

    internal GameKind Game { get; }
    internal string InstanceRoot { get; }
    internal string GameRoot { get; }
    internal string DataFolder { get; }
    internal string ProfileFolder { get; }
    internal IReadOnlyList<SourceLayer> LayersWeakToStrong { get; }
    internal IReadOnlyList<ActivePlugin> ActivePlugins { get; }
    internal IReadOnlyList<string> LoadOrderValidation { get; }
    internal ArchiveIniSettings ArchiveSettings { get; }

    internal static Mo2Profile Load(GameKind game, string instanceRoot, string profileName)
    {
        var organizerIniPath = Path.Combine(instanceRoot, "ModOrganizer.ini");
        var organizerIni = IniFile.Read(organizerIniPath);
        ValidateConfiguredGame(
            game,
            CleanQSettingsValue(organizerIni.Get("General", "gameName")),
            organizerIniPath);

        var gamePathValue = CleanQSettingsValue(organizerIni.Get("General", "gamePath"));
        if (string.IsNullOrWhiteSpace(gamePathValue))
        {
            throw new InvalidOperationException($"{organizerIniPath} has no [General] gamePath value.");
        }

        var gameRoot = ResolvePath(gamePathValue, instanceRoot, instanceRoot, "gamePath");
        var dataFolder = Path.Combine(gameRoot, "Data");
        RequireDirectory(dataFolder, "Physical game Data directory");

        var baseValue = CleanQSettingsValue(organizerIni.Get("Settings", "base_directory"));
        var baseDirectory = string.IsNullOrWhiteSpace(baseValue)
            ? instanceRoot
            : ResolvePath(baseValue, instanceRoot, instanceRoot, "base_directory");

        var modsFolder = ResolveConfiguredDirectory(
            organizerIni,
            "mod_directory",
            "mods",
            instanceRoot,
            baseDirectory);
        var profilesFolder = ResolveConfiguredDirectory(
            organizerIni,
            "profiles_directory",
            "profiles",
            instanceRoot,
            baseDirectory);
        var overwriteFolder = ResolveConfiguredDirectory(
            organizerIni,
            "overwrite_directory",
            "overwrite",
            instanceRoot,
            baseDirectory);

        RequireDirectory(modsFolder, "MO2 mods directory");
        RequireDirectory(profilesFolder, "MO2 profiles directory");

        var profileFolder = Path.GetFullPath(Path.Combine(profilesFolder, profileName));
        var profilesPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilesFolder)) + Path.DirectorySeparatorChar;
        if (!profileFolder.StartsWith(profilesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Profile resolves outside the MO2 profiles directory: {profileName}");
        }

        RequireDirectory(profileFolder, $"MO2 profile '{profileName}'");
        var modlistPath = RequireFile(profileFolder, "modlist.txt");
        var pluginsPath = RequireFile(profileFolder, "plugins.txt");
        var settingsPath = RequireFile(profileFolder, "settings.ini");
        var loadOrderPath = Path.Combine(profileFolder, "loadorder.txt");

        var enabledMods = ReadEnabledMods(modlistPath, modsFolder);
        var layers = BuildLayers(dataFolder, enabledMods, overwriteFolder);
        var activePlugins = BuildActivePlugins(game, gameRoot, pluginsPath, layers);
        var loadOrderValidation = File.Exists(loadOrderPath)
            ? ReadLoadOrder(loadOrderPath)
            : Array.Empty<string>();
        var archiveSettings = ReadArchiveSettings(
            game,
            gameRoot,
            profileFolder,
            settingsPath,
            organizerIni);

        return new Mo2Profile(
            game,
            instanceRoot,
            gameRoot,
            dataFolder,
            profileFolder,
            layers,
            activePlugins,
            loadOrderValidation,
            archiveSettings);
    }

    internal PhysicalSourceFile? ResolveRootFileStrongest(string fileName)
    {
        for (var index = LayersWeakToStrong.Count - 1; index >= 0; index--)
        {
            var layer = LayersWeakToStrong[index];
            var file = layer.FindRootFile(fileName);
            if (file is not null)
            {
                return new PhysicalSourceFile(layer, file);
            }
        }

        return null;
    }

    private static IReadOnlyList<SourceLayer> BuildLayers(
        string dataFolder,
        IReadOnlyList<EnabledMod> enabledMods,
        string overwriteFolder)
    {
        var layers = new List<SourceLayer>(enabledMods.Count + 2)
        {
            SourceLayer.Create("Game Data", dataFolder, modlistIndex: null, required: true)
        };

        foreach (var mod in enabledMods.Reverse())
        {
            layers.Add(SourceLayer.Create(mod.Name, mod.Path, mod.ModlistIndex, required: true));
        }

        layers.Add(SourceLayer.Create("Overwrite", overwriteFolder, modlistIndex: null, required: false));
        return layers;
    }

    private static IReadOnlyList<EnabledMod> ReadEnabledMods(string modlistPath, string modsFolder)
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
                throw MalformedProfileEntry(modlistPath, lineIndex + 1, raw, "unknown mod marker");
            }

            var name = line[1..].Trim();
            if (name.Length == 0)
            {
                throw MalformedProfileEntry(modlistPath, lineIndex + 1, raw, "missing mod name");
            }

            if (marker == '*' || name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seenManaged.Add(name))
            {
                throw MalformedProfileEntry(modlistPath, lineIndex + 1, raw, "duplicate managed mod");
            }

            if (marker == '-')
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(modsFolder, name));
            var modsPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsFolder)) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(modsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw MalformedProfileEntry(modlistPath, lineIndex + 1, raw, "mod name resolves outside the mods directory");
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

    private static IReadOnlyList<ActivePlugin> BuildActivePlugins(
        GameKind game,
        string gameRoot,
        string pluginsPath,
        IReadOnlyList<SourceLayer> layers)
    {
        var explicitPlugins = ReadPluginListings(pluginsPath);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var release = game == GameKind.SkyrimSE ? SkyrimRelease.SkyrimSE : SkyrimRelease.SkyrimVR;
        foreach (var modKey in Implicits.Listings.Skyrim(release))
        {
            AddUnique(names, seen, modKey.FileName);
        }

        var cccPath = Path.Combine(gameRoot, "Skyrim.ccc");
        if (File.Exists(cccPath))
        {
            foreach (var name in ReadCreationClubListings(cccPath))
            {
                if (FindRootFileStrongest(layers, name) is not null)
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
            var provider = FindRootFileStrongest(layers, name);
            if (provider is null)
            {
                throw new FileNotFoundException(
                    $"Active plugin '{name}' cannot be resolved through the selected MO2 profile.");
            }

            active.Add(new ActivePlugin(name, index, provider));
        }

        return active;
    }

    private static IReadOnlyList<string> ReadPluginListings(string path)
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
                throw MalformedProfileEntry(path, lineIndex + 1, raw, "duplicate plugin");
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
                throw MalformedProfileEntry(path, lineIndex + 1, raw, "duplicate Creation Club plugin");
            }

            names.Add(name);
        }

        return names;
    }

    private static IReadOnlyList<string> ReadLoadOrder(string path)
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
                throw MalformedProfileEntry(path, lineIndex + 1, raw, "duplicate load-order plugin");
            }

            names.Add(name);
        }

        return names;
    }

    private static ArchiveIniSettings ReadArchiveSettings(
        GameKind game,
        string gameRoot,
        string profileFolder,
        string profileSettingsPath,
        IniFile organizerIni)
    {
        var profileSettings = IniFile.Read(profileSettingsPath);
        var localSetting = profileSettings.Get("General", "LocalSettings");
        var profileLocal = localSetting is not null
            ? ParseBoolean(localSetting, profileSettingsPath, "LocalSettings")
            : ParseOptionalBoolean(organizerIni.Get("Settings", "profile_local_inis")) ?? false;

        string iniFolder;
        if (profileLocal)
        {
            iniFolder = profileFolder;
        }
        else
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var gameFolder = game == GameKind.SkyrimSE ? "Skyrim Special Edition" : "Skyrim VR";
            iniFolder = Path.Combine(documents, "My Games", gameFolder);
        }

        var mainIniName = game == GameKind.SkyrimSE ? "skyrim.ini" : "skyrimvr.ini";
        var mainIniPath = Path.Combine(iniFolder, mainIniName);
        var customIniPath = Path.Combine(iniFolder, "skyrimcustom.ini");
        var tweaksPath = Path.Combine(profileFolder, "initweaks.ini");

        var values = ReadDefaultArchiveValues(game, gameRoot);
        OverlayArchiveValues(values, mainIniPath);
        OverlayArchiveValues(values, customIniPath);
        OverlayArchiveValues(values, tweaksPath);

        return new ArchiveIniSettings(
            ParseArchiveList(values["sResourceArchiveList"], "sResourceArchiveList"),
            ParseArchiveList(values["sResourceArchiveList2"], "sResourceArchiveList2"),
            ParseArchiveList(values.GetValueOrDefault("sVrResourceArchiveList", string.Empty), "sVrResourceArchiveList"));
    }

    private static Dictionary<string, string> ReadDefaultArchiveValues(GameKind game, string gameRoot)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sResourceArchiveList"] = string.Join(", ", DefaultArchiveList),
            ["sResourceArchiveList2"] = string.Join(", ", DefaultArchiveList2),
            ["sVrResourceArchiveList"] = string.Empty
        };

        var defaultIniPath = Path.Combine(
            gameRoot,
            game == GameKind.SkyrimSE ? "Skyrim_Default.ini" : "Skyrim.ini");
        OverlayArchiveValues(values, defaultIniPath);
        return values;
    }

    private static void OverlayArchiveValues(IDictionary<string, string> values, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var ini = IniFile.Read(path);
        foreach (var key in new[]
                 {
                     "sResourceArchiveList", "sResourceArchiveList2", "sVrResourceArchiveList"
                 })
        {
            if (ini.TryGet("Archive", key, out var value))
            {
                values[key] = value;
            }
        }
    }

    private static IReadOnlyList<string> ParseArchiveList(string value, string setting)
    {
        var names = new List<string>();
        foreach (var part in value.Split(','))
        {
            var name = part.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (!Path.GetExtension(name).Equals(".bsa", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(name).Equals(name, StringComparison.Ordinal) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException(
                    $"Invalid BSA filename in [Archive] {setting}: {name}");
            }

            names.Add(name);
        }

        return names;
    }

    private static PhysicalSourceFile? FindRootFileStrongest(
        IReadOnlyList<SourceLayer> layers,
        string fileName)
    {
        for (var index = layers.Count - 1; index >= 0; index--)
        {
            var layer = layers[index];
            var path = layer.FindRootFile(fileName);
            if (path is not null)
            {
                return new PhysicalSourceFile(layer, path);
            }
        }

        return null;
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
            throw MalformedProfileEntry(path, line, raw, "invalid plugin filename");
        }
    }

    private static InvalidOperationException MalformedProfileEntry(
        string path,
        int line,
        string raw,
        string reason) =>
        new($"Malformed profile entry in {path}, line {line}: {reason}: {raw}");

    private static string ResolveConfiguredDirectory(
        IniFile ini,
        string key,
        string defaultName,
        string instanceRoot,
        string baseDirectory)
    {
        var value = CleanQSettingsValue(ini.Get("Settings", key));
        value = string.IsNullOrWhiteSpace(value) ? $"%BASE_DIR%/{defaultName}" : value;
        return ResolvePath(value, instanceRoot, baseDirectory, key);
    }

    private static string ResolvePath(
        string value,
        string relativeRoot,
        string baseDirectory,
        string setting)
    {
        value = ReplaceOrdinalIgnoreCase(value, "%BASE_DIR%", baseDirectory)
            .Replace('/', Path.DirectorySeparatorChar);
        try
        {
            return Path.GetFullPath(Path.IsPathFullyQualified(value)
                ? value
                : Path.Combine(relativeRoot, value));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"Invalid MO2 path setting {setting}: {value}", exception);
        }
    }

    private static string ReplaceOrdinalIgnoreCase(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            value = string.Concat(value.AsSpan(0, index), newValue, value.AsSpan(index + oldValue.Length));
            index = value.IndexOf(oldValue, index + newValue.Length, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static string? CleanQSettingsValue(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var value = raw.Trim();
        if (value.Length == 0 || value.Equals("@Invalid()", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        const string wrapper = "@ByteArray(";
        if (value.StartsWith(wrapper, StringComparison.Ordinal))
        {
            if (!value.EndsWith(')'))
            {
                throw new InvalidOperationException($"Malformed QSettings byte-array value: {raw}");
            }

            value = value[wrapper.Length..^1];
        }
        else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return value.Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static bool ParseBoolean(string value, string path, string key)
    {
        if (ParseOptionalBoolean(value) is { } parsed)
        {
            return parsed;
        }

        throw new InvalidOperationException($"Invalid Boolean {key} in {path}: {value}");
    }

    private static bool? ParseOptionalBoolean(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => null
        };
    }

    private static void ValidateConfiguredGame(GameKind game, string? configuredName, string iniPath)
    {
        if (string.IsNullOrWhiteSpace(configuredName))
        {
            throw new InvalidOperationException($"{iniPath} has no [General] gameName value.");
        }

        var expectedName = game switch
        {
            GameKind.SkyrimSE => "Skyrim Special Edition",
            GameKind.SkyrimVR => "Skyrim VR",
            _ => throw new ArgumentOutOfRangeException(nameof(game), game, null)
        };
        if (!configuredName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Requested game {game} requires {iniPath} gameName '{expectedName}', but found '{configuredName}'.");
        }
    }

    private static void RequireDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{description} does not exist: {path}");
        }
    }

    private static string RequireFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"MO2 profile file does not exist: {path}");
        }

        return path;
    }

    private sealed record EnabledMod(string Name, string Path, int ModlistIndex);
}

internal sealed class SourceLayer
{
    private readonly Dictionary<string, string> _rootFiles;

    private SourceLayer(string origin, string root, int? modlistIndex, bool exists)
    {
        Origin = origin;
        Root = root;
        ModlistIndex = modlistIndex;
        Exists = exists;
        _rootFiles = exists ? IndexRootFiles() : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal string Origin { get; }
    internal string Root { get; }
    internal int? ModlistIndex { get; }
    internal bool Exists { get; }

    internal static SourceLayer Create(
        string origin,
        string root,
        int? modlistIndex,
        bool required)
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

        return new SourceLayer(origin, root, modlistIndex, exists);
    }

    internal string? FindRootFile(string fileName) =>
        _rootFiles.GetValueOrDefault(fileName);

    internal LooseSourceFile? FindLooseFile(string canonicalPath)
    {
        if (!Exists)
        {
            return null;
        }

        var segments = canonicalPath.Split('/');
        var candidate = Path.Combine([Root, .. segments]);
        if (File.Exists(candidate + ".mohidden") || !File.Exists(candidate))
        {
            return null;
        }

        try
        {
            var current = Root;
            var actualSegments = new List<string>(segments.Length);
            for (var index = 0; index < segments.Length; index++)
            {
                var isLast = index == segments.Length - 1;
                var matches = Directory.EnumerateFileSystemEntries(current)
                    .Where(path => Path.GetFileName(path).Equals(segments[index], StringComparison.OrdinalIgnoreCase))
                    .Where(path => isLast ? File.Exists(path) : Directory.Exists(path))
                    .ToArray();
                if (matches.Length != 1)
                {
                    if (matches.Length == 0)
                    {
                        return null;
                    }

                    throw new InvalidOperationException(
                        $"Source '{Origin}' contains ambiguous case variants for '{canonicalPath}'.");
                }

                current = matches[0];
                actualSegments.Add(Path.GetFileName(current));
            }

            return new LooseSourceFile(current, string.Join('/', actualSegments));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Cannot inspect source directory for '{Origin}': {Root}",
                exception);
        }
    }

    private Dictionary<string, string> IndexRootFiles()
    {
        try
        {
            var files = Directory.EnumerateFiles(Root, "*", SearchOption.TopDirectoryOnly).ToArray();
            var hidden = files
                .Select(path => Path.GetFileName(path)!)
                .Where(name => name.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase))
                .Select(name => name[..^".mohidden".Length])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase) || hidden.Contains(name))
                {
                    continue;
                }

                if (!result.TryAdd(name, path))
                {
                    throw new InvalidOperationException(
                        $"Source '{Origin}' contains ambiguous root files named '{name}'.");
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot read source directory for '{Origin}': {Root}", exception);
        }
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

internal sealed record PhysicalSourceFile(SourceLayer Layer, string Path);
internal sealed record LooseSourceFile(string Path, string RelativePath);
internal sealed record ActivePlugin(string Name, int LoadOrderIndex, PhysicalSourceFile Provider);
internal sealed record ArchiveIniSettings(
    IReadOnlyList<string> ResourceArchiveList,
    IReadOnlyList<string> ResourceArchiveList2,
    IReadOnlyList<string> VrResourceArchiveList);

internal sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections;

    private IniFile(Dictionary<string, Dictionary<string, string>> sections)
    {
        _sections = sections;
    }

    internal static IniFile Read(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        var lines = File.ReadAllLines(path);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var raw = lines[lineIndex];
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']') || line.Length < 3)
                {
                    throw new InvalidOperationException(
                        $"Malformed INI section in {path}, line {lineIndex + 1}: {raw}");
                }

                section = line[1..^1].Trim();
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                throw new InvalidOperationException(
                    $"Malformed INI entry in {path}, line {lineIndex + 1}: {raw}");
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (!sections.TryGetValue(section, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add(section, values);
            }

            values[key] = value;
        }

        return new IniFile(sections);
    }

    internal string? Get(string section, string key) =>
        TryGet(section, key, out var value) ? value : null;

    internal bool TryGet(string section, string key, out string value)
    {
        value = string.Empty;
        if (!_sections.TryGetValue(section, out var values) ||
            !values.TryGetValue(key, out var found))
        {
            return false;
        }

        value = found;
        return true;
    }
}
