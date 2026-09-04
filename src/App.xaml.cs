using Microsoft.UI.Xaml;

namespace ImageCuller2;

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
        System.Diagnostics.Debug.WriteLine($"[ImageCuller2] Unhandled: {e.Exception}");
        e.Handled = true;
    }
}
