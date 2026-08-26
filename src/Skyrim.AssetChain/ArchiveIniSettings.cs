namespace Skyrim.AssetChain;

internal static class ArchiveIniSettingsReader
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

    private static readonly string[] ArchiveListSettingNames =
    [
        "sResourceArchiveList",
        "sResourceArchiveList2",
        "sVrResourceArchiveList"
    ];

    internal static ArchiveIniSettings Read(
        GameKind game,
        string profileFolder,
        string profileSettingsPath,
        IniFile organizerIni,
        SourceFileSystem sourceFiles)
    {
        var profileSettings = IniFile.Read(profileSettingsPath);
        var localSetting = profileSettings.Get("General", "LocalSettings");
        var profileLocal = localSetting is not null
            ? ParseBoolean(localSetting, profileSettingsPath, "LocalSettings")
            : ParseOptionalBoolean(organizerIni.Get("Settings", "profile_local_inis")) ?? true;

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

        var values = ReadDefaultArchiveValues(game, sourceFiles);
        OverlayArchiveValues(values, mainIniPath);
        OverlayArchiveValues(values, customIniPath);
        OverlayArchiveValues(values, tweaksPath);

        return new ArchiveIniSettings(
            ParseArchiveList(values["sResourceArchiveList"], "sResourceArchiveList"),
            ParseArchiveList(values["sResourceArchiveList2"], "sResourceArchiveList2"),
            ParseArchiveList(values.GetValueOrDefault("sVrResourceArchiveList", string.Empty), "sVrResourceArchiveList"));
    }

    internal static void RejectUnsupportedPluginSidecars(
        IReadOnlyList<ActivePlugin> activePlugins,
        SourceFileSystem sourceFiles)
    {
        foreach (var plugin in activePlugins)
        {
            var sidecarName = Path.ChangeExtension(plugin.Name, ".ini");
            var sidecar = sourceFiles.ResolveDataFiles(sidecarName).LastOrDefault();
            if (sidecar is null)
            {
                continue;
            }

            var ini = IniFile.Read(sidecar.Path);
            foreach (var setting in ArchiveListSettingNames)
            {
                if (!ini.TryGet("Archive", setting, out var value) ||
                    string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Unsupported plugin-sidecar archive setting [Archive] {setting}: " +
                    $"active plugin '{plugin.Name}' uses '{sidecarName}' from " +
                    $"'{sidecar.Layer.Origin}': {sidecar.Path}");
            }
        }
    }

    private static Dictionary<string, string> ReadDefaultArchiveValues(
        GameKind game,
        SourceFileSystem sourceFiles)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sResourceArchiveList"] = string.Join(", ", DefaultArchiveList),
            ["sResourceArchiveList2"] = string.Join(", ", DefaultArchiveList2),
            ["sVrResourceArchiveList"] = string.Empty
        };

        var defaultIni = sourceFiles.ResolveGameFiles(GetDefaultGameIniName(game)).LastOrDefault();
        if (defaultIni is not null)
        {
            OverlayArchiveValues(values, defaultIni.Path);
        }

        return values;
    }

    private static string GetDefaultGameIniName(GameKind game) => game switch
    {
        GameKind.SkyrimSE => "Skyrim_Default.ini",
        GameKind.SkyrimVR => "Skyrim.ini",
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, null)
    };

    private static void OverlayArchiveValues(IDictionary<string, string> values, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var ini = IniFile.Read(path);
        foreach (var key in ArchiveListSettingNames)
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
}

internal sealed record ArchiveIniSettings(
    IReadOnlyList<string> ResourceArchiveList,
    IReadOnlyList<string> ResourceArchiveList2,
    IReadOnlyList<string> VrResourceArchiveList);
