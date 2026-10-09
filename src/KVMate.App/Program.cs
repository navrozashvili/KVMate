using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace KVMate.App;

/// <summary>The entry point, replacing the one the XAML compiler generates.</summary>
/// <remarks>
/// It exists for one reason: the single-instance check has to happen before
/// <see cref="Application.Start"/>, because a second copy must exit without creating an
/// <see cref="Application"/>, a tray icon, or a second engine fighting the first over the speaker.
/// Everything else reproduces the generated entry point, which is still compiled as
/// <c>XamlGeneratedProgram.XamlGeneratedMain</c> and is the reference for this body.
/// </remarks>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Declared before the try and disposed in the finally: the claim has to outlive the whole
        // run and be released however it ends.
        SingleInstance? instance = null;

        try
        {
            instance = SingleInstance.TryAcquire();

            if (instance is null)
            {
                // Another copy owns the session. Ask it to open its settings window and stop.
                SingleInstance.SignalRunningInstance();
                return;
            }

            var claim = instance;

            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start(parameters =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);

                // Discarded rather than stored: the XAML runtime roots the Application it is handed,
                // and Application.Start does not return until the app exits.
                _ = new App(claim);
            });
        }
        finally
        {
            instance?.Dispose();
        }
    }
}
