using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace MoveCopyScrap;

internal static class Startup
{
    /// <summary>
    /// Tells the Windows App SDK where its own binaries are.
    ///
    /// Only matters for a single-file publish: everything is unpacked to a folder under
    /// %TEMP% and the runtime cannot infer that location for itself, so without this the
    /// app fails to find the Windows App SDK at startup. A module initializer is used
    /// because it has to be set before program entry, and these run before Main.
    ///
    /// Harmless in a normal folder publish, where it just names the folder the .exe is in.
    /// </summary>
    [ModuleInitializer]
    internal static void SetWindowsAppRuntimeBaseDirectory()
    {
        try
        {
            Environment.SetEnvironmentVariable(
                "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        }
        catch
        {
            // Nothing useful to do this early; a normal publish does not need it anyway.
        }
    }
}

/// <summary>
/// Application entry point. The app is unpackaged (WindowsPackageType=None), so the
/// Windows App SDK bootstrapper is initialised automatically by the generated Main().
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // A bad file or a codec hiccup should never take the whole app down mid-cull.
        System.Diagnostics.Debug.WriteLine($"[MoveCopyScrap] Unhandled: {e.Exception}");
        e.Handled = true;
    }
}
