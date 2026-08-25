using Xunit;
using static Skyrim.AssetChain.Tests.AssetChainTestDriver;

namespace Skyrim.AssetChain.Tests;

public sealed class AssetChainBatchTests(AssetChainFixture fixture) : IClassFixture<AssetChainFixture>
{
    private readonly AssetChainTestDriver _driver = new(fixture);

    [Fact]
    public void OnePathBatchMatchesSingularOutput()
    {
        Assert.Equal(
            _driver.Run("scripts/shared.pex"),
            _driver.RunBatch("scripts/shared.pex\n"));
    }

    [Fact]
    public void PreservesInputOrderAndContiguousChains()
    {
        var result = _driver.RunBatch(
            "scripts/blocked.pex\nscripts/looseonly.pex\nscripts/splitorder.pex\n");
        var rows = ParseRows(result);

        Assert.Equal(
        [
            "scripts/blocked.pex",
            "scripts/looseonly.pex",
            "scripts/looseonly.pex",
            "scripts/looseonly.pex",
            "scripts/splitorder.pex",
            "scripts/splitorder.pex"
        ],
            rows.Select(row => row.GetProperty("assetPath").GetString()));
        Assert.Equal(new[] { 0, 0, 1, 2, 0, 1 },
            rows.Select(row => row.GetProperty("providerIndex").GetInt32()));
    }

    [Fact]
    public void FileAndStandardInputProduceIdenticalOutput()
    {
        var input = "scripts/blocked.pex\nscripts/overwriteonly.pex\n";
        var inputPath = Path.Combine(fixture.Root, "asset-paths.txt");
        File.WriteAllText(inputPath, input);

        Assert.Equal(
            _driver.RunBatch(input),
            _driver.RunBatch(string.Empty, inputPath));
    }

    [Fact]
    public void RejectsCanonicalDuplicateWithoutOutput()
    {
        var result = _driver.RunBatch("Scripts\\Shared.pex\n/scripts/shared.PEX\n");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("duplicate asset path", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line 2", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsEmptyInputAndEmptyLines()
    {
        var empty = _driver.RunBatch(string.Empty);
        Assert.NotEqual(0, empty.ExitCode);
        Assert.Equal(string.Empty, empty.Stdout);
        Assert.Contains("empty", empty.Stderr, StringComparison.OrdinalIgnoreCase);

        var emptyLine = _driver.RunBatch("scripts/shared.pex\n\n");
        Assert.NotEqual(0, emptyLine.ExitCode);
        Assert.Equal(string.Empty, emptyLine.Stdout);
        Assert.Contains("line 2", emptyLine.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailureAfterValidPathProducesNoOutput()
    {
        var result = _driver.RunBatch("scripts/shared.pex\nscripts/missing.pex\n");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("scripts/missing.pex", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsPositionalAndBatchInputsTogether()
    {
        var result = Invoke(
        [
            "--game", "SkyrimSE",
            "--mo2-root", fixture.Root,
            "--profile", fixture.ProfileName,
            "--paths-from", "-",
            "scripts/shared.pex"
        ],
            "scripts/blocked.pex\n");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("exactly one", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }
}
