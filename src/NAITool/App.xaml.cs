using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace NAITool;

public partial class App : Application
{
    private Window? _window;
    private Window? _splashWindow;

    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _splashWindow = new SplashWindow();
        _splashWindow.Activate();

        var preloadTask = SplashWindow.PreloadAsync();
        var minDisplayTask = Task.Delay(2500);
        await Task.WhenAll(preloadTask, minDisplayTask);

        _window = new MainWindow();
        _window.Activate();

        _splashWindow.Close();
        _splashWindow = null;
    }

    public Window? GetMainWindow() => _window;

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[未处理异常] {e.Exception}");
        e.Handled = true;
    }
}
