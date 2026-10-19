namespace Skypod.Core;

public sealed record BossEncounter(
    string Id,
    string Name,
    string Location,
    string Plugin,
    string Group,
    string Detection,
    IReadOnlyList<string> ActorReferences,
    IReadOnlyList<string> ActorBases)
{
    public string SearchText => $"{Name} {Location} {Plugin} {Group}";
}

public sealed record BossMusicAssignment(BossEncounter Encounter, string MusicTypeFormKey);
