using Microsoft.UI.Xaml;

namespace KVMate.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "KVMate" };
        _window.Activate();
    }
}
