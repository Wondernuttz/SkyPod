namespace Skypod.Core;

public static class ModManagerBuildValidator
{
    public static void Validate(BuildSettings settings, string operation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (settings.ModManager == ModManagerKind.ModOrganizer2)
        {
            if (settings.RequireModOrganizer && !ModOrganizerDetector.IsRunningUnderUsvfs())
            {
                throw new InvalidOperationException(
                    $"Skypod Studio is not running through Mod Organizer 2. Launch it as an MO2 executable so {operation} sees the active profile and virtual file winners.");
            }
            return;
        }

        if (!SkyrimVrDataLocator.IsValidDataFolder(settings.TargetDataFolder))
        {
            throw new InvalidOperationException(
                $"Vortex mode requires Skyrim VR's deployed Data folder for {operation}. Deploy mods in Vortex, then select the folder containing Skyrim.esm.");
        }
    }
}
