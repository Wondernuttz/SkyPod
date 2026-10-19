namespace Skypod.Core;

public sealed class AudioConverter(ProcessRunner processRunner)
{
    private static readonly HashSet<string> SupportedInputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav", ".wma", ".xwm"
    };

    public static bool IsSupported(string path) => SupportedInputExtensions.Contains(Path.GetExtension(path));

    public async Task<bool> ConvertAsync(
        string sourcePath,
        string destinationPath,
        BuildSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The source track could not be found.", sourcePath);
        }

        if (!IsSupported(sourcePath))
        {
            throw new NotSupportedException($"'{Path.GetExtension(sourcePath)}' is not a supported audio format.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        destinationPath = Path.GetFullPath(destinationPath);
        if (Path.GetFullPath(sourcePath).Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The source track and output file must be different files.");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryOutput = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (Path.GetExtension(sourcePath).Equals(".xwm", StringComparison.OrdinalIgnoreCase))
            {
                XwmValidator.Validate(sourcePath);
                File.Copy(sourcePath, temporaryOutput);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryOutput, destinationPath, true);
                return false;
            }
            if (settings.BitrateKbps is not (48 or 96 or 160 or 192))
                throw new ArgumentOutOfRangeException(nameof(settings), "Supported audio bitrates are 48, 96, 160 and 192 kbps.");
            var encoder = ToolLocator.FindAudioEncoder()
                ?? throw new FileNotFoundException("SkyPod's included audio encoder is missing. Extract the complete Studio ZIP, including its tools folder.");
            await processRunner.RunAsync(encoder, [
                "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                "-i", sourcePath, "-map", "0:a:0", "-c:a", "wmav2",
                "-b:a", checked(settings.BitrateKbps * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-ar", "44100", "-ac", "2", "-af", "apad=pad_len=2048", "-f", "xwma", temporaryOutput
            ], cancellationToken);
            XwmValidator.Validate(temporaryOutput);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryOutput, destinationPath, true);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
        }
    }
}
