# SkyPod

Build a personal Skyrim soundtrack from your own music and the music already in your modlist. Arrange it in SkyPod Studio, choose where it plays, and control playback through the in-game PrismaUI player.

By **Wondernutts** ([Wondernuttz on GitHub](https://github.com/Wondernuttz)).

## Features

- Import MP3, FLAC, WAV, OGG, Opus, AAC, M4A, WMA and XWM. Audio conversion is included in the Studio download.
- Assign music to exploration, towns, taverns, dungeons, combat and specific places.
- Browse the Atlas to select locations, buildings and interiors from the active load order, including supported mod-added locations.
- Select boss encounters and assign songs or playlists through the Bosses picker.
- Browse vanilla, DLC and mod-added music alongside imported tracks.
- Open the in-game player with F8, search tracks, use Play Now, or return control to Skyrim. Change the hotkey through SkyPod's MCM.
- Export player soundtrack packs and editable author projects for modlists.

SkyPod includes no songs. Use your own collection, installed music mods or a soundtrack pack.

## Version 1.0.0 compatibility

The supplied game plugin targets **Skyrim VR** and requires **SKSEVR**. This source snapshot does not provide an SE/AE plugin build. Studio is a **Windows x64** desktop application and can be used without wearing a headset. Wine/Proton compatibility has not been verified.

The Windows Studio build, packaged encoder conversions and XAudio2.7 decoding/playback checks have passed. Full in-game integration, including live boss encounters, remains unverified.

## Installation

The runtime distribution consists of two ZIPs:

1. **Skypod-1.0.0-Main-Mod.zip** — install and enable through MO2 or Vortex. Install the Skyrim VR versions of SKSEVR, VR Address Library, PrismaUI, SkyUI and MCM Helper, following their own instructions.
2. **Skypod-Studio-1.0.0-win-x64.zip** — extract the entire archive to a tools folder outside the game and mod-manager staging folders. The .NET runtime, encoder and map assets are included. Keep the supplied files together.

For **MO2**, add `Skypod.Studio.exe` in MO2's executable settings, set **Start in** to its extracted folder, and launch it through the profile you play. Build into a dedicated `Skypod Output` mod folder, then enable that output and `Skypod.esp`.

For **Vortex**, deploy your mods first, run Studio normally, select Vortex and the deployed Skyrim VR `Data` folder, and build outside the game/staging folders. Install Studio's generated `Skypod Output-Vortex.zip`, let it win relevant conflicts, and deploy again.

Keep one generated SkyPod output enabled. Rebuild after changing soundtrack assignments or the source modlist, then restart Skyrim VR through SKSEVR. Players installing a finished soundtrack pack do not need Studio.

Detailed instructions: [main mod](packaging/BASE_MOD_README.txt), [Studio](packaging/STUDIO_README.txt).

## Source and building

The application source is available directly in this repository:

- `src/Skypod.Core` — library, conversion, routing and plugin generation.
- `src/Skypod.Studio` — Windows desktop Studio and Atlas.
- `src/Skypod.Plugin` — SKSEVR plugin, playback and PrismaUI integration.
- `mod/Data` and `scripts/source` — in-game interface and MCM.
- `tools/Skypod.BuildTool` — base plugin generation.

The [source distributions](source-distributions/) contain the complete matching SkyPod/dependency source snapshot and the separate modified FFmpeg encoder source. Extract `Skypod-1.0.0-Source.zip` and follow `SkyPod/BUILD-SOURCE.txt` for the verified source layout. The [build instructions](BUILD-SOURCE.txt) and [dependency source index](SOURCE-README.txt) are also available here.

Studio uses .NET SDK 10.0.301 and targets net9.0-windows. The native build uses Visual Studio C++, CMake, Ninja and the dependency versions included in the source archive. Runtime assets and the prebuilt encoder can be copied from the Studio download as described in the build instructions.

## License

Original SkyPod code is **GPL-3.0-only**. Copyright (C) 2026 **Wondernutts**. See [LICENSE.txt](LICENSE.txt), [NOTICE.txt](NOTICE.txt) and [SOURCES.txt](SOURCES.txt).

Third-party components retain their original licenses and attribution. The separately executed modified FFmpeg encoder remains **LGPL-2.1-or-later** and is excluded from SkyPod's GPL grant. Its source and notices are included in its own source archive.
