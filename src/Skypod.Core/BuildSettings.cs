namespace Skypod.Core;

public sealed class BuildSettings
{
    public string OutputDirectory { get; set; } = string.Empty;
    public string ModName { get; set; } = "Skypod Output";
    public string PluginName { get; set; } = "Skypod.esp";
    public string? FfmpegPath { get; set; }
    public string? XwmaEncoderPath { get; set; }
    public int BitrateKbps { get; set; } = 192;
    public bool PreserveExistingOutput { get; set; } = true;
    public bool GeneratePlugin { get; set; }
    public ModManagerKind ModManager { get; set; } = ModManagerKind.ModOrganizer2;
    public bool RequireModOrganizer { get; set; } = true;
    public string? FormIdAllocatorPath { get; set; }
    public string? TargetDataFolder { get; set; }
    public IReadOnlyList<string>? ExplicitLoadOrder { get; set; }
    public string? ModOrganizerInstanceDirectory { get; set; }
    public string? ModOrganizerModsDirectory { get; set; }
    public string? ModOrganizerProfileDirectory { get; set; }
    public string? ExclusionsPath { get; set; }
    public IReadOnlyCollection<string>? ExcludedTrackIds { get; set; }
    public IReadOnlyCollection<string>? RequiredPlugins { get; set; }
}
