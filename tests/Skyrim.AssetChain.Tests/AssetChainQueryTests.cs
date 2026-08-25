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
    public void MohiddenFileSuppressesOnlyItsOwnSource()
    {
        var rows = ParseRows(_driver.Run("scripts/hidden.pex"));

        Assert.Single(rows);
        Assert.Equal("Low", rows[0].GetProperty("sourceOrigin").GetString());
        Assert.True(rows[0].GetProperty("winner").GetBoolean());
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
        Assert.Contains("no eligible provider", result.Stderr, StringComparison.OrdinalIgnoreCase);
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
