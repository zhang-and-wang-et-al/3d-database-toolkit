using System.Windows;
using System.Windows.Threading;
using SceneKit;

namespace SceneInspector;

public partial class App : Application
{
    void OnStartup(object sender, StartupEventArgs e)
    {
        ImageResizer.Current = new WpfImageResizer();
        // remote desktops / broken GPU drivers: SCENEINSPECTOR_SOFTWARE=1 draws the window without the GPU
        if (Environment.GetEnvironmentVariable("SCENEINSPECTOR_SOFTWARE") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        string? path = null, osgconv = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--osgconv" && i + 1 < e.Args.Length) osgconv = e.Args[++i];
            else if (!e.Args[i].StartsWith("--")) path = e.Args[i];
        }
        // old checkpoints (14 days, 5 GB in all) go in the background; the window's session is created later, so it is never pruned here
        Task.Run(() =>
        {
            try { new CheckpointStore(CheckpointStore.DefaultDir).Prune(TimeSpan.FromDays(14), 5L << 30, null); }
            catch { }
        });
        var w = new MainWindow(osgconv);
        w.Show();
        if (path != null) w.OpenPath(path);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialogs.Error(MainWindow, "Unexpected error", e.Exception);
        e.Handled = true;
    }
}
