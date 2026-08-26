namespace Skyrim.AssetChain;

internal static class ArchiveOrder
{
    private const string ResourceArchiveList = "sResourceArchiveList";
    private const string ResourceArchiveList2 = "sResourceArchiveList2";
    private const string VrResourceArchiveList = "sVrResourceArchiveList";
    private const string VrDefaultArchive = "Skyrim_VR - Main.bsa";
    private const string VrDefaultSource = "Skyrim VR";

    internal static ArchiveOrderResult Build(Mo2Profile profile)
    {
        var archives = new List<MutableLogicalArchive>();
        var byName = new Dictionary<string, MutableLogicalArchive>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<string>();

        foreach (var name in profile.ArchiveSettings.ResourceArchiveList)
        {
            AddIniArchive(profile, archives, byName, diagnostics, name, ResourceArchiveList);
        }

        foreach (var name in profile.ArchiveSettings.ResourceArchiveList2)
        {
            AddIniArchive(profile, archives, byName, diagnostics, name, ResourceArchiveList2);
        }

        foreach (var plugin in profile.ActivePlugins)
        {
            var stem = Path.GetFileNameWithoutExtension(plugin.Name);
            AddPluginArchive(profile, archives, byName, stem + ".bsa", plugin);
            AddPluginArchive(profile, archives, byName, stem + " - Textures.bsa", plugin);
        }

        if (profile.Game == GameKind.SkyrimVR)
        {
            var loadedVrListArchive = false;
            foreach (var name in profile.ArchiveSettings.VrResourceArchiveList)
            {
                var copies = FindPhysicalCopies(profile, name);
                if (copies.Count == 0)
                {
                    diagnostics.Add($"warning: archive named by {VrResourceArchiveList} was not found: {name}");
                    continue;
                }

                loadedVrListArchive = true;
                AddOrMoveVrArchive(
                    archives,
                    byName,
                    name,
                    ArchiveLoadMechanism.IniList,
                    VrResourceArchiveList,
                    copies);
            }

            if (!loadedVrListArchive)
            {
                var copies = FindPhysicalCopies(profile, VrDefaultArchive);
                if (copies.Count > 0)
                {
                    AddOrMoveVrArchive(
                        archives,
                        byName,
                        VrDefaultArchive,
                        ArchiveLoadMechanism.EngineDefault,
                        VrDefaultSource,
                        copies);
                }
            }
        }

        var logicalArchives = archives
            .Select((archive, index) => archive.ToImmutable(index))
            .ToArray();
        ValidateLoadOrder(profile, logicalArchives);
        return new ArchiveOrderResult(logicalArchives, diagnostics);
    }

    private static void AddIniArchive(
        Mo2Profile profile,
        ICollection<MutableLogicalArchive> archives,
        IDictionary<string, MutableLogicalArchive> byName,
        ICollection<string> diagnostics,
        string name,
        string setting)
    {
        var copies = FindPhysicalCopies(profile, name);
        if (copies.Count == 0)
        {
            diagnostics.Add($"warning: archive named by {setting} was not found: {name}");
            return;
        }

        AddOrMove(
            archives,
            byName,
            new MutableLogicalArchive(
                name,
                ArchiveLoadMechanism.IniList,
                setting,
                associatedPlugin: null,
                pluginLoadOrderIndex: null,
                copies));
    }

    private static void AddPluginArchive(
        Mo2Profile profile,
        ICollection<MutableLogicalArchive> archives,
        IDictionary<string, MutableLogicalArchive> byName,
        string name,
        ActivePlugin plugin)
    {
        var copies = FindPhysicalCopies(profile, name);
        if (copies.Count == 0)
        {
            return;
        }

        AddOrMove(
            archives,
            byName,
            new MutableLogicalArchive(
                name,
                ArchiveLoadMechanism.PluginAssociation,
                plugin.Name,
                plugin.Name,
                plugin.LoadOrderIndex,
                copies));
    }

    private static void AddOrMoveVrArchive(
        ICollection<MutableLogicalArchive> archives,
        IDictionary<string, MutableLogicalArchive> byName,
        string name,
        ArchiveLoadMechanism loadMechanism,
        string loadSource,
        IReadOnlyList<PhysicalArchiveCopy> copies) =>
        AddOrMove(
            archives,
            byName,
            new MutableLogicalArchive(
                name,
                loadMechanism,
                loadSource,
                associatedPlugin: null,
                pluginLoadOrderIndex: null,
                copies));

    private static void AddOrMove(
        ICollection<MutableLogicalArchive> archives,
        IDictionary<string, MutableLogicalArchive> byName,
        MutableLogicalArchive archive)
    {
        if (byName.TryGetValue(archive.Name, out var existing))
        {
            archives.Remove(existing);
        }

        archives.Add(archive);
        byName[archive.Name] = archive;
    }

    private static IReadOnlyList<PhysicalArchiveCopy> FindPhysicalCopies(
        Mo2Profile profile,
        string logicalName) =>
        profile.ResolveDataFiles(logicalName)
            .Select(file => new PhysicalArchiveCopy(file.Layer, file.Path))
            .ToArray();

    private static void ValidateLoadOrder(
        Mo2Profile profile,
        IReadOnlyList<LogicalArchive> archives)
    {
        if (profile.LoadOrderValidation.Count == 0)
        {
            return;
        }

        var archivePlugins = archives
            .Where(archive => archive.LoadMechanism == ArchiveLoadMechanism.PluginAssociation)
            .Select(archive => archive.AssociatedPlugin!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (archivePlugins.Count < 2)
        {
            return;
        }

        var expected = profile.ActivePlugins
            .Select(plugin => plugin.Name)
            .Where(archivePlugins.Contains)
            .ToArray();
        var actual = profile.LoadOrderValidation
            .Where(archivePlugins.Contains)
            .ToArray();

        if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{Path.Combine(profile.ProfileFolder, "loadorder.txt")} disagrees with plugins.txt in a way that changes archive order.");
        }
    }

    private sealed class MutableLogicalArchive(
        string name,
        ArchiveLoadMechanism loadMechanism,
        string loadSource,
        string? associatedPlugin,
        int? pluginLoadOrderIndex,
        IReadOnlyList<PhysicalArchiveCopy> physicalCopies)
    {
        internal string Name => name;

        internal LogicalArchive ToImmutable(int loadIndex) =>
            new(
                name,
                loadMechanism,
                loadSource,
                loadIndex,
                associatedPlugin,
                pluginLoadOrderIndex,
                physicalCopies);
    }
}

internal sealed record ArchiveOrderResult(
    IReadOnlyList<LogicalArchive> Archives,
    IReadOnlyList<string> Diagnostics);

internal sealed record LogicalArchive(
    string Name,
    ArchiveLoadMechanism LoadMechanism,
    string LoadSource,
    int LoadIndex,
    string? AssociatedPlugin,
    int? PluginLoadOrderIndex,
    IReadOnlyList<PhysicalArchiveCopy> PhysicalCopies);

internal sealed record PhysicalArchiveCopy(SourceLayer Layer, string Path);

internal enum ArchiveLoadMechanism
{
    IniList,
    PluginAssociation,
    EngineDefault
}
