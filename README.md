# Skyrim Asset Chain

Skyrim Asset Chain reports the loose files and BSA members for requested asset paths in a Mod Organizer 2 profile.

It answers one question for each asset path in the game `Data` folder:

> Which loose files and BSA members match this path, and which copy does the game use?

## Requirements

- Windows
- .NET 10 runtime

## Usage

Run the tool outside MO2. It reads the game `Data` folder, each enabled mod, and `Overwrite` directly.
The tool stops if MO2 USVFS is active. USVFS combines these locations into one virtual `Data` folder and hides the origin of each file.

Query one asset path:

```powershell
skyrim-asset-chain.exe `
  --game SkyrimSE `
  --mo2-root "C:\Games\Skyrim\Wabbajack - CSVO 2.3" `
  --profile BottleRim `
  "scripts/QF_MQ101_0003372B.pex"
```

Use `--paths-from -` to read standard input:

```powershell
skyrim-asset-chain.exe `
  --game SkyrimVR `
  --mo2-root "C:\Games\Skyrim\Wabbajack - FUS Heavy" `
  --profile CANGAR `
  --paths-from "C:\Path\To\asset-paths.txt"
```

Batch input contains one path per line.

The tool normalizes case and separators. For example, these inputs identify the same asset:

```text
scripts/QF_MQ101_0003372B.pex
Scripts\qf_mq101_0003372b.PEX
/scripts/QF_MQ101_0003372B.pex
```

`--game` accepts `SkyrimSE` or `SkyrimVR`. `SkyrimSE` covers Special Edition and Anniversary Edition.

## Output

The tool writes compact JSONL. Each row identifies one loose file or BSA member that matches the requested path:

```jsonl
{"assetPath":"scripts/qf_mq101_0003372b.pex","providerIndex":0,"sourceKind":"archive","sourceOrigin":"Game Data","sourcePath":"C:/Game/Data/Skyrim - Misc.bsa","sourceAssetPath":"scripts/qf_mq101_0003372b.pex","archive":"Skyrim - Misc.bsa","archiveLoadMechanism":"ini-list","archiveLoadSource":"sResourceArchiveList","archiveLoadIndex":0,"associatedPlugin":null,"pluginLoadOrderIndex":null,"modlistIndex":null,"winner":false}
```

`archiveLoadMechanism` identifies how the engine registered the archive:

- `ini-list`: The game INI chain registered the archive through an archive-list setting.
- `plugin-association`: An active plugin caused the engine to register its associated archive.
- `engine-default`: The engine registered a default archive without an INI-list entry or plugin association.

`archiveLoadSource` identifies the exact INI setting, plugin, or game. `archiveLoadIndex` gives the final archive position.

Archive rows come first in archive-load order. Loose rows follow in MO2 priority order.

When the profile contains several copies of a registered BSA, the tool reports matching assets from every copy.
Only the copy that the game uses can have `winner:true`.
A successful query can report matches without a winner. This occurs when only overridden BSA files contain the asset.

A requested path with no matching loose file or BSA member produces no row.
A request with no matches succeeds with empty standard output.

Diagnostics use standard error. An error returns a nonzero exit code and produces no JSONL output.

## Resolution rules

The tool applies these rules:

1. MO2 skip rules exclude matching files and directories from enabled mods and `Overwrite`. They do not apply to the game's `Data` folder.
2. When several BSA files have the same name, MO2 priority selects one file.
3. When several registered BSAs contain the asset, the game archive order selects the winning BSA.
4. Loose files override BSA members.
5. When several loose files contain the asset, MO2 priority selects the winner.

The tool reads `skip_file_suffixes` and `skip_directories` from `[Settings]` in `<MO2 root>\ModOrganizer.ini`.
If a setting is absent, the tool uses the MO2 default: `.mohidden` for suffixes and `.git` for directories.
If a setting is present but empty, the tool skips nothing for that setting instead of using the MO2 default.
A skipped file does not hide an unsuffixed sibling.

For Skyrim SE, the engine derives two associated BSA names from each active plugin. For `Example.esp`, these names are:

```text
Example.bsa
Example - Textures.bsa
```

If both archives exist, the engine registers `Example.bsa` first and `Example - Textures.bsa` second.
Other BSA names require another registration mechanism, such as an archive-list INI setting.

Skyrim VR applies `sVrResourceArchiveList` after plugin archives.
If the VR list loads no archive, the engine registers `Skyrim_VR - Main.bsa` by default.

## Root Builder

The tool supports the conventional Root Builder layout. It applies these mappings to each enabled mod and `Overwrite`:

- The mod directory uses the normal MO2 `Data` mapping.
- The contents of its `Root` directory map to the game directory.

The tool assumes Root Builder behavior when an enabled mod or `Overwrite` contains a `Root` directory.
It does not inspect the Root Builder installation or its enabled state.
Custom Root Builder file patterns and exclusions are not supported.

## Limits

The tool does not model the runtime precedence of these known cases. It rejects them instead of inferring a winner:

- At one path in `Data`, `Overwrite` or a higher-priority mod contains a directory, but a lower-priority mod or the game contains a file.
- An active plugin's matching sidecar INI contains a nonempty archive-list setting, such as `sResourceArchiveList`.

The tool does not report archives mounted by game menus or archive-loader extensions.
This includes transient archives such as `MarketplaceTextures.bsa`.

The tool does not support custom mappings from other MO2 plugins that use `IPluginFileMapper`.

## Development

Source builds use [mise](https://mise.jdx.dev/). Mise installs the .NET 10 SDK.

### Build

```powershell
mise trust
mise install
mise run build
```

### Test

```powershell
mise run test
```

The tests use Skyrim BSA fixtures and isolated MO2 profiles.
The [fixture notes](tests/Skyrim.AssetChain.Tests/Fixtures/README.md) describe the archive formats.

### Publish

Create the Windows release:

```powershell
mise run publish
```

The task writes a folder and ZIP file under `artifacts`.
