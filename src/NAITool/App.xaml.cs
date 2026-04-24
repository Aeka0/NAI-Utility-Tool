using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using NAITool.Services;

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

    private static void InitializeLocalization(bool persistDetectedLanguage)
    {
        try
        {
            var settings = new SettingsService();
            settings.Load();

            bool shouldSaveDetectedLanguage = persistDetectedLanguage &&
                string.IsNullOrWhiteSpace(settings.Settings.LanguageCode);
            settings.Settings.LanguageCode = LocalizationService.Instance.Initialize(settings.Settings.LanguageCode);

            if (shouldSaveDetectedLanguage)
                settings.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Localization] Startup initialization failed: {ex.Message}");
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        bool isFirstRun = !SettingsService.SettingsFileExists;
        InitializeLocalization(persistDetectedLanguage: !isFirstRun);

        _splashWindow = new SplashWindow();
        _splashWindow.Activate();

        var preloadTask = SplashWindow.PreloadAsync();
        var minDisplayTask = Task.Delay(2500);
        await Task.WhenAll(preloadTask, minDisplayTask);

        _window = new MainWindow(isFirstRun);
        _window.Activate();

        _splashWindow.Close();
        _splashWindow = null;
    }

    public Window? GetMainWindow() => _window;

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[UnhandledException] {e.Exception}");
        e.Handled = true;
    }
}
