using System.Buffers.Binary;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace Skyrim.AssetChain;

internal static class AssetChainQuery
{
    internal static AssetChainResult Execute(
        Mo2Profile profile,
        IReadOnlyList<string> assetPaths)
    {
        var archiveOrder = ArchiveOrder.Build(profile);
        var requested = assetPaths.ToHashSet(StringComparer.Ordinal);
        var archiveMembers = IndexRequestedArchiveMembers(profile, archiveOrder.Archives, requested);
        var rows = new List<AssetProviderRow>();

        foreach (var assetPath in assetPaths)
        {
            rows.AddRange(ResolveChain(profile, archiveOrder.Archives, archiveMembers, assetPath));
        }

        return new AssetChainResult(rows, archiveOrder.Diagnostics);
    }

    private static IReadOnlyList<AssetProviderRow> ResolveChain(
        Mo2Profile profile,
        IReadOnlyList<LogicalArchive> archives,
        IReadOnlyDictionary<PhysicalArchiveCopy, IReadOnlyDictionary<string, string>> archiveMembers,
        string assetPath)
    {
        var candidates = new List<ProviderCandidate>();
        ProviderCandidate? archiveWinner = null;

        foreach (var archive in archives)
        {
            ProviderCandidate? survivingMember = null;
            var winningPhysicalCopy = archive.PhysicalCopies[^1];

            foreach (var copy in archive.PhysicalCopies)
            {
                if (!archiveMembers[copy].TryGetValue(assetPath, out var storedPath))
                {
                    continue;
                }

                var candidate = ProviderCandidate.CreateArchive(archive, copy, storedPath);
                candidates.Add(candidate);
                if (ReferenceEquals(copy, winningPhysicalCopy))
                {
                    survivingMember = candidate;
                }
            }

            if (survivingMember is not null)
            {
                archiveWinner = survivingMember;
            }
        }

        ProviderCandidate? looseWinner = null;
        foreach (var file in profile.ResolveDataFiles(assetPath))
        {
            var candidate = ProviderCandidate.CreateLoose(file);
            candidates.Add(candidate);
            looseWinner = candidate;
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No loose file or registered BSA contains this asset in the selected profile: {assetPath}");
        }

        var winner = looseWinner ?? archiveWinner;
        var rows = new List<AssetProviderRow>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var archive = candidate.Archive;
            rows.Add(new AssetProviderRow(
                AssetPath: assetPath,
                ProviderIndex: index,
                SourceKind: candidate.Kind,
                SourceOrigin: candidate.Layer.Origin,
                SourcePath: NormalizePhysicalPath(candidate.SourcePath),
                SourceAssetPath: candidate.SourceAssetPath,
                Archive: archive?.Name,
                ArchiveLoadMechanism: archive is null ? null : FormatLoadMechanism(archive.LoadMechanism),
                ArchiveLoadSource: archive?.LoadSource,
                ArchiveLoadIndex: archive?.LoadIndex,
                AssociatedPlugin: archive?.AssociatedPlugin,
                PluginLoadOrderIndex: archive?.PluginLoadOrderIndex,
                ModlistIndex: candidate.Layer.ModlistIndex,
                Winner: ReferenceEquals(candidate, winner)));
        }

        return rows;
    }

    private static IReadOnlyDictionary<PhysicalArchiveCopy, IReadOnlyDictionary<string, string>>
        IndexRequestedArchiveMembers(
            Mo2Profile profile,
            IReadOnlyList<LogicalArchive> archives,
            ISet<string> requested)
    {
        var result = new Dictionary<PhysicalArchiveCopy, IReadOnlyDictionary<string, string>>();
        var gameRelease = profile.Game == GameKind.SkyrimSE
            ? GameRelease.SkyrimSE
            : GameRelease.SkyrimVR;

        foreach (var archive in archives)
        {
            foreach (var copy in archive.PhysicalCopies)
            {
                if (result.ContainsKey(copy))
                {
                    continue;
                }

                var matches = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    ValidateBsaVersion(profile.Game, copy.Path);
                    var reader = Archive.CreateReader(gameRelease, copy.Path);
                    foreach (var file in reader.Files)
                    {
                        var canonical = NormalizeArchiveMemberPath(file.Path, copy.Path);
                        if (!requested.Contains(canonical))
                        {
                            continue;
                        }

                        if (!matches.TryAdd(canonical, NormalizeStoredPath(file.Path)))
                        {
                            throw new InvalidOperationException(
                                $"Archive contains duplicate case-insensitive member path '{canonical}': {copy.Path}");
                        }

                        ValidateMemberReadable(file, copy.Path);
                    }
                }
                catch (Exception exception) when (
                    exception is not InvalidOperationException ||
                    !exception.Message.StartsWith("Archive contains duplicate", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Cannot read active archive copy '{archive.Name}' from '{copy.Layer.Origin}': " +
                        $"{copy.Path}. {exception.Message}",
                        exception);
                }

                result.Add(copy, matches);
            }
        }

        return result;
    }

    private static void ValidateBsaVersion(GameKind game, string archivePath)
    {
        const uint supportedVersion = 0x69;
        Span<byte> header = stackalloc byte[8];
        using var stream = File.OpenRead(archivePath);
        stream.ReadExactly(header);

        if (!header[..4].SequenceEqual("BSA\0"u8))
        {
            throw new InvalidDataException($"Invalid BSA signature: {archivePath}");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (version != supportedVersion)
        {
            throw new InvalidDataException(
                $"Unsupported BSA version 0x{version:X2} for {game}; expected 0x{supportedVersion:X2}: {archivePath}");
        }
    }

    private static void ValidateMemberReadable(IArchiveFile file, string archivePath)
    {
        try
        {
            using var stream = file.AsStream();
            if (file.Size > 0 && stream.ReadByte() < 0)
            {
                throw new EndOfStreamException("Archive member ended before its declared size.");
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Cannot read archive member '{file.Path}' from {archivePath}",
                exception);
        }
    }

    private static string NormalizeArchiveMemberPath(string path, string archivePath)
    {
        try
        {
            return CommandLine.NormalizeAssetPath(path);
        }
        catch (CommandLineException exception)
        {
            throw new InvalidOperationException(
                $"Archive contains an invalid Data-relative member path '{path}': {archivePath}",
                exception);
        }
    }

    private static string NormalizeStoredPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string NormalizePhysicalPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static string FormatLoadMechanism(ArchiveLoadMechanism mechanism) => mechanism switch
    {
        ArchiveLoadMechanism.IniList => "ini-list",
        ArchiveLoadMechanism.PluginAssociation => "plugin-association",
        ArchiveLoadMechanism.EngineDefault => "engine-default",
        _ => throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, null)
    };

    private sealed class ProviderCandidate
    {
        private ProviderCandidate(
            string kind,
            SourceLayer layer,
            string sourcePath,
            string sourceAssetPath,
            LogicalArchive? archive)
        {
            Kind = kind;
            Layer = layer;
            SourcePath = sourcePath;
            SourceAssetPath = sourceAssetPath;
            Archive = archive;
        }

        internal string Kind { get; }
        internal SourceLayer Layer { get; }
        internal string SourcePath { get; }
        internal string SourceAssetPath { get; }
        internal LogicalArchive? Archive { get; }

        internal static ProviderCandidate CreateArchive(
            LogicalArchive archive,
            PhysicalArchiveCopy copy,
            string storedPath) =>
            new("archive", copy.Layer, copy.Path, storedPath, archive);

        internal static ProviderCandidate CreateLoose(PhysicalSourceFile file) =>
            new("loose", file.Layer, file.Path, file.RelativePath, archive: null);
    }
}

internal sealed record AssetChainResult(
    IReadOnlyList<AssetProviderRow> Rows,
    IReadOnlyList<string> Diagnostics);

internal sealed record AssetProviderRow(
    string AssetPath,
    int ProviderIndex,
    string SourceKind,
    string SourceOrigin,
    string SourcePath,
    string SourceAssetPath,
    string? Archive,
    string? ArchiveLoadMechanism,
    string? ArchiveLoadSource,
    int? ArchiveLoadIndex,
    string? AssociatedPlugin,
    int? PluginLoadOrderIndex,
    int? ModlistIndex,
    bool Winner);
