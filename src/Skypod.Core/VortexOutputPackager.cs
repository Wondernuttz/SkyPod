namespace Skypod.Core;

public static class VortexOutputPackager
{
    public static string Create(string outputDirectory, string? archivePath = null) =>
        PlayerSoundtrackPackager.Create(outputDirectory,
            archivePath ?? Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + "-Vortex.zip",
            requirePlugin: false);
}
