using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace NAITool;

public sealed class SplashWindow : Window
{
    private const int DefaultSplashWidth = 780;
    private const int DefaultSplashHeight = 446;
    private const string SplashResourcePrefix = "NAITool.splash.";
    private const string SplashTitleResourceName = SplashResourcePrefix + "SplashTitle.png";
    private readonly Border _rootBorder;
    private readonly UIElement _titleImage;
    private bool _titleAnimationStarted;

    public SplashWindow()
    {
        string splashBackgroundResourceName = PickRandomBackgroundResourceName();
        var splashSize = GetSplashWindowSize(splashBackgroundResourceName);

        var backgroundImage = new Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        backgroundImage.Source = LoadEmbeddedBitmap(splashBackgroundResourceName);

        var titleImage = new Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
        };
        titleImage.Source = LoadEmbeddedBitmap(SplashTitleResourceName);

        var layerGrid = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            Children = { backgroundImage, titleImage },
        };

        _rootBorder = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            Child = layerGrid,
        };
        _titleImage = titleImage;

        Content = _rootBorder;

        if (AppWindow != null)
        {
            AppWindow.Resize(new SizeInt32(splashSize.Width, splashSize.Height));

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            CenterOnScreen();
        }

        ApplyBorderlessChrome();
        _rootBorder.Loaded += (_, _) =>
        {
            if (_titleAnimationStarted) return;
            _titleAnimationStarted = true;
            StartTitleFadeIn(_titleImage);
        };
    }

    /// <summary>
    /// 在 Splash 展示期间预热文件系统缓存和 .NET 运行时，
    /// 使后续 MainWindow 的初始化更快。
    /// </summary>
    public static async Task PreloadAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var root = AppPathResolver.AppRootDir;

                WarmFileCache(Path.Combine(root, "assets", "tagsheet"), "*.csv");
                WarmFileCache(Path.Combine(root, "user", "fxpresets"), "*.json");

                WarmSingleFile(Path.Combine(root, "user", "config", "settings.json"));
                WarmSingleFile(Path.Combine(root, "user", "config", "apiconfig.json"));
            }
            catch
            {
            }
        });
    }

    private static void WarmFileCache(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string file in Directory.GetFiles(directory, pattern))
            _ = File.ReadAllBytes(file);
    }

    private static void WarmSingleFile(string path)
    {
        if (File.Exists(path))
            _ = File.ReadAllBytes(path);
    }

    private static SizeInt32 GetSplashWindowSize(string resourceName)
    {
        if (!TryGetPngDimensions(resourceName, out int imageWidth, out int imageHeight))
            return new SizeInt32(DefaultSplashWidth, DefaultSplashHeight);

        return new SizeInt32(imageWidth, Math.Max(1, imageHeight));
    }

    private static bool TryGetPngDimensions(string resourceName, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            using Stream? stream = typeof(SplashWindow).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return false;

            if (stream.Length < 24)
                return false;

            var header = new byte[24];
            if (stream.Read(header, 0, header.Length) != header.Length)
                return false;

            // PNG IHDR 中第 16-23 字节为大端宽高。
            width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    private void CenterOnScreen()
    {
        var displayArea = DisplayArea.GetFromWindowId(
            AppWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea == null) return;

        var workArea = displayArea.WorkArea;
        int x = (workArea.Width - AppWindow.Size.Width) / 2 + workArea.X;
        int y = (workArea.Height - AppWindow.Size.Height) / 2 + workArea.Y;
        AppWindow.Move(new PointInt32(x, y));
    }

    private void ApplyBorderlessChrome()
    {
        var hwnd = WindowNative.GetWindowHandle(this);

        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        style &= ~(NativeMethods.WS_THICKFRAME
                  | NativeMethods.WS_CAPTION
                  | NativeMethods.WS_SYSMENU);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style);

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);

        int cornerPref = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPref, sizeof(int));
    }

    private static string PickRandomBackgroundResourceName()
    {
        var resourceNames = typeof(SplashWindow).Assembly.GetManifestResourceNames()
            .Where(x => x.StartsWith(SplashResourcePrefix + "SplashBG", StringComparison.Ordinal)
                        && x.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return resourceNames.Length > 0
            ? resourceNames[Random.Shared.Next(resourceNames.Length)]
            : SplashResourcePrefix + "SplashBG1.png";
    }

    private static BitmapImage? LoadEmbeddedBitmap(string resourceName)
    {
        using Stream? stream = typeof(SplashWindow).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;

        var bitmap = new BitmapImage();
        bitmap.SetSource(stream.AsRandomAccessStream());
        return bitmap;
    }

    private void StartTitleFadeIn(UIElement titleImage)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(titleImage);
        visual.Opacity = 0.0f;
        titleImage.Opacity = 1;

        var compositor = visual.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0.0f, 0.0f);
        animation.InsertKeyFrame(1.0f, 1.0f);
        animation.Duration = TimeSpan.FromMilliseconds(1000);
        animation.DelayTime = TimeSpan.FromMilliseconds(150);
        visual.StartAnimation("Opacity", animation);
    }

    private static class NativeMethods
    {
        public const int GWL_STYLE = -16;
        public const int WS_THICKFRAME = 0x00040000;
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_SYSMENU = 0x00080000;

        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_FRAMECHANGED = 0x0020;

        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd,
            int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
