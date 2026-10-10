# Big Bellows Revival

A maintenance fork of [Big Bellows Fix](https://github.com/funions123/vs-BellowsFix) by Onions for Vintage Story 1.22.7.

The mod powers the vanilla large bellows through a wooden linkage arm. It keeps the existing `bellowsfix` mod ID and vanilla block IDs so existing bellows do not need to be replaced.

## Changes in this fork

- Safe animation frames when joining near powered bellows or loading their chunk
- Correct animation blending while following the mechanical shaft
- Pumping in either rotation direction, with gear ratios included
- Controlled air and sound delivery after stopping, reconnecting or receiving a network correction
- Static mesh fallback while the animated mesh is loading
- Renderer cleanup when bellows are removed or their chunk unloads
- Optional Improved Metallurgy blast furnace support without a required dependency

The inherited recipe accepts different hammer and chisel materials. Its layout differs from the windmill rotor recipe, including when ingredients are stacked for batch crafting.

Visual alignment, sound playback and distant multiplayer joins still need in-game playtesting.

## Installation

Install the same version on the server and every client. Remove the original Big Bellows Fix and VSA Working Bellows before installing this fork. They patch the same block and must not run together.

This version is not listed on Mod DB. Servers cannot auto-download it for players. Each player needs the same locally built package installed before joining.

This repository does not include game assemblies or a prebuilt download. Upstream has not declared a license. Redistribution permission needs to be settled before publishing packaged releases.

## Placement

The nozzle points toward the forge. Put the forge directly in front of it, at the same height as the bellows.

The linkage goes beneath the rear tile of the bellows, one block behind the nozzle position and one block lower. A linkage placed directly beneath the nozzle will not connect.

Side view

```text
     rear bellows tile     bellows nozzle  >  forge
     wooden linkage
```

| Nozzle points | Forge position | Linkage position |
| --- | --- | --- |
| North | One block north | One block south and one lower |
| East | One block east | One block west and one lower |
| South | One block south | One block north and one lower |
| West | One block west | One block east and one lower |

These offsets are measured from the bellows block position. Attach a powered horizontal axle to the connector face of the linkage. For example, a north-facing linkage connects to the axle immediately north of it, with the axle running north to south.

Use a lit forge with fuel and an item to heat when checking air delivery. Particles alone do not prove that the server is heating the forge.

## Building

Install the .NET 10 SDK and Vintage Story 1.22.7. The build uses the game assemblies from your own installation. It does not download or copy them into the mod.

On Windows, the default game directory is `%APPDATA%/Vintagestory`. For a different installation, pass its path explicitly.

```text
dotnet build BellowsFix.csproj -c Release -p:GameDirectory="PATH_TO_VINTAGE_STORY"
```

The output is written to `bin/Release/bellowsfix` and can be loaded as a local mod folder. To build a local ZIP, add `-p:PackageMod=true`. Packaging a local build does not grant redistribution rights.

## Reporting problems

Include the game version, mod version, bellows direction and what you were doing when the problem happened. Say whether the linkage was moving and whether the server was heating the forge. For a crash, attach the fresh client crash log and main log after removing personal details.

When packaged releases become available, each update will receive a new version. Older downloads should stay available so servers can keep using a matching client version.

## Credits

Original Big Bellows Fix by Onions. This fork is maintained by [AylanJ123](https://github.com/AylanJ123) and retains the upstream history and credits. The large bellows model, linkage and sound assets belong to Vintage Story and are not bundled here.
