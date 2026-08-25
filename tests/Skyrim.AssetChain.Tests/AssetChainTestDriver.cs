using System.Text.Json;
using Xunit;

namespace Skyrim.AssetChain.Tests;

internal sealed class AssetChainTestDriver(AssetChainFixture fixture)
{
    public RunResult Run(
        string assetPath,
        string game = "SkyrimSE",
        string? root = null,
        string? profile = null) =>
        Invoke(BuildArgs(
            assetPath,
            game,
            root ?? fixture.Root,
            profile ?? fixture.ProfileName));

    public RunResult RunBatch(
        string input,
        string source = "-",
        string game = "SkyrimSE",
        string? root = null,
        string? profile = null) =>
        Invoke(
        [
            "--game", game,
            "--mo2-root", root ?? fixture.Root,
            "--profile", profile ?? fixture.ProfileName,
            "--paths-from", source
        ],
            input);

    public static RunResult Invoke(string[] args, string input = "")
    {
        using var stdin = new StringReader(input);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = Program.Run(args, stdin, stdout, stderr);
        return new RunResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    public static List<JsonElement> ParseRows(RunResult result)
    {
        Assert.Equal(0, result.ExitCode);
        return result.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    public static string NormalizePath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    private static string[] BuildArgs(
        string assetPath,
        string game,
        string root,
        string profile) =>
    [
        "--game", game,
        "--mo2-root", root,
        "--profile", profile,
        assetPath
    ];
}

internal sealed record RunResult(int ExitCode, string Stdout, string Stderr);
