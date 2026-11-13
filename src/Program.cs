using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace MoveCopyScrap;

/// <summary>
/// Entry point, in place of the Main() WinUI would generate (DISABLE_XAML_GENERATED_MAIN in
/// the .csproj): Velopack has to run before anything else, and the generated one has no
/// place for it. The rest matches what WinUI generates.
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack's install, update and uninstall hooks run here. During those it does its
        // work and exits the process, so this must come before any window exists.
        VelopackApp.Build().Run();

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
