using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Skypod.Core;

public static class SkypodMcmPlugin
{
    public const uint ToggleHotkeyGlobalId = 0x800;
    public const uint McmQuestId = 0x801;
    public const short DefaultToggleScancode = 0x42; // F8

    private static readonly ModKey Skyrim = ModKey.FromNameAndExtension("Skyrim.esm");

    public static void AddRecords(SkyrimMod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);

        if (!mod.ModHeader.MasterReferences.Any(master => master.Master == Skyrim))
        {
            mod.ModHeader.MasterReferences.Add(new MasterReference { Master = Skyrim });
        }

        var globalKey = new FormKey(mod.ModKey, ToggleHotkeyGlobalId);
        if (!mod.Globals.ContainsKey(globalKey))
        {
            mod.Globals.Add(new GlobalShort(globalKey, SkyrimRelease.SkyrimVR)
            {
                EditorID = "Skypod_ToggleHotkey",
                Data = DefaultToggleScancode,
                FormVersion = 44
            });
        }

        var questKey = new FormKey(mod.ModKey, McmQuestId);
        if (!mod.Quests.ContainsKey(questKey))
        {
            mod.Quests.Add(new Quest(questKey, SkyrimRelease.SkyrimVR)
            {
                Name = "Skypod_MCMQuest",
                Flags = Quest.Flag.StartGameEnabled,
                FormVersion = 44,
                NextAliasID = 0,
                QuestFormVersion = 0,
                VirtualMachineAdapter = new QuestAdapter
                {
                    Version = 5,
                    ObjectFormat = 2,
                    ExtraBindDataVersion = 2,
                    Scripts = new ExtendedList<ScriptEntry>
                    {
                        new()
                        {
                            Name = "Skypod_MCM",
                            Flags = ScriptEntry.Flag.Local
                        }
                    }
                }
            });
        }

        if (mod.ModHeader.Stats.NextFormID <= McmQuestId)
        {
            mod.ModHeader.Stats.NextFormID = McmQuestId + 1;
        }
    }

    public static void WriteBasePlugin(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var mod = new SkyrimMod(ModKey.FromFileName(Path.GetFileName(fullPath)), SkyrimRelease.SkyrimVR);
        AddRecords(mod);
        mod.BeginWrite
            .ToPath(fullPath)
            .WithNoLoadOrder()
            .WithExtraIncludedMasters(Skyrim)
            .Write();
    }
}
