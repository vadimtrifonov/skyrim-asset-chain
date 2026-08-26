namespace Skyrim.AssetChain;

internal sealed class Mo2Profile
{
    private static readonly string[] DefaultSkipFileSuffixes = [".mohidden"];
    private static readonly string[] DefaultSkipDirectories = [".git"];

    private readonly SourceFileSystem _sourceFiles;

    private Mo2Profile(
        GameKind game,
        string profileFolder,
        SourceFileSystem sourceFiles,
        IReadOnlyList<ActivePlugin> activePlugins,
        IReadOnlyList<string> loadOrderValidation,
        ArchiveIniSettings archiveSettings)
    {
        Game = game;
        ProfileFolder = profileFolder;
        _sourceFiles = sourceFiles;
        ActivePlugins = activePlugins;
        LoadOrderValidation = loadOrderValidation;
        ArchiveSettings = archiveSettings;
    }

    internal GameKind Game { get; }
    internal string ProfileFolder { get; }
    internal IReadOnlyList<ActivePlugin> ActivePlugins { get; }
    internal IReadOnlyList<string> LoadOrderValidation { get; }
    internal ArchiveIniSettings ArchiveSettings { get; }

    internal static Mo2Profile Load(GameKind game, string instanceRoot, string profileName)
    {
        var organizerIniPath = Path.Combine(instanceRoot, "ModOrganizer.ini");
        var organizerIni = IniFile.Read(organizerIniPath);
        ValidateConfiguredGame(
            game,
            QSettingsValue.DecodeString(
                organizerIni.Get("General", "gameName"),
                $"[General] gameName in {organizerIniPath}"),
            organizerIniPath);

        var gamePathValue = QSettingsValue.DecodeString(
            organizerIni.Get("General", "gamePath"),
            $"[General] gamePath in {organizerIniPath}");
        if (string.IsNullOrWhiteSpace(gamePathValue))
        {
            throw new InvalidOperationException($"{organizerIniPath} has no [General] gamePath value.");
        }

        var gameRoot = ResolvePath(gamePathValue, instanceRoot, instanceRoot, "gamePath");
        RequireDirectory(Path.Combine(gameRoot, "Data"), "Physical game Data directory");

        var baseValue = QSettingsValue.DecodeString(
            organizerIni.Get("Settings", "base_directory"),
            $"[Settings] base_directory in {organizerIniPath}");
        var baseDirectory = string.IsNullOrWhiteSpace(baseValue)
            ? instanceRoot
            : ResolvePath(baseValue, instanceRoot, instanceRoot, "base_directory");

        var modsFolder = ResolveConfiguredDirectory(
            organizerIni,
            organizerIniPath,
            "mod_directory",
            "mods",
            instanceRoot,
            baseDirectory);
        var profilesFolder = ResolveConfiguredDirectory(
            organizerIni,
            organizerIniPath,
            "profiles_directory",
            "profiles",
            instanceRoot,
            baseDirectory);
        var overwriteFolder = ResolveConfiguredDirectory(
            organizerIni,
            organizerIniPath,
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

        var enabledMods = Mo2ModList.ReadEnabled(modlistPath, modsFolder);
        var skipRules = ReadSkipRules(organizerIni, organizerIniPath);
        var sourceFiles = SourceFileSystem.Create(gameRoot, enabledMods, overwriteFolder, skipRules);
        var activePlugins = PluginList.ReadActive(game, pluginsPath, sourceFiles);
        ArchiveIniSettingsReader.RejectUnsupportedPluginSidecars(activePlugins, sourceFiles);
        var loadOrderValidation = File.Exists(loadOrderPath)
            ? PluginList.ReadLoadOrder(loadOrderPath)
            : Array.Empty<string>();
        var archiveSettings = ArchiveIniSettingsReader.Read(
            game,
            profileFolder,
            settingsPath,
            organizerIni,
            sourceFiles);

        return new Mo2Profile(
            game,
            profileFolder,
            sourceFiles,
            activePlugins,
            loadOrderValidation,
            archiveSettings);
    }

    internal IReadOnlyList<PhysicalSourceFile> ResolveDataFiles(string canonicalPath) =>
        _sourceFiles.ResolveDataFiles(canonicalPath);

    private static Mo2SkipRules ReadSkipRules(IniFile ini, string iniPath) =>
        new(
            ReadQSettingsStringList(
                ini,
                iniPath,
                "skip_file_suffixes",
                DefaultSkipFileSuffixes),
            ReadQSettingsStringList(
                ini,
                iniPath,
                "skip_directories",
                DefaultSkipDirectories));

    private static IReadOnlyList<string> ReadQSettingsStringList(
        IniFile ini,
        string iniPath,
        string key,
        IReadOnlyList<string> defaultValue)
    {
        if (!ini.TryGet("Settings", key, out var raw))
        {
            return defaultValue.ToArray();
        }

        return QSettingsValue.DecodeStringList(
                raw,
                $"[Settings] {key} in {iniPath}")
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveConfiguredDirectory(
        IniFile ini,
        string iniPath,
        string key,
        string defaultName,
        string instanceRoot,
        string baseDirectory)
    {
        var value = QSettingsValue.DecodeString(
            ini.Get("Settings", key),
            $"[Settings] {key} in {iniPath}");
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
}
