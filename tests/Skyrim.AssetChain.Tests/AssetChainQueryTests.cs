using Xunit;
using static Skyrim.AssetChain.Tests.AssetChainTestDriver;

namespace Skyrim.AssetChain.Tests;

public sealed class AssetChainQueryTests(AssetChainFixture fixture) : IClassFixture<AssetChainFixture>
{
    private readonly AssetChainTestDriver _driver = new(fixture);

    [Fact]
    public void EmitsGoldenJsonl()
    {
        var result = _driver.Run("scripts/shared.pex");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        var expected = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Golden", "multiple-archives.jsonl"))
            .Replace("<ROOT>", NormalizePath(fixture.Root), StringComparison.Ordinal)
            .ReplaceLineEndings("\n");
        Assert.Equal(expected, result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void EmitsEveryArchiveEntryInDeterministicOrder()
    {
        var result = _driver.Run("Scripts\\SHARED.PEX");
        var rows = ParseRows(result);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal(
        [
            "BaseA.bsa",
            "BaseB.bsa",
            "Alpha.bsa",
            "Split.bsa",
            "Split - Textures.bsa",
            "Shadow.bsa",
            "Shadow.bsa",
            "Omega.bsa"
        ],
            rows.Select(row => row.GetProperty("archive").GetString()));
        Assert.Equal(Enumerable.Range(0, rows.Count),
            rows.Select(row => row.GetProperty("providerIndex").GetInt32()));
        Assert.All(rows, row => Assert.Equal("scripts/shared.pex", row.GetProperty("assetPath").GetString()));
        var winner = Assert.Single(rows, row => row.GetProperty("winner").GetBoolean());
        Assert.Equal("Omega.bsa", winner.GetProperty("archive").GetString());

        var shadowCopies = rows.Where(row => row.GetProperty("archive").GetString() == "Shadow.bsa").ToArray();
        Assert.Equal(new[] { "Low", "High" },
            shadowCopies.Select(row => row.GetProperty("sourceOrigin").GetString()));
        Assert.Equal(new[] { 2, 0 },
            shadowCopies.Select(row => row.GetProperty("modlistIndex").GetInt32()));
        Assert.Equal(shadowCopies[0].GetProperty("archiveLoadIndex").GetInt32(),
            shadowCopies[1].GetProperty("archiveLoadIndex").GetInt32());
    }

    [Fact]
    public void ActivatesInstalledCreationClubArchive()
    {
        var rows = ParseRows(_driver.Run("scripts/creationclub.pex"));

        var row = Assert.Single(rows);
        Assert.Equal("ccFixture.bsa", row.GetProperty("archive").GetString());
        Assert.Equal("ccFixture.esl", row.GetProperty("associatedPlugin").GetString());
        Assert.Equal(5, row.GetProperty("pluginLoadOrderIndex").GetInt32());
        Assert.True(row.GetProperty("winner").GetBoolean());
    }

    [Theory]
    [InlineData("High", "Omega.bsa")]
    [InlineData("Low", "Shadow.bsa")]
    public void RejectsSkyrimLeArchiveAnywhereInActiveArchiveChain(string mod, string archive)
    {
        var root = fixture.CreateCopy();
        try
        {
            var oldrimArchive = Path.Combine(AppContext.BaseDirectory, "Fixtures", "archive-skyrim-le.bsa");
            var activeArchive = Path.Combine(root, "managed-mods", mod, archive);
            File.Copy(oldrimArchive, activeArchive, overwrite: true);

            var result = _driver.Run("scripts/shared.pex", root: root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Stdout);
            Assert.Contains("Unsupported BSA version 0x68", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("expected 0x69", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadsSamePluginTexturesArchiveAfterPlainArchive()
    {
        var rows = ParseRows(_driver.Run("scripts/splitorder.pex"));

        Assert.Equal(new[] { "Split.bsa", "Split - Textures.bsa" },
            rows.Select(row => row.GetProperty("archive").GetString()));
        Assert.False(rows[0].GetProperty("winner").GetBoolean());
        Assert.True(rows[1].GetProperty("winner").GetBoolean());
    }

    [Fact]
    public void ReportsBlockedArchiveMemberWithNoWinner()
    {
        var rows = ParseRows(_driver.Run("scripts/blocked.pex"));

        Assert.Single(rows);
        Assert.Equal("Low", rows[0].GetProperty("sourceOrigin").GetString());
        Assert.Equal("Foo.bsa", rows[0].GetProperty("archive").GetString());
        Assert.False(rows[0].GetProperty("winner").GetBoolean());
    }

    [Fact]
    public void ResolvesLooseFilesAfterArchivesByMo2Priority()
    {
        var rows = ParseRows(_driver.Run("/scripts/LOOSEONLY.pex"));

        Assert.Equal(new[] { "Game Data", "Low", "High" },
            rows.Select(row => row.GetProperty("sourceOrigin").GetString()));
        Assert.All(rows, row => Assert.Equal("loose", row.GetProperty("sourceKind").GetString()));
        Assert.Equal("Scripts/LooseOnly.PEX", rows[0].GetProperty("sourceAssetPath").GetString());
        Assert.True(rows[^1].GetProperty("winner").GetBoolean());
        Assert.Equal(0, rows[^1].GetProperty("modlistIndex").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows[0].GetProperty("archive").ValueKind);
    }

    [Fact]
    public void RejectsStrongerDirectoryAtLooseAssetPath()
    {
        var root = fixture.CreateCopy();
        try
        {
            var relativePath = Path.Combine("Scripts", "LooseOnly.pex");
            Directory.CreateDirectory(Path.Combine(root, "managed-mods", "Middle", relativePath));

            var rows = ParseRows(_driver.Run("scripts/looseonly.pex", root: root));
            var winner = Assert.Single(rows, row => row.GetProperty("winner").GetBoolean());
            Assert.Equal("High", winner.GetProperty("sourceOrigin").GetString());

            File.Delete(Path.Combine(root, "managed-mods", "High", relativePath));
            var blocked = _driver.Run("scripts/looseonly.pex", root: root);

            Assert.NotEqual(0, blocked.ExitCode);
            Assert.Equal(string.Empty, blocked.Stdout);
            Assert.Contains("file/directory collision", blocked.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Middle", blocked.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LooseProvidersOverrideArchiveChain()
    {
        var root = fixture.CreateCopy();
        try
        {
            foreach (var sourceRoot in new[]
                     {
                         Path.Combine(root, "Game Root", "Data"),
                         Path.Combine(root, "managed-mods", "Low"),
                         Path.Combine(root, "managed-mods", "High"),
                         Path.Combine(root, "output")
                     })
            {
                var path = Path.Combine(sourceRoot, "Scripts", "Shared.pex");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "loose provider");
            }

            var rows = ParseRows(_driver.Run("scripts/shared.pex", root: root));
            var firstLoose = rows.FindIndex(row => row.GetProperty("sourceKind").GetString() == "loose");

            Assert.True(firstLoose > 1);
            Assert.All(rows.Take(firstLoose),
                row => Assert.Equal("archive", row.GetProperty("sourceKind").GetString()));
            Assert.All(rows.Skip(firstLoose),
                row => Assert.Equal("loose", row.GetProperty("sourceKind").GetString()));
            Assert.Equal(new[] { "Game Data", "Low", "High", "Overwrite" },
                rows.Skip(firstLoose).Select(row => row.GetProperty("sourceOrigin").GetString()));

            var winner = Assert.Single(rows, row => row.GetProperty("winner").GetBoolean());
            Assert.Equal("Overwrite", winner.GetProperty("sourceOrigin").GetString());
            Assert.Equal("loose", winner.GetProperty("sourceKind").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MohiddenFileIsSkippedWithoutHidingItsUnsuffixedSibling()
    {
        var rows = ParseRows(_driver.Run("scripts/hidden.pex"));

        Assert.Equal(new[] { "Low", "Middle" },
            rows.Select(row => row.GetProperty("sourceOrigin").GetString()));
        Assert.True(rows[^1].GetProperty("winner").GetBoolean());

        var hidden = _driver.Run("scripts/hidden.pex.mohidden");
        Assert.NotEqual(0, hidden.ExitCode);
        Assert.Equal(string.Empty, hidden.Stdout);
        Assert.Contains("no loose file or registered BSA", hidden.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MohiddenSiblingsDoNotHideUnsuffixedPluginOrArchive()
    {
        var rows = ParseRows(_driver.Run("scripts/shared.pex"));

        var omega = Assert.Single(rows, row => row.GetProperty("archive").GetString() == "Omega.bsa");
        Assert.Equal("Omega.esp", omega.GetProperty("associatedPlugin").GetString());
        Assert.True(omega.GetProperty("winner").GetBoolean());
    }

    [Fact]
    public void UsvfsSkipSuffixDoesNotFilterPhysicalGameData()
    {
        var rows = ParseRows(_driver.Run("scripts/physical.pex.mohidden"));

        var row = Assert.Single(rows);
        Assert.Equal("Game Data", row.GetProperty("sourceOrigin").GetString());
        Assert.True(row.GetProperty("winner").GetBoolean());
    }

    [Fact]
    public void DefaultSkippedDirectoryDoesNotEnterVirtualData()
    {
        var result = _driver.Run(".git/skipped.pex");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("no loose file or registered BSA", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HonorsConfiguredUsvfsSkipLists()
    {
        var root = fixture.CreateCopy();
        try
        {
            File.AppendAllText(
                Path.Combine(root, "ModOrganizer.ini"),
                ("\nskip_file_suffixes=.mohidden, .ignored, @@ignored, \".comma,suffix\"\n" +
                 "skip_directories=.git, Cache\n").ReplaceLineEndings());
            var middle = Path.Combine(root, "managed-mods", "Middle");
            var suffixFile = Path.Combine(middle, "Scripts", "Custom.pex.ignored");
            Directory.CreateDirectory(Path.GetDirectoryName(suffixFile)!);
            File.WriteAllText(suffixFile, "skipped suffix");
            File.WriteAllText(Path.Combine(middle, "Scripts", "Custom.pex@ignored"), "escaped at suffix");
            File.WriteAllText(Path.Combine(middle, "Scripts", "Custom.pex.comma,suffix"), "quoted comma suffix");
            var directoryFile = Path.Combine(middle, "Cache", "Custom.pex");
            Directory.CreateDirectory(Path.GetDirectoryName(directoryFile)!);
            File.WriteAllText(directoryFile, "skipped directory");

            foreach (var path in new[]
                     {
                         "scripts/custom.pex.ignored",
                         "scripts/custom.pex@ignored",
                         "scripts/custom.pex.comma,suffix",
                         "cache/custom.pex"
                     })
            {
                var result = _driver.Run(path, root: root);
                Assert.NotEqual(0, result.ExitCode);
                Assert.Equal(string.Empty, result.Stdout);
                Assert.Contains("no loose file or registered BSA", result.Stderr, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EmptyConfiguredSuffixListDoesNotApplyMohiddenDefault()
    {
        var root = fixture.CreateCopy();
        try
        {
            File.AppendAllText(
                Path.Combine(root, "ModOrganizer.ini"),
                "\nskip_file_suffixes=@Invalid()\n".ReplaceLineEndings());

            var rows = ParseRows(_driver.Run("scripts/hidden.pex.mohidden", root: root));

            var row = Assert.Single(rows);
            Assert.Equal("Middle", row.GetProperty("sourceOrigin").GetString());
            Assert.True(row.GetProperty("winner").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DoesNotFlattenNestedDataDirectory()
    {
        var rows = ParseRows(_driver.Run("data/scripts/nested.pex"));

        Assert.Single(rows);
        Assert.Equal("Low", rows[0].GetProperty("sourceOrigin").GetString());

        var missing = _driver.Run("scripts/nested.pex");
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Equal(string.Empty, missing.Stdout);
    }

    [Fact]
    public void OverwriteWinsLooseAsset()
    {
        var rows = ParseRows(_driver.Run("scripts/overwriteonly.pex"));

        Assert.Single(rows);
        Assert.Equal("Overwrite", rows[0].GetProperty("sourceOrigin").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows[0].GetProperty("modlistIndex").ValueKind);
        Assert.True(rows[0].GetProperty("winner").GetBoolean());
    }

    [Fact]
    public void DisabledModDoesNotProvideAsset()
    {
        var result = _driver.Run("scripts/disabledonly.pex");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("no loose file or registered BSA", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\absolute\\file.pex")]
    [InlineData("//server/share/file.pex")]
    [InlineData("scripts/./file.pex")]
    [InlineData("scripts/../file.pex")]
    public void RejectsInvalidAssetPath(string path)
    {
        var result = _driver.Run(path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("asset path", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HelpUsesStandardOutput()
    {
        var result = Invoke(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("skyrim-asset-chain", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("--paths-from", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Stderr);
    }
}
