using System.Windows;
using Skypod.Core;

namespace Skypod.Studio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Window window;
        if (e.Args is ["--atlas-preview", var path])
        {
            // The preview is launched before WPF's dispatcher begins pumping.
            // Load off the UI synchronization context so async file I/O cannot deadlock startup.
            var index = Task.Run(() => SoundtrackWorldIndexStore.TryLoadAsync(path)).GetAwaiter().GetResult()
                        ?? SoundtrackWorldIndex.Empty;
            window = new MapPickerWindow(
                index,
                [],
                [new AtlasSongOption("preview", "Atlas visual verification", string.Empty, TrackCategory.ExplorationDay, true)]);
        }
        else
        {
            window = new MainWindow();
        }
        MainWindow = window;
        window.Show();
    }
}
