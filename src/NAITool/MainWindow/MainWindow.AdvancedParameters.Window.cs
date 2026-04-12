using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Controls;
using NAITool.Models;
using NAITool.Services;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Runtime.InteropServices.WindowsRuntime;

namespace NAITool;

public sealed partial class MainWindow
{
    private bool IsAdvancedWindowOpen => _advParamsWindow != null;
    private bool _isSyncingSidebarAdv;

    private void SetupSidebarAdvancedSync()
    {
        NbSeed.ValueChanged += (_, _) =>
        {
            UpdateSeedRandomizeButtonStyle();
            if (_isSyncingSidebarAdv || !IsAdvancedWindowOpen) return;
            _isSyncingSidebarAdv = true;
            _advNbSeed.Value = NbSeed.Value;
            _isSyncingSidebarAdv = false;
        };

        ChkVariety.Click += (_, _) =>
        {
            if (_isSyncingSidebarAdv || !IsAdvancedWindowOpen) return;
            _isSyncingSidebarAdv = true;
            _advChkVariety.IsChecked = ChkVariety.IsChecked;
            _isSyncingSidebarAdv = false;
        };
    }

    private void SyncSidebarToAdvanced()
    {
        if (!IsAdvancedWindowOpen) return;
        _isSyncingSidebarAdv = true;
        _advNbSeed.Value = NbSeed.Value;
        _advChkVariety.IsChecked = ChkVariety.IsChecked;
        _advNbMaxWidth.Value = _customWidth;
        _advNbMaxHeight.Value = _customHeight;
        _isSyncingSidebarAdv = false;
    }

    private void OnAdvancedParams(object sender, RoutedEventArgs e)
    {
        if (_advParamsWindow != null)
        {
            _advParamsWindow.Activate();
            return;
        }
        ShowAdvancedParamsWindow();
    }

    private void ShowAdvancedParamsWindow()
    {
        SyncUIToParams();
        var p = CurrentParams;
        int maxSteps = _settings.Settings.AccountAssetProtectionMode ? 28 : 50;

        var window = new Window();
        window.Title = L("dialog.advanced.title");
        if (IsWindows11OrGreater())
        window.SystemBackdrop = new DesktopAcrylicBackdrop();
        window.ExtendsContentIntoTitleBar = true;

        _advCboSize = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 32,
            Visibility = Visibility.Collapsed,
            FontFamily = UiTextFontFamily,
        };
        _advCboSampler = new ComboBox { Header = L("panel.sampler"), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, FontFamily = UiTextFontFamily };
        _advCboSchedule = new ComboBox { Header = L("panel.scheduler"), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, FontFamily = UiTextFontFamily };
        _advNbSteps = new NumberBox
        {
            Header = L("panel.steps"), Minimum = 1,
            Maximum = maxSteps,
            Value = Math.Min(p.Steps, maxSteps),
            MinHeight = 32,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        _advNbSeed = new NumberBox
        {
            Header = L("panel.seed"), Minimum = 0, Value = p.Seed,
            MinHeight = 32,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        _advNbScale = new NumberBox
        {
            Header = L("dialog.advanced.cfg_scale"), Minimum = 0, Maximum = 10, Value = p.Scale,
            SmallChange = 0.1, LargeChange = 1,
            MinHeight = 32,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
                { FractionDigits = 1, IntegerDigits = 1 },
        };
        _advNbScale.ValueChanged += OnAdvScaleValueChanged;

        _advSliderCfgRescale = new Slider
        {
            Minimum = 0, Maximum = 1, StepFrequency = 0.02, Value = p.CfgRescale,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _advTxtCfgRescale = new TextBlock
        {
            Text = $"{p.CfgRescale:F2}", MinWidth = 36, TextAlignment = TextAlignment.Right,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        _advSliderCfgRescale.ValueChanged += (_, args) => _advTxtCfgRescale.Text = $"{args.NewValue:F2}";

        _advChkVariety = new CheckBox { Content = L("dialog.advanced.variety"), IsChecked = p.Variety };
        _advChkVariety.Visibility = Visibility.Visible;
        _advChkSmea = new CheckBox
        {
            Content = "SMEA",
            IsChecked = p.Sm,
            Visibility = (_currentMode == AppMode.ImageGeneration && IsCurrentModelV3())
                ? Visibility.Visible : Visibility.Collapsed,
        };
        _advNbSeed.ValueChanged += (_, _) =>
        {
            if (_isSyncingSidebarAdv) return;
            _isSyncingSidebarAdv = true;
            NbSeed.Value = _advNbSeed.Value;
            _isSyncingSidebarAdv = false;
        };
        _advChkVariety.Click += (_, _) =>
        {
            if (_isSyncingSidebarAdv) return;
            _isSyncingSidebarAdv = true;
            ChkVariety.IsChecked = _advChkVariety.IsChecked;
            _isSyncingSidebarAdv = false;
        };

        _advCboQuality = new ComboBox { Header = L("dialog.advanced.quality"), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, FontFamily = UiTextFontFamily };
        _advCboQuality.Items.Add(CreateTextComboBoxItem(L("common.yes")));
        _advCboQuality.Items.Add(CreateTextComboBoxItem(L("common.no")));
        _advCboQuality.SelectedIndex = p.QualityToggle ? 0 : 1;
        if (_advCboQuality.SelectedIndex < 0) _advCboQuality.SelectedIndex = 0;
        _advCboUcPreset = new ComboBox { Header = L("dialog.advanced.negative_quality"), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, FontFamily = UiTextFontFamily };
        _advCboUcPreset.Items.Add(CreateTextComboBoxItem(L("dialog.advanced.uc_preset.full")));
        _advCboUcPreset.Items.Add(CreateTextComboBoxItem(L("dialog.advanced.uc_preset.light")));
        _advCboUcPreset.Items.Add(CreateTextComboBoxItem(L("dialog.advanced.uc_preset.none")));
        _advCboUcPreset.SelectedIndex = p.UcPreset;
        if (_advCboUcPreset.SelectedIndex < 0) _advCboUcPreset.SelectedIndex = 0;

        _advNbMaxWidth = new NumberBox
        {
            Minimum = 64, Maximum = 2048, Value = _customWidth,
            SmallChange = 64, LargeChange = 64,
            MinHeight = 32,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        _advNbMaxHeight = new NumberBox
        {
            Minimum = 64, Maximum = 2048, Value = _customHeight,
            SmallChange = 64, LargeChange = 64,
            MinHeight = 32,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        _advNbMaxWidth.ValueChanged += OnAdvMaxSizeValueChanged;
        _advNbMaxHeight.ValueChanged += OnAdvMaxSizeValueChanged;

        _advNbSteps.ValueChanged += OnAdvStepsValueChanged;

        foreach (var s in GetAvailableSamplersForModel(p.Model)) _advCboSampler.Items.Add(CreateTextComboBoxItem(s));
        foreach (var s in AvailableSchedules) _advCboSchedule.Items.Add(CreateTextComboBoxItem(s));
        foreach (var preset in MaskCanvasControl.CanvasPresets)
            _advCboSize.Items.Add(CreateTextComboBoxItem(preset.Label));

        _advCboSize.SelectedIndex = CboSize.SelectedIndex >= 0 ? CboSize.SelectedIndex : 0;
        var availableSamplers = GetAvailableSamplersForModel(p.Model);
        p.Sampler = NormalizeSamplerForModel(p.Sampler, p.Model);
        _advCboSampler.SelectedIndex = Array.IndexOf(availableSamplers, p.Sampler);
        if (_advCboSampler.SelectedIndex < 0) _advCboSampler.SelectedIndex = 0;
        _advCboSchedule.SelectedIndex = Array.IndexOf(AvailableSchedules, p.Schedule);
        if (_advCboSchedule.SelectedIndex < 0) _advCboSchedule.SelectedIndex = 0;

        SuppressNumberBoxClearButton(_advNbSteps);
        SuppressNumberBoxClearButton(_advNbSeed);
        SuppressNumberBoxClearButton(_advNbScale);
        SuppressNumberBoxClearButton(_advNbMaxWidth);
        SuppressNumberBoxClearButton(_advNbMaxHeight);

        var sizeLabel = new TextBlock { Text = L("panel.size"), Margin = new Thickness(0, 0, 0, 8) };

        _advMaxSizePanel = new Grid { Visibility = Visibility.Visible };
        _advMaxSizePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _advMaxSizePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _advMaxSizePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _advMaxSizePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_advNbMaxWidth, 0);
        var timesBtn = new Button
        {
            Content = "×",
            MinWidth = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        timesBtn.Click += OnAdvSwapSizeDimensions;
        Grid.SetColumn(timesBtn, 1);
        Grid.SetColumn(_advNbMaxHeight, 3);
        _advMaxSizePanel.Children.Add(_advNbMaxWidth);
        _advMaxSizePanel.Children.Add(timesBtn);
        _advMaxSizePanel.Children.Add(_advNbMaxHeight);

        var sizeStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
        sizeStack.Children.Add(sizeLabel);
        sizeStack.Children.Add(_advCboSize);
        sizeStack.Children.Add(_advMaxSizePanel);

        // Custom title bar with grip and label
        var titleBarGrid = new Grid { Height = 40, Padding = new Thickness(12, 0, 0, 0) };
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gripIcon = new FontIcon
        {
            FontFamily = SymbolFontFamily, Glyph = "\uE76F", FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5,
        };
        Grid.SetColumn(gripIcon, 0);
        var titleText = new TextBlock
        {
            Text = L("dialog.advanced.title"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(titleText, 1);
        titleBarGrid.Children.Add(gripIcon);
        titleBarGrid.Children.Add(titleText);

        var paramsGrid = new Grid { Padding = new Thickness(16, 4, 16, 16), ColumnSpacing = 10, RowSpacing = 10 };
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++)
            paramsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(sizeStack, 0); Grid.SetColumn(sizeStack, 0);
        _advNbSteps.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetRow(_advNbSteps, 0); Grid.SetColumn(_advNbSteps, 1);

        Grid.SetRow(_advCboQuality, 1); Grid.SetColumn(_advCboQuality, 0);
        Grid.SetRow(_advCboUcPreset, 1); Grid.SetColumn(_advCboUcPreset, 1);

        Grid.SetRow(_advCboSampler, 2); Grid.SetColumn(_advCboSampler, 0);
        Grid.SetRow(_advCboSchedule, 2); Grid.SetColumn(_advCboSchedule, 1);

        Grid.SetRow(_advNbSeed, 3); Grid.SetColumn(_advNbSeed, 0);
        Grid.SetRow(_advNbScale, 3); Grid.SetColumn(_advNbScale, 1);

        var rescaleStack = new StackPanel();
        Grid.SetRow(rescaleStack, 4); Grid.SetColumn(rescaleStack, 0); Grid.SetColumnSpan(rescaleStack, 2);
        var rescaleLabel = new TextBlock
        {
            Text = L("dialog.advanced.cfg_rescale"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
        };
        var rescaleGrid = new Grid();
        rescaleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rescaleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_advSliderCfgRescale, 0);
        Grid.SetColumn(_advTxtCfgRescale, 1);
        rescaleGrid.Children.Add(_advSliderCfgRescale);
        rescaleGrid.Children.Add(_advTxtCfgRescale);
        rescaleStack.Children.Add(rescaleLabel);
        rescaleStack.Children.Add(rescaleGrid);

        Grid.SetRow(_advChkVariety, 5); Grid.SetColumn(_advChkVariety, 0);
        Grid.SetRow(_advChkSmea, 5); Grid.SetColumn(_advChkSmea, 1);

        paramsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        paramsGrid.Children.Add(sizeStack);
        paramsGrid.Children.Add(_advNbSteps);
        paramsGrid.Children.Add(_advCboQuality);
        paramsGrid.Children.Add(_advCboUcPreset);
        paramsGrid.Children.Add(_advCboSampler);
        paramsGrid.Children.Add(_advCboSchedule);
        paramsGrid.Children.Add(_advNbSeed);
        paramsGrid.Children.Add(_advNbScale);
        paramsGrid.Children.Add(rescaleStack);
        paramsGrid.Children.Add(_advChkVariety);
        paramsGrid.Children.Add(_advChkSmea);

        var rootPanel = new StackPanel();
        rootPanel.Children.Add(titleBarGrid);
        rootPanel.Children.Add(paramsGrid);
        rootPanel.IsTabStop = true;
        rootPanel.Loaded += (_, _) => rootPanel.Focus(FocusState.Programmatic);

        window.Content = rootPanel;
        window.SetTitleBar(titleBarGrid);

        if (this.Content is FrameworkElement mainRoot)
            ((FrameworkElement)window.Content).RequestedTheme = mainRoot.RequestedTheme;

        _advParamsWindow = window;
        _advRootPanel = rootPanel;
        _advTitleBarGrid = titleBarGrid;

        UpdateAdvSizeWarningVisuals();
        UpdateAdvStepsWarning();

        var appWindow = GetAppWindowForWindow(window);
        if (appWindow != null)
        {
            double dpiScale = this.Content.XamlRoot?.RasterizationScale ?? 1.0;
            int physW = (int)(540 * dpiScale);
            int physH = (int)(480 * dpiScale);
            appWindow.Resize(new SizeInt32(physW, physH));
            appWindow.SetIcon("NAIT.ico");
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
            }
            if (AppWindow != null)
            {
                var mainPos = AppWindow.Position;
                var mainSize = AppWindow.Size;
                appWindow.Move(new Windows.Graphics.PointInt32(
                    mainPos.X + mainSize.Width / 2 - physW / 2,
                    mainPos.Y + mainSize.Height / 2 - physH / 2));
            }
        }

        window.Closed += (_, _) =>
        {
            SaveAdvancedPanelToSettings();
            _advParamsWindow = null;
            _advRootPanel = null;
            _advTitleBarGrid = null;
        };

        bool isDark = IsDarkTheme();
        ApplyWindowChrome(window, isDark, titleBarGrid, rootPanel);
        window.Activated += (_, _) => ApplyWindowChrome(window, IsDarkTheme(), titleBarGrid, rootPanel);
        window.Activate();
    }

    private static AppWindow? GetAppWindowForWindow(Window window)
    {
        var hWnd = WindowNative.GetWindowHandle(window);
        var wId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        return AppWindow.GetFromWindowId(wId);
    }

    private void CloseAdvancedParamsWindow()
    {
        if (_advParamsWindow != null)
        {
            _advParamsWindow.Close();
            _advParamsWindow = null;
        }
    }

    private void SaveAdvancedPanelToSettings()
    {
        if (_advCboSampler == null) return;
        var p = CurrentParams;
        p.Sampler = GetSelectedComboText(_advCboSampler) ?? p.Sampler;
        p.Schedule = GetSelectedComboText(_advCboSchedule) ?? p.Schedule;
        p.Steps = (int)_advNbSteps.Value;
        p.Seed = (int)_advNbSeed.Value;
        p.Scale = Math.Round(_advNbScale.Value, 1);
        p.CfgRescale = Math.Round(_advSliderCfgRescale.Value, 2);
        p.Variety = _advChkVariety.IsChecked == true;
        p.Sm = _currentMode == AppMode.ImageGeneration && _advChkSmea.IsChecked == true;
        p.QualityToggle = _advCboQuality.SelectedIndex == 0;
        p.UcPreset = _advCboUcPreset.SelectedIndex >= 0 ? _advCboUcPreset.SelectedIndex : 0;

        NbSeed.Value = p.Seed;
        ChkVariety.IsChecked = p.Variety;

        UpdateSizeWarningVisuals();
        _settings.Save();
    }

    private void OnAdvScaleValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double rounded = Math.Round(args.NewValue, 1);
        if (Math.Abs(rounded - args.NewValue) > 0.0001)
            sender.Value = rounded;
    }

    private void OnAdvStepsValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateAdvStepsWarning();
    }

    private void UpdateAdvStepsWarning()
    {
        if (_advNbSteps == null) return;
        int steps = (int)_advNbSteps.Value;
        bool warn = steps > 28;
        ApplyWarningStyle(_advNbSteps, warn ? SizeWarningLevel.Yellow : SizeWarningLevel.None);
        UpdateGenerateButtonWarning();
    }

    // ═══════════════════════════════════════════════════════════
    //  种子按钮
    // ═══════════════════════════════════════════════════════════

    private void OnSeedRandomize(object sender, RoutedEventArgs e)
    {
        NbSeed.Value = 0;
        if (IsAdvancedWindowOpen) _advNbSeed.Value = 0;
    }

    private void OnSeedRestore(object sender, RoutedEventArgs e)
    {
        if (_lastUsedSeed > 0)
        {
            NbSeed.Value = _lastUsedSeed;
            if (IsAdvancedWindowOpen) _advNbSeed.Value = _lastUsedSeed;
            TxtStatus.Text = Lf("seed.restored", _lastUsedSeed);
        }
        else
        {
            TxtStatus.Text = L("seed.none_to_restore");
        }
    }

    private void UpdateSeedRandomizeButtonStyle()
    {
        bool isFixed = !double.IsNaN(NbSeed.Value) && (int)NbSeed.Value != 0;
        var accentStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        BtnSeedRandomize.Style = isFixed ? accentStyle : null;
    }

    // ═══════════════════════════════════════════════════════════
    //  尺寸模式与警告
    // ═══════════════════════════════════════════════════════════
}
