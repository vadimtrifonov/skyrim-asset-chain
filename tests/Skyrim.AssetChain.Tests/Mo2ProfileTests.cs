using Xunit;
using static Skyrim.AssetChain.Tests.AssetChainTestDriver;

namespace Skyrim.AssetChain.Tests;

public sealed class Mo2ProfileTests(AssetChainFixture fixture) : IClassFixture<AssetChainFixture>, IDisposable
{
    private readonly AssetChainTestDriver _driver = new(fixture);
    private readonly List<string> _copies = [];

    [Theory]
    [InlineData("Skyrim Special Edition", "SkyrimVR")]
    [InlineData("Fallout 4", "SkyrimSE")]
    public void RejectsUnsupportedOrMismatchedMo2Game(string configuredName, string requestedGame)
    {
        var root = CopyFixture();
        var organizerIni = Path.Combine(root, "ModOrganizer.ini");
        File.WriteAllText(
            organizerIni,
            File.ReadAllText(organizerIni).Replace(
                "gameName=Skyrim Special Edition",
                $"gameName={configuredName}",
                StringComparison.Ordinal));

        var result = _driver.Run("scripts/shared.pex", game: requestedGame, root: root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("gameName", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodesUtf8QSettingsByteArrayPath()
    {
        var root = CopyFixture("Tést-");

        var rows = ParseRows(_driver.Run("scripts/shared.pex", root: root));

        var gameData = Assert.Single(rows, row =>
            row.GetProperty("sourceOrigin").GetString() == "Game Data" &&
            row.GetProperty("archive").GetString() == "BaseA.bsa");
        Assert.StartsWith(
            NormalizePath(root) + "/",
            NormalizePath(gameData.GetProperty("sourcePath").GetString()!),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingLocalIniSettingsUseMo2ProfileDefault()
    {
        var root = CopyFixture();
        var organizerIni = Path.Combine(root, "ModOrganizer.ini");
        File.WriteAllText(
            organizerIni,
            File.ReadAllText(organizerIni).Replace(
                "profile_local_inis=true",
                string.Empty,
                StringComparison.Ordinal));

        var profile = Path.Combine(root, "named-profiles", fixture.ProfileName);
        var profileSettings = Path.Combine(profile, "settings.ini");
        File.WriteAllText(
            profileSettings,
            File.ReadAllText(profileSettings).Replace(
                "LocalSettings=true",
                string.Empty,
                StringComparison.Ordinal));

        const string archiveName = "ProfileDefault.bsa";
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "archive-b-compressed.bsa"),
            Path.Combine(root, "Game Root", "Data", archiveName));
        File.WriteAllText(
            Path.Combine(profile, "skyrimcustom.ini"),
            $"[Archive]{Environment.NewLine}sResourceArchiveList={archiveName}{Environment.NewLine}");

        var rows = ParseRows(_driver.Run("scripts/shared.pex", root: root));

        Assert.Single(rows, row => row.GetProperty("archive").GetString() == archiveName);
    }

    [Fact]
    public void MissingEnabledModDirectoryFailsWithoutOutput()
    {
        var root = CopyFixture();
        Directory.Delete(Path.Combine(root, "managed-mods", "High"), recursive: true);

        var result = _driver.Run("scripts/shared.pex", root: root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("High", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("modlist.txt", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingActivePluginFailsWithoutOutput()
    {
        var root = CopyFixture();
        File.Delete(Path.Combine(root, "managed-mods", "High", "Omega.esp"));

        var result = _driver.Run("scripts/shared.pex", root: root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("Omega.esp", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be resolved", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptShadowedArchiveFailsWithoutOutput()
    {
        var root = CopyFixture();
        File.WriteAllText(Path.Combine(root, "managed-mods", "Low", "Shadow.bsa"), "not a bsa");

        var result = _driver.Run("scripts/shared.pex", root: root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("Shadow.bsa", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Low", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadOrderDisagreementThatChangesArchiveOrderFails()
    {
        var root = CopyFixture();
        var path = Path.Combine(root, "named-profiles", fixture.ProfileName, "loadorder.txt");
        var text = File.ReadAllText(path);
        text = text.Replace(
            "Shadow.esp" + Environment.NewLine + "Omega.esp",
            "Omega.esp" + Environment.NewLine + "Shadow.esp",
            StringComparison.Ordinal);
        File.WriteAllText(path, text);

        var result = _driver.Run("scripts/shared.pex", root: root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("loadorder.txt", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("archive order", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedModlistEntryIncludesSourceAndLine()
    {
        var root = CopyFixture();
        var path = Path.Combine(root, "named-profiles", fixture.ProfileName, "modlist.txt");
        File.AppendAllText(path, Environment.NewLine + "?Broken" + Environment.NewLine);

        var result = _driver.Run("scripts/shared.pex", root: root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("modlist.txt", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line 8", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("?Broken", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingIniArchiveProducesWarningAndCompleteOutput()
    {
        var root = CopyFixture();
        var customIni = Path.Combine(root, "named-profiles", fixture.ProfileName, "skyrimcustom.ini");
        File.WriteAllText(
            customIni,
            "[Archive]" + Environment.NewLine +
            "sResourceArchiveList=Missing.bsa, BaseA.bsa" + Environment.NewLine);

        var result = _driver.Run("scripts/shared.pex", root: root);
        var rows = ParseRows(result);

        Assert.NotEmpty(rows);
        Assert.Contains("warning", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Missing.bsa", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Single(rows, row => row.GetProperty("winner").GetBoolean());
    }

    [Fact]
    public void LaterDuplicateArchiveRequestDefinesLogicalPosition()
    {
        var root = CopyFixture();
        var customIni = Path.Combine(root, "named-profiles", fixture.ProfileName, "skyrimcustom.ini");
        File.WriteAllText(
            customIni,
            "[Archive]" + Environment.NewLine +
            "sResourceArchiveList=BaseA.bsa, BaseB.bsa, BaseA.bsa" + Environment.NewLine +
            "sResourceArchiveList2=" + Environment.NewLine);

        var rows = ParseRows(_driver.Run("scripts/shared.pex", root: root));
        Assert.Equal(new[] { "BaseB.bsa", "BaseA.bsa" },
            rows.Take(2).Select(row => row.GetProperty("archive").GetString()));
    }

    [Fact]
    public void PluginRequestMovesIniRequestedArchiveToPluginPosition()
    {
        var root = CopyFixture();
        var customIni = Path.Combine(root, "named-profiles", fixture.ProfileName, "skyrimcustom.ini");
        File.WriteAllText(
            customIni,
            "[Archive]" + Environment.NewLine +
            "sResourceArchiveList=Omega.bsa" + Environment.NewLine +
            "sResourceArchiveList2=" + Environment.NewLine);

        var rows = ParseRows(_driver.Run("scripts/shared.pex", root: root));
        var omega = Assert.Single(rows, row => row.GetProperty("archive").GetString() == "Omega.bsa");
        Assert.Equal("plugin-association", omega.GetProperty("archiveLoadMechanism").GetString());
        Assert.Equal("Omega.esp", omega.GetProperty("archiveLoadSource").GetString());
        Assert.True(omega.GetProperty("winner").GetBoolean());
    }

    [Fact]
    public void VrIniListArchiveLoadsAfterPluginArchives()
    {
        var root = CreateVrFixture(includeVrListSetting: true);
        var rows = ParseRows(_driver.Run("scripts/shared.pex", game: "SkyrimVR", root: root));

        var winner = rows.Single(row => row.GetProperty("winner").GetBoolean());
        Assert.Equal("VrLate.bsa", winner.GetProperty("archive").GetString());
        Assert.Equal("ini-list", winner.GetProperty("archiveLoadMechanism").GetString());
        Assert.Equal("sVrResourceArchiveList", winner.GetProperty("archiveLoadSource").GetString());
    }

    [Fact]
    public void VrUsesEngineDefaultWhenVrListLoadsNothing()
    {
        var root = CreateVrFixture(includeVrListSetting: false);
        var rows = ParseRows(_driver.Run("scripts/shared.pex", game: "SkyrimVR", root: root));

        var winner = rows.Single(row => row.GetProperty("winner").GetBoolean());
        Assert.Equal("Skyrim_VR - Main.bsa", winner.GetProperty("archive").GetString());
        Assert.Equal("engine-default", winner.GetProperty("archiveLoadMechanism").GetString());
        Assert.Equal("Skyrim VR", winner.GetProperty("archiveLoadSource").GetString());
    }

    private string CreateVrFixture(bool includeVrListSetting)
    {
        var root = CopyFixture();
        var organizerIni = Path.Combine(root, "ModOrganizer.ini");
        File.WriteAllText(
            organizerIni,
            File.ReadAllText(organizerIni).Replace(
                "gameName=Skyrim Special Edition",
                "gameName=Skyrim VR",
                StringComparison.Ordinal));
        var data = Path.Combine(root, "Game Root", "Data");
        File.WriteAllText(Path.Combine(data, "SkyrimVR.esm"), string.Empty);
        var fixtureArchive = Path.Combine(AppContext.BaseDirectory, "Fixtures", "archive-b-compressed.bsa");
        var archiveName = includeVrListSetting ? "VrLate.bsa" : "Skyrim_VR - Main.bsa";
        File.Copy(fixtureArchive, Path.Combine(data, archiveName));

        var profile = Path.Combine(root, "named-profiles", fixture.ProfileName);
        var vrSetting = includeVrListSetting
            ? "sVrResourceArchiveList=VrLate.bsa" + Environment.NewLine
            : string.Empty;
        File.WriteAllText(
            Path.Combine(profile, "skyrimvr.ini"),
            "[Archive]" + Environment.NewLine +
            "sResourceArchiveList=Obsolete.bsa" + Environment.NewLine +
            "sResourceArchiveList2=BaseB.bsa" + Environment.NewLine +
            vrSetting);

        var loadOrderPath = Path.Combine(profile, "loadorder.txt");
        var loadOrder = File.ReadAllText(loadOrderPath).Replace(
            "Dragonborn.esm" + Environment.NewLine,
            "Dragonborn.esm" + Environment.NewLine + "SkyrimVR.esm" + Environment.NewLine,
            StringComparison.Ordinal);
        File.WriteAllText(loadOrderPath, loadOrder);
        return root;
    }

    private string CopyFixture(string namePrefix = "")
    {
        var copy = fixture.CreateCopy(namePrefix);
        _copies.Add(copy);
        return copy;
    }

    public void Dispose()
    {
        foreach (var copy in _copies.Where(Directory.Exists))
        {
            Directory.Delete(copy, recursive: true);
        }
    }
}
