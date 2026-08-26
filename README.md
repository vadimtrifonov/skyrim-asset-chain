# Skyrim Asset Chain

Skyrim Asset Chain reports the asset providers for one Mod Organizer 2 profile.

It answers this question for each Data-relative path:

> Which eligible sources contain this asset, and which source wins?

The command reports loose files and members of active BSA files. It does not inspect or compare asset content.

## Requirements

- Windows
- .NET 10 runtime

Source builds use [mise](https://mise.jdx.dev/).

`SkyrimSE` includes Special Edition and Anniversary Edition. Use `SkyrimVR` for Skyrim VR.

## Build

```powershell
mise trust
mise install
mise run build
mise run test
```

Create the Windows release:

```powershell
mise run publish
```

The task writes a folder and ZIP file under `artifacts`.

## Usage

Query one asset path:

```powershell
skyrim-asset-chain.exe `
  --game SkyrimSE `
  --mo2-root "C:\Games\Skyrim\Wabbajack - CSVO 2.3" `
  --profile BottleRim `
  "scripts/QF_MQ101_0003372B.pex"
```

Query paths from a file:

```powershell
skyrim-asset-chain.exe `
  --game SkyrimVR `
  --mo2-root "C:\Games\Skyrim\Wabbajack - FUS Heavy" `
  --profile CANGAR `
  --paths-from "C:\Path\To\asset-paths.txt"
```

Use `--paths-from -` to read standard input. Batch input contains one path per line.

The command normalizes case and separators. For example, these inputs identify the same asset:

```text
scripts/QF_MQ101_0003372B.pex
Scripts\qf_mq101_0003372b.PEX
/scripts/QF_MQ101_0003372B.pex
```

Run this command outside MO2. The command reads each physical source layer itself.

The command rejects a process that contains MO2 USVFS. USVFS merges mod files into physical paths and makes provenance incorrect.

## Output

The command writes compact JSONL. Each row describes one eligible provider entry:

```jsonl
{"assetPath":"scripts/qf_mq101_0003372b.pex","providerIndex":0,"sourceKind":"archive","sourceOrigin":"Game Data","sourcePath":"C:/Game/Data/Skyrim - Misc.bsa","sourceAssetPath":"scripts/qf_mq101_0003372b.pex","archive":"Skyrim - Misc.bsa","archiveLoadMechanism":"ini-list","archiveLoadSource":"sResourceArchiveList","archiveLoadIndex":0,"associatedPlugin":null,"pluginLoadOrderIndex":null,"modlistIndex":null,"winner":false}
```

`archiveLoadMechanism` identifies how the engine registered the archive:

- `ini-list`: An archive-list INI setting named the archive.
- `plugin-association`: An active plugin caused the engine to register its associated archive.
- `engine-default`: The engine registered a default archive without an INI-list entry or plugin association.

`archiveLoadSource` identifies the exact INI setting, plugin, or game. `archiveLoadIndex` gives the final archive position.

Archive rows come first in archive-load order. Loose rows follow in MO2 priority order.

The command reports matching members from shadowed physical copies of an active BSA name. Only the runtime-selected entry has `winner:true`.
A successful chain can contain only `winner:false` rows. This result means that matching entries exist, but container shadowing blocks all of them.

Diagnostics use standard error. An error returns a nonzero exit code and produces no JSONL output.

## Resolution rules

The command applies these rules:

1. MO2 skip rules filter files from enabled mods and `Overwrite`, but not from physical game `Data`.
2. MO2 priority selects the physical copy of each logical BSA name.
3. The game archive order selects among surviving BSA members.
4. Loose files override BSA members.
5. MO2 priority selects among loose files.

The command reads `skip_file_suffixes` and `skip_directories` from `[Settings]` in `<MO2 root>\ModOrganizer.ini`.
If a setting is absent, the command uses the MO2 default: `.mohidden` for suffixes and `.git` for directories.
An explicitly empty list supplies no entries for that setting.
A skipped file does not hide an unsuffixed sibling.

For Skyrim SE, the engine derives two associated BSA names from each active plugin. For `Example.esp`, these names are:

```text
Example.bsa
Example - Textures.bsa
```

If both archives exist, the engine registers `Example.bsa` first and `Example - Textures.bsa` second.
Other BSA names require another registration mechanism, such as an archive-list INI setting.

The command does not model archive-list settings from plugin-sidecar INIs, such as `sResourceArchiveList`.
It rejects a profile when an active plugin's matching INI contains a nonempty archive-list setting.

Skyrim VR applies `sVrResourceArchiveList` after plugin archives.
If the VR list loads no archive, the engine registers `Skyrim_VR - Main.bsa` by default.
