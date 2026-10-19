SKYPOD STUDIO 1.0.0
==================

Studio builds your music files and Skypod.esp. Install the SkyPod main mod
separately through Vortex or Mod Organizer 2.
No songs are included. Use your own music or a finished SkyPod soundtrack pack.

SETUP
1. Extract the whole Studio ZIP to a folder such as C:\Modding\SkyPod Studio.
   Keep it outside Skyrim's game folder and your mod manager's mods/staging
   folder. Do not install the Studio ZIP as a game mod.
2. Keep the files and subfolders together. Run Skypod.Studio.exe.
   This Windows x64 build includes its .NET runtime.
3. Follow the steps below for your mod manager. Audio conversion is included.

AUDIO IMPORT
Audio conversion is included. Studio imports AAC, FLAC, M4A, MP3, OGG Vorbis,
Opus, WAV and WMA and produces Skyrim-compatible XWM files. Valid existing
XWM files are copied without re-encoding. Choose 48, 96, 160 or 192 kbps.

Keep tools\Skypod.AudioEncoder.exe beside Studio in its supplied tools folder.
No separate encoder download or path selection is required. The bundled
encoder handles conversion and preview without Windows media codecs.
Wine/Proton compatibility has not been validated for this release.

MOD ORGANIZER 2
1. Install and enable the SkyPod main mod and its requirements in MO2.
2. Open MO2's executable settings and add an executable named SkyPod Studio.
   Binary: select Skypod.Studio.exe from your extracted Studio folder.
   Start in: select that same folder. Leave Arguments empty.
3. Select the profile you use to play Skyrim VR. Select SkyPod Studio in MO2's
   executable list and click Run. Always launch Studio through MO2 for this
   setup so it can read the profile's enabled mods.
4. In Studio, set Mod manager to Mod Organizer 2.
5. Choose a dedicated output folder inside that MO2 instance's mods folder,
   for example C:\Modding\MO2\mods\Skypod Output. Use an empty folder for the
   first build. The folder name must contain Skypod. Builds replace its contents.
6. Import your music, set each song's category and locations, and save the library.
   Keep Generate load-order-aware Skypod.esp checked. Click BUILD SKYPOD OUTPUT.
7. Close Studio. Refresh MO2's mod list and enable Skypod Output. Place it after
   the SkyPod main mod and the music mods used to build it so its files win
   conflicts. Make sure Skypod.esp is enabled in the Plugins tab.
8. Start Skyrim VR through SKSEVR using the same MO2 profile.

VORTEX
1. Install and enable the SkyPod main mod and its requirements in Vortex.
   Deploy your mods before opening Studio or refreshing its location list.
2. Run Skypod.Studio.exe normally. Set Mod manager to Vortex.
3. For Deployed Skyrim VR Data folder, select the game's Data folder containing
   Skyrim.esm. Do not select Vortex's mod staging folder.
4. Choose a dedicated output folder outside the game and Vortex staging folders,
   for example C:\Modding\Skypod Output. Use an empty folder for the first build.
   The folder name must contain Skypod. Builds replace its contents.
5. Import your music, set each song's category and locations, and save the library.
   Keep Generate load-order-aware Skypod.esp checked. Click BUILD SKYPOD OUTPUT.
6. Studio creates a ZIP beside the output folder, such as Skypod Output-Vortex.zip.
   Install that ZIP in Vortex and enable it. Set the generated output to load
   after the SkyPod main mod and the music mods used to build it when resolving
   file conflicts. Make sure Skypod.esp is enabled in the Plugins tab.
7. Deploy mods again. Start Skyrim VR through SKSEVR.
8. After rebuilding, install the new output ZIP in place of the old output and
   deploy again. Keep only one generated SkyPod output enabled.

USING THE ATLAS
Select a song, then click the location panel to open the Atlas. Click a place
to assign it. Click it again to remove the assignment. Gold rings show assigned
places; white brackets show the place you are viewing.

Scroll into a city to browse its buildings and scroll back out to the world map.
Opening a city map does not assign music. Full interior covers the building's
linked rooms and cellars. Use the scope controls for exterior or whole-location
music. Click APPLY TO SONG to save, including when clearing the last location.
Rebuild your output after changing assignments.

The AtlasTerrain folder included beside Studio contains the terrain and city
map images. Keep it with the tool when moving or updating Studio. Locations
come from your enabled plugins, so added or moved buildings may differ from
the background. Places without terrain imagery use a location diagram.

MODLIST AUTHORS
Open MODLIST AUTHOR to name your soundtrack, add credits and list dependencies.
BUILD + EXPORT PLAYER ZIP builds a soundtrack for players to install with the
main mod. Players do not need Studio or the encoders to use a finished pack.
Wabbajack authors can include that output in their list using a supported
download source. Use one generated SkyPod output per modlist.

EXPORT AUTHOR PROJECT shares an editable project. Include imported audio only
when you can redistribute it, or leave it unchecked to share the settings.
IMPORT AND MERGE PROJECT combines projects before building the final output.

IF SOMETHING DOES NOT WORK
- Encoder missing: extract the complete Studio ZIP, including its tools folder.
- MO2 not detected: close Studio and launch it from MO2's executable list.
- Vortex Data folder rejected: select the game's Data folder with Skyrim.esm.
- Missing modded places: check the active MO2 profile, or deploy in Vortex, then
  click Refresh locations. Rebuild after changing assignments or enabled mods.
- Music has not changed in game: enable the generated output and Skypod.esp.
  Check file conflicts. Vortex users must deploy the rebuilt output.
- Keep your source audio and saved library. Studio needs them for future builds.

BOSS MUSIC

Select a song, then click the gold dragon under Bosses. Search by boss, location, or mod and check the fights you want. Apply the selection and build SkyPod Output.

The list uses Boss markers from your active load order, including mods that use those markers. Named story encounters are listed separately. Skyrim also tags some residents as bosses; these appear under Other game-tagged actors and only trigger music during hostile combat.

Leave "Also keep this song's existing location or category assignments" unchecked for boss music only. Check it to use the song in both places. Multiple songs assigned to a boss share its playlist.

Update both SkyPod Studio and the SkyPod game mod for this feature. Boss assignments travel with exported author projects and player soundtrack packs. Players install the generated soundtrack as usual in MO2 or Vortex.

LICENSE AND SOURCE
Original SkyPod software is GPL-3.0-only. See LICENSE.txt, NOTICE.txt and
SOURCES.txt. Third-party components retain their existing licenses.
