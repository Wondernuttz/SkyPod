using System.ComponentModel;

namespace Skypod.Core;

public enum TrackCategory
{
    [Description("Exploration: Day")]
    ExplorationDay,

    [Description("Exploration: Night")]
    ExplorationNight,

    [Description("Exploration: Any Time")]
    ExplorationAnyTime,

    Dungeon,
    Town,
    City,
    Tavern,
    Castle,

    [Description("Combat: General")]
    Combat,

    [Description("Combat: Boss")]
    BossCombat,

    [Description("Combat: Dragon")]
    DragonCombat,

    [Description("Event / Special")]
    Special,

    [Description("Main Menu")]
    MainMenu
}
