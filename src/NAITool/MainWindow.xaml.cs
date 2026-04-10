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
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Controls;
using NAITool.Models;
using NAITool.Services;
using SkiaSharp;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Runtime.InteropServices.WindowsRuntime;

namespace NAITool;

public enum AppMode { ImageGeneration, Inpaint, Upscale, Effects, Inspect }
public enum PromptWeightFormat { StableDiffusion, NaiClassic, NaiNumeric }

public sealed class ResizeHandle : Microsoft.UI.Xaml.Controls.Grid
{
    public ResizeHandle()
    {
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
            Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
    }
}

public sealed partial class MainWindow : Window
{
    private readonly SettingsService _settings = new();
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly NovelAIService _naiService;
    private readonly ReverseImageTaggerService _reverseTaggerService = new();
    private readonly WildcardService _wildcardService = new();

    private const string MenuCommandNormalizePrompts = "normalize_prompts";
    private const string MenuCommandRandomStylePrompt = "random_style_prompt";
    private const string MenuCommandPromptShortcuts = "prompt_shortcuts";
    private const string MenuCommandSendToInpaint = "send_to_inpaint";
    private const string MenuCommandSendToPost = "send_to_post";
    private const string MenuCommandSendToUpscale = "send_to_upscale";
    private const string MenuCommandClearAllPrompts = "clear_all_prompts";
    private const string MenuCommandResetGenerationParams = "reset_generation_params";
    private const string MenuCommandEditRawMetadata = "edit_raw_metadata";
    private const string MenuCommandImageScramble = "image_scramble";
    private const string MenuCommandScramble = "scramble";
    private const string MenuCommandUnscramble = "unscramble";
    private const string MenuCommandUndo = "undo";
    private const string MenuCommandRedo = "redo";
    private const string MenuCommandAddPreset = "add_preset";
    private const string MenuCommandUsePreset = "use_preset";
    private const string MenuCommandClearAllEffects = "clear_all_effects";
    private const string MenuCommandApplyEffects = "apply_effects";
    private const string MenuCommandWeightConverter = "weight_converter";
    private const string MenuCommandVibeEncode = "vibe_encode";
    private const string MenuCommandWildcard = "wildcard";
    private const string MenuCommandAutomation = "automation";
    private const string MenuCommandExpandMask = "expand_mask";
    private const string MenuCommandContractMask = "contract_mask";
    private const string MenuCommandAlignImage = "align_image";
    private const string MenuCommandPromptInference = "prompt_inference";
    private const string MenuCommandMaskOps = "mask_ops";

    // ═══ 模式 ═══
    private AppMode _currentMode = AppMode.ImageGeneration;
    private bool _leftSidebarResizing;
    private double _leftSidebarDragStartX;
    private double _leftSidebarStartWidth;

    // ═══ 缩略图交互 ═══
    private bool _thumbDragging;
    private Vector2 _thumbDragStart;

    // ═══ 提示词（生图与重绘独立） ═══
    private string _genPositivePrompt = "";
    private string _genNegativePrompt = "";
    private string _genStylePrompt = "";
    private string _inpaintPositivePrompt = "";
    private string _inpaintNegativePrompt = "";
    private string _inpaintStylePrompt = "";
    private bool _isPositiveTab = true;
    private bool _isSplitPrompt;

    // ═══ 角色提示词 ═══
    private readonly List<CharacterEntry> _genCharacters = new();
    private const int MaxCharacters = 6;
    private readonly List<PromptShortcutEntry> _promptShortcuts = new();

    // ═══ 生成 ═══
    private CancellationTokenSource? _generateCts;
    private byte[]? _lastGeneratedImageBytes;
    private int _lastUsedSeed;
    private int _customWidth = 832;
    private int _customHeight = 1216;
    private bool _isUpdatingMaxSize;

    // ═══ 高级参数独立窗口 ═══
    private Window? _advParamsWindow;
    private ComboBox _advCboSize = null!;
    private ComboBox _advCboSampler = null!;
    private ComboBox _advCboSchedule = null!;
    private NumberBox _advNbSteps = null!;
    private NumberBox _advNbSeed = null!;
    private NumberBox _advNbScale = null!;
    private Slider _advSliderCfgRescale = null!;
    private TextBlock _advTxtCfgRescale = null!;
    private CheckBox _advChkVariety = null!;
    private CheckBox _advChkSmea = null!;
    private ComboBox _advCboQuality = null!;
    private ComboBox _advCboUcPreset = null!;
    private NumberBox _advNbMaxWidth = null!;
    private NumberBox _advNbMaxHeight = null!;
    private Grid _advMaxSizePanel = null!;
    private StackPanel? _advRootPanel;
    private Grid? _advTitleBarGrid;

    // ═══ 权重高亮 ═══
    private int _promptHighlightVer;
    private int _styleHighlightVer;

    // ═══ 自动生成 ═══
    private bool _autoGenRunning;
    private CancellationTokenSource? _autoGenCts;
    private bool _continuousGenRunning;
    private bool _generateRequestRunning;
    private CancellationTokenSource? _continuousGenCts;
    private int _continuousGenRemaining;
    private bool _continuousStopRequested;
    private int? _anlasBalance;
    private bool _isOpusSubscriber;
    private bool _anlasRefreshRunning;
    private bool _anlasInitialFetchDone;
    private bool _isWildcardDialogOpen;

    // ═══ 重绘预览工作流 ═══
    private CanvasBitmap? _pendingResultBitmap;
    private byte[]? _pendingResultBytes;
    private string? _cachedImageBase64;
    private string? _cachedMaskBase64;
    private string _cachedPrompt = "";
    private string _cachedNegPrompt = "";

    // ═══ 生图结果 ═══
    private byte[]? _currentGenImageBytes;
    private string? _currentGenImagePath;

    // ═══ 检视模式 ═══
    private ImageMetadata? _inspectMetadata;
    private byte[]? _inspectImageBytes;
    private string? _inspectImagePath;
    private bool _inspectRawModified;
    private MenuBarItem? _menuTools;
    private InspectPrimaryAction _inspectPrimaryAction = InspectPrimaryAction.SendMetadata;

    // ═══ 效果模式 ═══
    private readonly List<EffectEntry> _effects = new();
    private byte[]? _effectsImageBytes;
    private byte[]? _effectsPreviewImageBytes;
    private SKBitmap? _effectsSourceBitmap;
    private string? _effectsImagePath;
    private readonly Stack<EffectsWorkspaceState> _effectsUndoStack = new();
    private readonly Stack<EffectsWorkspaceState> _effectsRedoStack = new();
    private int _effectsPreviewVersion;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _effectsPreviewTimer;
    private bool _effectsPreviewQueuedFit;
    private Guid? _selectedEffectId;
    private bool _effectsApplyingHistory;
    private bool _effectsRegionDragging;
    private bool _effectsRegionResizing;
    private Point _effectsRegionDragStart;
    private double _effectsRegionStartCenterX;
    private double _effectsRegionStartCenterY;
    private double _effectsRegionStartWidth;
    private double _effectsRegionStartHeight;

    // ═══ 超分模式 ═══
    private byte[]? _upscaleInputImageBytes;
    private int _upscaleSourceWidth, _upscaleSourceHeight;
    private bool _upscaleRunning;
    private UpscaleService? _upscaleService;

    // ═══ 历史记录 ═══
    private readonly List<string> _historyFiles = new();
    private readonly Dictionary<string, List<string>> _historyByDate = new();
    private readonly List<string> _historyAvailableDates = new();
    private readonly HashSet<string> _historyAvailableDateSet = new();
    private string? _selectedHistoryDate;
    private int _historyLoadedCount;
    private const int HistoryPageSize = 40;
    private bool _historyLoadingMore;

    // ═══ 预览拖拽 ═══
    private bool _imgDragging;
    private Point _imgDragStart;
    private double _imgDragStartH, _imgDragStartV;
    private ScrollViewer? _imgDragScroller;

    // ═══ 模型列表 ═══
    private static readonly string[] GenerationModels =
    [
        "nai-diffusion-4-5-full",
        "nai-diffusion-4-5-curated",
        "nai-diffusion-4-full",
        "nai-diffusion-4-curated",
        "nai-diffusion-3",
    ];
    private static readonly string[] InpaintModels =
    [
        "nai-diffusion-4-5-full-inpainting",
        "nai-diffusion-4-5-curated-inpainting",
        "nai-diffusion-4-full-inpainting",
        "nai-diffusion-4-curated-inpainting",
        "nai-diffusion-3-inpainting",
    ];
    private static readonly string[] AvailableSamplers =
    [
        "k_euler_ancestral", "k_euler", "k_dpmpp_2m", "k_dpmpp_sde",
        "k_dpmpp_2s_ancestral", "k_dpm_2", "k_dpm_fast", "ddim", "ddim_v3",
    ];
    private static readonly string[] AvailableSchedules =
    [
        "native", "karras", "exponential", "polyexponential",
    ];

    private enum InspectPrimaryAction
    {
        SendMetadata,
        InferTags,
        DisabledSend,
    }

    private static string AppRootDir => AppPathResolver.AppRootDir;
    private static string OutputBaseDir => Path.Combine(AppRootDir, "output");
    private static string PromptShortcutsFilePath => Path.Combine(AppRootDir, "user", "userprompts", "userprompts.json");
    private static string FxPresetsDir => Path.Combine(AppRootDir, "user", "fxpresets");
    private static string DefaultFxPresetsDir => Path.Combine(AppRootDir, "assets", "fxpresets");
    private static string DefaultWildcardsDir => Path.Combine(AppRootDir, "user", "wildcards");
    private static string BundledWildcardsDir => Path.Combine(AppRootDir, "assets", "wildcards");
    private static string ModelsDir => Path.Combine(AppRootDir, "models");

    // ═══ 自动补全 ═══
    private readonly TagCompleteService _tagService = new();
    private int _acVersion;
    private TextBox? _acTargetTextBox;
    private bool _acInserting;

    // ═══════════════════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════════════════

    public MainWindow()
    {
        System.Diagnostics.Debug.WriteLine($"[Startup] App root: {AppRootDir}");
        System.Diagnostics.Debug.WriteLine($"[Startup] BaseDirectory: {AppContext.BaseDirectory}");

        this.InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new DesktopAcrylicBackdrop();

        if (AppWindow != null)
        {
            AppWindow.Resize(new SizeInt32(1400, 900));
            AppWindow.SetIcon("NAIT.ico");
        }
        this.Activated += (_, _) => ApplyWindowChrome(this, IsDarkTheme(), null, null);
        Closed += (_, _) =>
        {
            if (IsPromptMode(_currentMode))
            {
                SaveCurrentPromptToBuffer();
                SyncUIToParams();
            }
            SyncRememberedPromptAndParameterState();
            _settings.Save();
            _reverseTaggerService.Dispose();
        };

        _settings.Load();
        bool persistDetectedLanguage = string.IsNullOrWhiteSpace(_settings.Settings.LanguageCode);
        _settings.Settings.LanguageCode = _loc.Initialize(_settings.Settings.LanguageCode);
        _loc.LanguageChanged += OnAppLanguageChanged;
        if (persistDetectedLanguage)
            _settings.Save();

        DebugLog($"[Startup] App root={AppRootDir} | DevLog={_settings.Settings.DevLogEnabled} | Language={_settings.Settings.LanguageCode}");
        ApplyRememberedPromptAndParameterPreference();
        ApplyCachedAccountInfo();
        AutoDetectTaggerModel();
        _naiService = new NovelAIService(_settings);
        LoadPromptShortcuts();
        LoadWildcards();
        EnsureDefaultFxPresets();

        ApplyTheme(_settings.Settings.ThemeMode);
        SyncThemeMenuChecks(_settings.Settings.ThemeMode);

        MaskCanvas.ZoomChanged += z => TxtZoomInfo.Text = Lf("status.zoom", z * 100);
        MaskCanvas.ContentChanged += () =>
        {
            if (_currentMode == AppMode.Inpaint &&
                (MaskCanvas.CanvasW != _customWidth || MaskCanvas.CanvasH != _customHeight))
            {
                _customWidth = MaskCanvas.CanvasW;
                _customHeight = MaskCanvas.CanvasH;
                SetSizeInputsSilently(_customWidth, _customHeight);
                UpdateSizeWarningVisuals();
                UpdateGenerateButtonWarning();
            }
            QueueThumbnailRender();
            UpdateDynamicMenuStates();
        };
        MaskCanvas.StatusMessage += m => TxtStatus.Text = m;

        ThumbnailCanvas.CustomDevice = CanvasDevice.GetSharedDevice();

        MaskCanvas.InitializeCanvas(_customWidth, _customHeight);
        SetupThumbnailTimer();

        PopulateLeftSidebarControls();
        BtnSplitPrompt.IsChecked = _isSplitPrompt;
        ApplyStaticMenuAndComboTypography();
        CboModel.SelectionChanged += (_, _) => UpdateModelDependentUI();
        _menuTools = MenuTools;
        SyncParamsToUI();
        SwitchMode(AppMode.ImageGeneration);
        ApplyLocalization();
        SetupPromptContextFlyouts();
        SetupGenPreviewContextMenu();
        SetupPreviewScrollZoomAndDrag();
        SetupSidebarAdvancedSync();
        SetupGenerateButtonContextFlyout();
        UpdateBtnGenerateForApiKey();
        _ = RefreshAnlasInfoAsync();

        _ = LoadTagServiceAsync();

        this.Content.KeyDown += OnGlobalKeyDown;
        MaskCanvas.SizeChanged += OnMaskCanvasSizeChanged;

        BtnCompare.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnComparePressed), true);
        BtnCompare.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnCompareReleased), true);
        BtnCompare.AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnCompareReleased), true);
        BtnCompare.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnCompareReleased), true);

        _effectsPreviewTimer = DispatcherQueue.CreateTimer();
        _effectsPreviewTimer.IsRepeating = false;
        _effectsPreviewTimer.Interval = TimeSpan.FromMilliseconds(60);
        _effectsPreviewTimer.Tick += (_, _) => _ = RenderQueuedEffectsPreview();

        RefreshEffectsPanel();
        LoadHistoryAsync();
    }

    private string L(string key) => _loc.GetString(key);

    private string Lf(string key, params object?[] args) => _loc.Format(key, args);

    private static bool HasMenuCommand(MenuFlyoutItem item, string commandId) =>
        string.Equals(item.Tag as string, commandId, StringComparison.Ordinal);

    private static bool HasMenuCommand(MenuFlyoutSubItem item, string commandId) =>
        string.Equals(item.Tag as string, commandId, StringComparison.Ordinal);

    private MenuFlyoutItem CreateLocalizedMenuItem(string commandId, string key, IconElement? icon = null)
    {
        var item = new MenuFlyoutItem
        {
            Tag = commandId,
            Text = L(key),
        };
        if (icon != null)
            item.Icon = icon;
        return item;
    }

    private MenuFlyoutSubItem CreateLocalizedSubItem(string commandId, string key, IconElement? icon = null)
    {
        var item = new MenuFlyoutSubItem
        {
            Tag = commandId,
            Text = L(key),
        };
        if (icon != null)
            item.Icon = icon;
        return item;
    }

    private void OnAppLanguageChanged(object? sender, EventArgs e)
    {
        ApplyLocalization();
        ReplaceEditMenu();
        ReplaceToolMenu();
        RefreshVibeTransferPanel();
        RefreshPreciseReferencePanel();
        RefreshHistoryPanel();
        RefreshEffectsPanel();
        TxtStatus.Text = Lf("status.language_changed", _loc.GetLanguageDisplayName(_settings.Settings.LanguageCode));
    }

    private void ApplyLanguageSelectionChecks()
    {
        string code = _settings.Settings.LanguageCode;
        MenuLanguageEnglish.IsChecked = code == "en_us";
        MenuLanguageZhCn.IsChecked = code == "zh_cn";
        MenuLanguageZhTw.IsChecked = code == "zh_tw";
        MenuLanguageJaJp.IsChecked = code == "ja_jp";
    }

    private void ApplyLocalization()
    {
        Title = L("app.title");
        AppTitleText.Text = L("app.title");

        MenuFile.Title = L("menu.file");
        MenuOpenImage.Text = L("menu.file.open_image");
        MenuSave.Text = L("menu.file.save");
        MenuSaveAs.Text = L("menu.file.save_as");
        MenuSaveStripped.Text = L("menu.file.save_as_stripped");
        MenuOpenImageFolder.Text = L("menu.file.open_folder");
        MenuExit.Text = L("menu.file.exit");

        MenuView.Title = L("menu.view");
        MenuFitToScreen.Text = L("menu.view.fit_to_screen");
        MenuActualSize.Text = L("menu.view.actual_size");
        MenuCenterView.Text = L("menu.view.center");
        MenuZoomIn.Text = L("menu.view.zoom_in");
        MenuZoomOut.Text = L("menu.view.zoom_out");

        MenuSettings.Title = L("menu.settings");
        MenuUsageSettings.Text = L("menu.settings.usage");
        MenuNetworkSettings.Text = L("menu.settings.network");
        MenuReverseTaggerSettings.Text = L("menu.settings.reverse_tagger");
        MenuAppearance.Text = L("menu.settings.appearance");
        MenuThemeSystem.Text = L("menu.settings.theme.system");
        MenuThemeLight.Text = L("menu.settings.theme.light");
        MenuThemeDark.Text = L("menu.settings.theme.dark");
        MenuLanguage.Text = L("menu.settings.language");
        MenuLanguageEnglish.Text = _loc.GetLanguageDisplayName("en_us");
        MenuLanguageZhCn.Text = _loc.GetLanguageDisplayName("zh_cn");
        MenuLanguageZhTw.Text = _loc.GetLanguageDisplayName("zh_tw");
        MenuLanguageJaJp.Text = _loc.GetLanguageDisplayName("ja_jp");
        MenuDevSettings.Text = L("menu.settings.developer");
        ApplyLanguageSelectionChecks();

        MenuHelp.Title = L("menu.help");
        MenuHelpOverview.Text = L("menu.help.overview");
        MenuHelpHighlights.Text = L("menu.help.highlights");
        MenuHelpLinks.Text = L("menu.help.links");
        MenuAbout.Text = L("menu.help.about");

        TabGenerate.Content = L("mode.generate");
        TabInpaint.Content = L("mode.inpaint");
        TabUpscale.Content = L("mode.upscale");
        TabEffects.Content = L("mode.post");
        TabInspect.Content = L("mode.inspect");
        TabPositive.Content = L("prompt.positive");
        TabNegative.Content = L("prompt.negative");

        ToolTipService.SetToolTip(BtnSplitPrompt, L("tooltip.split_prompt"));
        TxtStylePrompt.PlaceholderText = L("prompt.style_placeholder");
        TxtPrompt.PlaceholderText = L("prompt.placeholder");

        TxtModelLabel.Text = L("panel.model");
        TxtSizeLabel.Text = L("panel.size");
        TxtSeedLabel.Text = L("panel.seed");
        ToolTipService.SetToolTip(BtnSwapSizeDimensions, L("tooltip.swap_dimensions"));
        ToolTipService.SetToolTip(BtnSeedRandomize, L("tooltip.random_seed"));
        ToolTipService.SetToolTip(BtnSeedRestore, L("tooltip.restore_last_seed"));
        ToolTipService.SetToolTip(BtnBrush, L("tooltip.brush"));
        ToolTipService.SetToolTip(BtnEraser, L("tooltip.eraser"));
        ToolTipService.SetToolTip(BtnRect, L("tooltip.rectangle"));
        ChkVariety.Content = L("panel.variety");
        TxtAdvancedParamsButton.Text = L("button.advanced_parameters");
        TxtGenerateButton.Text = L("button.generate");

        InspectPlaceholder.Text = L("inspect.placeholder.drop_or_open");
        TxtInspectPositiveLabel.Text = L("inspect.positive_prompt");
        TxtInspectNegativeLabel.Text = L("inspect.negative_prompt");
        TxtInspectParametersLabel.Text = L("inspect.parameters");
        TxtInspectSizeLabel.Text = L("panel.size");
        TxtInspectStepsLabel.Text = L("panel.steps");
        TxtInspectSamplerLabel.Text = L("panel.sampler");
        TxtInspectScheduleLabel.Text = L("panel.scheduler");
        TxtInspectSeedLabel.Text = L("panel.seed_short");
        TxtSendInspectToInpaint.Text = L("button.send_whole_to_inpaint");

        TxtUpscaleModelLabel.Text = L("upscale.model");
        TxtUpscaleScaleLabel.Text = L("upscale.scale");
        TxtUpscaleDeviceLabel.Text = L("upscale.device");
        CboUpscaleDeviceGpu.Content = L("upscale.device_gpu");
        CboUpscaleDeviceCpu.Content = L("upscale.device_cpu");
        TxtUpscaleBeforeLabel.Text = L("upscale.before");
        TxtUpscaleAfterLabel.Text = L("upscale.after_estimated");
        TxtStartUpscaleButton.Text = _upscaleRunning ? L("button.upscaling") : L("button.start_upscale");

        GenPlaceholder.Text = L("placeholder.generate");
        TxtSendGenToInpaint.Text = L("button.send_to_inpaint");
        TxtSendGenToEffects.Text = L("button.send_to_post");
        TxtDeleteGenResult.Text = L("button.delete");
        TxtCloseGenResult.Text = L("button.close");
        TxtApplyResult.Text = L("button.apply");
        TxtRedoGenerate.Text = L("button.regenerate");
        TxtCompareResult.Text = L("button.compare");
        TxtDiscardResult.Text = L("button.discard");

        InspectImagePlaceholder.Text = L("placeholder.drop_or_open");
        EffectsImagePlaceholder.Text = L("placeholder.drop_or_open");
        UpscalePlaceholder.Text = L("placeholder.upscale");
        TxtHistoryTitle.Text = L("history.title");
        HistoryDatePicker.PlaceholderText = L("history.select_date");

        TxtInpaintPreviewLabel.Text = L("inpaint.preview");
        TxtZoomInfo.Text = Lf("status.zoom", 100d);
        ChkPreviewMask.Content = L("inpaint.preview_mask");
        TxtInpaintToolsLabel.Text = L("inpaint.tools");
        TxtBrushSizeLabel.Text = L("inpaint.brush_size");

        if (string.IsNullOrWhiteSpace(TxtStatus.Text) || TxtStatus.Text == "就绪" || TxtStatus.Text == "Ready")
            TxtStatus.Text = L("status.ready");
    }

    private void OnLanguageChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item || item.Tag is not string languageCode)
            return;

        _settings.Settings.LanguageCode = LocalizationService.NormalizeLanguageCode(languageCode);
        _loc.SetLanguage(_settings.Settings.LanguageCode);
        _settings.Save();
    }

    // ═══════════════════════════════════════════════════════════
    //  左侧栏控件初始化
    // ═══════════════════════════════════════════════════════════

    private void PopulateLeftSidebarControls()
    {
        foreach (var p in MaskCanvasControl.CanvasPresets)
            CboSize.Items.Add(CreateTextComboBoxItem(p.Label));
        ApplyMenuTypography(CboSize);
        SetSizeInputsSilently(_customWidth, _customHeight);
        SuppressNumberBoxClearButton(NbMaxWidth);
        SuppressNumberBoxClearButton(NbMaxHeight);
        SuppressNumberBoxClearButton(NbSeed);
        UpdateSizeControlMode();
        UpdateSizeWarningVisuals();
    }

    private void SyncParamsToUI()
    {
        var p = CurrentParams;
        NbSeed.Value = p.Seed;
        ChkVariety.IsChecked = p.Variety;
        SetSizeInputsSilently(_customWidth, _customHeight);
        UpdateModelDependentUI();
    }

    private void SetSizeInputsSilently(int width, int height)
    {
        _isUpdatingMaxSize = true;
        try
        {
            if (NbMaxWidth != null) NbMaxWidth.Value = width;
            if (NbMaxHeight != null) NbMaxHeight.Value = height;
            if (IsAdvancedWindowOpen)
            {
                if (_advNbMaxWidth != null) _advNbMaxWidth.Value = width;
                if (_advNbMaxHeight != null) _advNbMaxHeight.Value = height;
            }
        }
        finally
        {
            _isUpdatingMaxSize = false;
        }
    }

    private bool IsCurrentModelV3()
    {
        return IsV3ModelKey(GetCurrentModelKey());
    }

    private static string[] GetAvailableSamplersForModel(string? model)
    {
        bool isV3 = IsV3ModelKey(model ?? "");
        return AvailableSamplers
            .Where(x => isV3
                ? !string.Equals(x, "ddim", StringComparison.Ordinal)
                : !string.Equals(x, "ddim_v3", StringComparison.Ordinal))
            .ToArray();
    }

    private static string NormalizeSamplerForModel(string? sampler, string? model)
    {
        var available = GetAvailableSamplersForModel(model);
        if (!string.IsNullOrWhiteSpace(sampler) && available.Contains(sampler, StringComparer.Ordinal))
            return sampler;
        return available.FirstOrDefault() ?? "k_euler_ancestral";
    }

    private void RefreshAdvancedSamplerOptions()
    {
        if (_advCboSampler == null)
            return;

        string model = GetCurrentModelKey();
        string selected = NormalizeSamplerForModel(CurrentParams.Sampler, model);
        var samplers = GetAvailableSamplersForModel(model);

        _advCboSampler.Items.Clear();
        foreach (var s in samplers)
            _advCboSampler.Items.Add(CreateTextComboBoxItem(s));

        _advCboSampler.SelectedIndex = Array.IndexOf(samplers, selected);
        if (_advCboSampler.SelectedIndex < 0)
            _advCboSampler.SelectedIndex = 0;
    }

    private void UpdateModelDependentUI()
    {
        if (ChkVariety == null || CboModel == null) return;
        bool isV3 = IsCurrentModelV3();
        CurrentParams.Sampler = NormalizeSamplerForModel(CurrentParams.Sampler, GetCurrentModelKey());
        RecheckVibeTransferCacheState();
        UpdateReferenceButtonAndPanelState();
        ChkVariety.Visibility = Visibility.Visible;
        if (IsAdvancedWindowOpen)
        {
            if (_advChkVariety != null)
                _advChkVariety.Visibility = Visibility.Visible;
            if (_advChkSmea != null)
                _advChkSmea.Visibility = (_currentMode == AppMode.ImageGeneration && isV3)
                    ? Visibility.Visible : Visibility.Collapsed;
            RefreshAdvancedSamplerOptions();
        }

        UpdateAnlasBalanceText();
        UpdateBtnGenerateForApiKey();
        UpdateGenerateButtonWarning();
    }

    private NAIParameters CurrentParams => _currentMode == AppMode.ImageGeneration
        ? _settings.Settings.GenParameters
        : _settings.Settings.InpaintParameters;

    private void SyncUIToParams()
    {
        var p = CurrentParams;
        p.Seed = (int)NbSeed.Value;
        p.Variety = ChkVariety.IsChecked == true;
        p.Model = GetSelectedComboText(CboModel) ?? p.Model;
        p.Sampler = NormalizeSamplerForModel(p.Sampler, p.Model);
    }

    private static NAIParameters CreateDefaultGenerationParameters() => new()
    {
        Model = "nai-diffusion-4-5-full",
    };

    private static NAIParameters CreateDefaultInpaintParameters() => new()
    {
        Model = "nai-diffusion-4-5-full-inpainting",
    };

    private string GetWildcardsRootDir() => DefaultWildcardsDir;

    private static void EnsureDefaultWildcards()
    {
        try
        {
            if (!Directory.Exists(BundledWildcardsDir)) return;
            Directory.CreateDirectory(DefaultWildcardsDir);

            foreach (string sourceFile in Directory.GetFiles(BundledWildcardsDir, "*.txt", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(BundledWildcardsDir, sourceFile);
                string targetPath = Path.Combine(DefaultWildcardsDir, relative);
                if (!File.Exists(targetPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Copy(sourceFile, targetPath, overwrite: false);
                }
            }
        }
        catch { }
    }

    private void LoadWildcards()
    {
        try
        {
            EnsureDefaultWildcards();
            _wildcardService.Reload(GetWildcardsRootDir());
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"加载抽卡器失败: {ex.Message}";
        }
    }

    private WildcardWeightFormat GetWildcardWeightFormatForModel(string model) =>
        IsV3ModelKey(model)
            ? WildcardWeightFormat.NaiClassic
            : WildcardWeightFormat.NaiNumeric;

    private WildcardExpandContext CreateWildcardContext(int seed, string model) =>
        new(seed, GetWildcardWeightFormatForModel(model));

    private string ExpandPromptFeatures(string text, WildcardExpandContext context, bool isNegativeText = false)
    {
        string expanded = ExpandPromptShortcuts(text);
        if (!_settings.Settings.WildcardsEnabled)
            return expanded;

        return _wildcardService
            .ExpandText(expanded, context, _settings.Settings.WildcardsRequireExplicitSyntax, true)
            .Text;
    }

    private void ApplyRememberedPromptAndParameterPreference()
    {
        if (!_settings.Settings.RememberPromptAndParameters)
        {
            _settings.Settings.GenParameters = CreateDefaultGenerationParameters();
            _settings.Settings.InpaintParameters = CreateDefaultInpaintParameters();
            _customWidth = 832;
            _customHeight = 1216;
            _genPositivePrompt = "";
            _genNegativePrompt = "";
            _genStylePrompt = "";
            _inpaintPositivePrompt = "";
            _inpaintNegativePrompt = "";
            _inpaintStylePrompt = "";
            _isSplitPrompt = false;
            return;
        }

        var remembered = _settings.Settings.RememberedPrompts ?? new RememberedPromptState();
        _genPositivePrompt = remembered.GenPositivePrompt ?? "";
        _genNegativePrompt = remembered.GenNegativePrompt ?? "";
        _genStylePrompt = remembered.GenStylePrompt ?? "";
        _inpaintPositivePrompt = remembered.InpaintPositivePrompt ?? "";
        _inpaintNegativePrompt = remembered.InpaintNegativePrompt ?? "";
        _inpaintStylePrompt = remembered.InpaintStylePrompt ?? "";
        _isSplitPrompt = remembered.IsSplitPrompt;
        _customWidth = SnapToMultipleOf64(_settings.Settings.RememberedCustomWidth);
        _customHeight = SnapToMultipleOf64(_settings.Settings.RememberedCustomHeight);
    }

    private void ClearRememberedPromptState()
    {
        _settings.Settings.RememberedPrompts = new RememberedPromptState();
        _settings.Settings.RememberedCustomWidth = 832;
        _settings.Settings.RememberedCustomHeight = 1216;
    }

    private void SyncRememberedPromptAndParameterState()
    {
        if (!_settings.Settings.RememberPromptAndParameters)
            return;

        _settings.Settings.RememberedPrompts = new RememberedPromptState
        {
            GenPositivePrompt = _genPositivePrompt,
            GenNegativePrompt = _genNegativePrompt,
            GenStylePrompt = _genStylePrompt,
            InpaintPositivePrompt = _inpaintPositivePrompt,
            InpaintNegativePrompt = _inpaintNegativePrompt,
            InpaintStylePrompt = _inpaintStylePrompt,
            IsSplitPrompt = _isSplitPrompt,
        };
        _settings.Settings.RememberedCustomWidth = _customWidth;
        _settings.Settings.RememberedCustomHeight = _customHeight;
    }

    private bool IsAnyGenerateLoopRunning() => _autoGenRunning || _continuousGenRunning;

    private void SetupGenerateButtonContextFlyout()
    {
        var title = new TextBlock
        {
            Text = "连续生成",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var countRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        var hintText = new TextBlock
        {
            Text = "使用当前参数连续发送请求，种子每次自动随机。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            FontSize = 12,
        };

        var buttons = new List<Button>();
        Flyout? flyout = null;
        for (int i = 1; i <= 6; i++)
        {
            int count = i;
            var button = new Button
            {
                Content = count.ToString(),
                MinWidth = 34,
                Padding = new Thickness(10, 4, 10, 4),
                Tag = count,
            };
            button.Click += (_, _) =>
            {
                flyout?.Hide();
                StartContinuousGeneration(count);
            };
            buttons.Add(button);
            countRow.Children.Add(button);
        }

        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                title,
                countRow,
                hintText,
            },
        };

        flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
            Content = new Border
            {
                Padding = new Thickness(14, 12, 14, 12),
                Child = panel,
            },
        };
        flyout.Opening += (_, _) =>
        {
            bool canStart = !_autoGenRunning &&
                            !_continuousGenRunning &&
                            IsPromptMode(_currentMode) &&
                            !string.IsNullOrWhiteSpace(_settings.Settings.ApiToken);
            foreach (var button in buttons)
                button.IsEnabled = canStart;
            hintText.Text = canStart
                ? "使用当前参数连续发送请求，种子每次自动随机。"
                : "当前状态下不可启动连续生成。";
        };
        BtnGenerate.ContextFlyout = flyout;
    }

    private void StartContinuousGeneration(int count)
    {
        if (count <= 0 || IsAnyGenerateLoopRunning())
            return;

        _ = RunContinuousGenerationAsync(count);
    }

    private void StopContinuousGeneration()
    {
        if (_continuousStopRequested)
            return;
        _continuousStopRequested = true;
        TxtStatus.Text = "正在停止连续生成...";
        _continuousGenCts?.Cancel();
        UpdateAutoGenUI();
    }

    private async Task RefreshAnlasInfoAsync(bool forceRefresh = false)
    {
        if (TxtAnlasBalance == null)
            return;

        if (string.IsNullOrWhiteSpace(_settings.Settings.ApiToken))
        {
            _anlasBalance = null;
            _isOpusSubscriber = false;
            _anlasInitialFetchDone = false;
            UpdateAnlasBalanceText();
            UpdateBtnGenerateForApiKey();
            UpdateGenerateButtonWarning();
            return;
        }

        if (_anlasInitialFetchDone && !forceRefresh)
        {
            UpdateAnlasBalanceText();
            return;
        }

        if (_anlasRefreshRunning)
            return;

        _anlasRefreshRunning = true;
        UpdateBtnGenerateForApiKey();
        try
        {
            var accountInfo = await _naiService.GetAccountInfoAsync();
            _anlasBalance = accountInfo?.AnlasBalance;
            _isOpusSubscriber = accountInfo?.IsOpus == true;
            _anlasInitialFetchDone = true;

            if (accountInfo != null)
            {
                _settings.UpdateCachedAccountInfo(
                    accountInfo.AnlasBalance,
                    accountInfo.TierName,
                    accountInfo.TierLevel,
                    accountInfo.ExpiresAt);
            }
        }
        finally
        {
            _anlasRefreshRunning = false;
            UpdateAnlasBalanceText();
            UpdateBtnGenerateForApiKey();
            UpdateGenerateButtonWarning();
        }
    }

    private void ApplyCachedAccountInfo()
    {
        var cached = _settings.CachedApiConfig;
        if (cached.CachedAnlas.HasValue)
            _anlasBalance = cached.CachedAnlas;
        if (cached.SubscriptionTierLevel.HasValue)
            _isOpusSubscriber = cached.SubscriptionTierLevel.Value >= 3;
    }

    private void UpdateAnlasBalanceText()
    {
        if (TxtAnlasBalance == null)
            return;

        bool visible = IsPromptMode(_currentMode) &&
                       !string.IsNullOrWhiteSpace(_settings.Settings.ApiToken);
        TxtAnlasBalance.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;

        TxtAnlasBalance.Text = _anlasBalance.HasValue
            ? $"Anlas: {_anlasBalance.Value:N0}"
            : "Anlas: --";
    }

    // ═══════════════════════════════════════════════════════════
    //  模式切换
    // ═══════════════════════════════════════════════════════════

    private void OnModeTabSwitch(object sender, RoutedEventArgs e)
    {
        AppMode? target = null;
        if (ReferenceEquals(sender, TabGenerate) && TabGenerate.IsChecked == true)
            target = AppMode.ImageGeneration;
        else if (ReferenceEquals(sender, TabInpaint) && TabInpaint.IsChecked == true)
            target = AppMode.Inpaint;
        else if (ReferenceEquals(sender, TabUpscale) && TabUpscale.IsChecked == true)
            target = AppMode.Upscale;
        else if (ReferenceEquals(sender, TabEffects) && TabEffects.IsChecked == true)
            target = AppMode.Effects;
        else if (ReferenceEquals(sender, TabInspect) && TabInspect.IsChecked == true)
            target = AppMode.Inspect;

        if (target.HasValue)
            SwitchMode(target.Value);
        else if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
                tb.IsChecked = true;
    }

    private void SwitchMode(AppMode mode)
    {
        if (_autoGenRunning) StopAutoGeneration();
        if (_continuousGenRunning) StopContinuousGeneration();

        if (IsPromptMode(_currentMode))
        {
        SaveCurrentPromptToBuffer();
        SyncUIToParams();
        }
        _currentMode = mode;
        if (IsPromptMode(mode))
        SyncParamsToUI();

        if (IsPromptMode(mode))
        {
        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        }

        bool isGen = mode == AppMode.ImageGeneration;
        bool isInpaint = mode == AppMode.Inpaint;
        bool isUpscale = mode == AppMode.Upscale;
        bool isPost = mode == AppMode.Effects;
        bool isReader = mode == AppMode.Inspect;

        GenPreviewArea.Visibility = isGen ? Visibility.Visible : Visibility.Collapsed;
        MaskCanvas.Visibility = isInpaint ? Visibility.Visible : Visibility.Collapsed;
        UpscalePreviewArea.Visibility = isUpscale ? Visibility.Visible : Visibility.Collapsed;
        EffectsPreviewArea.Visibility = isPost ? Visibility.Visible : Visibility.Collapsed;
        InspectPreviewArea.Visibility = isReader ? Visibility.Visible : Visibility.Collapsed;

        GenResultBar.Visibility = Visibility.Collapsed;
        if (isInpaint && MaskCanvas.IsInPreviewMode)
            ResultActionBar.Visibility = Visibility.Visible;
        else
        ResultActionBar.Visibility = Visibility.Collapsed;

        PanelLeftMain.Visibility = (isReader || isPost || isUpscale) ? Visibility.Collapsed : Visibility.Visible;
        PanelLeftEffects.Visibility = isPost ? Visibility.Visible : Visibility.Collapsed;
        PanelLeftUpscale.Visibility = isUpscale ? Visibility.Visible : Visibility.Collapsed;
        PanelLeftInspect.Visibility = isReader ? Visibility.Visible : Visibility.Collapsed;

        PanelHistory.Visibility = isGen ? Visibility.Visible : Visibility.Collapsed;
        PanelInpaintTools.Visibility = isInpaint ? Visibility.Visible : Visibility.Collapsed;
        CharacterPanel.Visibility = (isGen || isInpaint) ? Visibility.Visible : Visibility.Collapsed;

        UpdateFileMenuState();
        MenuSaveStripped.Visibility = (isReader || isGen) ? Visibility.Visible : Visibility.Collapsed;

        TabGenerate.IsChecked = isGen;
        TabInpaint.IsChecked = isInpaint;
        TabUpscale.IsChecked = isUpscale;
        TabEffects.IsChecked = isPost;
        TabInspect.IsChecked = isReader;

        if (IsPromptMode(mode)) PopulateModelList();
        if (isUpscale) PopulateUpscaleModelList();
        ReplaceEditMenu();
        ReplaceToolMenu();
        if (IsPromptMode(mode))
        {
            UpdateSizeControlMode();
            UpdateAdvSizeControlMode();
        }
        UpdateSizeWarningVisuals();
        UpdateAnlasBalanceText();
        _ = RefreshAnlasInfoAsync();
    }

    private static bool IsPromptMode(AppMode mode) =>
        mode == AppMode.ImageGeneration || mode == AppMode.Inpaint;

    private void OnLeftSidebarResizeStart(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement handle) return;
        _leftSidebarResizing = true;
        _leftSidebarDragStartX = e.GetCurrentPoint(MainContentGrid).Position.X;
        _leftSidebarStartWidth = MainContentGrid.ColumnDefinitions[0].ActualWidth;
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnLeftSidebarResizeMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_leftSidebarResizing) return;

        double currentX = e.GetCurrentPoint(MainContentGrid).Position.X;
        double newWidth = _leftSidebarStartWidth + (currentX - _leftSidebarDragStartX);
        newWidth = Math.Clamp(newWidth, 280, 720);
        MainContentGrid.ColumnDefinitions[0].Width = new GridLength(newWidth);
        e.Handled = true;
    }

    private void OnLeftSidebarResizeEnd(object sender, PointerRoutedEventArgs e)
    {
        if (!_leftSidebarResizing) return;
        _leftSidebarResizing = false;
        if (sender is UIElement handle)
            handle.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnLeftSidebarHandlePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Panel panel)
            panel.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80));
    }

    private void OnLeftSidebarHandlePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Panel panel && !_leftSidebarResizing)
            panel.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
    }

    private enum EffectType
    {
        BrightnessContrast,
        SaturationVibrance,
        Temperature,
        Glow,
        RadialBlur,
        Vignette,
        ChromaticAberration,
        Noise,
        Gamma,
        Pixelate,
        SolidBlock,
        Scanline,
    }

    private sealed class EffectEntry
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public EffectType Type { get; init; }
        public double Value1 { get; set; }
        public double Value2 { get; set; }
        public double Value3 { get; set; }
        public double Value4 { get; set; }
        public double Value5 { get; set; }
        public double Value6 { get; set; }
        public string TextValue { get; set; } = "";
    }

    private sealed class EffectsWorkspaceState
    {
        public byte[]? ImageBytes { get; init; }
        public string? ImagePath { get; init; }
        public Guid? SelectedEffectId { get; init; }
        public List<EffectEntry> Effects { get; init; } = new();
    }

    private sealed class EffectsPresetFile
    {
        public string Name { get; set; } = "";
        public DateTime SavedAt { get; set; }
        public List<EffectEntry> Effects { get; set; } = new();
    }

    private static IconElement CreateEffectsIcon() =>
        new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" };

    private static bool IsRegionEffect(EffectType type) =>
        type == EffectType.Pixelate || type == EffectType.SolidBlock;

    private static bool IsInteractiveEffectCardSource(object? source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current != null)
        {
            if (current is ComboBox or ComboBoxItem or Button or Slider or TextBox or ToggleSwitch or NumberBox)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void MoveEffect(Guid effectId, int direction)
    {
        int index = _effects.FindIndex(x => x.Id == effectId);
        if (index < 0) return;

        int newIndex = Math.Clamp(index + direction, 0, _effects.Count - 1);
        if (newIndex == index) return;

        PushEffectsUndoState();
        var item = _effects[index];
        _effects.RemoveAt(index);
        _effects.Insert(newIndex, item);
        RefreshEffectsPanel();
        QueueEffectsPreviewRefresh(immediate: true);
        UpdateDynamicMenuStates();
        TxtStatus.Text = "已调整效果顺序";
    }

    private EffectEntry? GetSelectedEffect()
    {
        if (_selectedEffectId == null) return null;
        return _effects.FirstOrDefault(x => x.Id == _selectedEffectId.Value);
    }

    private async void OnSendToInpaintFromEffects(object sender, RoutedEventArgs e)
    {
        try
        {
            var bytes = await GetEffectsSaveBytesAsync();
            if (bytes == null || bytes.Length == 0)
            {
                TxtStatus.Text = "没有图像可发送到重绘";
                return;
            }

            SendImageToInpaint(bytes);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"发送到重绘失败: {ex.Message}";
        }
    }

    private void PopulateModelList()
    {
        CboModel.Items.Clear();
        var models = _currentMode == AppMode.ImageGeneration ? GenerationModels : InpaintModels;
        foreach (var m in models) CboModel.Items.Add(CreateTextComboBoxItem(m));

        CboModel.SelectedIndex = Array.IndexOf(models, CurrentParams.Model);
        if (CboModel.SelectedIndex < 0) CboModel.SelectedIndex = 0;
        ApplyMenuTypography(CboModel);
    }

    // ═══════════════════════════════════════════════════════════
    //  编辑菜单：替换整个 MenuBarItem 避免 WinUI Clear() bug
    // ═══════════════════════════════════════════════════════════

    private void ReplaceEditMenu()
    {
        int idx = AppMenuBar.Items.IndexOf(MenuEdit);
        if (idx < 0) idx = 1;

        AppMenuBar.Items.Remove(MenuEdit);

        var newEdit = new MenuBarItem { Title = L("menu.edit") };

        if (_currentMode == AppMode.ImageGeneration)
        {
            newEdit.Items.Add(BuildPresetResolutionSubMenu());
            newEdit.Items.Add(new MenuFlyoutSeparator());

            var normalizeItem = CreateLocalizedMenuItem(
                MenuCommandNormalizePrompts,
                "menu.edit.normalize_prompts",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8D2" });
            normalizeItem.Click += OnNormalizePrompts;
            newEdit.Items.Add(normalizeItem);

            var randomStyleItem = CreateLocalizedMenuItem(
                MenuCommandRandomStylePrompt,
                "menu.edit.random_style_prompt",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B1" });
            randomStyleItem.Click += OnRandomStylePrompt;
            newEdit.Items.Add(randomStyleItem);
            newEdit.Items.Add(BuildPromptShortcutsMenuItem());
            newEdit.Items.Add(new MenuFlyoutSeparator());

            var sendItem = CreateLocalizedMenuItem(
                MenuCommandSendToInpaint,
                "action.send_to_inpaint",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" });
            sendItem.Click += OnSendToInpaint;
            newEdit.Items.Add(sendItem);

            var postItem = CreateLocalizedMenuItem(
                MenuCommandSendToPost,
                "action.send_to_post",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" });
            postItem.Click += OnSendToEffectsFromGen;
            newEdit.Items.Add(postItem);

            var upscaleItem = CreateLocalizedMenuItem(
                MenuCommandSendToUpscale,
                "action.send_to_upscale",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9" });
            upscaleItem.Click += OnSendToUpscaleFromGen;
            newEdit.Items.Add(upscaleItem);

            newEdit.Items.Add(new MenuFlyoutSeparator());
            var clearAllItem = CreateLocalizedMenuItem(
                MenuCommandClearAllPrompts,
                "menu.edit.clear_all_prompts",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74D" });
            clearAllItem.Click += OnClearAllPrompts;
            newEdit.Items.Add(clearAllItem);

            var resetParamsItem = CreateLocalizedMenuItem(
                MenuCommandResetGenerationParams,
                "menu.edit.reset_generation_params",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE777" });
            resetParamsItem.Click += OnResetGenParams;
            newEdit.Items.Add(resetParamsItem);
        }
        else if (_currentMode == AppMode.Inpaint)
        {
            BuildInpaintEditMenuItems(newEdit);
        }
        else if (_currentMode == AppMode.Inspect)
        {
            var rawItem = CreateLocalizedMenuItem(
                MenuCommandEditRawMetadata,
                "menu.edit.edit_raw_metadata",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE70F" });
            rawItem.IsEnabled = _inspectImageBytes != null;
            rawItem.Click += OnEditRawMetadata;
            newEdit.Items.Add(rawItem);

            newEdit.Items.Add(new MenuFlyoutSeparator());

            var scrambleMenu = CreateLocalizedSubItem(
                MenuCommandImageScramble,
                "menu.edit.image_scramble",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uF404" });
            scrambleMenu.IsEnabled = _inspectImageBytes != null;
            var encryptItem = CreateLocalizedMenuItem(
                MenuCommandScramble,
                "menu.edit.scramble",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE727" });
            encryptItem.Click += (_, _) => RunInspectImageScrambleAsync(ImageScrambleService.ProcessType.Encrypt);
            var decryptItem = CreateLocalizedMenuItem(
                MenuCommandUnscramble,
                "menu.edit.unscramble",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8D7" });
            decryptItem.Click += (_, _) => RunInspectImageScrambleAsync(ImageScrambleService.ProcessType.Decrypt);

            scrambleMenu.Items.Add(encryptItem);
            scrambleMenu.Items.Add(decryptItem);
            newEdit.Items.Add(scrambleMenu);
        }
        else if (_currentMode == AppMode.Upscale)
        {
            var sendInpaintItem = CreateLocalizedMenuItem(
                MenuCommandSendToInpaint,
                "action.send_to_inpaint",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" });
            sendInpaintItem.Click += OnSendToInpaintFromUpscale;
            newEdit.Items.Add(sendInpaintItem);

            var sendPostItem = CreateLocalizedMenuItem(
                MenuCommandSendToPost,
                "action.send_to_post",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" });
            sendPostItem.Click += OnSendToEffectsFromUpscale;
            newEdit.Items.Add(sendPostItem);
        }
        else if (_currentMode == AppMode.Effects)
        {
            var undoItem = CreateLocalizedMenuItem(MenuCommandUndo, "menu.edit.undo", new SymbolIcon(Symbol.Undo));
            undoItem.Click += OnUndo;
            undoItem.KeyboardAccelerators.Add(new KeyboardAccelerator
            { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.Z });
            newEdit.Items.Add(undoItem);

            var redoItem = CreateLocalizedMenuItem(MenuCommandRedo, "menu.edit.redo", new SymbolIcon(Symbol.Redo));
            redoItem.Click += OnRedo;
            redoItem.KeyboardAccelerators.Add(new KeyboardAccelerator
            { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.Y });
            newEdit.Items.Add(redoItem);

            newEdit.Items.Add(new MenuFlyoutSeparator());

            var sendInpaintItem = CreateLocalizedMenuItem(
                MenuCommandSendToInpaint,
                "action.send_to_inpaint",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" });
            sendInpaintItem.Click += OnSendToInpaintFromEffects;
            newEdit.Items.Add(sendInpaintItem);
            newEdit.Items.Add(new MenuFlyoutSeparator());

            var addPresetItem = CreateLocalizedMenuItem(
                MenuCommandAddPreset,
                "menu.edit.add_preset",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE70F" });
            addPresetItem.Click += OnAddEffectsPreset;
            newEdit.Items.Add(addPresetItem);

            var usePresetItem = CreateLocalizedMenuItem(
                MenuCommandUsePreset,
                "menu.edit.use_preset",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE790" });
            usePresetItem.Click += OnUseEffectsPreset;
            newEdit.Items.Add(usePresetItem);
            newEdit.Items.Add(new MenuFlyoutSeparator());

            var clearEffectsItem = CreateLocalizedMenuItem(
                MenuCommandClearAllEffects,
                "menu.edit.clear_all_effects",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74D" });
            clearEffectsItem.Click += OnClearAllEffects;
            newEdit.Items.Add(clearEffectsItem);

            var applyEffectsItem = CreateLocalizedMenuItem(
                MenuCommandApplyEffects,
                "menu.edit.apply_effects",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8C7" });
            applyEffectsItem.Click += OnApplyEffects;
            newEdit.Items.Add(applyEffectsItem);
        }

        AppMenuBar.Items.Insert(idx, newEdit);
        MenuEdit = newEdit;
        ApplyMenuTypography(newEdit);
        UpdateDynamicMenuStates();
    }

    private void ReplaceToolMenu()
    {
        _menuTools ??= MenuTools;
        int idx = _menuTools != null ? AppMenuBar.Items.IndexOf(_menuTools) : -1;
        if (idx < 0) idx = 2;

        if (_menuTools != null)
            AppMenuBar.Items.Remove(_menuTools);

        var newTools = new MenuBarItem { Title = L("menu.tools") };

        var weightItem = CreateLocalizedMenuItem(
            MenuCommandWeightConverter,
            "menu.tools.weight_converter",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE943" });
        weightItem.Click += (_, _) => ShowWeightConversionDialog();
        newTools.Items.Add(weightItem);

        var vibeEncodeItem = CreateLocalizedMenuItem(
            MenuCommandVibeEncode,
            "menu.tools.vibe_encode",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE706" });
        vibeEncodeItem.Click += (_, _) => ShowVibeEncodeDialog();
        newTools.Items.Add(vibeEncodeItem);

        var wildcardItem = CreateLocalizedMenuItem(
            MenuCommandWildcard,
            "menu.tools.wildcard",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74C" });
        wildcardItem.Click += (_, _) => ShowWildcardDialog();
        newTools.Items.Add(wildcardItem);

        if (_currentMode == AppMode.ImageGeneration)
        {
            newTools.Items.Add(new MenuFlyoutSeparator());

            var autoItem = CreateLocalizedMenuItem(
                MenuCommandAutomation,
                "menu.tools.automation",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE768" });
            autoItem.Click += OnAutoGenSettings;
            newTools.Items.Add(autoItem);
        }

        AppMenuBar.Items.Insert(idx, newTools);
        _menuTools = newTools;
        ApplyMenuTypography(newTools);
        UpdateDynamicMenuStates();
    }

    private void UpdateDynamicMenuStates()
    {
        UpdateToolMenuStates();
        if (MenuEdit == null) return;

        if (_currentMode == AppMode.ImageGeneration)
        {
            bool hasImage = _currentGenImageBytes != null;
            foreach (var baseItem in MenuEdit.Items)
            {
                if (baseItem is MenuFlyoutItem item &&
                    (HasMenuCommand(item, MenuCommandSendToInpaint) ||
                     HasMenuCommand(item, MenuCommandSendToPost) ||
                     HasMenuCommand(item, MenuCommandSendToUpscale)))
                    item.IsEnabled = hasImage;
            }
        }
        else if (_currentMode == AppMode.Upscale)
        {
            bool hasImage = _upscaleInputImageBytes != null;
            foreach (var baseItem in MenuEdit.Items)
            {
                if (baseItem is MenuFlyoutItem item &&
                    (HasMenuCommand(item, MenuCommandSendToInpaint) ||
                     HasMenuCommand(item, MenuCommandSendToPost)))
                    item.IsEnabled = hasImage;
            }
        }
        else if (_currentMode == AppMode.Effects)
        {
            bool hasImage = _effectsImageBytes != null;
            bool hasEffects = _effects.Count > 0;
            foreach (var baseItem in MenuEdit.Items)
            {
                if (baseItem is not MenuFlyoutItem item) continue;
                if (HasMenuCommand(item, MenuCommandSendToInpaint))
                    item.IsEnabled = hasImage;
                else if (HasMenuCommand(item, MenuCommandAddPreset))
                    item.IsEnabled = hasEffects;
                else if (HasMenuCommand(item, MenuCommandUsePreset))
                    item.IsEnabled = HasEffectsPresets();
                else if (HasMenuCommand(item, MenuCommandClearAllEffects))
                    item.IsEnabled = hasEffects;
                else if (HasMenuCommand(item, MenuCommandApplyEffects))
                    item.IsEnabled = hasImage && hasEffects;
                else if (HasMenuCommand(item, MenuCommandUndo))
                    item.IsEnabled = _effectsUndoStack.Count > 0;
                else if (HasMenuCommand(item, MenuCommandRedo))
                    item.IsEnabled = _effectsRedoStack.Count > 0;
            }
        }
        else if (_currentMode == AppMode.Inpaint)
        {
            bool hasImageLoaded = MaskCanvas.Document.OriginalImage != null && !MaskCanvas.IsInPreviewMode;
            bool hasMaskContent = MaskCanvas.HasMaskContent() && !MaskCanvas.IsInPreviewMode;
            bool hasInpaintImage = MaskCanvas.Document.OriginalImage != null ||
                (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null);

            foreach (var baseItem in MenuEdit.Items)
            {
                if (baseItem is MenuFlyoutItem item)
                {
                    if (HasMenuCommand(item, MenuCommandExpandMask) || HasMenuCommand(item, MenuCommandContractMask))
                        item.IsEnabled = hasMaskContent;
                    else if (HasMenuCommand(item, MenuCommandSendToPost) || HasMenuCommand(item, MenuCommandSendToUpscale))
                        item.IsEnabled = hasInpaintImage;
                }
                else if (baseItem is MenuFlyoutSubItem sub && HasMenuCommand(sub, MenuCommandAlignImage))
                {
                    sub.IsEnabled = hasImageLoaded;
                    foreach (var child in sub.Items)
                    {
                        if (child is MenuFlyoutItem childItem)
                            childItem.IsEnabled = hasImageLoaded;
                    }
                }
                else if (baseItem is MenuFlyoutSubItem inferSub && HasMenuCommand(inferSub, MenuCommandPromptInference))
                {
                    inferSub.IsEnabled = hasInpaintImage;
                    foreach (var child in inferSub.Items)
                    {
                        if (child is MenuFlyoutItem childItem)
                            childItem.IsEnabled = hasInpaintImage;
                    }
                }
                else if (baseItem is MenuFlyoutSubItem maskSub && HasMenuCommand(maskSub, MenuCommandMaskOps))
                {
                    foreach (var child in maskSub.Items)
                    {
                        if (child is not MenuFlyoutItem childItem) continue;
                        if (HasMenuCommand(childItem, MenuCommandExpandMask) || HasMenuCommand(childItem, MenuCommandContractMask))
                            childItem.IsEnabled = hasMaskContent;
                        else
                            childItem.IsEnabled = !MaskCanvas.IsInPreviewMode;
                    }
                }
            }
        }

        UpdateFileMenuState();
    }

    private void UpdateToolMenuStates()
    {
        if (_menuTools == null) return;

        foreach (var baseItem in _menuTools.Items)
        {
            if (baseItem is MenuFlyoutItem item && HasMenuCommand(item, MenuCommandWeightConverter))
                item.IsEnabled = true;
        }
    }

    private void UpdateFileMenuState()
    {
        bool hasGenImage = _currentGenImageBytes != null;
        bool hasInpaintImage = MaskCanvas.Document.OriginalImage != null ||
            (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null);
        bool hasUpscaleImage = _upscaleInputImageBytes != null;
        bool hasPostImage = _effectsImageBytes != null;
        bool hasReaderImage = _inspectImageBytes != null;

        MenuSave.Visibility = _currentMode switch
        {
            AppMode.Inpaint => Visibility.Visible,
            AppMode.Inspect when _inspectRawModified => Visibility.Visible,
            AppMode.Effects when _effectsImageBytes != null => Visibility.Visible,
            _ => Visibility.Collapsed,
        };
        MenuSave.IsEnabled = _currentMode switch
        {
            AppMode.Inpaint => hasInpaintImage,
            AppMode.Inspect => hasReaderImage && _inspectRawModified,
            AppMode.Effects => hasPostImage,
            _ => false,
        };

        MenuSaveAs.IsEnabled = _currentMode switch
        {
            AppMode.ImageGeneration => hasGenImage,
            AppMode.Inpaint => hasInpaintImage,
            AppMode.Upscale => hasUpscaleImage,
            AppMode.Effects => hasPostImage,
            AppMode.Inspect => hasReaderImage,
            _ => false,
        };

        MenuSaveStripped.Visibility = (_currentMode == AppMode.Inspect || _currentMode == AppMode.ImageGeneration)
            ? Visibility.Visible
            : Visibility.Collapsed;
        MenuSaveStripped.IsEnabled = _currentMode switch
        {
            AppMode.Inspect => hasReaderImage,
            AppMode.ImageGeneration => hasGenImage,
            _ => false,
        };
    }

    private static FontFamily SymbolFontFamily =>
        (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];

    private static FontFamily UiTextFontFamily => new("Microsoft YaHei UI");
    private const string UiLanguageTag = "zh-CN";

    private static ComboBoxItem CreateTextComboBoxItem(string text) => new()
    {
        Content = text,
        FontFamily = UiTextFontFamily,
        Language = UiLanguageTag,
    };

    private static void ApplyMenuTypography(object? item)
    {
        switch (item)
        {
            case MenuBarItem menuBarItem:
                menuBarItem.FontFamily = UiTextFontFamily;
                menuBarItem.Language = UiLanguageTag;
                foreach (var child in menuBarItem.Items)
                    ApplyMenuTypography(child);
                break;
            case MenuFlyoutSubItem subItem:
                subItem.FontFamily = UiTextFontFamily;
                subItem.Language = UiLanguageTag;
                foreach (var child in subItem.Items)
                    ApplyMenuTypography(child);
                break;
            case ToggleMenuFlyoutItem toggleItem:
                toggleItem.FontFamily = UiTextFontFamily;
                toggleItem.Language = UiLanguageTag;
                break;
            case MenuFlyoutItem menuItem:
                menuItem.FontFamily = UiTextFontFamily;
                menuItem.Language = UiLanguageTag;
                break;
            case ComboBox comboBox:
                comboBox.FontFamily = UiTextFontFamily;
                comboBox.Language = UiLanguageTag;
                foreach (var child in comboBox.Items)
                    ApplyMenuTypography(child);
                break;
            case ComboBoxItem comboBoxItem:
                comboBoxItem.FontFamily = UiTextFontFamily;
                comboBoxItem.Language = UiLanguageTag;
                break;
        }
    }

    private void ApplyStaticMenuAndComboTypography()
    {
        AppMenuBar.FontFamily = UiTextFontFamily;
        AppMenuBar.Language = UiLanguageTag;
        foreach (var item in AppMenuBar.Items)
            ApplyMenuTypography(item);

        ApplyMenuTypography(CboModel);
        ApplyMenuTypography(CboSize);
    }

    private static string? GetSelectedComboText(ComboBox comboBox) =>
        comboBox.SelectedItem switch
        {
            string text => text,
            ComboBoxItem { Content: string text } => text,
            _ => null,
        };

    private static bool IsWindows11OrGreater() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    private static Windows.UI.Color GetWindowSurfaceColor(bool isDark) =>
        isDark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
            : Windows.UI.Color.FromArgb(255, 243, 243, 243);

    private void ApplyWindowChrome(Window window, bool isDark, Panel? titleBarPanel = null, Panel? rootPanel = null)
    {
        var surfaceColor = GetWindowSurfaceColor(isDark);
        bool isMainWindow = ReferenceEquals(window, this);
        var titleBarBaseColor = isMainWindow
            ? Windows.UI.Color.FromArgb(0, 0, 0, 0)
            : surfaceColor;

        if (titleBarPanel != null)
            titleBarPanel.Background = new SolidColorBrush(surfaceColor);
        if (rootPanel != null)
            rootPanel.Background = new SolidColorBrush(surfaceColor);

        var appWindow = isMainWindow ? AppWindow : GetAppWindowForWindow(window);
        if (appWindow == null) return;
        if (!IsWindows11OrGreater() && appWindow.Presenter is OverlappedPresenter presenter)
        {
            try
            {
                // Win10 下保留系统标题栏按钮，但移除边框，规避顶部 1px 线。
                presenter.SetBorderAndTitleBar(false, true);
            }
            catch
            {
                // 某些系统/窗口状态可能不支持，忽略并继续使用默认行为。
            }
        }

        if (appWindow.TitleBar == null) return;

        var tb = appWindow.TitleBar;
        tb.ExtendsContentIntoTitleBar = true;
        tb.BackgroundColor = titleBarBaseColor;
        tb.InactiveBackgroundColor = titleBarBaseColor;
        tb.ButtonBackgroundColor = titleBarBaseColor;
        tb.ButtonInactiveBackgroundColor = titleBarBaseColor;

        if (isDark)
        {
            tb.ForegroundColor = Colors.White;
            tb.InactiveForegroundColor = Windows.UI.Color.FromArgb(180, 255, 255, 255);
            tb.ButtonForegroundColor = Colors.White;
            tb.ButtonHoverForegroundColor = Colors.White;
            tb.ButtonPressedForegroundColor = Colors.White;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 255, 255, 255);
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(30, 255, 255, 255);
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(60, 255, 255, 255);
        }
        else
        {
            tb.ForegroundColor = Colors.Black;
            tb.InactiveForegroundColor = Windows.UI.Color.FromArgb(180, 0, 0, 0);
            tb.ButtonForegroundColor = Colors.Black;
            tb.ButtonHoverForegroundColor = Colors.Black;
            tb.ButtonPressedForegroundColor = Colors.Black;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 0, 0, 0);
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 0, 0, 0);
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 0, 0, 0);
        }
    }

    private void OnAppTitleBarDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 禁止双击自定义标题栏触发最大化/还原。
        e.Handled = true;
    }

    private void OnResetGenParams(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            _settings.Settings.GenParameters = CreateDefaultGenerationParameters();
        }
        else if (_currentMode == AppMode.Inpaint)
        {
            _settings.Settings.InpaintParameters = CreateDefaultInpaintParameters();
        }
        _settings.Save();
        SyncParamsToUI();
        UpdateModelDependentUI();
        TxtStatus.Text = "已重置生成参数为默认值";
    }

    private void OnClearAllPrompts(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            _genPositivePrompt = "";
            _genNegativePrompt = "";
            _genStylePrompt = "";
            _genCharacters.Clear();
            ClearReferenceFeatures();
            RefreshCharacterPanel();
        }
        else if (_currentMode == AppMode.Inpaint)
        {
            _inpaintPositivePrompt = "";
            _inpaintNegativePrompt = "";
            _inpaintStylePrompt = "";
            _genCharacters.Clear();
            ClearReferenceFeatures();
            RefreshCharacterPanel();
        }
        TxtPrompt.Text = "";
        TxtStylePrompt.Text = "";
        UpdatePromptHighlights();
        UpdateStyleHighlights();
        TxtStatus.Text = "已清空所有提示词";
    }

    private void BuildInpaintEditMenuItems(MenuBarItem menu)
    {
        var undoItem = CreateLocalizedMenuItem(MenuCommandUndo, "menu.edit.undo", new SymbolIcon(Symbol.Undo));
        undoItem.Click += OnUndo;
        undoItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.Z });
        menu.Items.Add(undoItem);

        var redoItem = CreateLocalizedMenuItem(MenuCommandRedo, "menu.edit.redo", new SymbolIcon(Symbol.Redo));
        redoItem.Click += OnRedo;
        redoItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.Y });
        menu.Items.Add(redoItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var maskSub = CreateLocalizedSubItem(
            MenuCommandMaskOps,
            "menu.inpaint.mask_ops",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE7C3" });
        var fillItem = CreateLocalizedMenuItem(
            "fill_empty",
            "menu.inpaint.fill_empty",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE771" });
        fillItem.Click += OnFillEmpty;
        fillItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, Key = Windows.System.VirtualKey.I });
        maskSub.Items.Add(fillItem);

        var invertItem = CreateLocalizedMenuItem(
            "invert_mask",
            "menu.inpaint.invert_mask",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE895" });
        invertItem.Click += OnInvertMask;
        invertItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.I });
        maskSub.Items.Add(invertItem);

        var expandItem = CreateLocalizedMenuItem(
            MenuCommandExpandMask,
            "menu.inpaint.expand_mask",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE744" });
        expandItem.KeyboardAcceleratorTextOverride = "Ctrl++";
        expandItem.Click += OnExpandMask;
        maskSub.Items.Add(expandItem);

        var shrinkItem = CreateLocalizedMenuItem(
            MenuCommandContractMask,
            "menu.inpaint.contract_mask",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE743" });
        shrinkItem.KeyboardAcceleratorTextOverride = "Ctrl+-";
        shrinkItem.Click += OnShrinkMask;
        maskSub.Items.Add(shrinkItem);

        var clearItem = CreateLocalizedMenuItem("clear_mask", "menu.inpaint.clear_mask", new SymbolIcon(Symbol.Delete));
        clearItem.Click += OnClearMask;
        clearItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.D });
        maskSub.Items.Add(clearItem);
        menu.Items.Add(maskSub);

        var trimItem = CreateLocalizedMenuItem(
            "trim_canvas",
            "menu.inpaint.trim_canvas",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE7A8" });
        trimItem.Click += OnTrimCanvas;
        menu.Items.Add(trimItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var alignSub = CreateLocalizedSubItem(MenuCommandAlignImage, "menu.inpaint.align_image");
        string[,] alignments =
        {
            {"align.top_left","TL"}, {"align.top_center","TC"}, {"align.top_right","TR"},
            {"-",""}, {"align.center_left","CL"}, {"align.center","CC"}, {"align.center_right","CR"},
            {"-",""}, {"align.bottom_left","BL"}, {"align.bottom_center","BC"}, {"align.bottom_right","BR"},
        };
        for (int i = 0; i < alignments.GetLength(0); i++)
        {
            if (alignments[i, 0] == "-")
                alignSub.Items.Add(new MenuFlyoutSeparator());
            else
            {
                var ai = new MenuFlyoutItem { Text = L(alignments[i, 0]), Tag = alignments[i, 1] };
                ai.Click += OnAlign;
                alignSub.Items.Add(ai);
            }
        }
        menu.Items.Add(alignSub);

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(BuildPresetResolutionSubMenu());
        menu.Items.Add(new MenuFlyoutSeparator());

        var inferSub = CreateLocalizedSubItem(
            MenuCommandPromptInference,
            "menu.inpaint.prompt_inference",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8A5" });
        var inferGlobalItem = CreateLocalizedMenuItem(
            "infer_global",
            "menu.inpaint.infer_global",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE9A6" });
        inferGlobalItem.Click += async (_, _) => await RunInpaintPromptInferenceAsync(canvasOnly: false);
        inferSub.Items.Add(inferGlobalItem);

        var inferCanvasItem = CreateLocalizedMenuItem(
            "infer_canvas",
            "menu.inpaint.infer_canvas",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE799" });
        inferCanvasItem.Click += async (_, _) => await RunInpaintPromptInferenceAsync(canvasOnly: true);
        inferSub.Items.Add(inferCanvasItem);
        menu.Items.Add(inferSub);

        menu.Items.Add(new MenuFlyoutSeparator());

        var postItem = CreateLocalizedMenuItem(
            MenuCommandSendToPost,
            "action.send_to_post",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" });
        postItem.Click += OnSendToEffectsFromInpaint;
        menu.Items.Add(postItem);

        var upscaleItem = CreateLocalizedMenuItem(
            MenuCommandSendToUpscale,
            "action.send_to_upscale",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9" });
        upscaleItem.Click += OnSendToUpscaleFromInpaint;
        menu.Items.Add(upscaleItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var normalizeItem = CreateLocalizedMenuItem(
            MenuCommandNormalizePrompts,
            "menu.edit.normalize_prompts",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8D2" });
        normalizeItem.Click += OnNormalizePrompts;
        menu.Items.Add(normalizeItem);

        var randomStyleItem = CreateLocalizedMenuItem(
            MenuCommandRandomStylePrompt,
            "menu.edit.random_style_prompt",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B1" });
        randomStyleItem.Click += OnRandomStylePrompt;
        menu.Items.Add(randomStyleItem);
        menu.Items.Add(BuildPromptShortcutsMenuItem());

        menu.Items.Add(new MenuFlyoutSeparator());
        var clearAllItem = CreateLocalizedMenuItem(
            MenuCommandClearAllPrompts,
            "menu.edit.clear_all_prompts",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74D" });
        clearAllItem.Click += OnClearAllPrompts;
        menu.Items.Add(clearAllItem);

        var resetParamsItem = CreateLocalizedMenuItem(
            MenuCommandResetGenerationParams,
            "menu.edit.reset_generation_params",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE777" });
        resetParamsItem.Click += OnResetGenParams;
        menu.Items.Add(resetParamsItem);
    }

    private MenuFlyoutSubItem BuildPresetResolutionSubMenu()
    {
        var presetSub = CreateLocalizedSubItem(
            "preset_resolution",
            "menu.edit.preset_resolution",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE740" });
        foreach (var p in MaskCanvasControl.CanvasPresets)
        {
            string glyph = p.W == p.H
                ? "\uF16B"
                : p.W > p.H
                    ? "\uF5A1"
                    : "\uF599";
            var item = new MenuFlyoutItem
            {
                Text = p.Label,
                Tag = (p.W, p.H),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = glyph },
            };
            item.Click += OnPresetResolutionSelected;
            presetSub.Items.Add(item);
        }
        return presetSub;
    }

    private MenuFlyoutItem BuildPromptShortcutsMenuItem()
    {
        var item = CreateLocalizedMenuItem(
            MenuCommandPromptShortcuts,
            "menu.edit.prompt_shortcuts",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8A7" });
        item.Click += OnPromptShortcuts;
        return item;
    }

    private sealed class WeightedSpan
    {
        public WeightedSpan(string text, double weight)
        {
            Text = text;
            Weight = weight;
        }

        public string Text { get; }
        public double Weight { get; }
    }

    private bool _isWeightDialogOpen;
    private bool _isVibeEncodeDialogOpen;

    private static PromptWeightFormat IndexToPromptWeightFormat(int index) => index switch
    {
        0 => PromptWeightFormat.StableDiffusion,
        1 => PromptWeightFormat.NaiClassic,
        _ => PromptWeightFormat.NaiNumeric,
    };

    private static int PromptWeightFormatToIndex(PromptWeightFormat format) => format switch
    {
        PromptWeightFormat.StableDiffusion => 0,
        PromptWeightFormat.NaiClassic => 1,
        _ => 2,
    };

    private bool CanApplyWeightConversionToCurrentWorkspace() =>
        _currentMode switch
        {
            AppMode.ImageGeneration => true,
            AppMode.Inpaint => true,
            AppMode.Inspect => _inspectMetadata != null &&
                              (_inspectMetadata.IsNaiParsed || _inspectMetadata.IsSdFormat || _inspectMetadata.IsModelInference),
            _ => false,
        };

    private async void ShowWeightConversionDialog()
    {
        if (_isWeightDialogOpen) return;
        _isWeightDialogOpen = true;

        try
        {
            const double panelWidth = 350;
            const double panelGap = 20;
            const double swapColumnWidth = 56;
            string[] formatLabels = { "SD-WebUI (小括号内冒号数字权重)", "NovelAI 1~3 (大/中括号嵌套)", "NovelAI 4+ (数字权重+封闭双冒号)" };

            SaveCurrentPromptToBuffer();
            string initialText = _currentMode switch
            {
                AppMode.ImageGeneration => _genPositivePrompt,
                AppMode.Inpaint => _inpaintPositivePrompt,
                AppMode.Inspect when _inspectMetadata != null => _inspectMetadata.PositivePrompt ?? string.Empty,
                _ => string.Empty,
            };

            var sourceCombo = new ComboBox
            {
                Width = panelWidth,
                MinWidth = panelWidth,
                MaxWidth = panelWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = formatLabels,
                SelectedIndex = PromptWeightFormatToIndex(PromptWeightFormat.StableDiffusion),
            };
            ApplyMenuTypography(sourceCombo);

            var targetCombo = new ComboBox
            {
                Width = panelWidth,
                MinWidth = panelWidth,
                MaxWidth = panelWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = formatLabels,
                SelectedIndex = PromptWeightFormatToIndex(PromptWeightFormat.NaiNumeric),
            };
            ApplyMenuTypography(targetCombo);

            var swapBtn = new Button
            {
                Content = "\u21C4",
                Width = 40,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(swapBtn, "交换源格式与目标格式");

            var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelWidth) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelGap + swapColumnWidth) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelWidth) });
            Grid.SetColumn(sourceCombo, 0);
            Grid.SetColumn(swapBtn, 1);
            Grid.SetColumn(targetCombo, 2);
            topGrid.Children.Add(sourceCombo);
            topGrid.Children.Add(swapBtn);
            topGrid.Children.Add(targetCombo);

            var inputBox = new TextBox
            {
                Width = panelWidth,
                MinWidth = panelWidth,
                MaxWidth = panelWidth,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                PlaceholderText = "输入提示词...",
                MinHeight = 240,
                MaxHeight = 240,
                VerticalAlignment = VerticalAlignment.Stretch,
                Text = initialText,
            };

            var outputBox = new TextBox
            {
                Width = panelWidth,
                MinWidth = panelWidth,
                MaxWidth = panelWidth,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true,
                PlaceholderText = "转换结果...",
                MinHeight = 240,
                MaxHeight = 240,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            var textGrid = new Grid();
            textGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelWidth) });
            textGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelGap + swapColumnWidth) });
            textGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelWidth) });
            Grid.SetColumn(inputBox, 0);
            Grid.SetColumn(outputBox, 2);
            textGrid.Children.Add(inputBox);
            textGrid.Children.Add(outputBox);

            var mainPanel = new StackPanel();
            mainPanel.Children.Add(topGrid);
            mainPanel.Children.Add(textGrid);

            void DoConvert()
            {
                var source = IndexToPromptWeightFormat(sourceCombo.SelectedIndex);
                var target = IndexToPromptWeightFormat(targetCombo.SelectedIndex);
                outputBox.Text = ConvertPromptWeightSyntax(inputBox.Text, source, target);
            }

            inputBox.TextChanged += (_, _) => DoConvert();
            sourceCombo.SelectionChanged += (_, _) => DoConvert();
            targetCombo.SelectionChanged += (_, _) => DoConvert();
            swapBtn.Click += (_, _) =>
            {
                int sourceIndex = sourceCombo.SelectedIndex;
                sourceCombo.SelectedIndex = targetCombo.SelectedIndex;
                targetCombo.SelectedIndex = sourceIndex;
                inputBox.Text = outputBox.Text;
            };

            DoConvert();

            bool canApply = CanApplyWeightConversionToCurrentWorkspace();
            var dialog = new ContentDialog
            {
                Title = "权重转换",
                Content = mainPanel,
                CloseButtonText = "关闭",
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };
            if (canApply)
            {
                dialog.PrimaryButtonText = "发送到当前工作区";
                dialog.DefaultButton = ContentDialogButton.Primary;
            }
            else
            {
                dialog.DefaultButton = ContentDialogButton.Close;
            }
            dialog.Resources["ContentDialogMaxWidth"] = 860.0;

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var source = IndexToPromptWeightFormat(sourceCombo.SelectedIndex);
                var target = IndexToPromptWeightFormat(targetCombo.SelectedIndex);
                ConvertPromptWeightsInCurrentWorkspace(source, target);
            }
        }
        finally
        {
            _isWeightDialogOpen = false;
        }
    }

    private async void ShowVibeEncodeDialog()
    {
        if (_isVibeEncodeDialogOpen) return;
        _isVibeEncodeDialogOpen = true;

        try
        {
            byte[]? selectedImageBytes = null;
            string? selectedFileName = null;

            string[] vibeModels = ["nai-diffusion-4-5-curated", "nai-diffusion-4-5-full", "nai-diffusion-4-curated", "nai-diffusion-4-full"];

            var modelCombo = new ComboBox
            {
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                ItemsSource = vibeModels,
                SelectedIndex = 0,
            };
            ApplyMenuTypography(modelCombo);

            var fileNameBlock = new TextBlock
            {
                Text = "未选择图片",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260,
                Opacity = 0.6,
            };

            var browseBtn = new Button
            {
                Content = "选择图片...",
                MinWidth = 100,
            };

            var thumbImage = new Microsoft.UI.Xaml.Controls.Image
            {
                Width = 120,
                Height = 120,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };

            var ieSlider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                StepFrequency = 0.01,
                Value = 1.0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var ieValueText = new TextBlock
            {
                Text = "1.00",
                MinWidth = 38,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ieSlider.ValueChanged += (_, args) =>
            {
                ieValueText.Text = args.NewValue.ToString("0.00");
            };

            var statusBlock = new TextBlock
            {
                Text = "",
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(0, 8, 0, 0),
                Opacity = 0.8,
            };

            var encodeBtn = new Button
            {
                Content = CreateAnlasActionButtonContent("开始编码", 2),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                IsEnabled = false,
                Margin = new Thickness(0, 4, 0, 0),
            };
            ApplyGoldAccentButtonStyle(encodeBtn);

            browseBtn.Click += async (_, _) =>
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".bmp");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(this));

                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                byte[]? pngBytes = await ReadImageFileAsPngAsync(file);
                if (pngBytes == null || pngBytes.Length == 0)
                {
                    statusBlock.Text = $"无法读取图像：{file.Name}";
                    return;
                }

                selectedImageBytes = pngBytes;
                selectedFileName = file.Name;
                fileNameBlock.Text = file.Name;
                fileNameBlock.Opacity = 1.0;
                encodeBtn.IsEnabled = true;
                statusBlock.Text = "";

                try
                {
                    using var ms = new MemoryStream(pngBytes);
                    var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                    thumbImage.Source = bmp;
                    thumbImage.Visibility = Visibility.Visible;
                }
                catch
                {
                    thumbImage.Visibility = Visibility.Collapsed;
                }
            };

            encodeBtn.Click += async (_, _) =>
            {
                if (selectedImageBytes == null)
                {
                    statusBlock.Text = "请先选择图片。";
                    return;
                }

                if (!_settings.Settings.MaxMode)
                {
                    var warnDialog = new ContentDialog
                    {
                        Title = "需要 Max 模式",
                        Content = "氛围预编码功能会消耗 Anlas（每次 2 Anlas）。\n请在 设置 → 网络/API设置 中启用 Max 模式后使用。",
                        CloseButtonText = "确定",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = this.Content.XamlRoot,
                        RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
                    };
                    await warnDialog.ShowAsync();
                    return;
                }

                string model = vibeModels[modelCombo.SelectedIndex];
                double ie = Math.Round(ieSlider.Value, 2);
                string cacheDir = VibeCacheService.GetCacheDir(AppRootDir);

                string? cached = VibeCacheService.TryGetCachedVibe(cacheDir, selectedImageBytes, ie);
                if (cached != null)
                {
                    statusBlock.Text = $"缓存命中！已有该图片在 IE={ie:0.00} 下的编码，无需重复请求。\n保存位置: {cacheDir}";
                    return;
                }

                encodeBtn.IsEnabled = false;
                browseBtn.IsEnabled = false;
                statusBlock.Text = "正在编码，请稍候...（消耗 2 Anlas）";

                string imageBase64 = Convert.ToBase64String(selectedImageBytes);
                DebugLog($"[VibeEncode] Start | Model={model} | IE={ie:0.00}");
                var (vibeData, error) = await _naiService.EncodeVibeAsync(imageBase64, model, ie);

                if (vibeData != null && vibeData.Length > 0)
                {
                    string savePath = VibeCacheService.SaveVibe(cacheDir, selectedImageBytes, vibeData, ie, model);
                    DebugLog($"[VibeEncode] Completed | Saved={savePath}");
                    statusBlock.Text = $"编码成功！已保存到:\n{savePath}";
                    _ = RefreshAnlasInfoAsync(forceRefresh: true);
                }
                else
                {
                    DebugLog($"[VibeEncode] Failed: {error ?? "Unknown error"}");
                    statusBlock.Text = $"编码失败: {error ?? "未知错误"}";
                }

                encodeBtn.IsEnabled = true;
                browseBtn.IsEnabled = true;
            };

            var rootGrid = (Grid)this.Content;

            var panel = new StackPanel { Spacing = 10, MinWidth = 400 };

            panel.Children.Add(CreateThemedSubLabel("模型"));
            panel.Children.Add(modelCombo);

            var fileRow = new Grid { ColumnSpacing = 8 };
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(fileNameBlock, 0);
            Grid.SetColumn(browseBtn, 1);
            fileRow.Children.Add(fileNameBlock);
            fileRow.Children.Add(browseBtn);

            panel.Children.Add(CreateThemedSubLabel("参考图片"));
            panel.Children.Add(fileRow);
            panel.Children.Add(thumbImage);

            panel.Children.Add(CreateThemedSubLabel("信息提取强度（影响编码结果）"));
            var ieGrid = new Grid();
            ieGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ieGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(ieSlider, 0);
            Grid.SetColumn(ieValueText, 1);
            ieGrid.Children.Add(ieSlider);
            ieGrid.Children.Add(ieValueText);
            panel.Children.Add(ieGrid);

            panel.Children.Add(encodeBtn);
            panel.Children.Add(statusBlock);

            var hintBlock = new TextBlock
            {
                Text = "提示: 编码结果按 图片SHA256+IE值 缓存，相同图片和参数不会重复请求。",
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.5,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
            };
            panel.Children.Add(hintBlock);

            var dialog = new ContentDialog
            {
                Title = "氛围预编码",
                Content = panel,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 520.0;

            await dialog.ShowAsync();
        }
        finally
        {
            _isVibeEncodeDialogOpen = false;
        }
    }

    private async void ShowWildcardDialog()
    {
        if (_isWildcardDialogOpen) return;
        _isWildcardDialogOpen = true;

        try
        {
            string rootDir = GetWildcardsRootDir();
            Directory.CreateDirectory(rootDir);

            WildcardIndexEntry? selectedEntry = null;
            string currentRelativePath = "";

            var breadcrumbIcon = new FontIcon
            {
                FontFamily = SymbolFontFamily,
                Glyph = "\uEDA2",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var breadcrumb = new BreadcrumbBar();
            var breadcrumbItems = new System.Collections.ObjectModel.ObservableCollection<string>();
            breadcrumb.ItemsSource = breadcrumbItems;

            var breadcrumbRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            breadcrumbRow.Children.Add(breadcrumbIcon);
            breadcrumbRow.Children.Add(breadcrumb);

            var browserList = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),
            };

            var treeScroller = new ScrollViewer
            {
                Content = browserList,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            var editorBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 0,
                IsSpellCheckEnabled = false,
                IsEnabled = false,
                PlaceholderText = "选择一个条目后在此编辑内容",
            };
            ScrollViewer.SetVerticalScrollBarVisibility(editorBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(editorBox, ScrollBarVisibility.Auto);

            var metaBlock = new TextBlock
            {
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.8,
                FontSize = 12,
            };

            var statsBlock = new TextBlock
            {
                TextWrapping = TextWrapping.WrapWholeWords,
                FontSize = 12,
            };

            var listItemPathMap = new Dictionary<FrameworkElement, string>();
            var listItemEntryMap = new Dictionary<FrameworkElement, WildcardIndexEntry>();
            var expandedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            static string GetEntryDirectoryName(string entryName)
            {
                int slashIndex = entryName.LastIndexOf('/');
                return slashIndex >= 0 ? entryName[..slashIndex] : "";
            }

            FrameworkElement CreateListRow(string glyph, string label, string? detail, int depth, bool isBold)
            {
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Padding = new Thickness(depth * 16, 4, 8, 4),
                };
                sp.Children.Add(new FontIcon
                {
                    FontFamily = SymbolFontFamily,
                    Glyph = glyph,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isBold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                });
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = detail,
                        Opacity = 0.55,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                return sp;
            }

            void UpdateBreadcrumbItems()
            {
                breadcrumbItems.Clear();
                breadcrumbItems.Add("wildcards");
                if (!string.IsNullOrEmpty(currentRelativePath))
                {
                    foreach (string part in currentRelativePath.Split('/'))
                    {
                        if (!string.IsNullOrEmpty(part))
                            breadcrumbItems.Add(part);
                    }
                }
            }

            void UpdateDirectoryMeta(string? detail = null)
            {
                string dirLabel = string.IsNullOrEmpty(currentRelativePath) ? "wildcards" : currentRelativePath.Replace('/', '/');
                metaBlock.Text = string.IsNullOrWhiteSpace(detail)
                    ? $"当前目录: {dirLabel}"
                    : detail;
                statsBlock.Text = $"已索引文件: {_wildcardService.FileCount}  |  候选总数: {_wildcardService.OptionCount}";
            }

            void SelectEntry(WildcardIndexEntry entry)
            {
                selectedEntry = entry;
                currentRelativePath = GetEntryDirectoryName(entry.Name);
                UpdateBreadcrumbItems();
                editorBox.IsEnabled = true;
                editorBox.Text = File.Exists(entry.FilePath)
                    ? File.ReadAllText(entry.FilePath, Encoding.UTF8) : "";
                UpdateDirectoryMeta(
                    $"条目: {entry.Name}  |  候选数: {entry.OptionCount}  |  " +
                    $"更新: {entry.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }

            void ClearEntrySelectionForDirectory(string relativePath)
            {
                selectedEntry = null;
                currentRelativePath = relativePath;
                UpdateBreadcrumbItems();
                editorBox.IsEnabled = false;
                editorBox.Text = "";
                UpdateDirectoryMeta("浏览或选择一个条目进行编辑。");
            }

            void PopulateDirectoryRecursive(string relativePath, int depth)
            {
                bool isExpanded = expandedDirs.Contains(relativePath);

                foreach (string dir in _wildcardService.ListSubDirectories(relativePath))
                {
                    string childPath = string.IsNullOrEmpty(relativePath) ? dir : $"{relativePath}/{dir}";
                    bool childExpanded = expandedDirs.Contains(childPath);
                    string arrow = childExpanded ? "▾" : "▸";
                    var row = (FrameworkElement)CreateListRow("\uE8B7", $"{arrow} {dir}/", null, depth, isBold: true);
                    listItemPathMap[row] = childPath;
                    browserList.Items.Add(row);

                    if (childExpanded)
                        PopulateDirectoryRecursive(childPath, depth + 1);
                }

                foreach (var entry in _wildcardService.ListEntriesInDirectory(relativePath))
                {
                    string shortName = entry.Name.Contains('/')
                        ? entry.Name[(entry.Name.LastIndexOf('/') + 1)..]
                        : entry.Name;
                    var row = (FrameworkElement)CreateListRow("\uE8A5", shortName + ".txt", $"{entry.OptionCount} 项", depth, isBold: false);
                    listItemEntryMap[row] = entry;
                    browserList.Items.Add(row);
                }
            }

            void RebuildBrowserTree(string? preferredEntryName = null, string? preferredDirectory = null, bool autoExpand = true)
            {
                listItemPathMap.Clear();
                listItemEntryMap.Clear();
                browserList.Items.Clear();

                if (autoExpand && !string.IsNullOrWhiteSpace(preferredDirectory) && !expandedDirs.Contains(preferredDirectory))
                {
                    var parts = preferredDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    string accum = "";
                    foreach (var part in parts)
                    {
                        accum = string.IsNullOrEmpty(accum) ? part : $"{accum}/{part}";
                        expandedDirs.Add(accum);
                    }
                }

                PopulateDirectoryRecursive("", 0);

                if (!string.IsNullOrWhiteSpace(preferredEntryName))
                {
                    foreach (var kvp in listItemEntryMap)
                    {
                        if (string.Equals(kvp.Value.Name, preferredEntryName, StringComparison.OrdinalIgnoreCase))
                        {
                            browserList.SelectedItem = kvp.Key;
                            SelectEntry(kvp.Value);
                            return;
                        }
                    }
                }
                ClearEntrySelectionForDirectory(preferredDirectory ?? currentRelativePath);
            }

            breadcrumb.ItemClicked += (_, args) =>
            {
                int clickedIndex = breadcrumbItems.IndexOf(args.Item?.ToString() ?? "");
                if (clickedIndex <= 0)
                {
                    RebuildBrowserTree(preferredDirectory: "");
                }
                else
                {
                    var parts = new List<string>();
                    for (int i = 1; i <= clickedIndex; i++)
                        parts.Add(breadcrumbItems[i]);
                    RebuildBrowserTree(preferredDirectory: string.Join("/", parts));
                }
            };

            browserList.SelectionChanged += (_, _) =>
            {
                if (browserList.SelectedItem is not FrameworkElement fe) return;

                if (listItemEntryMap.TryGetValue(fe, out var entry))
                {
                    SelectEntry(entry);
                }
                else if (listItemPathMap.TryGetValue(fe, out var dirPath))
                {
                    bool wasExpanded = expandedDirs.Contains(dirPath);
                    if (wasExpanded)
                        expandedDirs.Remove(dirPath);
                    else
                        expandedDirs.Add(dirPath);
                    RebuildBrowserTree(preferredDirectory: dirPath, autoExpand: !wasExpanded);
                }
            };

            var openBtn = new Button { Content = "打开目录", MinWidth = 96 };
            openBtn.Click += (_, _) =>
            {
                string targetDir = string.IsNullOrEmpty(currentRelativePath)
                    ? rootDir : Path.Combine(rootDir, currentRelativePath.Replace('/', '\\'));
                Directory.CreateDirectory(targetDir);
                System.Diagnostics.Process.Start("explorer.exe", targetDir);
            };

            var reloadBtn = new Button { Content = "重新扫描", MinWidth = 96 };
            reloadBtn.Click += (_, _) =>
            {
                LoadWildcards();
                RebuildBrowserTree(selectedEntry?.Name, currentRelativePath);
                TxtStatus.Text = "抽卡器索引已重载";
            };

            var saveBtn = new Button
            {
                Width = 32, Height = 32,
                Padding = new Thickness(0),
                Content = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74E" },
            };
            ToolTipService.SetToolTip(saveBtn, "保存当前条目");
            saveBtn.Click += (_, _) =>
            {
                if (selectedEntry == null)
                {
                    TxtStatus.Text = "当前没有可保存的抽卡器条目";
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(selectedEntry.FilePath)!);
                File.WriteAllText(selectedEntry.FilePath, editorBox.Text ?? "", Encoding.UTF8);
                LoadWildcards();
                RebuildBrowserTree(selectedEntry.Name, currentRelativePath);
                TxtStatus.Text = $"已保存抽卡器：{selectedEntry.Name}";
            };

            var rightPanel = new Grid { RowSpacing = 8 };
            rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var editorHeader = new Grid { ColumnSpacing = 8 };
            editorHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editorHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var editorLabel = new TextBlock
            {
                Text = "条目编辑",
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(editorLabel, 0);
            Grid.SetColumn(saveBtn, 1);
            editorHeader.Children.Add(editorLabel);
            editorHeader.Children.Add(saveBtn);
            Grid.SetRow(editorHeader, 0);
            Grid.SetRow(editorBox, 1);
            rightPanel.Children.Add(editorHeader);
            rightPanel.Children.Add(editorBox);

            var bottomButtonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            bottomButtonRow.Children.Add(openBtn);
            bottomButtonRow.Children.Add(reloadBtn);

            var bodyGrid = new Grid
            {
                ColumnSpacing = 12,
                Height = 420,
            };
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240, GridUnitType.Pixel) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(treeScroller, 0);
            Grid.SetColumn(rightPanel, 1);
            bodyGrid.Children.Add(treeScroller);
            bodyGrid.Children.Add(rightPanel);

            var footerGrid = new Grid { ColumnSpacing = 12 };
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metaBlock.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(statsBlock, 0);
            Grid.SetColumn(metaBlock, 1);
            footerGrid.Children.Add(statsBlock);
            footerGrid.Children.Add(metaBlock);

            var panel = new StackPanel
            {
                Spacing = 10,
                Width = 840,
            };
            panel.Children.Add(breadcrumbRow);
            panel.Children.Add(bodyGrid);
            panel.Children.Add(footerGrid);
            panel.Children.Add(bottomButtonRow);

            RebuildBrowserTree(preferredDirectory: "");

            var dialog = new ContentDialog
            {
                Title = "抽卡器",
                Content = panel,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 900.0;

            await dialog.ShowAsync();
        }
        finally
        {
            _isWildcardDialogOpen = false;
        }
    }

    private void ConvertPromptWeightsInCurrentWorkspace(PromptWeightFormat source, PromptWeightFormat target)
    {
        if (source == target)
        {
            TxtStatus.Text = "源格式与目标格式相同，无需转换";
            return;
        }

        if (_currentMode == AppMode.Effects)
        {
            TxtStatus.Text = "当前工作区没有可转换的提示词";
            return;
        }

        if (_currentMode == AppMode.Inspect)
        {
            if (_inspectMetadata == null || (!_inspectMetadata.IsNaiParsed && !_inspectMetadata.IsSdFormat && !_inspectMetadata.IsModelInference))
            {
                TxtStatus.Text = "检视工作区没有可转换的提示词";
                return;
            }

            _inspectMetadata.PositivePrompt = ConvertPromptWeightSyntax(_inspectMetadata.PositivePrompt, source, target);
            _inspectMetadata.NegativePrompt = ConvertPromptWeightSyntax(_inspectMetadata.NegativePrompt, source, target);
            _inspectMetadata.CharacterPrompts = _inspectMetadata.CharacterPrompts
                .Select(x => ConvertPromptWeightSyntax(x, source, target))
                .ToList();
            _inspectMetadata.CharacterNegativePrompts = _inspectMetadata.CharacterNegativePrompts
                .Select(x => ConvertPromptWeightSyntax(x, source, target))
                .ToList();
            _inspectMetadata.IsSdFormat = target == PromptWeightFormat.StableDiffusion;

            DisplayInspectMetadata(_inspectMetadata);
            UpdateDynamicMenuStates();
            TxtStatus.Text = $"已将检视工作区提示词从 {GetPromptWeightFormatLabel(source)} 转为 {GetPromptWeightFormatLabel(target)}";
            return;
        }

        SaveCurrentPromptToBuffer();
        SaveAllCharacterPrompts();

        if (_currentMode == AppMode.ImageGeneration)
        {
            _genPositivePrompt = ConvertPromptWeightSyntax(_genPositivePrompt, source, target);
            _genNegativePrompt = ConvertPromptWeightSyntax(_genNegativePrompt, source, target);
            _genStylePrompt = ConvertPromptWeightSyntax(_genStylePrompt, source, target);
        }
        else
        {
            _inpaintPositivePrompt = ConvertPromptWeightSyntax(_inpaintPositivePrompt, source, target);
            _inpaintNegativePrompt = ConvertPromptWeightSyntax(_inpaintNegativePrompt, source, target);
            _inpaintStylePrompt = ConvertPromptWeightSyntax(_inpaintStylePrompt, source, target);
        }

        foreach (var entry in _genCharacters)
        {
            entry.PositivePrompt = ConvertPromptWeightSyntax(entry.PositivePrompt, source, target);
            entry.NegativePrompt = ConvertPromptWeightSyntax(entry.NegativePrompt, source, target);
        }

        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        RefreshCharacterPanel();
        UpdatePromptHighlights();
        UpdateStyleHighlights();
        TxtStatus.Text = $"已将当前工作区提示词从 {GetPromptWeightFormatLabel(source)} 转为 {GetPromptWeightFormatLabel(target)}";
    }

    private static string ConvertPromptWeightSyntax(string text, PromptWeightFormat source, PromptWeightFormat target)
    {
        if (string.IsNullOrEmpty(text) || source == target) return text;

        List<WeightedSpan> spans = ParsePromptWeightedSpans(text, source);
        return RenderPromptWeightedSpans(spans, target);
    }

    private static List<WeightedSpan> ParsePromptWeightedSpans(string text, PromptWeightFormat format)
    {
        var spans = new List<WeightedSpan>();
        ParsePromptWeightedSpans(text, format, 1.0, spans);
        return MergeWeightedSpans(spans);
    }

    private static void ParsePromptWeightedSpans(string text, PromptWeightFormat format, double weight, List<WeightedSpan> spans)
    {
        var buffer = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (format == PromptWeightFormat.NaiNumeric &&
                TryReadNaiNumericSegment(text, i, out double numericWeight, out string numericInner, out int numericNext))
            {
                AppendWeightedSpan(spans, buffer, weight);
                ParsePromptWeightedSpans(numericInner, format, weight * numericWeight, spans);
                i = numericNext;
                continue;
            }

            if (TryReadBracketedSegment(text, format, i, out double segmentWeight, out string segmentInner, out int segmentNext))
            {
                AppendWeightedSpan(spans, buffer, weight);
                ParsePromptWeightedSpans(segmentInner, format, weight * segmentWeight, spans);
                i = segmentNext;
                continue;
            }

            buffer.Append(text[i]);
            i++;
        }

        AppendWeightedSpan(spans, buffer, weight);
    }

    private static bool TryReadBracketedSegment(string text, PromptWeightFormat format, int start, out double weight, out string inner, out int next)
    {
        weight = 1.0;
        inner = "";
        next = start;
        if (start < 0 || start >= text.Length) return false;

        char open = text[start];
        char close;
        switch (format)
        {
            case PromptWeightFormat.StableDiffusion when open == '(' || open == '[':
                close = open == '(' ? ')' : ']';
                break;
            case PromptWeightFormat.NaiClassic when open == '{' || open == '[':
            case PromptWeightFormat.NaiNumeric when open == '{' || open == '[':
                close = open == '{' ? '}' : ']';
                break;
            default:
                return false;
        }

        if (!TryReadDelimitedGroup(text, start, open, close, out string rawInner, out next))
            return false;

        if (format == PromptWeightFormat.StableDiffusion)
        {
            if (TryParseTrailingExplicitWeight(rawInner, out string explicitInner, out double explicitWeight))
            {
                inner = explicitInner;
                weight = explicitWeight;
            }
            else
            {
                inner = rawInner;
                weight = open == '(' ? 1.1 : 1.0 / 1.1;
            }
            return true;
        }

        inner = rawInner;
        weight = open == '{' ? 1.05 : 1.0 / 1.05;
        return true;
    }

    private static bool TryReadDelimitedGroup(string text, int start, char open, char close, out string inner, out int next)
    {
        inner = "";
        next = start;
        if (start < 0 || start >= text.Length || text[start] != open) return false;

        int depth = 1;
        for (int i = start + 1; i < text.Length; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    inner = text.Substring(start + 1, i - start - 1);
                    next = i + 1;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseTrailingExplicitWeight(string text, out string inner, out double weight)
    {
        inner = text;
        weight = 1.0;
        int depth = 0;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char ch = text[i];
            if (ch == ')' || ch == ']' || ch == '}') depth++;
            else if (ch == '(' || ch == '[' || ch == '{') depth--;
            else if (ch == ':' && depth == 0)
            {
                string weightText = text[(i + 1)..].Trim();
                if (!double.TryParse(weightText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                    return false;

                string content = text[..i];
                if (string.IsNullOrWhiteSpace(content)) return false;

                inner = content;
                weight = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadNaiNumericSegment(string text, int start, out double weight, out string inner, out int next)
    {
        weight = 1.0;
        inner = "";
        next = start;
        if (start < 0 || start >= text.Length) return false;

        int prefixEnd = text.IndexOf("::", start, StringComparison.Ordinal);
        if (prefixEnd <= start) return false;

        string numberText = text.Substring(start, prefixEnd - start).Trim();
        if (!double.TryParse(numberText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out weight))
            return false;

        int close = text.IndexOf("::", prefixEnd + 2, StringComparison.Ordinal);
        if (close < 0) return false;

        inner = text.Substring(prefixEnd + 2, close - prefixEnd - 2);
        next = close + 2;
        return true;
    }

    private static void AppendWeightedSpan(List<WeightedSpan> spans, StringBuilder buffer, double weight)
    {
        if (buffer.Length == 0) return;
        spans.Add(new WeightedSpan(buffer.ToString(), weight));
        buffer.Clear();
    }

    private static List<WeightedSpan> MergeWeightedSpans(List<WeightedSpan> spans)
    {
        var merged = new List<WeightedSpan>();
        foreach (var span in spans)
        {
            if (string.IsNullOrEmpty(span.Text)) continue;
            if (merged.Count > 0 && Math.Abs(merged[^1].Weight - span.Weight) < 0.0001)
            {
                var last = merged[^1];
                merged[^1] = new WeightedSpan(last.Text + span.Text, last.Weight);
            }
            else
            {
                merged.Add(span);
            }
        }
        return merged;
    }

    private static string RenderPromptWeightedSpans(List<WeightedSpan> spans, PromptWeightFormat target)
    {
        var sb = new StringBuilder();
        foreach (var span in spans)
            sb.Append(RenderWeightedSpan(span, target));
        return sb.ToString();
    }

    private static string RenderWeightedSpan(WeightedSpan span, PromptWeightFormat target)
    {
        if (Math.Abs(span.Weight - 1.0) < 0.0001) return span.Text;

        return WrapWeightedContent(span.Text, core =>
        {
            switch (target)
            {
                case PromptWeightFormat.StableDiffusion:
                    if (span.Weight > 1.0)
                        return $"({core}:{FormatWeightValue(span.Weight)})";
                    if (span.Weight >= 0)
                        return $"[{core}:{FormatWeightValue(span.Weight)}]";
                    return $"({core}:{FormatWeightValue(span.Weight)})";

                case PromptWeightFormat.NaiClassic:
                    if (span.Weight <= 0)
                        return $"{FormatWeightValue(span.Weight)}::{core}::";

                    double ratio = Math.Log(span.Weight) / Math.Log(1.05);
                    int layers = (int)Math.Round(ratio, MidpointRounding.AwayFromZero);
                    if (layers == 0) return core;
                    return layers > 0
                        ? new string('{', layers) + core + new string('}', layers)
                        : new string('[', -layers) + core + new string(']', -layers);

                default:
                    return $"{FormatWeightValue(span.Weight)}::{core}::";
            }
        });
    }

    private static string WrapWeightedContent(string text, Func<string, string> wrapper)
    {
        if (string.IsNullOrEmpty(text)) return text;

        int start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;

        int end = text.Length - 1;
        while (end >= start && char.IsWhiteSpace(text[end])) end--;

        if (start > end) return text;

        string leading = text[..start];
        string core = text.Substring(start, end - start + 1);
        string trailing = text[(end + 1)..];
        return leading + wrapper(core) + trailing;
    }

    private static string FormatWeightValue(double value)
    {
        double rounded = Math.Round(value, 3, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetPromptWeightFormatLabel(PromptWeightFormat format) => format switch
    {
        PromptWeightFormat.StableDiffusion => "SD",
        PromptWeightFormat.NaiClassic => "NAI3经典",
        PromptWeightFormat.NaiNumeric => "NAI4+新权重",
        _ => "未知格式",
    };

    // ═══════════════════════════════════════════════════════════
    //  效果工作区
    // ═══════════════════════════════════════════════════════════

    private static EffectEntry CreateEffect(EffectType type) => type switch
    {
        EffectType.BrightnessContrast => new EffectEntry { Type = type, Value1 = 0, Value2 = 0 },
        EffectType.SaturationVibrance => new EffectEntry { Type = type, Value1 = 0, Value2 = 0 },
        EffectType.Temperature => new EffectEntry { Type = type, Value1 = 0, Value2 = 0 },
        EffectType.Glow => new EffectEntry { Type = type, Value1 = 24, Value2 = 55, Value3 = 70, Value4 = 1.0, Value5 = 0, Value6 = 0 },
        EffectType.RadialBlur => new EffectEntry { Type = type, Value1 = 18, Value2 = 50, Value3 = 50, Value4 = 0 },
        EffectType.Vignette => new EffectEntry { Type = type, Value1 = 35, Value2 = 55 },
        EffectType.ChromaticAberration => new EffectEntry { Type = type, Value1 = 3, Value2 = 0 },
        EffectType.Noise => new EffectEntry { Type = type, Value1 = 0, Value2 = 0 },
        EffectType.Gamma => new EffectEntry { Type = type, Value1 = 1.0, Value2 = 0 },
        EffectType.Pixelate => new EffectEntry { Type = type, Value1 = 8, Value2 = 50, Value3 = 50, Value4 = 30, Value5 = 30 },
        EffectType.SolidBlock => new EffectEntry { Type = type, Value1 = 50, Value2 = 50, Value3 = 30, Value4 = 30, TextValue = "#000000" },
        EffectType.Scanline => new EffectEntry { Type = type, Value1 = 2, Value2 = 4, Value3 = 30, Value4 = 0, Value5 = 50 },
        _ => new EffectEntry { Type = type },
    };

    private static string GetEffectTitle(EffectType type) => type switch
    {
        EffectType.BrightnessContrast => "亮度 / 对比度",
        EffectType.SaturationVibrance => "饱和度 / 自然饱和度",
        EffectType.Temperature => "色温",
        EffectType.Glow => "泛光",
        EffectType.RadialBlur => "径向模糊",
        EffectType.Vignette => "暗角",
        EffectType.ChromaticAberration => "镜头色散",
        EffectType.Noise => "杂色",
        EffectType.Gamma => "Gamma",
        EffectType.Pixelate => "像素化",
        EffectType.SolidBlock => "实色遮挡",
        EffectType.Scanline => "扫描线",
        _ => "效果",
    };

    private static EffectEntry CloneEffect(EffectEntry x) => new()
    {
        Id = x.Id,
        Type = x.Type,
        Value1 = x.Value1,
        Value2 = x.Value2,
        Value3 = x.Value3,
        Value4 = x.Value4,
        Value5 = x.Value5,
        Value6 = x.Value6,
        TextValue = x.TextValue,
    };

    private static Brush GetThemeBrush(string key) =>
        (Brush)Application.Current.Resources[key];

    private ElementTheme GetResolvedTheme()
    {
        if (this.Content is not FrameworkElement root) return ElementTheme.Light;
        if (root.RequestedTheme == ElementTheme.Dark) return ElementTheme.Dark;
        if (root.RequestedTheme == ElementTheme.Light) return ElementTheme.Light;
        return root.ActualTheme == ElementTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
    }

    private bool IsDarkTheme() => GetResolvedTheme() == ElementTheme.Dark;

    private Brush CreateEffectsBrush(byte lightR, byte lightG, byte lightB, byte darkR, byte darkG, byte darkB, byte alpha = 255)
    {
        bool isDark = IsDarkTheme();
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha,
            isDark ? darkR : lightR,
            isDark ? darkG : lightG,
            isDark ? darkB : lightB));
    }

    private Brush GetEffectsCardBackgroundBrush() => CreateEffectsBrush(251, 251, 251, 37, 37, 38);
    private Brush GetEffectsCardBorderBrush() => CreateEffectsBrush(214, 214, 214, 75, 75, 78);
    private Brush GetEffectsPrimaryTextBrush() => CreateEffectsBrush(26, 26, 26, 245, 245, 245);
    private Brush GetEffectsSecondaryTextBrush() => CreateEffectsBrush(80, 80, 80, 210, 210, 210);
    private Brush GetEffectsTertiaryTextBrush() => CreateEffectsBrush(120, 120, 120, 160, 160, 160);
    private Brush GetEffectsButtonBackgroundBrush() => CreateEffectsBrush(245, 245, 245, 45, 45, 48);
    private Brush GetEffectsButtonBorderBrush() => CreateEffectsBrush(210, 210, 210, 85, 85, 88);
    private Brush GetEffectsTextBoxBackgroundBrush() => CreateEffectsBrush(255, 255, 255, 30, 30, 30);
    private Brush GetEffectsTextBoxBorderBrush() => CreateEffectsBrush(196, 196, 196, 90, 90, 90);

    private void ApplyEffectsButtonTheme(Button button)
    {
        button.RequestedTheme = GetResolvedTheme();
        button.Background = GetEffectsButtonBackgroundBrush();
        button.BorderBrush = GetEffectsButtonBorderBrush();
        button.Foreground = button.Foreground ?? GetEffectsPrimaryTextBrush();
        button.CornerRadius = new CornerRadius(4);
    }

    private void ApplyEffectsTextBoxTheme(TextBox textBox)
    {
        textBox.RequestedTheme = GetResolvedTheme();
        textBox.Background = GetEffectsTextBoxBackgroundBrush();
        textBox.BorderBrush = GetEffectsTextBoxBorderBrush();
        textBox.Foreground = GetEffectsPrimaryTextBrush();
    }

    private bool HasEffectsWorkspaceState() => _effectsImageBytes != null || _effects.Count > 0;

    private EffectsWorkspaceState CaptureEffectsWorkspaceState() => new()
    {
        ImageBytes = _effectsImageBytes?.ToArray(),
        ImagePath = _effectsImagePath,
        SelectedEffectId = _selectedEffectId,
        Effects = _effects.Select(CloneEffect).ToList(),
    };

    private void PushEffectsUndoState()
    {
        if (_effectsApplyingHistory || !HasEffectsWorkspaceState()) return;
        _effectsUndoStack.Push(CaptureEffectsWorkspaceState());
        while (_effectsUndoStack.Count > 60)
        {
            var trimmed = _effectsUndoStack.Reverse().Take(60).Reverse().ToArray();
            _effectsUndoStack.Clear();
            foreach (var state in trimmed) _effectsUndoStack.Push(state);
        }
        _effectsRedoStack.Clear();
        UpdateDynamicMenuStates();
    }

    private async Task RestoreEffectsWorkspaceStateAsync(EffectsWorkspaceState state)
    {
        _effectsApplyingHistory = true;
        try
        {
            _effectsImageBytes = state.ImageBytes?.ToArray();
            _effectsPreviewImageBytes = _effectsImageBytes;
            ReplaceEffectsSourceBitmap(_effectsImageBytes);
            _effectsImagePath = state.ImagePath;
            _selectedEffectId = state.SelectedEffectId;

            _effects.Clear();
            _effects.AddRange(state.Effects.Select(CloneEffect));

            RefreshEffectsPanel();
            if (_effectsImageBytes == null)
            {
                EffectsPreviewImage.Source = null;
                EffectsImagePlaceholder.Visibility = Visibility.Visible;
                RefreshEffectsOverlay();
                UpdateDynamicMenuStates();
                UpdateFileMenuState();
                return;
            }

            QueueEffectsPreviewRefresh(immediate: true);
            await Task.Yield();
            RefreshEffectsOverlay();
            UpdateDynamicMenuStates();
            UpdateFileMenuState();
        }
        finally
        {
            _effectsApplyingHistory = false;
        }
    }

    private Button CreateEffectsCardIconButton(string glyph, Brush iconBrush, bool isEnabled, string toolTip)
    {
        var icon = new FontIcon
        {
            FontFamily = SymbolFontFamily,
            Glyph = glyph,
            FontSize = 12,
            Foreground = iconBrush,
        };

        var button = new Button
        {
            Width = 28,
            Height = 28,
            MinWidth = 28,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = isEnabled,
            Content = icon,
            Foreground = GetEffectsPrimaryTextBrush(),
        };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private void RefreshEffectsPanel()
    {
        EffectsPanel.Children.Clear();

        for (int i = 0; i < _effects.Count; i++)
        {
            var effect = _effects[i];
            bool isSelectedEffect = effect.Id == _selectedEffectId;
            var card = new Border
            {
                RequestedTheme = GetResolvedTheme(),
                Background = GetEffectsCardBackgroundBrush(),
                BorderBrush = isSelectedEffect
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215))
                    : GetEffectsCardBorderBrush(),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
            };
            card.Tapped += (_, args) =>
            {
                if (IsInteractiveEffectCardSource(args.OriginalSource)) return;
                _selectedEffectId = effect.Id;
                RefreshEffectsPanel();
                RefreshEffectsOverlay();
                TxtStatus.Text = IsRegionEffect(effect.Type)
                    ? "已选择区域效果，可在预览区直接拖动编辑"
                    : $"已选中特效：{GetEffectTitle(effect.Type)}";
            };

            var stack = new StackPanel { Spacing = 10 };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = $"{i + 1}. {GetEffectTitle(effect.Type)}",
                Style = (Style)((Grid)this.Content).Resources["InspectCaptionStyle"],
                Foreground = isSelectedEffect
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215))
                    : GetEffectsPrimaryTextBrush(),
                VerticalAlignment = VerticalAlignment.Center,
            };
            header.Children.Add(title);

            var upBtn = CreateEffectsCardIconButton("\uE70E", GetEffectsSecondaryTextBrush(), i > 0, "上移");
            ApplyEffectsButtonTheme(upBtn);
            upBtn.Margin = new Thickness(0, 0, 4, 0);
            upBtn.Click += (_, _) => MoveEffect(effect.Id, -1);
            Grid.SetColumn(upBtn, 1);
            header.Children.Add(upBtn);

            var downBtn = CreateEffectsCardIconButton("\uE70D", GetEffectsSecondaryTextBrush(), i < _effects.Count - 1, "下移");
            ApplyEffectsButtonTheme(downBtn);
            downBtn.Margin = new Thickness(0, 0, 4, 0);
            downBtn.Click += (_, _) => MoveEffect(effect.Id, 1);
            Grid.SetColumn(downBtn, 2);
            header.Children.Add(downBtn);

            var deleteBtn = CreateEffectsCardIconButton("\uE74D",
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 72, 86)),
                true, "删除");
            ApplyEffectsButtonTheme(deleteBtn);
            deleteBtn.Click += (_, _) =>
            {
                PushEffectsUndoState();
                _effects.RemoveAll(x => x.Id == effect.Id);
                if (_selectedEffectId == effect.Id) _selectedEffectId = null;
                RefreshEffectsPanel();
                QueueEffectsPreviewRefresh();
                UpdateDynamicMenuStates();
                UpdateFileMenuState();
                RefreshEffectsOverlay();
                TxtStatus.Text = "已移除效果";
            };
            Grid.SetColumn(deleteBtn, 3);
            header.Children.Add(deleteBtn);

            stack.Children.Add(header);

            switch (effect.Type)
            {
                case EffectType.BrightnessContrast:
                    AddEffectSlider(stack, "亮度", -100, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, "对比度", -100, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    break;
                case EffectType.SaturationVibrance:
                    AddEffectSlider(stack, "饱和度", -100, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, "自然饱和度", -100, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    break;
                case EffectType.Temperature:
                    AddEffectSlider(stack, "色温（冷/暖）", -100, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, "色调（绿/紫）", -100, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    break;
                case EffectType.Glow:
                    AddEffectSlider(stack, "泛光尺寸", 1, 120, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, "泛光阈值", 0, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    AddEffectSlider(stack, "泛光强度", 0, 200, 1, effect.Value3, "F0", v => effect.Value3 = v);
                    AddEffectCenteredLogSlider(stack, "泛光纵横比", 0.05, 1.0, 8.0, effect.Value4, "F2", v => effect.Value4 = v);
                    AddEffectSlider(stack, "泛光倾斜", -90, 90, 1, effect.Value6, "F0", v => effect.Value6 = v);
                    AddEffectSlider(stack, "泛光饱和度", -100, 100, 1, effect.Value5, "F0", v => effect.Value5 = v);
                    break;
                case EffectType.RadialBlur:
                    AddEffectCombo(stack, "算法",
                        new[] { "放射", "旋转", "渐进" },
                        (int)Math.Clamp(effect.Value4, 0, 2),
                        v => effect.Value4 = v);
                    AddEffectSlider(stack, "强度", 0, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, "中心 X", 0, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    AddEffectSlider(stack, "中心 Y", 0, 100, 1, effect.Value3, "F0", v => effect.Value3 = v);
                    break;
                case EffectType.Vignette:
                    AddEffectSlider(stack, "暗角强度", 0, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, "羽化", 0, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    break;
                case EffectType.ChromaticAberration:
                    AddEffectSlider(stack, "色散强度", 0, 20, 0.1, effect.Value1, "F1", v => effect.Value1 = v);
                    break;
                case EffectType.Noise:
                    AddEffectSlider(stack, "单色杂色", 0, 100, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectSlider(stack, "彩色杂色", 0, 100, 1, effect.Value2, "F0", v => effect.Value2 = v);
                    break;
                case EffectType.Gamma:
                    AddEffectSlider(stack, "Gamma", 0.2, 3.0, 0.05, effect.Value1, "F2", v => effect.Value1 = v);
                    break;
                case EffectType.Pixelate:
                    AddEffectSlider(stack, "像素粒度", 1, 64, 1, effect.Value1, "F0", v => effect.Value1 = v);
                    AddEffectRegionSliders(stack,
                        centerX: effect.Value2,
                        centerY: effect.Value3,
                        width: effect.Value4,
                        height: effect.Value5,
                        centerXSetter: v => effect.Value2 = v,
                        centerYSetter: v => effect.Value3 = v,
                        widthSetter: v => effect.Value4 = v,
                        heightSetter: v => effect.Value5 = v);
                    break;
                case EffectType.SolidBlock:
                    AddEffectColorTextBox(stack, "颜色 Hex", effect.TextValue, v => effect.TextValue = v);
                    AddEffectRegionSliders(stack,
                        centerX: effect.Value1,
                        centerY: effect.Value2,
                        width: effect.Value3,
                        height: effect.Value4,
                        centerXSetter: v => effect.Value1 = v,
                        centerYSetter: v => effect.Value2 = v,
                        widthSetter: v => effect.Value3 = v,
                        heightSetter: v => effect.Value4 = v);
                    break;
                case EffectType.Scanline:
                    AddEffectSlider(stack, "线宽", 0.5, 10, 0.1, effect.Value1, "F1", v => effect.Value1 = v);
                    AddEffectSlider(stack, "间距", 0.5, 20, 0.1, effect.Value2, "F1", v => effect.Value2 = v);
                    AddEffectSlider(stack, "柔和度", 0, 100, 1, effect.Value3, "F0", v => effect.Value3 = v);
                    AddEffectSlider(stack, "旋转角度", -90, 90, 1, effect.Value4, "F0", v => effect.Value4 = v);
                    AddEffectSlider(stack, "透明度", 0, 100, 1, effect.Value5, "F0", v => effect.Value5 = v);
                    break;
            }

            card.Child = stack;
            EffectsPanel.Children.Add(card);
            EffectsPanel.Children.Add(new Border
            {
                Height = 1,
                Background = GetEffectsCardBorderBrush(),
                Margin = new Thickness(0, 2, 0, 2),
            });
        }

        EffectsPanel.Children.Add(CreateAddEffectButton());
    }

    private void AddEffectSlider(
        Panel parent,
        string label,
        double min,
        double max,
        double step,
        double value,
        string format,
        Action<double> setValue)
    {
        var row = new StackPanel { Spacing = 4 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetEffectsSecondaryTextBrush(),
        });

        var valueText = new TextBlock
        {
            Text = value.ToString(format),
            Foreground = GetEffectsTertiaryTextBrush(),
        };
        Grid.SetColumn(valueText, 1);
        header.Children.Add(valueText);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Value = value,
            RequestedTheme = GetResolvedTheme(),
        };
        slider.PointerPressed += (_, _) => PushEffectsUndoState();
        slider.ValueChanged += (_, args) =>
        {
            setValue(args.NewValue);
            valueText.Text = args.NewValue.ToString(format);
            QueueEffectsPreviewRefresh();
            UpdateDynamicMenuStates();
            UpdateFileMenuState();
        };
        slider.PointerCaptureLost += (_, _) => QueueEffectsPreviewRefresh(immediate: true);
        slider.PointerReleased += (_, _) => QueueEffectsPreviewRefresh(immediate: true);

        row.Children.Add(header);
        row.Children.Add(slider);
        parent.Children.Add(row);
    }

    private void AddEffectCenteredLogSlider(
        Panel parent,
        string label,
        double min,
        double center,
        double max,
        double value,
        string format,
        Action<double> setValue)
    {
        var row = new StackPanel { Spacing = 4 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetEffectsSecondaryTextBrush(),
        });

        var valueText = new TextBlock
        {
            Text = value.ToString(format),
            Foreground = GetEffectsTertiaryTextBrush(),
        };
        Grid.SetColumn(valueText, 1);
        header.Children.Add(valueText);

        double normalizedValue = value <= center
            ? 0.5 * Math.Log(value / min) / Math.Log(center / min)
            : 0.5 + 0.5 * Math.Log(value / center) / Math.Log(max / center);
        normalizedValue = Math.Clamp(normalizedValue, 0, 1);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.001,
            Value = normalizedValue,
            RequestedTheme = GetResolvedTheme(),
        };
        slider.PointerPressed += (_, _) => PushEffectsUndoState();
        slider.ValueChanged += (_, args) =>
        {
            double t = Math.Clamp(args.NewValue, 0, 1);
            double mapped = t <= 0.5
                ? min * Math.Pow(center / min, t / 0.5)
                : center * Math.Pow(max / center, (t - 0.5) / 0.5);
            mapped = Math.Clamp(mapped, min, max);
            setValue(mapped);
            valueText.Text = mapped.ToString(format);
            QueueEffectsPreviewRefresh();
            UpdateDynamicMenuStates();
            UpdateFileMenuState();
        };
        slider.PointerCaptureLost += (_, _) => QueueEffectsPreviewRefresh(immediate: true);
        slider.PointerReleased += (_, _) => QueueEffectsPreviewRefresh(immediate: true);

        row.Children.Add(header);
        row.Children.Add(slider);
        parent.Children.Add(row);
    }

    private void AddEffectCombo(Panel parent, string label, IReadOnlyList<string> options, int selectedIndex, Action<int> setValue)
    {
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetEffectsSecondaryTextBrush(),
        });

        var combo = new ComboBox
        {
            RequestedTheme = GetResolvedTheme(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 32,
            FontFamily = UiTextFontFamily,
        };
        foreach (var option in options)
            combo.Items.Add(CreateTextComboBoxItem(option));
        combo.SelectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, options.Count - 1));
        combo.SelectionChanged += (_, _) =>
        {
            setValue(Math.Max(0, combo.SelectedIndex));
            QueueEffectsPreviewRefresh();
            UpdateFileMenuState();
        };
        row.Children.Add(combo);
        parent.Children.Add(row);
    }

    private void AddEffectRegionSliders(
        Panel parent,
        double centerX,
        double centerY,
        double width,
        double height,
        Action<double> centerXSetter,
        Action<double> centerYSetter,
        Action<double> widthSetter,
        Action<double> heightSetter)
    {
        AddEffectSlider(parent, "中心 X", 0, 100, 1, centerX, "F0", centerXSetter);
        AddEffectSlider(parent, "中心 Y", 0, 100, 1, centerY, "F0", centerYSetter);
        AddEffectSlider(parent, "区域宽度", 1, 100, 1, width, "F0", widthSetter);
        AddEffectSlider(parent, "区域高度", 1, 100, 1, height, "F0", heightSetter);
    }

    private void AddEffectColorTextBox(Panel parent, string label, string value, Action<string> setValue)
    {
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetEffectsSecondaryTextBrush(),
        });

        var colorRow = new Grid();
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var previewBorder = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = GetEffectsTextBoxBorderBrush(),
            Background = new SolidColorBrush(ToUiColor(TryParseEffectsColor(value) ?? new SKColor(0, 0, 0, 255))),
            Margin = new Thickness(0, 0, 8, 0),
        };
        colorRow.Children.Add(previewBorder);

        var textBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(value) ? "#000000" : value,
            PlaceholderText = "#000000",
        };
        ApplyEffectsTextBoxTheme(textBox);
        textBox.GotFocus += (_, _) => PushEffectsUndoState();
        textBox.TextChanged += (_, _) =>
        {
            string newValue = textBox.Text.Trim();
            setValue(newValue);
            var parsed = TryParseEffectsColor(newValue) ?? new SKColor(0, 0, 0, 255);
            previewBorder.Background = new SolidColorBrush(ToUiColor(parsed));
            QueueEffectsPreviewRefresh();
            UpdateFileMenuState();
        };
        Grid.SetColumn(textBox, 1);
        colorRow.Children.Add(textBox);

        var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
        {
            IsAlphaEnabled = true,
            Color = ToUiColor(TryParseEffectsColor(value) ?? new SKColor(0, 0, 0, 255)),
            RequestedTheme = GetResolvedTheme(),
        };
        picker.ColorChanged += (_, args) =>
        {
            string hex = args.NewColor.A == 255
                ? $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}"
                : $"#{args.NewColor.A:X2}{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
            textBox.Text = hex;
            previewBorder.Background = new SolidColorBrush(args.NewColor);
            setValue(hex);
            QueueEffectsPreviewRefresh();
            UpdateFileMenuState();
        };

        var flyout = new Flyout { Content = picker };
        var pickerBtn = new Button
        {
            Content = "调色盘",
            Margin = new Thickness(8, 0, 0, 0),
        };
        ApplyEffectsButtonTheme(pickerBtn);
        pickerBtn.Click += (_, _) => PushEffectsUndoState();
        pickerBtn.Click += (_, _) => flyout.ShowAt(pickerBtn);
        Grid.SetColumn(pickerBtn, 2);
        colorRow.Children.Add(pickerBtn);

        row.Children.Add(colorRow);
        parent.Children.Add(row);
    }

    private Button CreateAddEffectButton()
    {
        var btn = new Button
        {
            Content = "添加效果",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = _effects.Count < 10,
        };
        ApplyEffectsButtonTheme(btn);

        var flyout = new MenuFlyout();
        foreach (var type in Enum.GetValues<EffectType>())
        {
            var item = new MenuFlyoutItem
            {
                Text = GetEffectTitle(type),
                IsEnabled = _effects.Count < 10,
            };
            item.Click += (_, _) =>
            {
                if (_effects.Count >= 10)
                {
                    TxtStatus.Text = "最多只能添加 10 个效果";
                    return;
                }

                PushEffectsUndoState();
                var entry = CreateEffect(type);
                _effects.Add(entry);
                if (IsRegionEffect(type))
                    _selectedEffectId = entry.Id;
                RefreshEffectsPanel();
                QueueEffectsPreviewRefresh();
                UpdateDynamicMenuStates();
                UpdateFileMenuState();
                RefreshEffectsOverlay();
                TxtStatus.Text = $"已添加效果：{GetEffectTitle(type)}";
            };
            flyout.Items.Add(item);
        }

        foreach (var item in flyout.Items)
            ApplyMenuTypography(item);
        btn.ContextFlyout = flyout;
        btn.Click += (_, _) => flyout.ShowAt(btn);
        return btn;
    }

    private static bool HasEffectsPresets()
    {
        EnsureDefaultFxPresets();
        if (!Directory.Exists(FxPresetsDir)) return false;
        return Directory.EnumerateFiles(FxPresetsDir, "*.json").Any();
    }

    private static void EnsureDefaultFxPresets()
    {
        try
        {
            if (!Directory.Exists(DefaultFxPresetsDir)) return;

            Directory.CreateDirectory(FxPresetsDir);
            foreach (string sourcePath in Directory.EnumerateFiles(DefaultFxPresetsDir, "*.json"))
            {
                string targetPath = Path.Combine(FxPresetsDir, Path.GetFileName(sourcePath));
                if (!File.Exists(targetPath))
                    File.Copy(sourcePath, targetPath, overwrite: false);
            }
        }
        catch
        {
            // 默认预设复制失败时静默跳过，不影响主流程。
        }
    }

    private static string SanitizePresetFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }

    private static EffectEntry RehydratePresetEffect(EffectEntry x) => new()
    {
        Type = x.Type,
        Value1 = x.Value1,
        Value2 = x.Value2,
        Value3 = x.Value3,
        Value4 = x.Value4,
        Value5 = x.Value5,
        Value6 = x.Value6,
        TextValue = x.TextValue ?? "",
    };

    private async void OnAddEffectsPreset(object sender, RoutedEventArgs e)
    {
        if (_effects.Count == 0)
        {
            TxtStatus.Text = "当前没有效果可保存为预设";
            return;
        }

        var nameBox = new TextBox
        {
            PlaceholderText = "请输入预设名称",
            Text = $"预设_{DateTime.Now:MMdd_HHmm}",
            MinWidth = 260,
        };

        var dialog = new ContentDialog
        {
            Title = "添加预设",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "请输入预设名称..." },
                    nameBox,
                },
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string presetName = (nameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(presetName))
        {
            TxtStatus.Text = "预设名称不能为空";
            return;
        }

        string fileName = SanitizePresetFileName(presetName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            TxtStatus.Text = "预设名称不合法";
            return;
        }

        var payload = new EffectsPresetFile
        {
            Name = presetName,
            SavedAt = DateTime.Now,
            Effects = _effects.Select(CloneEffect).ToList(),
        };

        try
        {
            Directory.CreateDirectory(FxPresetsDir);
            string path = Path.Combine(FxPresetsDir, $"{fileName}.json");
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
            TxtStatus.Text = $"已保存预设：{presetName}";
            UpdateDynamicMenuStates();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"保存预设失败: {ex.Message}";
        }
    }

    private async void OnUseEffectsPreset(object sender, RoutedEventArgs e)
    {
        EnsureDefaultFxPresets();
        if (!Directory.Exists(FxPresetsDir))
        {
            TxtStatus.Text = "没有可用预设";
            return;
        }

        var files = Directory.EnumerateFiles(FxPresetsDir, "*.json")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .ToList();
        if (files.Count == 0)
        {
            TxtStatus.Text = "没有可用预设";
            return;
        }

        var fileToDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var presetCombo = new ComboBox { MinWidth = 320, FontFamily = UiTextFontFamily };
        foreach (var file in files)
        {
            string label = Path.GetFileNameWithoutExtension(file);
            try
            {
                var parsed = JsonSerializer.Deserialize<EffectsPresetFile>(await File.ReadAllTextAsync(file));
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Name))
                    label = parsed.Name;
            }
            catch { }

            string uniqueLabel = label;
            int suffix = 2;
            while (fileToDisplay.ContainsKey(uniqueLabel))
            {
                uniqueLabel = $"{label} ({suffix})";
                suffix++;
            }
            fileToDisplay[uniqueLabel] = file;
            presetCombo.Items.Add(CreateTextComboBoxItem(uniqueLabel));
        }
        presetCombo.SelectedIndex = 0;
        ApplyMenuTypography(presetCombo);

        var dialog = new ContentDialog
        {
            Title = "使用预设",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "选择一个预设并整体应用到效果链" },
                    presetCombo,
                },
            },
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string? selectedName = GetSelectedComboText(presetCombo);
        if (selectedName == null || !fileToDisplay.TryGetValue(selectedName, out var selectedFile))
            return;

        try
        {
            var parsed = JsonSerializer.Deserialize<EffectsPresetFile>(await File.ReadAllTextAsync(selectedFile));
            var loadedEffects = parsed?.Effects ?? new List<EffectEntry>();
            if (loadedEffects.Count == 0)
            {
                TxtStatus.Text = "预设为空，未应用";
                return;
            }

            PushEffectsUndoState();
            _effects.Clear();
            foreach (var fx in loadedEffects.Take(10))
                _effects.Add(RehydratePresetEffect(fx));

            _selectedEffectId = _effects.Count > 0 ? _effects[0].Id : null;
            RefreshEffectsPanel();
            RefreshEffectsOverlay();
            QueueEffectsPreviewRefresh(immediate: true);
            UpdateDynamicMenuStates();
            UpdateFileMenuState();
            TxtStatus.Text = $"已应用预设：{selectedName}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"读取预设失败: {ex.Message}";
        }
    }

    private void OnClearAllEffects(object sender, RoutedEventArgs e)
    {
        if (_effects.Count == 0) return;
        PushEffectsUndoState();
        _effects.Clear();
        _selectedEffectId = null;
        RefreshEffectsPanel();
        QueueEffectsPreviewRefresh();
        UpdateDynamicMenuStates();
        UpdateFileMenuState();
        RefreshEffectsOverlay();
        TxtStatus.Text = "已清空所有效果";
    }

    private async void OnApplyEffects(object sender, RoutedEventArgs e)
    {
        if (_effectsImageBytes == null || _effects.Count == 0) return;
        PushEffectsUndoState();

        var bytes = await GetEffectsSaveBytesAsync();
        if (bytes == null)
        {
            TxtStatus.Text = "没有可应用的图片";
            return;
        }

        _effectsImageBytes = bytes;
        _effectsPreviewImageBytes = bytes;
        ReplaceEffectsSourceBitmap(bytes);
        _effectsPreviewVersion++;
        _effects.Clear();
        _selectedEffectId = null;
        RefreshEffectsPanel();
        await ShowEffectsPreviewAsync(bytes, fitToScreen: false);
        UpdateDynamicMenuStates();
        UpdateFileMenuState();
        TxtStatus.Text = "已应用效果";
    }

    private async Task LoadEffectsImageAsync(string filePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            await LoadEffectsImageFromBytesAsync(bytes, filePath);
            TxtStatus.Text = $"已加载: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"加载失败: {ex.Message}";
        }
    }

    private Task LoadEffectsImageFromBytesAsync(byte[] bytes, string? filePath = null)
    {
        PushEffectsUndoState();
        _effectsImageBytes = bytes;
        _effectsPreviewImageBytes = bytes;
        ReplaceEffectsSourceBitmap(bytes);
        _effectsImagePath = filePath;
        if (_currentMode == AppMode.Effects)
            ReplaceEditMenu();
        UpdateFileMenuState();
        QueueEffectsPreviewRefresh(fitToScreen: true);
        return Task.CompletedTask;
    }

    private void ReplaceEffectsSourceBitmap(byte[]? bytes)
    {
        _effectsSourceBitmap?.Dispose();
        _effectsSourceBitmap = null;
        if (bytes == null || bytes.Length == 0)
            return;

        using var decoded = SKBitmap.Decode(bytes);
        if (decoded == null)
            return;

        _effectsSourceBitmap = decoded.Copy();
    }

    private async Task SendBytesToEffectsAsync(byte[] bytes, string? sourcePath = null)
    {
        SwitchMode(AppMode.Effects);
        await LoadEffectsImageFromBytesAsync(bytes, sourcePath);
        TxtStatus.Text = sourcePath != null
            ? $"已发送到效果: {Path.GetFileName(sourcePath)}"
            : "已发送到效果";
    }

    private void QueueEffectsPreviewRefresh(bool fitToScreen = false, bool immediate = false)
    {
        _effectsPreviewQueuedFit |= fitToScreen;
        if (_effectsPreviewTimer == null)
        {
            _ = RenderQueuedEffectsPreview();
            return;
        }

        _effectsPreviewTimer.Stop();
        _effectsPreviewTimer.Interval = TimeSpan.FromMilliseconds(immediate ? 1 : 60);
        _effectsPreviewTimer.Start();
    }

    private async Task RenderQueuedEffectsPreview()
    {
        int version = ++_effectsPreviewVersion;
        bool fitToScreen = _effectsPreviewQueuedFit;
        _effectsPreviewQueuedFit = false;
        var sourceBytes = _effectsImageBytes;
        var sourceBitmap = _effectsSourceBitmap;

        if (sourceBytes == null)
        {
            _effectsPreviewImageBytes = null;
            ReplaceEffectsSourceBitmap(null);
            EffectsPreviewImage.Source = null;
            EffectsImagePlaceholder.Visibility = Visibility.Visible;
            UpdateDynamicMenuStates();
            return;
        }

        var snapshot = _effects
            .Select(CloneEffect)
            .ToList();

        try
        {
            if (snapshot.Count == 0)
            {
                _effectsPreviewImageBytes = sourceBytes;
                await ShowEffectsPreviewAsync(sourceBytes, fitToScreen);
                UpdateDynamicMenuStates();
                return;
            }

            using var previewBitmap = await Task.Run(() => RenderEffectsPreview(sourceBitmap, sourceBytes, snapshot));

            if (version != _effectsPreviewVersion) return;

            _effectsPreviewImageBytes = null;
            await ShowEffectsPreviewBitmapAsync(previewBitmap, fitToScreen);
            UpdateDynamicMenuStates();
        }
        catch (Exception ex)
        {
            if (version != _effectsPreviewVersion) return;
            DebugLog($"[Effects] Preview failed: {ex}");
            TxtStatus.Text = $"效果预览失败: {ex.Message}";
        }
    }

    private async Task<byte[]?> GetEffectsSaveBytesAsync()
    {
        var sourceBytes = _effectsImageBytes;
        if (sourceBytes == null) return null;
        if (_effects.Count == 0) return sourceBytes;

        var snapshot = _effects
            .Select(CloneEffect)
            .ToList();
        return await Task.Run(() => RenderEffects(sourceBytes, snapshot));
    }

    private async Task ShowEffectsPreviewBitmapAsync(SKBitmap bitmap, bool fitToScreen)
    {
        var writeable = new WriteableBitmap(bitmap.Width, bitmap.Height);
        byte[] buffer = new byte[bitmap.ByteCount];
        Marshal.Copy(bitmap.GetPixels(), buffer, 0, buffer.Length);
        using (var stream = writeable.PixelBuffer.AsStream())
        {
            stream.Seek(0, SeekOrigin.Begin);
            await stream.WriteAsync(buffer, 0, buffer.Length);
            stream.SetLength(buffer.Length);
        }

        EffectsPreviewImage.Source = writeable;
        EffectsPreviewContent.Width = writeable.PixelWidth;
        EffectsPreviewContent.Height = writeable.PixelHeight;
        EffectsOverlayCanvas.Width = writeable.PixelWidth;
        EffectsOverlayCanvas.Height = writeable.PixelHeight;
        EffectsImagePlaceholder.Visibility = Visibility.Collapsed;
        RefreshEffectsOverlay();

        if (fitToScreen)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FitEffectsPreviewToScreen());
        }
    }

    private async Task ShowEffectsPreviewAsync(byte[] bytes, bool fitToScreen)
    {
        var bitmapImage = new BitmapImage();
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(ms);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        writer.DetachStream();
        ms.Seek(0);
        await bitmapImage.SetSourceAsync(ms);
        EffectsPreviewImage.Source = bitmapImage;
        EffectsPreviewContent.Width = bitmapImage.PixelWidth;
        EffectsPreviewContent.Height = bitmapImage.PixelHeight;
        EffectsOverlayCanvas.Width = bitmapImage.PixelWidth;
        EffectsOverlayCanvas.Height = bitmapImage.PixelHeight;
        EffectsImagePlaceholder.Visibility = Visibility.Collapsed;
        RefreshEffectsOverlay();

        if (fitToScreen)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FitEffectsPreviewToScreen());
        }
    }

    private void FitEffectsPreviewToScreen()
    {
        if (EffectsPreviewImage.Source is not BitmapSource bmp) return;
        double imgW = bmp.PixelWidth;
        double imgH = bmp.PixelHeight;
        if (imgW <= 0 || imgH <= 0) return;

        double viewW = EffectsImageScroller.ViewportWidth;
        double viewH = EffectsImageScroller.ViewportHeight;
        if (viewW <= 0 || viewH <= 0) return;

        float zoom = (float)Math.Min(viewW / imgW, viewH / imgH);
        zoom = Math.Min(zoom, 1.0f);
        EffectsImageScroller.ChangeView(0, 0, zoom);
    }

    private void CenterEffectsPreview()
    {
        if (EffectsPreviewImage.Source is not BitmapSource bmp) return;
        double contentW = bmp.PixelWidth * EffectsImageScroller.ZoomFactor;
        double contentH = bmp.PixelHeight * EffectsImageScroller.ZoomFactor;
        double offsetX = Math.Max(0, (contentW - EffectsImageScroller.ViewportWidth) / 2);
        double offsetY = Math.Max(0, (contentH - EffectsImageScroller.ViewportHeight) / 2);
        EffectsImageScroller.ChangeView(offsetX, offsetY, null);
    }

    private void RefreshEffectsOverlay()
    {
        if (EffectsOverlayCanvas == null) return;

        EffectsOverlayCanvas.Children.Clear();
        var effect = GetSelectedEffect();
        if (effect == null || !IsRegionEffect(effect.Type) || EffectsPreviewImage.Source is not BitmapSource bmp)
        {
            EffectsOverlayCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        EffectsOverlayCanvas.Visibility = Visibility.Visible;
        GetEffectRegionValues(effect, out double centerX, out double centerY, out double widthPct, out double heightPct);
        GetEffectRect(bmp.PixelWidth, bmp.PixelHeight, centerX, centerY, widthPct, heightPct,
            out int left, out int top, out int right, out int bottom);

        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = Math.Max(1, right - left),
            Height = Math.Max(1, bottom - top),
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 120, 215)),
            RadiusX = 4,
            RadiusY = 4,
            Tag = "region",
        };
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
        EffectsOverlayCanvas.Children.Add(rect);

        var handle = new Border
        {
            Width = 12,
            Height = 12,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215)),
            CornerRadius = new CornerRadius(2),
            Tag = "resize",
        };
        Canvas.SetLeft(handle, right - 6);
        Canvas.SetTop(handle, bottom - 6);
        EffectsOverlayCanvas.Children.Add(handle);
    }

    private static byte[] RenderEffects(byte[] sourceBytes, List<EffectEntry> effects)
    {
        using var bitmap = RenderEffectsPreview(null, sourceBytes, effects);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? sourceBytes;
    }

    private static SKBitmap RenderEffectsPreview(
        SKBitmap? cachedSourceBitmap,
        byte[] sourceBytes,
        List<EffectEntry> effects)
    {
        SKBitmap? baseBitmap = cachedSourceBitmap?.Copy() ?? SKBitmap.Decode(sourceBytes);
        if (baseBitmap == null)
            throw new InvalidOperationException("无法解码效果源图像");

        foreach (var effect in effects)
        {
            switch (effect.Type)
            {
                case EffectType.BrightnessContrast:
                    ApplyBrightnessContrast(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.SaturationVibrance:
                    ApplySaturationVibrance(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.Temperature:
                    ApplyTemperature(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.Glow:
                    ApplyGlow(baseBitmap, effect.Value1, effect.Value2, effect.Value3, effect.Value4, effect.Value6, effect.Value5);
                    break;
                case EffectType.RadialBlur:
                    ApplyRadialBlur(baseBitmap, effect.Value1, effect.Value2, effect.Value3, (int)Math.Round(effect.Value4));
                    break;
                case EffectType.Vignette:
                    ApplyVignette(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.ChromaticAberration:
                    ApplyChromaticAberration(baseBitmap, effect.Value1);
                    break;
                case EffectType.Noise:
                    ApplyNoise(baseBitmap, effect.Value1, effect.Value2);
                    break;
                case EffectType.Gamma:
                    ApplyGamma(baseBitmap, effect.Value1);
                    break;
                case EffectType.Pixelate:
                    ApplyPixelateRegion(baseBitmap, effect.Value1, effect.Value2, effect.Value3, effect.Value4, effect.Value5);
                    break;
                case EffectType.SolidBlock:
                    ApplySolidBlock(baseBitmap, effect.TextValue, effect.Value1, effect.Value2, effect.Value3, effect.Value4);
                    break;
                case EffectType.Scanline:
                    ApplyScanline(baseBitmap, effect.Value1, effect.Value2, effect.Value3, effect.Value4, effect.Value5);
                    break;
            }
        }

        return baseBitmap;
    }

    private static void ApplyBrightnessContrast(SKBitmap bitmap, double brightness, double contrast)
    {
        float b = (float)(brightness / 100.0 * 255.0);
        float c = (float)(1.0 + contrast / 100.0);
        var pixels = bitmap.Pixels;
        Parallel.For(0, pixels.Length, i =>
        {
            var px = pixels[i];
            byte r = ClampToByte((px.Red - 128f) * c + 128f + b);
            byte g = ClampToByte((px.Green - 128f) * c + 128f + b);
            byte bl = ClampToByte((px.Blue - 128f) * c + 128f + b);
            pixels[i] = new SKColor(r, g, bl, px.Alpha);
        });
        bitmap.Pixels = pixels;
    }

    private static void ApplySaturationVibrance(SKBitmap bitmap, double saturation, double vibrance)
    {
        float sat = (float)(1.0 + saturation / 100.0);
        float vib = (float)(vibrance / 100.0);
        var pixels = bitmap.Pixels;
        Parallel.For(0, pixels.Length, i =>
        {
            var px = pixels[i];
            float r = px.Red;
            float g = px.Green;
            float b = px.Blue;

            float gray = 0.299f * r + 0.587f * g + 0.114f * b;
            r = gray + (r - gray) * sat;
            g = gray + (g - gray) * sat;
            b = gray + (b - gray) * sat;

            float max = Math.Max(r, Math.Max(g, b));
            float avg = (r + g + b) / 3f;
            float amt = vib * (1f - Math.Abs(max - avg) / 255f);
            r += (r - avg) * amt;
            g += (g - avg) * amt;
            b += (b - avg) * amt;

            pixels[i] = new SKColor(ClampToByte(r), ClampToByte(g), ClampToByte(b), px.Alpha);
        });
        bitmap.Pixels = pixels;
    }

    private static void ApplyTemperature(SKBitmap bitmap, double temperature, double tint)
    {
        float delta = (float)(temperature / 100.0 * 45.0);
        float tintDelta = (float)(tint / 100.0 * 35.0);
        var pixels = bitmap.Pixels;
        Parallel.For(0, pixels.Length, i =>
        {
            var px = pixels[i];
            float r = px.Red + delta + tintDelta * 0.55f;
            float g = px.Green + delta * 0.15f - tintDelta;
            float b = px.Blue - delta + tintDelta * 0.55f;
            pixels[i] = new SKColor(ClampToByte(r), ClampToByte(g), ClampToByte(b), px.Alpha);
        });
        bitmap.Pixels = pixels;
    }

    private static void ApplyGlow(
        SKBitmap bitmap,
        double sizeValue,
        double thresholdValue,
        double strengthValue,
        double aspectRatioValue,
        double tiltValue,
        double saturationValue)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        if (width <= 1 || height <= 1) return;

        float glowSize = (float)Math.Clamp(sizeValue, 1, 120);
        float threshold = (float)Math.Clamp(thresholdValue, 0, 100) / 100f * 255f;
        float strength = (float)Math.Clamp(strengthValue, 0, 200) / 100f;
        float aspectRatio = (float)Math.Clamp(aspectRatioValue, 0.05, 8.0);
        float tiltDegrees = (float)Math.Clamp(tiltValue, -90, 90);
        float saturationNorm = (float)Math.Clamp(saturationValue, -100, 100) / 100f;
        float saturation = saturationNorm >= 0f
            ? MathF.Pow(4f, saturationNorm)
            : MathF.Pow(0.25f, -saturationNorm);
        
        // 更紧凑的软阈值 (Tighter soft-knee)，防止泛光过度溢出到暗部/中间调
        float knee = MathF.Max(1f, threshold * 0.15f);

        float ratioPow = MathF.Pow(aspectRatio, 1.25f);
        float sigmaX = MathF.Max(0.1f, glowSize * ratioPow / 3.0f);
        float sigmaY = MathF.Max(0.1f, glowSize / MathF.Max(0.05f, ratioPow) / 3.0f);

        var src = bitmap.Pixels;
        var brightPixels = new SKColor[src.Length];

        Parallel.For(0, src.Length, i =>
        {
            var px = src[i];
            
            // 修复“泛光偏白”：使用 Max(R,G,B) 而不是 Luminance。
            // 亮度(Luminance)会极大地压低高饱和度颜色（如纯蓝、纯红）的权重，导致只有白色能发光。
            // 使用 Max 可以让高饱和度的亮色与白色同等发光，从而保留泛光的色彩。
            float maxColor = Math.Max(px.Red, Math.Max(px.Green, px.Blue));
            
            float soft = maxColor - threshold + knee;
            soft = Math.Clamp(soft, 0f, 2f * knee);
            soft = soft * soft / (4f * knee + 0.0001f);
            
            float contribution = Math.Max(soft, maxColor - threshold);
            float factor = maxColor > 0.0001f ? contribution / maxColor : 0f;

            // 修复“边缘灼烧/硬截断”：强制 Alpha 为 255。
            // 之前低于阈值的像素 Alpha 为 0，导致高斯模糊时 Alpha 通道产生锐利边缘，进而引发色彩断层。
            brightPixels[i] = new SKColor(
                ClampToByte(px.Red * factor),
                ClampToByte(px.Green * factor),
                ClampToByte(px.Blue * factor),
                255);
        });

        using var bright = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bright.Pixels = brightPixels;
        using var blurred = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        if (Math.Abs(tiltDegrees) < 0.01f)
        {
            using var canvas = new SKCanvas(blurred);
            using var paint = new SKPaint
            {
                IsAntialias = false,
                FilterQuality = SKFilterQuality.High,
                ImageFilter = SKImageFilter.CreateBlur(sigmaX, sigmaY),
            };
            canvas.Clear(SKColors.Black); // 必须用纯黑不透明底色
            canvas.DrawBitmap(bright, 0, 0, paint);
            canvas.Flush();
        }
        else
        {
            int glowPadding = (int)Math.Ceiling(Math.Max(sigmaX, sigmaY) * 3f) + 2;
            int rotatedSize = (int)Math.Ceiling(Math.Sqrt(width * width + height * height) + glowPadding * 2 + 2);
            int sourceX = (rotatedSize - width) / 2;
            int sourceY = (rotatedSize - height) / 2;
            float center = rotatedSize / 2f;

            using var rotatedInput = new SKBitmap(rotatedSize, rotatedSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var rotatedInputCanvas = new SKCanvas(rotatedInput))
            {
                rotatedInputCanvas.Clear(SKColors.Black);
                rotatedInputCanvas.Translate(center, center);
                rotatedInputCanvas.RotateDegrees(tiltDegrees);
                rotatedInputCanvas.Translate(-center, -center);
                rotatedInputCanvas.DrawBitmap(bright, sourceX, sourceY);
                rotatedInputCanvas.Flush();
            }

            using var rotatedBlurred = new SKBitmap(rotatedSize, rotatedSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var rotatedBlurCanvas = new SKCanvas(rotatedBlurred))
            using (var paint = new SKPaint
            {
                IsAntialias = false,
                FilterQuality = SKFilterQuality.High,
                ImageFilter = SKImageFilter.CreateBlur(sigmaX, sigmaY),
            })
            {
                rotatedBlurCanvas.Clear(SKColors.Black);
                rotatedBlurCanvas.DrawBitmap(rotatedInput, 0, 0, paint);
                rotatedBlurCanvas.Flush();
            }

            using var untilted = new SKBitmap(rotatedSize, rotatedSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var untiltedCanvas = new SKCanvas(untilted))
            {
                untiltedCanvas.Clear(SKColors.Black);
                untiltedCanvas.Translate(center, center);
                untiltedCanvas.RotateDegrees(-tiltDegrees);
                untiltedCanvas.Translate(-center, -center);
                untiltedCanvas.DrawBitmap(rotatedBlurred, 0, 0);
                untiltedCanvas.Flush();
            }

            using var canvas = new SKCanvas(blurred);
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(
                untilted,
                new SKRect(sourceX, sourceY, sourceX + width, sourceY + height),
                new SKRect(0, 0, width, height));
            canvas.Flush();
        }

        var glowPixels = blurred.Pixels;
        var outPixels = new SKColor[src.Length];

        Parallel.For(0, outPixels.Length, i =>
        {
            var basePx = src[i];
            var glowPx = glowPixels[i];

            float gr = glowPx.Red;
            float gg = glowPx.Green;
            float gb = glowPx.Blue;

            // 对泛光本身做更强的色度增强，并保持峰值亮度，避免“饱和度拉高但仍偏白”。
            float peakBefore = MathF.Max(gr, MathF.Max(gg, gb));
            float gGray = 0.299f * gr + 0.587f * gg + 0.114f * gb;
            gr = gGray + (gr - gGray) * saturation;
            gg = gGray + (gg - gGray) * saturation;
            gb = gGray + (gb - gGray) * saturation;
            float peakAfter = MathF.Max(gr, MathF.Max(gg, gb));
            if (peakBefore > 0.001f && peakAfter > 0.001f)
            {
                float preservePeak = peakBefore / peakAfter;
                gr *= preservePeak;
                gg *= preservePeak;
                gb *= preservePeak;
            }

            // 恢复为 Linear Additive (线性叠加) 混合模式。
            // 之前的 Screen 模式会压制亮部背景上的泛光，导致泛光显得无力且偏白。
            float glowR = Math.Max(0f, gr * strength);
            float glowG = Math.Max(0f, gg * strength);
            float glowB = Math.Max(0f, gb * strength);

            float r = basePx.Red + glowR;
            float g = basePx.Green + glowG;
            float b = basePx.Blue + glowB;

            outPixels[i] = new SKColor(ClampToByte(r), ClampToByte(g), ClampToByte(b), basePx.Alpha);
        });

        bitmap.Pixels = outPixels;
    }

    private static void ApplyRadialBlur(SKBitmap bitmap, double strengthValue, double centerXPct, double centerYPct, int mode)
    {
        float strength = (float)Math.Clamp(strengthValue, 0, 100);
        if (strength <= 0.01f) return;

        int width = bitmap.Width;
        int height = bitmap.Height;
        var source = bitmap.Pixels;
        var result = new SKColor[source.Length];

        float cx = (float)(Math.Clamp(centerXPct, 0, 100) / 100.0 * (width - 1));
        float cy = (float)(Math.Clamp(centerYPct, 0, 100) / 100.0 * (height - 1));
        int sampleCount = 4 + GetRadialBlurSampleCount(strength, mode) * 2;
        float zoomRadius = 0.0025f + strength / 100f * 0.075f;
        float spinAngle = strength / 100f * 0.22f;
        float maxDist = MathF.Sqrt(MathF.Max(cx, width - 1 - cx) * MathF.Max(cx, width - 1 - cx) +
                                   MathF.Max(cy, height - 1 - cy) * MathF.Max(cy, height - 1 - cy));
        maxDist = MathF.Max(maxDist, 1f);

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float accumR = 0, accumG = 0, accumB = 0, accumA = 0, weightSum = 0;

                for (int i = 0; i < sampleCount; i++)
                {
                    float t = sampleCount == 1 ? 0f : (i / (float)(sampleCount - 1) - 0.5f) * 2f;
                    float sampleX;
                    float sampleY;
                    float weight;

                    switch (mode)
                    {
                        case 1: // 旋转
                            float angle = t * spinAngle;
                            float cos = MathF.Cos(angle);
                            float sin = MathF.Sin(angle);
                            sampleX = cx + dx * cos - dy * sin;
                            sampleY = cy + dx * sin + dy * cos;
                            weight = 1f - MathF.Abs(t) * 0.5f;
                            break;
                        case 2: // 高斯
                            float gaussianScale = t * zoomRadius;
                            sampleX = x - dx * gaussianScale;
                            sampleY = y - dy * gaussianScale;
                            weight = MathF.Exp(-(t * t) * 4f);
                            break;
                        default: // 放射
                            float scale = t * zoomRadius;
                            sampleX = x - dx * scale;
                            sampleY = y - dy * scale;
                            weight = 1f;
                            break;
                    }

                    SKColor sample;
                    if (mode == 2) // 渐进：离中心越远，越进行各向同性模糊
                    {
                        float distNorm = MathF.Sqrt(dx * dx + dy * dy) / maxDist;
                        float localRadius = distNorm * (0.5f + strength / 100f * 14f);
                        if (localRadius < 0.75f)
                        {
                            sample = SampleEffectsPixel(source, width, height, x, y);
                        }
                        else
                        {
                            float angleStep = MathF.Tau / sampleCount;
                            float ang = i * angleStep;
                            sampleX = x + MathF.Cos(ang) * localRadius;
                            sampleY = y + MathF.Sin(ang) * localRadius;
                            sample = SampleEffectsPixel(source, width, height, sampleX, sampleY);
                        }
                        weight = 1f;
                    }
                    else
                    {
                        sample = SampleEffectsPixel(source, width, height, sampleX, sampleY);
                    }
                    accumR += sample.Red * weight;
                    accumG += sample.Green * weight;
                    accumB += sample.Blue * weight;
                    accumA += sample.Alpha * weight;
                    weightSum += weight;
                }

                if (weightSum <= 0.0001f)
                {
                    result[y * width + x] = source[y * width + x];
                    continue;
                }

                result[y * width + x] = new SKColor(
                    ClampToByte(accumR / weightSum),
                    ClampToByte(accumG / weightSum),
                    ClampToByte(accumB / weightSum),
                    ClampToByte(accumA / weightSum));
            }
        });

        bitmap.Pixels = result;
    }

    private static int GetRadialBlurSampleCount(float strength, int mode)
    {
        int baseCount = mode switch
        {
            1 => 16, // 旋转更依赖采样
            2 => 14, // 渐进模糊
            _ => 3, // 放射默认更轻
        };
        int scaled = mode switch
        {
            0 => baseCount + (int)MathF.Round(strength / 100f * 12f),
            _ => baseCount + (int)MathF.Round(strength / 100f * 24f),
        };
        return Math.Clamp(scaled, baseCount, 40);
    }

    private static void ApplyVignette(SKBitmap bitmap, double strengthValue, double featherValue)
    {
        float strength = (float)(strengthValue / 100.0);
        float softness = 0.15f + (float)(featherValue / 100.0) * 0.75f;
        float start = Math.Clamp(1f - softness, 0.05f, 0.95f);
        float cx = (bitmap.Width - 1) / 2f;
        float cy = (bitmap.Height - 1) / 2f;
        float maxDist = MathF.Sqrt(cx * cx + cy * cy);
        int width = bitmap.Width;
        int height = bitmap.Height;
        var pixels = bitmap.Pixels;

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                var px = pixels[idx];
                float dx = x - cx;
                float dy = y - cy;
                float dist = MathF.Sqrt(dx * dx + dy * dy) / maxDist;
                float t = Math.Clamp((dist - start) / Math.Max(softness, 0.001f), 0f, 1f);
                float factor = 1f - strength * t * t;

                pixels[idx] = new SKColor(
                    ClampToByte(px.Red * factor),
                    ClampToByte(px.Green * factor),
                    ClampToByte(px.Blue * factor),
                    px.Alpha);
            }
        });
        bitmap.Pixels = pixels;
    }

    private static void ApplyChromaticAberration(SKBitmap bitmap, double amountValue)
    {
        float shift = (float)(amountValue / 20.0 * 6.0);
        if (shift <= 0.01f) return;

        int width = bitmap.Width;
        int height = bitmap.Height;
        var source = bitmap.Pixels;
        var result = new SKColor[source.Length];

        float cx = (width - 1) / 2f;
        float cy = (height - 1) / 2f;

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                float ux = len > 0.001f ? dx / len : 0f;
                float uy = len > 0.001f ? dy / len : 0f;

                var center = SampleEffectsPixel(source, width, height, x, y);
                var red = SampleEffectsPixel(source, width, height, x + ux * shift, y + uy * shift);
                var blue = SampleEffectsPixel(source, width, height, x - ux * shift, y - uy * shift);

                result[y * width + x] = new SKColor(red.Red, center.Green, blue.Blue, center.Alpha);
            }
        });
        bitmap.Pixels = result;
    }

    private static void ApplyNoise(SKBitmap bitmap, double monoValue, double colorValue)
    {
        float monoStrength = (float)(monoValue / 100.0 * 64.0);
        float colorStrength = (float)(colorValue / 100.0 * 64.0);
        if (monoStrength <= 0.01f && colorStrength <= 0.01f) return;

        int width = bitmap.Width;
        int height = bitmap.Height;
        var pixels = bitmap.Pixels;

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                var px = pixels[idx];

                float monoNoise = monoStrength > 0.01f
                    ? (HashNoise(x, y, 0) * 2f - 1f) * monoStrength
                    : 0f;

                float colorNoiseR = colorStrength > 0.01f
                    ? (HashNoise(x, y, 1) * 2f - 1f) * colorStrength
                    : 0f;
                float colorNoiseG = colorStrength > 0.01f
                    ? (HashNoise(x, y, 2) * 2f - 1f) * colorStrength
                    : 0f;
                float colorNoiseB = colorStrength > 0.01f
                    ? (HashNoise(x, y, 3) * 2f - 1f) * colorStrength
                    : 0f;

                pixels[idx] = new SKColor(
                    ClampToByte(px.Red + monoNoise + colorNoiseR),
                    ClampToByte(px.Green + monoNoise + colorNoiseG),
                    ClampToByte(px.Blue + monoNoise + colorNoiseB),
                    px.Alpha);
            }
        });

        bitmap.Pixels = pixels;
    }

    private static void ApplyGamma(SKBitmap bitmap, double gammaValue)
    {
        float gamma = Math.Clamp((float)gammaValue, 0.2f, 3.0f);
        if (Math.Abs(gamma - 1f) < 0.001f) return;

        float invGamma = 1f / gamma;
        var pixels = bitmap.Pixels;
        Parallel.For(0, pixels.Length, i =>
        {
            var px = pixels[i];
            pixels[i] = new SKColor(
                ClampToByte(MathF.Pow(px.Red / 255f, invGamma) * 255f),
                ClampToByte(MathF.Pow(px.Green / 255f, invGamma) * 255f),
                ClampToByte(MathF.Pow(px.Blue / 255f, invGamma) * 255f),
                px.Alpha);
        });
        bitmap.Pixels = pixels;
    }

    private static void ApplyScanline(SKBitmap bitmap, double lineWidth, double spacing, double softness, double angle, double opacity)
    {
        float lw = MathF.Max(0.1f, (float)lineWidth);
        float sp = MathF.Max(0.1f, (float)spacing);
        float period = lw + sp;
        float soft = Math.Clamp((float)softness / 100f, 0f, 1f);
        float alpha = Math.Clamp((float)opacity / 100f, 0f, 1f);
        if (alpha <= 0.001f) return;

        // angle=0 -> horizontal (project onto Y axis); ±90 -> vertical
        float rad = (float)(angle * Math.PI / 180.0);
        float cosA = MathF.Cos(rad);
        float sinA = MathF.Sin(rad);

        // Signed-distance transition half-width
        float blur = soft * period * 0.5f;

        int w = bitmap.Width;
        int h = bitmap.Height;
        var pixels = bitmap.Pixels;

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float projected = -x * sinA + y * cosA;
                float pos = projected - MathF.Floor(projected / period) * period;

                // Signed distance to line band [0, lw]: positive = inside line
                float sd;
                if (pos <= lw)
                    sd = MathF.Min(pos, lw - pos);
                else
                    sd = -MathF.Min(pos - lw, period - pos);

                float darken;
                if (blur > 0.01f)
                    darken = alpha * Math.Clamp((sd + blur) / (2f * blur), 0f, 1f);
                else
                    darken = sd >= 0f ? alpha : 0f;

                if (darken > 0.001f)
                {
                    int idx = y * w + x;
                    var px = pixels[idx];
                    float keep = 1f - darken;
                    pixels[idx] = new SKColor(
                        ClampToByte(px.Red * keep),
                        ClampToByte(px.Green * keep),
                        ClampToByte(px.Blue * keep),
                        px.Alpha);
                }
            }
        });

        bitmap.Pixels = pixels;
    }

    private static void ApplyPixelateRegion(SKBitmap bitmap, double blockSizeValue, double centerX, double centerY, double widthPct, double heightPct)
    {
        int blockSize = Math.Max(1, (int)Math.Round(blockSizeValue));
        GetEffectRect(bitmap.Width, bitmap.Height, centerX, centerY, widthPct, heightPct, out int left, out int top, out int right, out int bottom);
        if (right <= left || bottom <= top) return;

        int width = bitmap.Width;
        var source = bitmap.Pixels;
        var result = (SKColor[])source.Clone();

        for (int y = top; y < bottom; y += blockSize)
        for (int x = left; x < right; x += blockSize)
        {
            int blockRight = Math.Min(x + blockSize, right);
            int blockBottom = Math.Min(y + blockSize, bottom);
            int count = 0;
            int sumR = 0, sumG = 0, sumB = 0, sumA = 0;

            for (int yy = y; yy < blockBottom; yy++)
            for (int xx = x; xx < blockRight; xx++)
            {
                var px = source[yy * width + xx];
                sumR += px.Red;
                sumG += px.Green;
                sumB += px.Blue;
                sumA += px.Alpha;
                count++;
            }

            if (count == 0) continue;
            var avg = new SKColor(
                (byte)(sumR / count),
                (byte)(sumG / count),
                (byte)(sumB / count),
                (byte)(sumA / count));

            for (int yy = y; yy < blockBottom; yy++)
            for (int xx = x; xx < blockRight; xx++)
                result[yy * width + xx] = avg;
        }

        bitmap.Pixels = result;
    }

    private static void ApplySolidBlock(SKBitmap bitmap, string colorText, double centerX, double centerY, double widthPct, double heightPct)
    {
        GetEffectRect(bitmap.Width, bitmap.Height, centerX, centerY, widthPct, heightPct, out int left, out int top, out int right, out int bottom);
        if (right <= left || bottom <= top) return;

        var color = TryParseEffectsColor(colorText) ?? new SKColor(0, 0, 0, 255);
        int width = bitmap.Width;
        var pixels = bitmap.Pixels;
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
            pixels[y * width + x] = color;
        bitmap.Pixels = pixels;
    }

    private static void GetEffectRegionValues(EffectEntry effect, out double centerX, out double centerY, out double widthPct, out double heightPct)
    {
        if (effect.Type == EffectType.Pixelate)
        {
            centerX = effect.Value2;
            centerY = effect.Value3;
            widthPct = effect.Value4;
            heightPct = effect.Value5;
        }
        else
        {
            centerX = effect.Value1;
            centerY = effect.Value2;
            widthPct = effect.Value3;
            heightPct = effect.Value4;
        }
    }

    private static void SetEffectRegionValues(EffectEntry effect, double centerX, double centerY, double widthPct, double heightPct)
    {
        if (effect.Type == EffectType.Pixelate)
        {
            effect.Value2 = centerX;
            effect.Value3 = centerY;
            effect.Value4 = widthPct;
            effect.Value5 = heightPct;
        }
        else
        {
            effect.Value1 = centerX;
            effect.Value2 = centerY;
            effect.Value3 = widthPct;
            effect.Value4 = heightPct;
        }
    }

    private static SKColor SampleEffectsPixel(SKColor[] pixels, int width, int height, float x, float y)
    {
        int px = Math.Clamp((int)MathF.Round(x), 0, width - 1);
        int py = Math.Clamp((int)MathF.Round(y), 0, height - 1);
        return pixels[py * width + px];
    }

    private static void GetEffectRect(int imageWidth, int imageHeight, double centerXPct, double centerYPct, double widthPct, double heightPct,
        out int left, out int top, out int right, out int bottom)
    {
        float cx = (float)(Math.Clamp(centerXPct, 0, 100) / 100.0 * imageWidth);
        float cy = (float)(Math.Clamp(centerYPct, 0, 100) / 100.0 * imageHeight);
        float halfW = (float)(Math.Clamp(widthPct, 1, 100) / 100.0 * imageWidth / 2.0);
        float halfH = (float)(Math.Clamp(heightPct, 1, 100) / 100.0 * imageHeight / 2.0);

        left = Math.Clamp((int)MathF.Round(cx - halfW), 0, imageWidth - 1);
        top = Math.Clamp((int)MathF.Round(cy - halfH), 0, imageHeight - 1);
        right = Math.Clamp((int)MathF.Round(cx + halfW), left + 1, imageWidth);
        bottom = Math.Clamp((int)MathF.Round(cy + halfH), top + 1, imageHeight);
    }

    private static SKColor? TryParseEffectsColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string value = text.Trim();
        if (value.StartsWith("#")) value = value[1..];

        try
        {
            if (value.Length == 6)
            {
                byte r = Convert.ToByte(value[..2], 16);
                byte g = Convert.ToByte(value.Substring(2, 2), 16);
                byte b = Convert.ToByte(value.Substring(4, 2), 16);
                return new SKColor(r, g, b, 255);
            }
            if (value.Length == 8)
            {
                byte a = Convert.ToByte(value[..2], 16);
                byte r = Convert.ToByte(value.Substring(2, 2), 16);
                byte g = Convert.ToByte(value.Substring(4, 2), 16);
                byte b = Convert.ToByte(value.Substring(6, 2), 16);
                return new SKColor(r, g, b, a);
            }
        }
        catch { }

        return null;
    }

    private static Windows.UI.Color ToUiColor(SKColor color) =>
        Windows.UI.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);

    private static float HashNoise(int x, int y, int salt)
    {
        unchecked
        {
            uint n = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(salt * 83492791);
            n ^= n >> 13;
            n *= 1274126177;
            n ^= n >> 16;
            return (n & 0x00FFFFFF) / 16777215f;
        }
    }

    private static byte ClampToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    // ═══════════════════════════════════════════════════════════
    //  随机风格提示词
    // ═══════════════════════════════════════════════════════════

    private void SetupPromptContextFlyouts()
    {
        var promptFlyout = new MenuFlyout();
        promptFlyout.Opening += (_, _) => ConfigurePromptContextFlyout(promptFlyout, TxtPrompt, isStyleBox: false, allowQuickRandomStyle: true);
        TxtPrompt.ContextFlyout = promptFlyout;

        var styleFlyout = new MenuFlyout();
        styleFlyout.Opening += (_, _) => ConfigurePromptContextFlyout(styleFlyout, TxtStylePrompt, isStyleBox: true, allowQuickRandomStyle: true);
        TxtStylePrompt.ContextFlyout = styleFlyout;
    }

    private void ConfigurePromptContextFlyout(MenuFlyout flyout, TextBox textBox, bool isStyleBox, bool allowQuickRandomStyle)
    {
        flyout.Items.Clear();

        var undoItem = new MenuFlyoutItem { Text = "撤销", IsEnabled = textBox.CanUndo, Icon = new SymbolIcon(Symbol.Undo) };
        undoItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            if (textBox.CanUndo) textBox.Undo();
        };
        flyout.Items.Add(undoItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var cutItem = new MenuFlyoutItem { Text = "剪切", IsEnabled = textBox.SelectionLength > 0, Icon = new SymbolIcon(Symbol.Cut) };
        cutItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.CutSelectionToClipboard();
        };
        flyout.Items.Add(cutItem);

        var copyItem = new MenuFlyoutItem { Text = "复制", IsEnabled = textBox.SelectionLength > 0, Icon = new SymbolIcon(Symbol.Copy) };
        copyItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.CopySelectionToClipboard();
        };
        flyout.Items.Add(copyItem);

        var pasteItem = new MenuFlyoutItem { Text = "粘贴", Icon = new SymbolIcon(Symbol.Paste) };
        pasteItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.PasteFromClipboard();
        };
        flyout.Items.Add(pasteItem);

        var deleteItem = new MenuFlyoutItem { Text = "删除", IsEnabled = textBox.SelectionLength > 0, Icon = new SymbolIcon(Symbol.Delete) };
        deleteItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.SelectedText = "";
        };
        flyout.Items.Add(deleteItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var selectAllItem = new MenuFlyoutItem
        {
            Text = "全选",
            IsEnabled = !string.IsNullOrEmpty(textBox.Text),
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B3" },
        };
        selectAllItem.Click += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.SelectAll();
        };
        flyout.Items.Add(selectAllItem);

        if (!isStyleBox)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var actionInteractionSub = new MenuFlyoutSubItem
            {
                Text = "动作交互",
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE805" },
            };

            void AddActionInteractionItem(string label, string prefix, string glyph)
            {
                var item = new MenuFlyoutItem
                {
                    Text = label,
                    Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = glyph },
                };
                item.Click += (_, _) => InsertInteractionPrefixIntoPromptSelection(textBox, prefix);
                actionInteractionSub.Items.Add(item);
            }

            AddActionInteractionItem("发起方", "source#", "\uE87B");
            AddActionInteractionItem("被动方", "target#", "\uE879");
            AddActionInteractionItem("对等交互", "mutual#", "\uE7FD");
            flyout.Items.Add(actionInteractionSub);
        }

        bool shouldShow =
            allowQuickRandomStyle &&
            (_currentMode == AppMode.ImageGeneration || _currentMode == AppMode.Inpaint) &&
            _isPositiveTab;

        if (!shouldShow)
        {
            foreach (var item in flyout.Items)
                ApplyMenuTypography(item);
            return;
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var quickItem = new MenuFlyoutItem
        {
            Text = "快速插入随机风格词",
            Icon = new SymbolIcon(Symbol.Shuffle),
        };
        quickItem.Click += OnQuickRandomStylePrompt;
        flyout.Items.Add(quickItem);
        foreach (var item in flyout.Items)
            ApplyMenuTypography(item);
    }

    private void InsertInteractionPrefixIntoPromptSelection(TextBox textBox, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;

        int selectionStart = textBox.SelectionStart;
        int selectionLength = textBox.SelectionLength;
        string text = textBox.Text ?? string.Empty;

        textBox.Focus(FocusState.Programmatic);
        textBox.Text = text.Insert(selectionStart, prefix);
        textBox.Select(selectionStart + prefix.Length, selectionLength);

        if (ReferenceEquals(textBox, TxtPrompt) || ReferenceEquals(textBox, TxtStylePrompt))
        {
            SaveCurrentPromptToBuffer();
            UpdatePromptHighlights();
            UpdateStyleHighlights();
        }
        else
        {
            CharacterEntry? entry = _genCharacters.FirstOrDefault(x => ReferenceEquals(x.PromptBox, textBox));
            if (entry != null)
            {
                SaveCharacterPrompt(entry);
                UpdateCharacterHighlight(entry);
            }
        }

        TxtStatus.Text = selectionLength > 0
            ? $"已在选区前插入动作交互前缀：{prefix}"
            : $"已在光标处插入动作交互前缀：{prefix}";
    }

    private RandomStyleOptions GetRandomStyleOptions() => new(
        Math.Clamp(_settings.Settings.RandomStyleTagCount, 1, 10),
        Math.Max(0, _settings.Settings.RandomStyleMinCount),
        _settings.Settings.RandomStyleUseWeight);

    private void SaveRandomStyleOptions(RandomStyleOptions options)
    {
        _settings.Settings.RandomStyleTagCount = options.TagCount;
        _settings.Settings.RandomStyleMinCount = options.MinCount;
        _settings.Settings.RandomStyleUseWeight = options.UseWeight;
        _settings.Save();
    }

    private string? BuildRandomStylePrefixForRequest()
    {
        if (!TryBuildRandomStylePrompt(GetRandomStyleOptions(), out string result, out _))
            return null;
        return result;
    }

    private bool TryBuildRandomStylePrompt(RandomStyleOptions options, out string result, out int tagCount)
    {
        result = "";
        tagCount = 0;

        if (!_tagService.IsLoaded)
        {
            TxtStatus.Text = "无可用 Tag 表格数据。请将可用 CSV 文件放入 assets/tagsheet 目录。";
            return false;
        }

        var tags = _tagService.GetRandomTags(options.TagCount, 1, options.MinCount);
        if (tags.Count == 0)
        {
            TxtStatus.Text = "最小数量设置过高，无法找到符合条件的风格标签。";
            return false;
        }

        bool isSplit = _isSplitPrompt && _isPositiveTab;
        var rng = Random.Shared;
        var parts = new List<string>(tags.Count);
        foreach (var t in tags)
        {
            string tagText = t.Tag.Replace('_', ' ');
            if (options.UseWeight)
            {
                double w = Math.Round(0.5 + rng.NextDouble() * 1.5, 1);
                parts.Add($"{w:F1}::{tagText}::");
            }
            else
            {
                parts.Add(tagText);
            }
        }

        result = isSplit ? string.Join(", ", parts) : string.Concat(parts.Select(p => p + ", "));
        tagCount = tags.Count;
        return true;
    }

    private bool ApplyRandomStylePrompt(RandomStyleOptions options)
    {
        if (!TryBuildRandomStylePrompt(options, out string result, out int tagCount))
            return false;

        bool isSplit = _isSplitPrompt && _isPositiveTab;
        if (isSplit)
        {
            TxtStylePrompt.Text = result;
            TxtStylePrompt.SelectionStart = TxtStylePrompt.Text.Length;
            TxtStylePrompt.Focus(FocusState.Programmatic);
        }
        else
        {
            string existing = TxtPrompt.Text;
            TxtPrompt.Text = result + existing;
            TxtPrompt.SelectionStart = result.Length;
            TxtPrompt.Focus(FocusState.Programmatic);
        }

        TxtStatus.Text = $"已插入 {tagCount} 个随机风格标签";
        return true;
    }

    private async void OnRandomStylePrompt(object sender, RoutedEventArgs e)
    {
        if (!_tagService.IsLoaded)
        {
            var noTagDlg = new ContentDialog
            {
                Title = "随机风格提示词",
                Content = new TextBlock { Text = "无可用 Tag 表格数据。请将可用 CSV 文件放入 assets/tagsheet 目录。" },
                CloseButtonText = "确定",
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };
            await noTagDlg.ShowAsync();
            return;
        }

        var lastOptions = GetRandomStyleOptions();
        var sliderCount = new Slider
        {
            Minimum = 1, Maximum = 10, Value = lastOptions.TagCount, StepFrequency = 1,
            Header = "随机选择标签数量",
        };
        var nbMinCount = new NumberBox
        {
            Header = "最小Booru排名数量", Minimum = 0, Value = lastOptions.MinCount,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var chkRandomWeight = new CheckBox { Content = "随机添加权重", IsChecked = lastOptions.UseWeight };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(sliderCount);
        panel.Children.Add(nbMinCount);
        panel.Children.Add(chkRandomWeight);

        var dialog = new ContentDialog
        {
            Title = "随机风格提示词",
            Content = panel,
            PrimaryButtonText = "生成",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var options = new RandomStyleOptions(
            (int)sliderCount.Value,
            (int)nbMinCount.Value,
            chkRandomWeight.IsChecked == true);

        if (!ApplyRandomStylePrompt(options))
            return;

        SaveRandomStyleOptions(options);
    }

    private void OnQuickRandomStylePrompt(object sender, RoutedEventArgs e)
    {
        _ = sender;
        ApplyRandomStylePrompt(GetRandomStyleOptions());
    }

    private async void OnPromptShortcuts(object sender, RoutedEventArgs e)
    {
        _ = sender;

        var rowsHost = new StackPanel { Spacing = 0 };
        var rowEditors = new List<(TextBox Shortcut, TextBox Prompt, Border Row)>();

        Border CreateSheetRow(string shortcut = "", string prompt = "", bool isHeader = false)
        {
            var rowGrid = new Grid { ColumnSpacing = 8, Padding = new Thickness(10, 8, 10, 8) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            if (isHeader)
            {
                var left = new TextBlock
                {
                    Text = "快捷提示词",
                    Style = (Style)((Grid)this.Content).Resources["InspectCaptionStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var right = new TextBlock
                {
                    Text = "完整提示词",
                    Style = (Style)((Grid)this.Content).Resources["InspectCaptionStyle"],
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(right, 1);
                rowGrid.Children.Add(left);
                rowGrid.Children.Add(right);
            }
            else
            {
                var shortcutBox = new TextBox
                {
                    PlaceholderText = "如: 角色光影",
                    Text = shortcut,
                };
                var promptBox = new TextBox
                {
                    PlaceholderText = "如: cinematic lighting, dramatic shadow",
                    Text = prompt,
                };
                Grid.SetColumn(promptBox, 1);

                var deleteBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74D", FontSize = 12 },
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)),
                };

                var rowBorder = new Border
                {
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = rowGrid,
                };
                deleteBtn.Click += (_, _) =>
                {
                    rowsHost.Children.Remove(rowBorder);
                    rowEditors.RemoveAll(x => ReferenceEquals(x.Row, rowBorder));
                };
                Grid.SetColumn(deleteBtn, 2);

                rowGrid.Children.Add(shortcutBox);
                rowGrid.Children.Add(promptBox);
                rowGrid.Children.Add(deleteBtn);
                rowEditors.Add((shortcutBox, promptBox, rowBorder));
                return rowBorder;
            }

            bool isDark = IsDarkTheme();
            var headerBg = isDark
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 40, 40))
                : (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
            return new Border
            {
                Background = headerBg,
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = rowGrid,
            };
        }

        void AddShortcutRow(string shortcut = "", string prompt = "") =>
            rowsHost.Children.Add(CreateSheetRow(shortcut, prompt));

        var tips = new TextBlock
        {
            Text = "当任意提示词区域中输入左侧快捷提示词时，发送请求前会自动替换为右侧完整提示词。按逗号分隔进行匹配。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        };
        var addBtn = new Button
        {
            Content = "添加一行",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        addBtn.Click += (_, _) => AddShortcutRow();

        if (_promptShortcuts.Count == 0)
            AddShortcutRow();
        else
            foreach (var item in _promptShortcuts)
                AddShortcutRow(item.Shortcut, item.Prompt);

        var sheetStack = new StackPanel { Spacing = 0 };
        sheetStack.Children.Add(CreateSheetRow(isHeader: true));
        sheetStack.Children.Add(new ScrollViewer
        {
            Content = rowsHost,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(tips);
        panel.Children.Add(new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = sheetStack,
        });
        panel.Children.Add(addBtn);

        var dialog = new ContentDialog
        {
            Title = "快捷提示词",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            var items = rowEditors.Select(x => new PromptShortcutEntry
            {
                Shortcut = x.Shortcut.Text.Trim(),
                Prompt = x.Prompt.Text.Trim(),
            }).ToList();
            SavePromptShortcuts(items);
            TxtStatus.Text = $"已保存 {_promptShortcuts.Count} 条快捷提示词";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"保存快捷提示词失败: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  尺寸选择
    // ═══════════════════════════════════════════════════════════

    private void OnPresetResolutionSelected(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is (int w, int h))
        {
            _isUpdatingMaxSize = true;
            try
            {
                _customWidth = w;
                _customHeight = h;
                NbMaxWidth.Value = w;
                NbMaxHeight.Value = h;
                int idx = Array.FindIndex(MaskCanvasControl.CanvasPresets, p => p.W == w && p.H == h);
                if (idx >= 0) CboSize.SelectedIndex = idx;
                if (IsAdvancedWindowOpen)
                {
                    _advNbMaxWidth.Value = w;
                    _advNbMaxHeight.Value = h;
                    if (_advCboSize != null && idx >= 0) _advCboSize.SelectedIndex = idx;
                }
                if (_currentMode == AppMode.Inpaint &&
                    (MaskCanvas.CanvasW != _customWidth || MaskCanvas.CanvasH != _customHeight))
                {
                    MaskCanvas.InitializeCanvas(_customWidth, _customHeight);
            MaskCanvas.FitToScreen();
                }
                TxtStatus.Text = $"已应用预设分辨率 {w} × {h}";
                UpdateSizeWarningVisuals();
            }
            finally
            {
                _isUpdatingMaxSize = false;
            }
        }
    }

    private void OnSwapSizeDimensions(object sender, RoutedEventArgs e)
    {
        ApplyMaxSizeInput(_customHeight, _customWidth, fromAdvancedPanel: false, changedBox: NbMaxWidth);
        TxtStatus.Text = $"已交换尺寸为 {_customWidth} × {_customHeight}";
    }

    private void OnAdvSwapSizeDimensions(object sender, RoutedEventArgs e)
    {
        ApplyMaxSizeInput(_customHeight, _customWidth, fromAdvancedPanel: true, changedBox: _advNbMaxWidth);
        TxtStatus.Text = $"已交换尺寸为 {_customWidth} × {_customHeight}";
    }

    private static int SnapToMultipleOf64(double rawValue)
    {
        var snapped = (int)Math.Round(rawValue / 64d) * 64;
        return Math.Max(64, snapped);
    }

    private void OnMaxSizeValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isUpdatingMaxSize) return;
        if (NbMaxWidth == null || NbMaxHeight == null) return;
        if (double.IsNaN(NbMaxWidth.Value) || double.IsNaN(NbMaxHeight.Value)) return;
        ApplyMaxSizeInput((int)NbMaxWidth.Value, (int)NbMaxHeight.Value, fromAdvancedPanel: false, changedBox: sender);
    }

    private void OnAdvMaxSizeValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isUpdatingMaxSize || !IsAdvancedWindowOpen) return;
        ApplyMaxSizeInput((int)_advNbMaxWidth.Value, (int)_advNbMaxHeight.Value, fromAdvancedPanel: true, changedBox: sender);
    }

    private void ApplyMaxSizeInput(int width, int height, bool fromAdvancedPanel, NumberBox? changedBox = null)
    {
        _isUpdatingMaxSize = true;
        try
        {
            _customWidth = SnapToMultipleOf64(width);
            _customHeight = SnapToMultipleOf64(height);

            if (!_settings.Settings.MaxMode)
                AutoAdjustNonMaxSize(changedBox, fromAdvancedPanel);

            NbMaxWidth.Value = _customWidth;
            NbMaxHeight.Value = _customHeight;
            if (IsAdvancedWindowOpen)
            {
                _advNbMaxWidth.Value = _customWidth;
                _advNbMaxHeight.Value = _customHeight;
            }

            if (_currentMode == AppMode.Inpaint &&
                (MaskCanvas.CanvasW != _customWidth || MaskCanvas.CanvasH != _customHeight))
            {
                MaskCanvas.InitializeCanvas(_customWidth, _customHeight);
                MaskCanvas.FitToScreen();
                TxtStatus.Text = $"画布大小已更改为 {_customWidth} × {_customHeight}";
            }
            else if (fromAdvancedPanel)
            {
                TxtStatus.Text = $"尺寸已更新为 {_customWidth} × {_customHeight}";
            }

            UpdateSizeWarningVisuals();
        }
        finally
        {
            _isUpdatingMaxSize = false;
        }
    }

    private void AutoAdjustNonMaxSize(NumberBox? changedBox, bool fromAdvancedPanel)
    {
        const long maxPixels = 1024L * 1024;
        if ((long)_customWidth * _customHeight <= maxPixels) return;

        bool userChangedWidth = changedBox == NbMaxWidth ||
                                (fromAdvancedPanel && changedBox == _advNbMaxWidth);

        if (userChangedWidth)
        {
            while ((long)_customWidth * _customHeight > maxPixels && _customHeight > 64)
                _customHeight -= 64;
        }
        else
        {
            while ((long)_customWidth * _customHeight > maxPixels && _customWidth > 64)
                _customWidth -= 64;
        }
    }

    private (int W, int H) GetSelectedSize()
    {
        return (_customWidth, _customHeight);
    }

    // ═══════════════════════════════════════════════════════════
    //  高级参数独立窗口
    // ═══════════════════════════════════════════════════════════

    private bool IsAdvancedWindowOpen => _advParamsWindow != null;
    private bool _isSyncingSidebarAdv;

    private void SetupSidebarAdvancedSync()
    {
        NbSeed.ValueChanged += (_, _) =>
        {
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
        bool maxMode = _settings.Settings.MaxMode;

        var window = new Window();
        window.Title = "高级参数";
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
        _advCboSampler = new ComboBox { Header = "采样器", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, FontFamily = UiTextFontFamily };
        _advCboSchedule = new ComboBox { Header = "调度器", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, FontFamily = UiTextFontFamily };
        _advNbSteps = new NumberBox
        {
            Header = "步数", Minimum = 1,
            Maximum = maxMode ? 50 : 28,
            Value = Math.Min(p.Steps, maxMode ? 50 : 28),
            MinHeight = 32,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        _advNbSeed = new NumberBox
        {
            Header = "种子 (0=随机)", Minimum = 0, Value = p.Seed,
            MinHeight = 32,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        _advNbScale = new NumberBox
        {
            Header = "CFG 缩放", Minimum = 0, Maximum = 10, Value = p.Scale,
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

        _advChkVariety = new CheckBox { Content = "多样化 (Variety+)", IsChecked = p.Variety };
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

        _advCboQuality = new ComboBox { Header = "添加质量词", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, FontFamily = UiTextFontFamily };
        _advCboQuality.Items.Add(CreateTextComboBoxItem("是"));
        _advCboQuality.Items.Add(CreateTextComboBoxItem("否"));
        _advCboQuality.SelectedIndex = p.QualityToggle ? 0 : 1;
        if (_advCboQuality.SelectedIndex < 0) _advCboQuality.SelectedIndex = 0;
        _advCboUcPreset = new ComboBox { Header = "添加负面质量词", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, FontFamily = UiTextFontFamily };
        _advCboUcPreset.Items.Add(CreateTextComboBoxItem("全面"));
        _advCboUcPreset.Items.Add(CreateTextComboBoxItem("简略"));
        _advCboUcPreset.Items.Add(CreateTextComboBoxItem("不添加"));
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

        var sizeLabel = new TextBlock { Text = "尺寸", Margin = new Thickness(0, 0, 0, 8) };

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
            Text = "高级参数", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13,
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
            Text = "CFG 再缩放 (0=关闭)", FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
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
            TxtStatus.Text = $"已还原种子: {_lastUsedSeed}";
        }
        else
        {
            TxtStatus.Text = "尚无可还原的种子";
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  尺寸模式与警告
    // ═══════════════════════════════════════════════════════════

    private enum SizeWarningLevel
    {
        None,
        Yellow,
        Red,
    }

    private void UpdateSizeControlMode()
    {
        CboSize.Visibility = Visibility.Collapsed;
        MaxSizePanel.Visibility = Visibility.Visible;
    }

    private void UpdateAdvSizeControlMode()
    {
        if (!IsAdvancedWindowOpen) return;
        _advCboSize.Visibility = Visibility.Collapsed;
        _advMaxSizePanel.Visibility = Visibility.Visible;
    }

    private SizeWarningLevel GetSizeWarningLevel()
    {
        if (!_settings.Settings.MaxMode)
        {
        long pixels = (long)_customWidth * _customHeight;
            if (pixels > 1024L * 1024) return SizeWarningLevel.Red;
            return SizeWarningLevel.None;
        }
        if (!_settings.Settings.MaxMode) return SizeWarningLevel.None;
        long px = (long)_customWidth * _customHeight;
        if (px > 2048L * 2048) return SizeWarningLevel.Red;
        if (px > 1024L * 1024) return SizeWarningLevel.Yellow;
        return SizeWarningLevel.None;
    }

    private void ApplyWarningStyle(NumberBox box, SizeWarningLevel level)
    {
        if (level == SizeWarningLevel.None)
        {
            box.ClearValue(Control.BackgroundProperty);
            return;
        }

        bool isDark = ((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark;
        Windows.UI.Color color = level switch
        {
            SizeWarningLevel.Red => isDark
                ? Windows.UI.Color.FromArgb(200, 180, 60, 60)
                : Windows.UI.Color.FromArgb(200, 255, 210, 210),
            _ => isDark
                ? Windows.UI.Color.FromArgb(200, 120, 110, 40)
                : Windows.UI.Color.FromArgb(200, 255, 245, 190),
        };
        box.Background = new SolidColorBrush(color);
    }

    private void UpdateSizeWarningVisuals()
    {
        var level = GetSizeWarningLevel();
        ApplyWarningStyle(NbMaxWidth, level);
        ApplyWarningStyle(NbMaxHeight, level);
        UpdateAdvSizeWarningVisuals();
        UpdateGenerateButtonWarning();
    }

    private void UpdateAdvSizeWarningVisuals()
    {
        if (!IsAdvancedWindowOpen) return;
        var level = GetSizeWarningLevel();
        ApplyWarningStyle(_advNbMaxWidth, level);
        ApplyWarningStyle(_advNbMaxHeight, level);
    }

    // ═══════════════════════════════════════════════════════════
    //  NumberBox 清除按钮隐藏 & 辅助方法
    // ═══════════════════════════════════════════════════════════

    private static void SuppressNumberBoxClearButton(NumberBox nb)
    {
        nb.Loaded += (_, _) => nb.DispatcherQueue?.TryEnqueue(() =>
        {
            var textBox = FindDescendant<TextBox>(nb);
            if (textBox == null) return;

            int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(textBox);
            if (childCount == 0)
            {
                textBox.Loaded += (_, _) => DisableTextBoxClearButton(textBox);
                return;
            }
            DisableTextBoxClearButton(textBox);
        });
    }

    private static void SuppressNumberBoxInitialSelection(NumberBox nb)
    {
        nb.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var textBox = FindDescendant<TextBox>(nb);
            if (textBox == null) return;
            textBox.SelectionStart = textBox.Text?.Length ?? 0;
            textBox.Select(textBox.SelectionStart, 0);
        });
    }

    private static void DisableTextBoxClearButton(TextBox textBox)
    {
        int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(textBox);
        if (childCount == 0) return;

        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(textBox, 0) is not FrameworkElement templateRoot)
            return;

        foreach (var group in VisualStateManager.GetVisualStateGroups(templateRoot))
        {
            if (group.Name != "ButtonStates") continue;
            foreach (var state in group.States)
            {
                if (state.Name == "ButtonVisible")
                {
                    state.Storyboard = null;
                    state.Setters.Clear();
                    return;
                }
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent, string? name = null) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t && (name == null || (t is FrameworkElement fe && fe.Name == name)))
                return t;
            var result = FindDescendant<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════
    //  生成按钮警告状态
    // ═══════════════════════════════════════════════════════════

    private bool HasAnyWarning()
    {
        if (GetSizeWarningLevel() != SizeWarningLevel.None) return true;
        if (CurrentRequestUsesAnlas()) return true;
        if (_settings.Settings.MaxMode)
        {
            int steps = IsAdvancedWindowOpen ? (int)_advNbSteps.Value : CurrentParams.Steps;
            if (steps > 28) return true;
        }
        return false;
    }

    private StackPanel CreateAnlasActionButtonContent(string actionText, int anlasCost)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon
        {
            FontFamily = SymbolFontFamily,
            Glyph = "\uF159",
            FontSize = 16,
        });
        content.Children.Add(new TextBlock
        {
            Text = anlasCost.ToString(),
            Opacity = 0.9,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = actionText,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return content;
    }

    private StackPanel CreateSymbolActionButtonContent(Symbol symbol, string actionText)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new SymbolIcon { Symbol = symbol });
        content.Children.Add(new TextBlock
        {
            Text = actionText,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return content;
    }

    private void ApplyGoldAccentButtonStyle(Button button)
    {
        if (button == null) return;

        bool isLight = GetResolvedTheme() != ElementTheme.Dark;
        const byte darkGoldR = 0xB2, darkGoldG = 0x8E, darkGoldB = 0x36;
        const byte lightGoldR = 0xF6, lightGoldG = 0xD2, lightGoldB = 0x7E;

        static LinearGradientBrush CreateDiagonalGold(byte r0, byte g0, byte b0, byte r1, byte g1, byte b1)
        {
            var stops = new GradientStopCollection
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, r0, g0, b0), Offset = 0.0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, r1, g1, b1), Offset = 1.0 },
            };
            var brush = new LinearGradientBrush(stops, 0.0)
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
            };
            return brush;
        }

        static byte Brighten(byte value, double amount) =>
            (byte)Math.Clamp((int)Math.Round(value + (255 - value) * amount), 0, 255);

        byte baseStartR = isLight ? Brighten(darkGoldR, 0.24) : darkGoldR;
        byte baseStartG = isLight ? Brighten(darkGoldG, 0.24) : darkGoldG;
        byte baseStartB = isLight ? Brighten(darkGoldB, 0.24) : darkGoldB;
        byte baseEndR = isLight ? Brighten(lightGoldR, 0.18) : lightGoldR;
        byte baseEndG = isLight ? Brighten(lightGoldG, 0.18) : lightGoldG;
        byte baseEndB = isLight ? Brighten(lightGoldB, 0.18) : lightGoldB;

        button.Resources["AccentButtonBackground"] = CreateDiagonalGold(baseStartR, baseStartG, baseStartB, baseEndR, baseEndG, baseEndB);
        button.Resources["AccentButtonBackgroundPointerOver"] = CreateDiagonalGold(
            Brighten(baseStartR, isLight ? 0.16 : 0.08), Brighten(baseStartG, isLight ? 0.16 : 0.08), Brighten(baseStartB, isLight ? 0.16 : 0.08),
            Brighten(baseEndR, isLight ? 0.12 : 0.06), Brighten(baseEndG, isLight ? 0.12 : 0.06), Brighten(baseEndB, isLight ? 0.12 : 0.06));
        button.Resources["AccentButtonBackgroundPressed"] = CreateDiagonalGold(
            (byte)(baseStartR * 0.88), (byte)(baseStartG * 0.88), (byte)(baseStartB * 0.88),
            (byte)(baseEndR * 0.88), (byte)(baseEndG * 0.88), (byte)(baseEndB * 0.88));

        var fgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 28, 20));
        button.Resources["AccentButtonForeground"] = fgBrush;
        button.Resources["AccentButtonForegroundPointerOver"] = fgBrush;
        button.Resources["AccentButtonForegroundPressed"] = fgBrush;

        var style = button.Style;
        button.Style = null;
        button.Style = style;
    }

    private static void ClearGoldAccentButtonStyle(Button button)
    {
        if (button == null) return;

        foreach (var key in new[]
        {
            "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
            "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
        })
            button.Resources.Remove(key);

        var style = button.Style;
        button.Style = null;
        button.Style = style;
    }

    private void UpdateGenerateButtonWarning()
    {
        if (IsAnyGenerateLoopRunning() || BtnGenerate == null || this.Content == null) return;
        UpdateBtnGenerateForApiKey();
        bool warn = EstimateCurrentRequestAnlasCost() > 0;

        if (warn)
            ApplyGoldAccentButtonStyle(BtnGenerate);
        else
            ClearGoldAccentButtonStyle(BtnGenerate);

        UpdateInpaintRedoButtonWarning();
    }

    private void UpdateInpaintRedoButtonWarning()
    {
        if (BtnRedoGenerate == null) return;

        bool warn = _currentMode == AppMode.Inpaint &&
                    MaskCanvas.IsInPreviewMode &&
                    EstimateCurrentRequestAnlasCost() > 0;

        if (warn)
        {
            BtnRedoGenerate.Content = CreateAnlasActionButtonContent("重做", EstimateCurrentRequestAnlasCost());
            ApplyGoldAccentButtonStyle(BtnRedoGenerate);
        }
        else
        {
            BtnRedoGenerate.Content = CreateSymbolActionButtonContent(Symbol.Refresh, "重做");
            ClearGoldAccentButtonStyle(BtnRedoGenerate);
        }
    }

    private void UpdateBtnGenerateForApiKey()
    {
        if (IsAnyGenerateLoopRunning()) return;
        BtnGenerate.IsEnabled = !_generateRequestRunning;
        bool hasKey = !string.IsNullOrEmpty(_settings.Settings.ApiToken);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (hasKey && _anlasRefreshRunning && !_anlasInitialFetchDone)
        {
            content.Children.Add(new SymbolIcon(Symbol.Sync));
            content.Children.Add(new TextBlock { Text = "正在同步账户信息" });
            BtnGenerate.Content = content;
            BtnGenerate.IsEnabled = false;
            return;
        }

        if (!IsAnyGenerateLoopRunning())
            BtnGenerate.IsEnabled = !_generateRequestRunning;

        if (hasKey)
        {
            int estimatedAnlas = EstimateCurrentRequestAnlasCost();
            if (estimatedAnlas > 0)
            {
                content.Children.Add(new FontIcon
                {
                    FontFamily = SymbolFontFamily,
                    Glyph = "\uF159",
                    FontSize = 16,
                });
                content.Children.Add(new TextBlock
                {
                    Text = estimatedAnlas.ToString(),
                    Opacity = 0.9,
                });
            }
            else
            {
                content.Children.Add(new SymbolIcon(Symbol.Send));
            }
            content.Children.Add(new TextBlock { Text = "发送" });
        }
        else
        {
            content.Children.Add(new SymbolIcon(Symbol.Globe));
            content.Children.Add(new TextBlock { Text = "设置 API" });
        }
        BtnGenerate.Content = content;
    }

    // ═══════════════════════════════════════════════════════════
    //  自动化生成
    // ═══════════════════════════════════════════════════════════

    private async void OnAutoGenSettings(object sender, RoutedEventArgs e)
    {
        if (_autoGenRunning)
        {
            StopAutoGeneration();
            return;
        }

        await ShowAutomationDialogAsync();
    }

    private async Task RunAutoGenerationAsync()
    {
        var automationSettings = GetAutomationSettings().Clone();
        automationSettings.Normalize();

        _autoGenRunning = true;
        _autoGenCts = new CancellationTokenSource();
        _activeAutomationSettings = automationSettings;
        _automationRunContext = CreateAutomationRunContext(automationSettings);
        UpdateAutoGenUI();
        int requestCount = 0;
        int consecutiveFailures = 0;

        try
        {
            while (!_autoGenCts.IsCancellationRequested)
            {
                SyncUIToParams();
                if (IsAdvancedWindowOpen) SaveAdvancedPanelToSettings();
                SaveCurrentPromptToBuffer();
                if (_automationRunContext != null)
                    PrepareAutomationIteration(_automationRunContext);
                _settings.Save();

                bool success = await DoImageGenerationAsync();
                requestCount++;

                if (success)
                {
                    consecutiveFailures = 0;
                }
                else
                {
                    consecutiveFailures++;
                    if (automationSettings.Generation.FailureRetryLimit > 0 &&
                        consecutiveFailures >= automationSettings.Generation.FailureRetryLimit)
                    {
                        TxtStatus.Text = $"自动生成已停止：连续失败 {consecutiveFailures} 次";
                        break;
                    }
                }

                if (automationSettings.Generation.RequestLimit > 0 &&
                    requestCount >= automationSettings.Generation.RequestLimit)
                {
                    TxtStatus.Text = $"自动生成已停止：已发送 {requestCount} 次请求";
                    break;
                }

                if (_autoGenCts.IsCancellationRequested) break;

                var delay = automationSettings.Generation.MinDelaySeconds +
                            Random.Shared.NextDouble() *
                            (automationSettings.Generation.MaxDelaySeconds - automationSettings.Generation.MinDelaySeconds);
                TxtStatus.Text = $"自动生成: 等待 {delay:F1} 秒后继续...";

                try { await Task.Delay(TimeSpan.FromSeconds(delay), _autoGenCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _autoGenRunning = false;
            _autoGenCts = null;
            _activeAutomationSettings = null;
            _automationRunContext = null;
            UpdateAutoGenUI();
            if (!TxtStatus.Text.StartsWith("自动生成已停止"))
                TxtStatus.Text = "自动生成已停止";
        }
    }

    private async Task RunContinuousGenerationAsync(int totalCount)
    {
        _continuousGenRunning = true;
        _continuousGenCts = new CancellationTokenSource();
        _continuousGenRemaining = totalCount;
        _continuousStopRequested = false;
        UpdateAutoGenUI();
        int completedCount = 0;

        try
        {
            SyncUIToParams();
            if (IsAdvancedWindowOpen) SaveAdvancedPanelToSettings();
            _settings.Save();

            for (int i = 0; i < totalCount; i++)
            {
                if (_continuousGenCts.IsCancellationRequested)
                    break;

                _continuousGenRemaining = totalCount - i;
                UpdateAutoGenUI();
                TxtStatus.Text = $"连续生成: 正在执行第 {i + 1}/{totalCount} 次请求...";

                bool success = await ExecuteCurrentGenerationAsync(forceRandomSeed: true);
                if (!success)
                {
                    TxtStatus.Text = $"连续生成已停止：第 {i + 1} 次请求失败";
                    break;
                }

                completedCount++;
            }
        }
        finally
        {
            bool wasCancelled = _continuousGenCts?.IsCancellationRequested == true;
            _continuousGenRunning = false;
            _continuousGenCts = null;
            _continuousGenRemaining = 0;
            _continuousStopRequested = false;
            UpdateAutoGenUI();

            if (wasCancelled)
                TxtStatus.Text = "连续生成已停止";
            else if (completedCount == totalCount)
                TxtStatus.Text = $"连续生成完成：共 {completedCount} 次";
        }
    }

    private void StopAutoGeneration()
    {
        _autoGenCts?.Cancel();
    }

    private void UpdateAutoGenUI()
    {
        if (_autoGenRunning)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(new FontIcon
            {
                FontFamily = SymbolFontFamily, Glyph = "\uE71A", FontSize = 16,
            });
            content.Children.Add(new TextBlock { Text = "停止自动生成" });
            BtnGenerate.Content = content;
            BtnGenerate.Resources["AccentButtonBackground"] = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 196, 43, 28));
            BtnGenerate.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 220, 60, 45));
            BtnGenerate.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 170, 35, 22));
            var s = BtnGenerate.Style;
            BtnGenerate.Style = null;
            BtnGenerate.Style = s;
        }
        else if (_continuousGenRunning)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(new FontIcon
            {
                FontFamily = SymbolFontFamily, Glyph = "\uE71A", FontSize = 16,
            });
            content.Children.Add(new TextBlock
            {
                Text = _continuousStopRequested
                    ? "正在停止..."
                    : (_continuousGenRemaining > 0 ? $"停止连续生成 ({_continuousGenRemaining})" : "停止连续生成")
            });
            BtnGenerate.Content = content;
            BtnGenerate.Resources["AccentButtonBackground"] = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 196, 43, 28));
            BtnGenerate.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 220, 60, 45));
            BtnGenerate.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 170, 35, 22));
            var s = BtnGenerate.Style;
            BtnGenerate.Style = null;
            BtnGenerate.Style = s;
        }
        else
        {
            foreach (var key in new[]
            {
                "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
                "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
            })
                BtnGenerate.Resources.Remove(key);
            var s = BtnGenerate.Style;
            BtnGenerate.Style = null;
            BtnGenerate.Style = s;
            UpdateBtnGenerateForApiKey();
            UpdateGenerateButtonWarning();
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  缩略图（重绘模式）
    // ═══════════════════════════════════════════════════════════

    private void SetupThumbnailTimer()
    {
        ThumbnailCanvas.Paused = false;
    }

    private void QueueThumbnailRender() { }

    private void OnThumbnailCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        try
        {
            float width = (float)sender.Size.Width;
            float height = (float)sender.Size.Height;
            if (width <= 0 || height <= 0) return;
            MaskCanvas.RenderThumbnail(args.DrawingSession, width, height);
        }
        catch { }
    }

    private void OnThumbnailPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ThumbnailContainer);
        if (pt.Properties.IsLeftButtonPressed)
        {
            _thumbDragging = true;
            ThumbnailContainer.CapturePointer(e.Pointer);
            _thumbDragStart = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
            MaskCanvas.BeginMoveImage();
            e.Handled = true;
        }
    }

    private void OnThumbnailPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_thumbDragging) return;
        var pt = e.GetCurrentPoint(ThumbnailContainer);
        var cur = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
        var delta = cur - _thumbDragStart;
        _thumbDragStart = cur;

        float ctrlW = (float)ThumbnailContainer.ActualWidth;
        float ctrlH = (float)ThumbnailContainer.ActualHeight;
        float scale = MaskCanvas.GetThumbnailScale(ctrlW, ctrlH);
        if (scale > 0)
            MaskCanvas.MoveImage(delta / scale);
        e.Handled = true;
    }

    private void OnThumbnailPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_thumbDragging)
        {
            _thumbDragging = false;
            ThumbnailContainer.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  提示词标签页
    // ═══════════════════════════════════════════════════════════

    private void OnPromptTabSwitch(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, TabPositive) && TabPositive.IsChecked == true)
        {
            SaveCurrentPromptToBuffer();
            _isPositiveTab = true;
            TabNegative.IsChecked = false;
            LoadPromptFromBuffer();
            UpdateSplitVisibility();
        }
        else if (ReferenceEquals(sender, TabNegative) && TabNegative.IsChecked == true)
        {
            SaveCurrentPromptToBuffer();
            _isPositiveTab = false;
            TabPositive.IsChecked = false;
            LoadPromptFromBuffer();
            UpdateSplitVisibility();
        }
        else
        {
            if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
                tb.IsChecked = true;
        }
        UpdatePromptHighlights();
    }

    private void OnSplitPromptToggle(object sender, RoutedEventArgs e)
    {
        _isSplitPrompt = BtnSplitPrompt.IsChecked == true;

        if (_isSplitPrompt)
        {
            string curStyle = _currentMode == AppMode.ImageGeneration
                ? _genStylePrompt : _inpaintStylePrompt;
            TxtStylePrompt.Text = curStyle;
            TxtPrompt.PlaceholderText = "输入正向提示词...";
        }
        else
        {
            string merged = MergeStyleAndMain(TxtStylePrompt.Text, TxtPrompt.Text);
            TxtPrompt.Text = merged;
            TxtStylePrompt.Text = "";
            if (_currentMode == AppMode.ImageGeneration) _genStylePrompt = "";
            else _inpaintStylePrompt = "";
        }

        UpdateSplitVisibility();
        UpdatePromptHighlights();
        SyncRememberedPromptAndParameterState();
    }

    private void UpdateSplitVisibility()
    {
        bool showSplit = _isSplitPrompt && _isPositiveTab;
        StylePromptGrid.Visibility = showSplit ? Visibility.Visible : Visibility.Collapsed;
        BtnSplitPrompt.Visibility = _isPositiveTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string MergeStyleAndMain(string style, string main)
    {
        style = style?.Trim() ?? "";
        main = main?.Trim() ?? "";
        if (style.Length > 0 && main.Length > 0) return style + ", " + main;
        return style.Length > 0 ? style : main;
    }

    private void LoadPromptFromBuffer()
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            TxtPrompt.Text = _isPositiveTab ? _genPositivePrompt : _genNegativePrompt;
            if (_isPositiveTab && _isSplitPrompt) TxtStylePrompt.Text = _genStylePrompt;
        }
        else
        {
            TxtPrompt.Text = _isPositiveTab ? _inpaintPositivePrompt : _inpaintNegativePrompt;
            if (_isPositiveTab && _isSplitPrompt) TxtStylePrompt.Text = _inpaintStylePrompt;
        }
        BtnSplitPrompt.IsChecked = _isSplitPrompt;
        TxtPrompt.PlaceholderText = _isPositiveTab ? "输入正向提示词..." : "输入负向提示词...";
    }

    private void SaveCurrentPromptToBuffer()
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            if (_isPositiveTab)
            {
                _genPositivePrompt = TxtPrompt.Text;
                if (_isSplitPrompt) _genStylePrompt = TxtStylePrompt.Text;
            }
            else _genNegativePrompt = TxtPrompt.Text;
            SaveAllCharacterPrompts();
        }
        else
        {
            if (_isPositiveTab)
            {
                _inpaintPositivePrompt = TxtPrompt.Text;
                if (_isSplitPrompt) _inpaintStylePrompt = TxtStylePrompt.Text;
            }
            else _inpaintNegativePrompt = TxtPrompt.Text;
            SaveAllCharacterPrompts();
        }

        SyncRememberedPromptAndParameterState();
    }

    private (string Positive, string Negative) GetPrompts(WildcardExpandContext? wildcardContext = null)
    {
        SaveCurrentPromptToBuffer();
        string genStyle = _currentMode == AppMode.ImageGeneration ? _genStylePrompt : _inpaintStylePrompt;
        string genPos = _currentMode == AppMode.ImageGeneration ? _genPositivePrompt : _inpaintPositivePrompt;
        string neg = _currentMode == AppMode.ImageGeneration ? _genNegativePrompt : _inpaintNegativePrompt;
        string positiveRaw = MergeStyleAndMain(genStyle, genPos);
        if (wildcardContext == null)
            return (ExpandPromptShortcuts(positiveRaw), ExpandPromptShortcuts(neg));

        string positive = ExpandPromptFeatures(positiveRaw, wildcardContext);
        string negative = ExpandPromptFeatures(neg, wildcardContext, isNegativeText: true);
        return (positive, negative);
    }

    private void LoadPromptShortcuts()
    {
        _promptShortcuts.Clear();
        try
        {
            if (!File.Exists(PromptShortcutsFilePath))
                return;
            var json = File.ReadAllText(PromptShortcutsFilePath);
            var items = JsonSerializer.Deserialize<List<PromptShortcutEntry>>(json) ?? new();
            _promptShortcuts.AddRange(items
                .Where(x => !string.IsNullOrWhiteSpace(x.Shortcut) && !string.IsNullOrWhiteSpace(x.Prompt))
                .Select(x => new PromptShortcutEntry
                {
                    Shortcut = x.Shortcut.Trim(),
                    Prompt = x.Prompt.Trim(),
                }));
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"加载快捷提示词失败: {ex.Message}";
        }
    }

    private void SavePromptShortcuts(IEnumerable<PromptShortcutEntry> items)
    {
        var normalized = items
            .Where(x => !string.IsNullOrWhiteSpace(x.Shortcut) && !string.IsNullOrWhiteSpace(x.Prompt))
            .GroupBy(x => x.Shortcut.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new PromptShortcutEntry
            {
                Shortcut = g.First().Shortcut.Trim(),
                Prompt = g.First().Prompt.Trim(),
            })
            .OrderBy(x => x.Shortcut, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(PromptShortcutsFilePath)!);
        File.WriteAllText(PromptShortcutsFilePath, JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        _promptShortcuts.Clear();
        _promptShortcuts.AddRange(normalized);
    }

    private string ExpandPromptShortcuts(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _promptShortcuts.Count == 0)
            return text;

        var shortcutMap = _promptShortcuts
            .Where(x => !string.IsNullOrWhiteSpace(x.Shortcut) && !string.IsNullOrWhiteSpace(x.Prompt))
            .GroupBy(x => x.Shortcut.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Prompt.Trim(), StringComparer.OrdinalIgnoreCase);

        if (shortcutMap.Count == 0)
            return text;

        var parts = Regex.Split(text, @"[,\r\n]+");
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            if (shortcutMap.TryGetValue(token, out var fullPrompt))
                parts[i] = fullPrompt;
            else
                parts[i] = token;
        }
        return string.Join(", ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    // ═══════════════════════════════════════════════════════════
    //  角色提示词管理
    // ═══════════════════════════════════════════════════════════

    private class CharacterEntry
    {
        public string PositivePrompt { get; set; } = "";
        public string NegativePrompt { get; set; } = "";
        public double CenterX { get; set; } = 0.5;
        public double CenterY { get; set; } = 0.5;
        public bool IsPositiveTab { get; set; } = true;
        public bool IsCollapsed { get; set; }
        public bool UseCustomPosition { get; set; }
        public TextBox? PromptBox { get; set; }
        public Canvas? HighlightCanvas { get; set; }
        public int HighlightVersion { get; set; }
    }

    private sealed class PromptShortcutEntry
    {
        public string Shortcut { get; set; } = "";
        public string Prompt { get; set; } = "";
    }

    private void OnAddCharacter(object sender, RoutedEventArgs e)
    {
        if (_genCharacters.Count >= MaxCharacters) return;
        _genCharacters.Add(new CharacterEntry());
        RefreshCharacterPanel();
    }

    private void RefreshCharacterPanel()
    {
        SaveAllCharacterPrompts();
        CharacterContainer.Children.Clear();
        for (int i = 0; i < _genCharacters.Count; i++)
            CharacterContainer.Children.Add(BuildCharacterUI(_genCharacters[i], i));
        RefreshVibeTransferPanel();
        RefreshPreciseReferencePanel();
        UpdateReferenceButtonAndPanelState();
        UpdateGenerateButtonWarning();
    }

    private UIElement BuildCharacterUI(CharacterEntry entry, int index)
    {
        var container = new StackPanel { Spacing = 4 };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tabPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        var rootGrid = (Grid)this.Content;
        var collapseBtn = CreateCharacterCollapseButton(entry.IsCollapsed);
        Grid.SetColumn(collapseBtn, 0);
        headerGrid.Children.Add(collapseBtn);

        var label = new TextBlock
        {
            Text = $"角色 {index + 1}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)rootGrid.Resources["InspectCaptionStyle"],
        };
        tabPanel.Children.Add(label);

        var tabPos = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
        {
            Content = "正向", IsChecked = entry.IsPositiveTab,
            CornerRadius = new CornerRadius(4, 0, 0, 4),
            MinWidth = 0, Height = 26, Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
        };
        var tabNeg = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
        {
            Content = "负向", IsChecked = !entry.IsPositiveTab,
            CornerRadius = new CornerRadius(0, 4, 4, 0),
            MinWidth = 0, Height = 26, Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
        };
        tabPanel.Children.Add(tabPos);
        tabPanel.Children.Add(tabNeg);
        Grid.SetColumn(tabPanel, 1);
        headerGrid.Children.Add(tabPanel);

        var movePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var upBtn = CreateCharacterActionButton("\uE70E", "上移", index > 0);
        var downBtn = CreateCharacterActionButton("\uE70D", "下移", index < _genCharacters.Count - 1);
        movePanel.Children.Add(upBtn);
        movePanel.Children.Add(downBtn);
        Grid.SetColumn(movePanel, 2);
        headerGrid.Children.Add(movePanel);

        int capturedMoveIdx = index;
        upBtn.Click += (_, _) => MoveCharacter(capturedMoveIdx, -1);
        downBtn.Click += (_, _) => MoveCharacter(capturedMoveIdx, 1);

        var posBtn = CreateCharacterActionButton("\uE819", "角色位置", true);
        posBtn.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(posBtn, 3);
        headerGrid.Children.Add(posBtn);

        var delBtn = CreateCharacterActionButton("\uE74D", "删除角色", true, isDelete: true);
        delBtn.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(delBtn, 4);
        headerGrid.Children.Add(delBtn);

        var textGrid = new Grid { MinHeight = 50, MaxHeight = 120 };
        var highlightCanvas = new Canvas { IsHitTestVisible = false };
        var textBox = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = false,
            PlaceholderText = entry.IsPositiveTab ? "角色正向提示词..." : "角色负向提示词...",
            Text = entry.IsPositiveTab ? entry.PositivePrompt : entry.NegativePrompt,
            MinHeight = 50, MaxHeight = 120,
            FontSize = 12,
        };
        ApplyTransparentTextBoxBackground(textBox);
        textBox.TextChanged += (_, _) =>
        {
            UpdateCharacterHighlight(entry);
            if (!_acInserting) TriggerAutoComplete(textBox);
        };
        textBox.SizeChanged += (_, _) => UpdateCharacterHighlight(entry);
        textBox.PreviewKeyDown += OnPromptPreviewKeyDown;
        textBox.KeyDown += OnPromptKeyDown;
        textBox.LostFocus += (_, _) => CloseAutoComplete();
        var promptFlyout = new MenuFlyout();
        promptFlyout.Opening += (_, _) => ConfigurePromptContextFlyout(promptFlyout, textBox, isStyleBox: false, allowQuickRandomStyle: false);
        textBox.ContextFlyout = promptFlyout;
        textGrid.Children.Add(highlightCanvas);
        textGrid.Children.Add(textBox);
        entry.PromptBox = textBox;
        entry.HighlightCanvas = highlightCanvas;

        tabPos.Click += (_, _) =>
        {
            if (entry.IsPositiveTab) { tabPos.IsChecked = true; return; }
            SaveCharacterPrompt(entry);
            entry.IsPositiveTab = true;
            tabPos.IsChecked = true; tabNeg.IsChecked = false;
            textBox.Text = entry.PositivePrompt;
            textBox.PlaceholderText = "角色正向提示词...";
        };
        tabNeg.Click += (_, _) =>
        {
            if (!entry.IsPositiveTab) { tabNeg.IsChecked = true; return; }
            SaveCharacterPrompt(entry);
            entry.IsPositiveTab = false;
            tabNeg.IsChecked = true; tabPos.IsChecked = false;
            textBox.Text = entry.NegativePrompt;
            textBox.PlaceholderText = "角色负向提示词...";
        };

        posBtn.Click += (_, _) => ShowCharacterPositionFlyout(posBtn, entry);

        int capturedIndex = index;
        delBtn.Click += (_, _) =>
        {
            SaveCharacterPrompt(entry);
            _genCharacters.Remove(entry);
            RefreshCharacterPanel();
        };

        collapseBtn.Click += (_, _) =>
        {
            SaveCharacterPrompt(entry);
            entry.IsCollapsed = !entry.IsCollapsed;
            RefreshCharacterPanel();
        };

        Visibility collapsedVisibility = entry.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        tabPos.Visibility = collapsedVisibility;
        tabNeg.Visibility = collapsedVisibility;
        movePanel.Visibility = collapsedVisibility;
        posBtn.Visibility = collapsedVisibility;
        delBtn.Visibility = collapsedVisibility;
        textGrid.Visibility = collapsedVisibility;

        container.Children.Add(headerGrid);
        if (!entry.IsCollapsed)
            container.Children.Add(textGrid);
        return container;
    }

    private Button CreateCharacterCollapseButton(bool isCollapsed)
    {
        var button = new Button
        {
            Width = 24,
            Height = 24,
            MinWidth = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(-2, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                FontFamily = SymbolFontFamily,
                Glyph = isCollapsed ? "\uE76C" : "\uE70D",
                FontSize = 10,
            },
        };

        if (this.Content is FrameworkElement root &&
            root.Resources.TryGetValue("SubtleButtonStyle", out object? rootStyleObj) &&
            rootStyleObj is Style rootSubtleStyle)
        {
            button.Style = rootSubtleStyle;
        }
        else if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out object? styleObj) && styleObj is Style subtleStyle)
        {
            button.Style = subtleStyle;
        }

        // 保底：确保非悬停状态是平面视觉，不出现凸起。
        button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        button.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        button.BorderThickness = new Thickness(0);

        ToolTipService.SetToolTip(button, isCollapsed ? "展开角色提示词" : "折叠角色提示词");
        return button;
    }

    private Button CreateCharacterActionButton(string glyph, string toolTip, bool isEnabled, bool isDelete = false)
    {
        var button = new Button
        {
            Width = 22,
            Height = 22,
            MinWidth = 22,
            Padding = new Thickness(0),
            IsEnabled = isEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                FontFamily = SymbolFontFamily,
                Glyph = glyph,
                FontSize = 10,
            },
        };
        if (this.Content is FrameworkElement root &&
            root.Resources.TryGetValue("SubtleButtonStyle", out object? rootStyleObj) &&
            rootStyleObj is Style rootSubtleStyle)
        {
            button.Style = rootSubtleStyle;
        }
        else if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out object? styleObj) && styleObj is Style subtleStyle)
        {
            button.Style = subtleStyle;
        }

        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        button.Background = transparent;
        button.BorderBrush = transparent;
        button.BorderThickness = new Thickness(0);

        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;

        if (!isEnabled)
        {
            var dimColor = IsDarkTheme()
                ? Windows.UI.Color.FromArgb(255, 80, 80, 80)
                : Windows.UI.Color.FromArgb(255, 180, 180, 180);
            var dimBrush = new SolidColorBrush(dimColor);
            button.Foreground = dimBrush;
            button.Resources["ButtonForegroundDisabled"] = dimBrush;
        }
        else if (isDelete)
        {
            button.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
        }

        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private void ShowCharacterPositionFlyout(Button anchor, CharacterEntry entry)
    {
        const int padSize = 120;
        var flyout = new Flyout();

        var panel = new StackPanel { Spacing = 8, Width = padSize + 16 };

        var rootGrid = (Grid)this.Content;
        var titleText = new TextBlock
        {
            Text = "角色位置",
            Style = (Style)rootGrid.Resources["InspectCaptionStyle"],
        };
        panel.Children.Add(titleText);

        var customPosToggle = new Microsoft.UI.Xaml.Controls.ToggleSwitch
        {
            Header = "自定义位置",
            IsOn = entry.UseCustomPosition,
            FontSize = 12,
        };
        panel.Children.Add(customPosToggle);

        var padBorder = new Border
        {
            Width = padSize, Height = padSize,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };

        var padCanvas = new Canvas { Width = padSize, Height = padSize };
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 12, Height = 12,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
            StrokeThickness = 2,
        };
        Canvas.SetLeft(dot, entry.CenterX * padSize - 6);
        Canvas.SetTop(dot, entry.CenterY * padSize - 6);
        padCanvas.Children.Add(dot);
        padBorder.Child = padCanvas;

        var coordText = new TextBlock
        {
            Text = $"X: {entry.CenterX:F2}    Y: {entry.CenterY:F2}",
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)rootGrid.Resources["InspectSubLabelStyle"],
        };

        var resetBtn = new Button
        {
            Content = "重置为中心", HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 11,
        };
        resetBtn.Click += (_, _) =>
        {
            entry.CenterX = 0.5; entry.CenterY = 0.5;
            Canvas.SetLeft(dot, padSize / 2.0 - 6);
            Canvas.SetTop(dot, padSize / 2.0 - 6);
            coordText.Text = "X: 0.50    Y: 0.50";
        };

        bool dragging = false;
        void UpdatePosition(double localX, double localY)
        {
            double nx = Math.Clamp(localX / padSize, 0, 1);
            double ny = Math.Clamp(localY / padSize, 0, 1);
            entry.CenterX = Math.Round(nx, 2);
            entry.CenterY = Math.Round(ny, 2);
            Canvas.SetLeft(dot, entry.CenterX * padSize - 6);
            Canvas.SetTop(dot, entry.CenterY * padSize - 6);
            coordText.Text = $"X: {entry.CenterX:F2}    Y: {entry.CenterY:F2}";
        }

        padCanvas.PointerPressed += (s, e) =>
        {
            dragging = true;
            (s as UIElement)?.CapturePointer(e.Pointer);
            var pt = e.GetCurrentPoint(padCanvas);
            UpdatePosition(pt.Position.X, pt.Position.Y);
        };
        padCanvas.PointerMoved += (s, e) =>
        {
            if (!dragging) return;
            var pt = e.GetCurrentPoint(padCanvas);
            UpdatePosition(pt.Position.X, pt.Position.Y);
        };
        padCanvas.PointerReleased += (s, e) =>
        {
            dragging = false;
            (s as UIElement)?.ReleasePointerCapture(e.Pointer);
        };

        var posControlsPanel = new StackPanel { Spacing = 8 };
        posControlsPanel.Children.Add(padBorder);
        posControlsPanel.Children.Add(coordText);
        posControlsPanel.Children.Add(resetBtn);
        posControlsPanel.Opacity = entry.UseCustomPosition ? 1.0 : 0.4;
        padCanvas.IsHitTestVisible = entry.UseCustomPosition;
        resetBtn.IsEnabled = entry.UseCustomPosition;

        customPosToggle.Toggled += (_, _) =>
        {
            entry.UseCustomPosition = customPosToggle.IsOn;
            posControlsPanel.Opacity = customPosToggle.IsOn ? 1.0 : 0.4;
            padCanvas.IsHitTestVisible = customPosToggle.IsOn;
            resetBtn.IsEnabled = customPosToggle.IsOn;
        };

        panel.Children.Add(posControlsPanel);

        flyout.Content = panel;
        flyout.ShowAt(anchor);
    }

    private static void ApplyTransparentTextBoxBackground(TextBox textBox)
    {
        textBox.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF));

        var lightDict = new ResourceDictionary();
        lightDict["TextControlBackground"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x02, 0xFF, 0xFF, 0xFF));
        lightDict["TextControlBackgroundPointerOver"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x02, 0xFF, 0xFF, 0xFF));
        lightDict["TextControlBackgroundFocused"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x02, 0xFF, 0xFF, 0xFF));
        lightDict["TextControlForeground"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
        lightDict["TextControlForegroundPointerOver"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
        lightDict["TextControlForegroundFocused"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));

        var darkDict = new ResourceDictionary();
        darkDict["TextControlBackground"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x02, 0x00, 0x00, 0x00));
        darkDict["TextControlBackgroundPointerOver"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x02, 0x00, 0x00, 0x00));
        darkDict["TextControlBackgroundFocused"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x02, 0x00, 0x00, 0x00));
        darkDict["TextControlForeground"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        darkDict["TextControlForegroundPointerOver"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        darkDict["TextControlForegroundFocused"] = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        var rd = new ResourceDictionary();
        rd.ThemeDictionaries["Light"] = lightDict;
        rd.ThemeDictionaries["Dark"] = darkDict;
        textBox.Resources = rd;
    }

    private void UpdateCharacterHighlight(CharacterEntry entry)
    {
        if (entry.HighlightCanvas == null || entry.PromptBox == null) return;
        entry.HighlightCanvas.Children.Clear();
        if (string.IsNullOrEmpty(entry.PromptBox.Text) || !_settings.Settings.WeightHighlight) return;
        entry.HighlightVersion++;
        int version = entry.HighlightVersion;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (version != entry.HighlightVersion) return;
            DrawHighlightsFor(entry.PromptBox, entry.HighlightCanvas);
        });
    }

    private static void SaveCharacterPrompt(CharacterEntry entry)
    {
        if (entry.PromptBox == null) return;
        if (entry.IsPositiveTab)
            entry.PositivePrompt = entry.PromptBox.Text;
        else
            entry.NegativePrompt = entry.PromptBox.Text;
    }

    private void SaveAllCharacterPrompts()
    {
        foreach (var entry in _genCharacters)
            SaveCharacterPrompt(entry);
    }

    private static readonly System.Text.RegularExpressions.Regex CharCountPrefixRegex =
        new(@"\b1(girl|boy|other)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripCharCountPrefix(string prompt)
    {
        return CharCountPrefixRegex.Replace(prompt, "$1");
    }

    private void MoveCharacter(int index, int direction)
    {
        int newIndex = index + direction;
        if (newIndex < 0 || newIndex >= _genCharacters.Count) return;
        SaveAllCharacterPrompts();
        var entry = _genCharacters[index];
        _genCharacters.RemoveAt(index);
        _genCharacters.Insert(newIndex, entry);
        RefreshCharacterPanel();
    }

    private void ApplyCharCountPrefixStrip()
    {
        SaveAllCharacterPrompts();
        foreach (var entry in _genCharacters)
        {
            entry.PositivePrompt = StripCharCountPrefix(entry.PositivePrompt);
            entry.NegativePrompt = StripCharCountPrefix(entry.NegativePrompt);
            if (entry.PromptBox != null)
            {
                entry.PromptBox.Text = entry.IsPositiveTab ? entry.PositivePrompt : entry.NegativePrompt;
            }
        }
    }

    private List<CharacterPromptInfo> GetCharacterData(WildcardExpandContext? wildcardContext = null)
    {
        SaveAllCharacterPrompts();
        var result = new List<CharacterPromptInfo>();
        foreach (var entry in _genCharacters)
        {
            result.Add(new CharacterPromptInfo
            {
                PositivePrompt = wildcardContext == null
                    ? ExpandPromptShortcuts(entry.PositivePrompt)
                    : ExpandPromptFeatures(entry.PositivePrompt, wildcardContext),
                NegativePrompt = wildcardContext == null
                    ? ExpandPromptShortcuts(entry.NegativePrompt)
                    : ExpandPromptFeatures(entry.NegativePrompt, wildcardContext, isNegativeText: true),
                CenterX = entry.CenterX,
                CenterY = entry.CenterY,
                UseCustomPosition = entry.UseCustomPosition,
            });
        }
        return result;
    }

    private void SetGenCharactersFromMetadata(ImageMetadata meta)
    {
        _genCharacters.Clear();
        int count = Math.Min(meta.CharacterPrompts.Count, MaxCharacters);
        for (int i = 0; i < count; i++)
        {
            var entry = new CharacterEntry
            {
                PositivePrompt = meta.CharacterPrompts[i],
                NegativePrompt = i < meta.CharacterNegativePrompts.Count
                    ? meta.CharacterNegativePrompts[i] : "",
                CenterX = i < meta.CharacterCenters.Count ? meta.CharacterCenters[i].X : 0.5,
                CenterY = i < meta.CharacterCenters.Count ? meta.CharacterCenters[i].Y : 0.5,
            };
            _genCharacters.Add(entry);
        }
    }

    private void OnPromptTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePromptHighlights();
        if (!_acInserting) TriggerAutoComplete(TxtPrompt);
    }
    private void OnPromptSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePromptHighlights();
    private void OnPromptLostFocus(object sender, RoutedEventArgs e) => CloseAutoComplete();
    private void OnStylePromptTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateStyleHighlights();
        if (!_acInserting) TriggerAutoComplete(TxtStylePrompt);
    }
    private void OnStylePromptSizeChanged(object sender, SizeChangedEventArgs e) => UpdateStyleHighlights();

    private sealed class NormalizeOptions
    {
        public bool Lowercase { get; set; } = true;
        public bool HalfWidth { get; set; } = true;
        public bool RemoveSpecial { get; set; } = true;
        public bool UnderscoreToSpace { get; set; } = true;
        public bool RemoveNewlines { get; set; } = true;
        public bool RemoveJunk { get; set; } = true;
        public bool RemoveNonAscii { get; set; } = true;
        public bool PreserveWildcards { get; set; } = true;
    }

    private async void OnNormalizePrompts(object sender, RoutedEventArgs e)
    {
        var options = new NormalizeOptions();
        var chkLower = new CheckBox { Content = "转为小写", IsChecked = options.Lowercase };
        var chkHalf = new CheckBox { Content = "使用半角标点", IsChecked = options.HalfWidth };
        var chkSpecial = new CheckBox { Content = "移除特殊符号（【】）", IsChecked = options.RemoveSpecial };
        var chkUnderscore = new CheckBox { Content = "下划线转空格", IsChecked = options.UnderscoreToSpace };
        var chkNewlines = new CheckBox { Content = "换行转逗号", IsChecked = options.RemoveNewlines };
        var chkJunk = new CheckBox { Content = "移除常见质量/artist前缀", IsChecked = options.RemoveJunk };
        var chkAscii = new CheckBox { Content = "移除非 ASCII 字符", IsChecked = options.RemoveNonAscii };
        var chkPreserveWild = new CheckBox { Content = "保留 Wildcards 语法", IsChecked = options.PreserveWildcards };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(chkLower);
        panel.Children.Add(chkHalf);
        panel.Children.Add(chkSpecial);
        panel.Children.Add(chkUnderscore);
        panel.Children.Add(chkNewlines);
        panel.Children.Add(chkJunk);
        panel.Children.Add(chkAscii);
        panel.Children.Add(chkPreserveWild);

        var dialog = new ContentDialog
        {
            Title = "提示词标准化",
            Content = panel,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        options.Lowercase = chkLower.IsChecked == true;
        options.HalfWidth = chkHalf.IsChecked == true;
        options.RemoveSpecial = chkSpecial.IsChecked == true;
        options.UnderscoreToSpace = chkUnderscore.IsChecked == true;
        options.RemoveNewlines = chkNewlines.IsChecked == true;
        options.RemoveJunk = chkJunk.IsChecked == true;
        options.RemoveNonAscii = chkAscii.IsChecked == true;
        options.PreserveWildcards = chkPreserveWild.IsChecked == true;

        ApplyPromptNormalization(options);
    }

    private void ApplyPromptNormalization(NormalizeOptions options)
    {
        SaveCurrentPromptToBuffer();
        SaveAllCharacterPrompts();

        if (_currentMode == AppMode.ImageGeneration)
        {
            _genPositivePrompt = NormalizeAnnotation(_genPositivePrompt, options);
            _genNegativePrompt = NormalizeAnnotation(_genNegativePrompt, options);
            _genStylePrompt = NormalizeAnnotation(_genStylePrompt, options);
        }
        else if (_currentMode == AppMode.Inpaint)
        {
            _inpaintPositivePrompt = NormalizeAnnotation(_inpaintPositivePrompt, options);
            _inpaintNegativePrompt = NormalizeAnnotation(_inpaintNegativePrompt, options);
            _inpaintStylePrompt = NormalizeAnnotation(_inpaintStylePrompt, options);
        }

        foreach (var entry in _genCharacters)
        {
            entry.PositivePrompt = NormalizeAnnotation(entry.PositivePrompt, options);
            entry.NegativePrompt = NormalizeAnnotation(entry.NegativePrompt, options);
        }

        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        RefreshCharacterPanel();
        UpdatePromptHighlights();
        UpdateStyleHighlights();
        TxtStatus.Text = "已完成提示词标准化";
    }

    private static readonly Regex WildcardTokenPreserveRegex = new(@"__(.+?)__", RegexOptions.Compiled);

    private static string NormalizeAnnotation(string text, NormalizeOptions opts)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var wildcardSlots = new Dictionary<string, string>();
        if (opts.PreserveWildcards)
        {
            int slotIdx = 0;
            text = WildcardTokenPreserveRegex.Replace(text, m =>
            {
                string placeholder = $"\x01WC{slotIdx++}\x01";
                wildcardSlots[placeholder] = m.Value;
                return placeholder;
            });
        }

        if (opts.Lowercase)
            text = text.ToLowerInvariant();

        if (opts.HalfWidth)
            text = text
                .Replace('，', ',')
                .Replace('　', ' ')
                .Replace('（', '(')
                .Replace('）', ')')
                .Replace('、', ',');
        else
            text = text
                .Replace(',', '，')
                .Replace('(', '（')
                .Replace(')', '）');

        if (opts.RemoveSpecial)
            text = Regex.Replace(text, @"[【】]", "");

        if (opts.UnderscoreToSpace)
        {
            if (opts.PreserveWildcards)
            {
                var sb = new StringBuilder(text);
                for (int i = 0; i < sb.Length; i++)
                {
                    if (sb[i] == '_' && !IsInsidePlaceholder(text, i))
                        sb[i] = ' ';
                }
                text = sb.ToString();
            }
            else
            {
                text = text.Replace('_', ' ');
            }
        }

        if (opts.RemoveNewlines)
            text = text.Replace("\r\n", "\n").Replace('\n', ',');

        if (opts.RemoveJunk)
        {
            var phrases = new[] { "artist:", "best quality", "amazing quality", "very aesthetic", "absurdres" };
            string pattern = @"\b(" + string.Join("|", phrases.Select(Regex.Escape)) + @")\b";
            text = Regex.Replace(text, pattern, "", RegexOptions.IgnoreCase);
        }

        if (opts.RemoveNonAscii)
            text = new string(text.Where(c => c <= 127 || c == '\x01').ToArray());

        string tempText = text.Replace('，', ',');
        var tags = tempText.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var uniqueTags = new List<string>();
        var seenTags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            string cleaned = tag.Trim().Replace("/", "");
            if (string.IsNullOrEmpty(cleaned) || !seenTags.Add(cleaned)) continue;
            uniqueTags.Add(cleaned);
        }

        string joinSep = opts.HalfWidth ? ", " : "，";
        string result = string.Join(joinSep, uniqueTags);

        foreach (var kvp in wildcardSlots)
            result = result.Replace(kvp.Key, kvp.Value);

        return result;
    }

    private static bool IsInsidePlaceholder(string text, int index)
    {
        int prevMarker = text.LastIndexOf('\x01', index);
        if (prevMarker < 0) return false;
        int nextMarker = text.IndexOf('\x01', index);
        if (nextMarker < 0) return false;
        string between = text[prevMarker..(nextMarker + 1)];
        return between.Contains("WC");
    }

    private void DebugLog(string msg)
    {
        if (!_settings.Settings.DevLogEnabled) return;
        try
        {
            string dir = System.IO.Path.Combine(AppRootDir, "logs");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir,
                $"debug_{DateTime.Now:yyyy-MM-dd}.txt");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private void UpdatePromptHighlights()
    {
        PromptHighlightCanvas.Children.Clear();
        string text = TxtPrompt.Text;
        bool wh = _settings.Settings.WeightHighlight;
        DebugLog($"UpdatePromptHighlights: text.len={text?.Length}, WeightHighlight={wh}");
        if (string.IsNullOrEmpty(text) || !wh) return;
        _promptHighlightVer++;
        int version = _promptHighlightVer;
        DebugLog($"  enqueue ver={version}");
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            DebugLog($"  callback ver={version}, current={_promptHighlightVer}");
            if (version != _promptHighlightVer) return;
            DrawHighlightsFor(TxtPrompt, PromptHighlightCanvas);
        });
    }

    private void UpdateStyleHighlights()
    {
        StyleHighlightCanvas.Children.Clear();
        if (string.IsNullOrEmpty(TxtStylePrompt.Text) || !_settings.Settings.WeightHighlight) return;
        _styleHighlightVer++;
        int version = _styleHighlightVer;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (version != _styleHighlightVer) return;
            DrawHighlightsFor(TxtStylePrompt, StyleHighlightCanvas);
        });
    }

    private static readonly Regex WildcardHighlightExplicitRegex = new(@"__(.+?)__", RegexOptions.Compiled);

    private void DrawHighlightsFor(TextBox textBox, Canvas canvas)
    {
        canvas.Children.Clear();
        var text = textBox.Text;
        if (string.IsNullOrEmpty(text) || !_settings.Settings.WeightHighlight) return;

        bool isDark = ((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark;
        var greenColor = isDark
            ? Windows.UI.Color.FromArgb(50, 16, 185, 129)
            : Windows.UI.Color.FromArgb(70, 16, 185, 129);
        var redColor = isDark
            ? Windows.UI.Color.FromArgb(50, 239, 68, 68)
            : Windows.UI.Color.FromArgb(70, 239, 68, 68);
        var purpleColor = isDark
            ? Windows.UI.Color.FromArgb(55, 168, 85, 247)
            : Windows.UI.Color.FromArgb(65, 147, 51, 234);

        var pattern = new Regex(@"(\d+\.?\d*)::");
        var matches = pattern.Matches(text);
        DebugLog($"DrawHighlightsFor: textBox={textBox.Name}, text.len={text.Length}, matches={matches.Count}, canvas.W={canvas.ActualWidth}, canvas.H={canvas.ActualHeight}, textBox.W={textBox.ActualWidth}, textBox.H={textBox.ActualHeight}");

        int drawn = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double w) || w == 1.0)
            {
                DebugLog($"  match[{i}] '{m.Value}' w={m.Groups[1].Value} -> skip (w=1 or parse fail)");
                continue;
            }

            int contentStart = m.Index + m.Length;
            int searchEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;

            int closingIdx = -1;
            int searchLen = searchEnd - contentStart;
            if (searchLen >= 2)
                closingIdx = text.IndexOf("::", contentStart, searchLen);

            int segEnd = (closingIdx >= 0) ? closingIdx + 2 : searchEnd;
            DebugLog($"  match[{i}] '{m.Value}' w={w}, start={m.Index}, len={segEnd - m.Index}");

            DrawSegmentHighlight(textBox, canvas, m.Index, segEnd - m.Index, w > 1 ? greenColor : redColor);
            drawn++;
        }

        DrawWildcardHighlights(textBox, canvas, text, purpleColor);
        DebugLog($"  drawn={drawn}, canvas.Children={canvas.Children.Count}");
    }

    private void DrawWildcardHighlights(TextBox textBox, Canvas canvas, string text, Windows.UI.Color color)
    {
        if (!_settings.Settings.WildcardsEnabled || !_wildcardService.IsLoaded) return;

        bool requireExplicit = _settings.Settings.WildcardsRequireExplicitSyntax;

        var explicitMatches = WildcardHighlightExplicitRegex.Matches(text);
        foreach (Match m in explicitMatches)
        {
            string body = m.Groups[1].Value.Trim();
            string name = body;
            int atIdx = body.LastIndexOf('@');
            if (atIdx > 0) name = body[..atIdx].Trim();
            if (body.StartsWith("@", StringComparison.Ordinal) || _wildcardService.HasEntry(name))
                DrawSegmentHighlight(textBox, canvas, m.Index, m.Length, color);
        }

        if (!requireExplicit)
        {
            var tokens = text.Split(new[] { ',' }, StringSplitOptions.None);
            int pos = 0;
            foreach (var token in tokens)
            {
                string trimmed = token.Trim();
                if (trimmed.Length > 0
                    && !(trimmed.StartsWith("__") && trimmed.EndsWith("__"))
                    && _wildcardService.HasEntry(trimmed))
                {
                    int trimStart = pos + token.IndexOf(trimmed[0]);
                    DrawSegmentHighlight(textBox, canvas, trimStart, trimmed.Length, color);
                }
                pos += token.Length + 1;
            }
        }
    }

    private void DrawSegmentHighlight(TextBox textBox, Canvas canvas,
        int start, int length, Windows.UI.Color color)
    {
        int textLen = textBox.Text.Length;
        if (length <= 0 || start >= textLen) return;
        int end = Math.Min(start + length, textLen);

        double offsetX = textBox.BorderThickness.Left + textBox.Padding.Left;
        double offsetY = textBox.BorderThickness.Top + textBox.Padding.Top;

        try
        {
            var brush = new SolidColorBrush(color);
            var lines = new List<(double x, double y, double right, double rawH)>();
            double currentY = -1, lineStartX = 0, lineHeight = 0;

            for (int ci = start; ci < end; ci++)
            {
                var cr = textBox.GetRectFromCharacterIndex(ci, false);
                if (ci == start)
                    DebugLog($"    GetRect[{ci}] = x={cr.X}, y={cr.Y}, w={cr.Width}, h={cr.Height}");
                if (cr.Height <= 0) continue;

                double charX = cr.X + offsetX;
                double charY = cr.Y + offsetY;

                if (currentY < 0 || Math.Abs(charY - currentY) > 2)
                {
                    if (currentY >= 0)
                    {
                        var trail = textBox.GetRectFromCharacterIndex(ci - 1, true);
                        lines.Add((lineStartX, currentY, trail.X + offsetX, lineHeight));
                    }
                    currentY = charY;
                    lineStartX = charX;
                    lineHeight = cr.Height;
                }
            }

            if (currentY >= 0)
            {
                var trail = textBox.GetRectFromCharacterIndex(end - 1, true);
                lines.Add((lineStartX, currentY, trail.X + offsetX, lineHeight));
            }

            DebugLog($"    lines={lines.Count}");
            for (int li = 0; li < lines.Count; li++)
            {
                var (x, y, right, rawH) = lines[li];
                double h = rawH - 4;
                if (li + 1 < lines.Count)
                    h = Math.Min(h, lines[li + 1].y - y);
                DebugLog($"    rect[{li}] x={x:F1}, y={y:F1}, w={right - x:F1}, h={h:F1}");
                if (h <= 0 || right - x <= 0) continue;
                var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = right - x, Height = h, Fill = brush, RadiusX = 3, RadiusY = 3,
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                canvas.Children.Add(rect);
            }
        }
        catch (Exception ex) { DebugLog($"    EXCEPTION: {ex.Message}"); }
    }

    // ═══════════════════════════════════════════════════════════
    //  菜单事件
    // ═══════════════════════════════════════════════════════════

    private async void OnOpenImage(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".webp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            if (_currentMode == AppMode.Inspect)
            {
                await LoadInspectImageAsync(file.Path);
            }
            else if (_currentMode == AppMode.Effects)
            {
                await LoadEffectsImageAsync(file.Path);
            }
            else if (_currentMode == AppMode.Inpaint)
            {
                await MaskCanvas.LoadImageAsync(file);
            }
            else
            {
                try
                {
                    var bitmapImage = new BitmapImage();
                    using var stream = await file.OpenReadAsync();
                    await bitmapImage.SetSourceAsync(stream);
                    GenPreviewImage.Source = bitmapImage;
                    GenPlaceholder.Visibility = Visibility.Collapsed;
                    TxtStatus.Text = $"已加载: {file.Path}";
                }
                catch (Exception ex) { TxtStatus.Text = $"加载失败: {ex.Message}"; }
            }
        }
    }

    private async void OnSaveOverwrite(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.Inspect)
        {
            await SaveInspectOverwriteAsync();
        }
        else if (_currentMode == AppMode.Effects)
        {
            await SaveEffectsOverwriteAsync();
        }
        else if (_currentMode == AppMode.Inpaint)
        {
            await SaveInpaintOverwriteAsync();
        }
        else
        {
            if (_currentGenImageBytes == null)
            { TxtStatus.Text = "没有生成结果可保存"; return; }
            if (!string.IsNullOrEmpty(_currentGenImagePath) && File.Exists(_currentGenImagePath))
            {
                TxtStatus.Text = $"已保存: {_currentGenImagePath}";
            }
            else
            {
                TxtStatus.Text = "生图结果已自动保存到 output 文件夹";
            }
        }
    }

    private async Task SaveInpaintOverwriteAsync()
    {
        var filePath = MaskCanvas.LoadedFilePath;
        if (string.IsNullOrEmpty(filePath))
        { TxtStatus.Text = "未导入图片，无法保存"; return; }

        byte[]? bytesToSave = null;
        if (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null)
        {
            try { bytesToSave = await CreatePreviewCompositeBytes(); }
            catch (Exception ex) { TxtStatus.Text = $"合成预览图失败: {ex.Message}"; return; }
        }
        else
        {
            bytesToSave = await CreateCurrentFullImageBytes();
        }

        if (bytesToSave == null || bytesToSave.Length == 0)
        { TxtStatus.Text = "没有可保存的图像内容"; return; }

        string sizeWarning = "";
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists)
            {
                using var origStream = File.OpenRead(filePath);
                using var skBmp = SkiaSharp.SKBitmap.Decode(origStream);
                if (skBmp != null)
                {
                    using var newStream = new MemoryStream(bytesToSave);
                    using var newBmp = SkiaSharp.SKBitmap.Decode(newStream);
                    if (newBmp != null && (skBmp.Width != newBmp.Width || skBmp.Height != newBmp.Height))
                        sizeWarning = $"\n\n注意：图像尺寸已从 {skBmp.Width}×{skBmp.Height} 变为 {newBmp.Width}×{newBmp.Height}。";
                }
            }
        }
        catch { }

        var dialog = new ContentDialog
        {
            Title = "确认保存",
            Content = $"将覆盖原始文件：\n{filePath}{sizeWarning}",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await File.WriteAllBytesAsync(filePath, bytesToSave);
            TxtStatus.Text = $"已保存: {filePath}";
        }
        catch (Exception ex) { TxtStatus.Text = $"保存失败: {ex.Message}"; }
    }

    private async Task<byte[]?> CreateCurrentFullImageBytes()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null) return null;
        var doc = MaskCanvas.Document;
        if (doc.OriginalImage == null) return null;

        var offset = doc.PixelAlignedImageOffset;
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;
        float origW = doc.OriginalImage.SizeInPixels.Width;
        float origH = doc.OriginalImage.SizeInPixels.Height;

        float canvasInOrigX = -offset.X, canvasInOrigY = -offset.Y;
        float minX = Math.Min(0, canvasInOrigX);
        float minY = Math.Min(0, canvasInOrigY);
        float maxX = Math.Max(origW, canvasInOrigX + canvasW);
        float maxY = Math.Max(origH, canvasInOrigY + canvasH);
        int compositeW = (int)Math.Ceiling(maxX - minX);
        int compositeH = (int)Math.Ceiling(maxY - minY);

        using var composite = new CanvasRenderTarget(device, compositeW, compositeH, 96f);
        using (var ds = composite.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawImage(doc.OriginalImage, -minX, -minY);
        }

        using var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await composite.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
        saveStream.Seek(0);
        var bytes = new byte[saveStream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(saveStream);
        await reader.LoadAsync((uint)saveStream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        await SaveAsInternal(stripMetadata: false);
    }

    private async void OnSaveAsStripped(object sender, RoutedEventArgs e)
    {
        await SaveAsInternal(stripMetadata: true);
    }

    private async Task SaveAsInternal(bool stripMetadata)
    {
        byte[]? bytesToSave = null;

        if (_currentMode == AppMode.Inspect)
        {
            bytesToSave = GetInspectSaveBytes(stripMetadata);
        }
        else if (_currentMode == AppMode.Effects)
        {
            bytesToSave = await GetEffectsSaveBytesAsync();
        }
        else if (_currentMode == AppMode.Inpaint)
        {
            if (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null)
            {
                try { bytesToSave = await CreatePreviewCompositeBytes(); }
                catch (Exception ex) { TxtStatus.Text = $"合成预览图失败: {ex.Message}"; return; }
            }
            else
            {
                bytesToSave = _lastGeneratedImageBytes;
            }
        }
        else
        {
            bytesToSave = stripMetadata && _currentGenImageBytes != null
                ? ImageMetadataService.StripPngMetadata(_currentGenImageBytes)
                : _currentGenImageBytes;
        }

        if (bytesToSave == null || bytesToSave.Length == 0)
        { TxtStatus.Text = "没有可保存的图片"; return; }

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
        picker.SuggestedFileName = $"nai_{DateTime.Now:yyyyMMdd_HHmmss}";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            try
            {
                await Windows.Storage.FileIO.WriteBytesAsync(file, bytesToSave);
                TxtStatus.Text = stripMetadata
                    ? $"已保存（已抹除元数据）: {file.Path}"
                    : $"已保存: {file.Path}";
            }
            catch (Exception ex) { TxtStatus.Text = $"保存失败: {ex.Message}"; }
        }
    }

    private async Task<byte[]?> CreatePreviewCompositeBytes()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null || _pendingResultBitmap == null) return null;

        var doc = MaskCanvas.Document;
        if (doc.OriginalImage == null) return _pendingResultBytes;

        var offset = doc.PixelAlignedImageOffset;
        int canvasW = MaskCanvas.CanvasW, canvasH = MaskCanvas.CanvasH;
        float origW = doc.OriginalImage.SizeInPixels.Width;
        float origH = doc.OriginalImage.SizeInPixels.Height;

        float canvasInOrigX = -offset.X, canvasInOrigY = -offset.Y;
        float minX = Math.Min(0, canvasInOrigX);
        float minY = Math.Min(0, canvasInOrigY);
        float maxX = Math.Max(origW, canvasInOrigX + canvasW);
        float maxY = Math.Max(origH, canvasInOrigY + canvasH);
        int compositeW = (int)Math.Ceiling(maxX - minX);
        int compositeH = (int)Math.Ceiling(maxY - minY);

        using var composite = new CanvasRenderTarget(device, compositeW, compositeH, 96f);
        using (var ds = composite.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawImage(doc.OriginalImage, -minX, -minY);
            ds.DrawImage(_pendingResultBitmap, canvasInOrigX - minX, canvasInOrigY - minY);
        }

        using var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await composite.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
        saveStream.Seek(0);
        var bytes = new byte[saveStream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(saveStream);
        await reader.LoadAsync((uint)saveStream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private void OnOpenImageFolder(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            if (!string.IsNullOrEmpty(_currentGenImagePath) && File.Exists(_currentGenImagePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_currentGenImagePath}\"");
                return;
            }
            var outputDir = OutputBaseDir;
            if (Directory.Exists(outputDir))
            {
                System.Diagnostics.Process.Start("explorer.exe", outputDir);
                return;
            }
            TxtStatus.Text = "尚无输出文件夹";
            return;
        }

        if (_currentMode == AppMode.Effects)
        {
            if (string.IsNullOrEmpty(_effectsImagePath) || !File.Exists(_effectsImagePath))
            { TxtStatus.Text = "尚未加载图片，无法打开文件夹"; return; }
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_effectsImagePath}\"");
            return;
        }

        var path = MaskCanvas.LoadedFilePath;
        if (string.IsNullOrEmpty(path))
        { TxtStatus.Text = "尚未加载图片，无法打开文件夹"; return; }
        var dir = Path.GetDirectoryName(path);
        if (dir != null && Directory.Exists(dir))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            TxtStatus.Text = "文件夹不存在或路径无效";
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        CloseAdvancedParamsWindow();
        Close();
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.Inpaint && !MaskCanvas.IsInPreviewMode)
        {
            MaskCanvas.PerformUndo();
            return;
        }

        if (_currentMode == AppMode.Effects && _effectsUndoStack.Count > 0)
        {
            _effectsRedoStack.Push(CaptureEffectsWorkspaceState());
            var state = _effectsUndoStack.Pop();
            await RestoreEffectsWorkspaceStateAsync(state);
            TxtStatus.Text = "已撤销效果操作";
        }
    }

    private async void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.Inpaint && !MaskCanvas.IsInPreviewMode)
        {
            MaskCanvas.PerformRedo();
            return;
        }

        if (_currentMode == AppMode.Effects && _effectsRedoStack.Count > 0)
        {
            _effectsUndoStack.Push(CaptureEffectsWorkspaceState());
            var state = _effectsRedoStack.Pop();
            await RestoreEffectsWorkspaceStateAsync(state);
            TxtStatus.Text = "已重做效果操作";
        }
    }

    private void OnClearMask(object sender, RoutedEventArgs e)
    { if (_currentMode == AppMode.Inpaint && !MaskCanvas.IsInPreviewMode) MaskCanvas.ClearMask(); }

    private void OnFillEmpty(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.FillEmptyAreas();
        TxtStatus.Text = "已填充空白区域";
    }

    private void OnInvertMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.InvertMask();
        TxtStatus.Text = "已反转遮罩";
    }

    private void OnExpandMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.ExpandMask();
        TxtStatus.Text = "已扩展遮罩 (边缘+1px)";
    }

    private void OnShrinkMask(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        MaskCanvas.ShrinkMask();
        TxtStatus.Text = "已收缩遮罩 (边缘-1px)";
    }

    private void OnTrimCanvas(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.Inpaint) return;
        if (MaskCanvas.IsInPreviewMode) { TxtStatus.Text = "预览模式下无法修剪画布"; return; }
        if (MaskCanvas.TrimCanvas())
            TxtStatus.Text = $"画布已修剪至 {MaskCanvas.CanvasW} × {MaskCanvas.CanvasH}";
        else
            TxtStatus.Text = "无可修剪内容（画布为空或已是最小）";
    }

    private void OnFitToScreen(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.Inpaint) MaskCanvas.FitToScreen();
        else if (_currentMode == AppMode.Inspect) FitInspectPreviewToScreen();
        else if (_currentMode == AppMode.Upscale) FitUpscalePreviewToScreen();
        else if (_currentMode == AppMode.Effects) FitEffectsPreviewToScreen();
        else FitGenPreviewToScreen();
    }
    private void OnActualSize(object sender, RoutedEventArgs e)
    {
        float dpiScale = (float)(this.Content.XamlRoot?.RasterizationScale ?? 1.0);
        float trueZoom = 1.0f / dpiScale;
        if (_currentMode == AppMode.Inpaint) MaskCanvas.ActualSize();
        else if (_currentMode == AppMode.Inspect) InspectImageScroller.ChangeView(null, null, trueZoom);
        else if (_currentMode == AppMode.Upscale) UpscaleImageScroller.ChangeView(null, null, trueZoom);
        else if (_currentMode == AppMode.Effects) EffectsImageScroller.ChangeView(null, null, trueZoom);
        else GenImageScroller.ChangeView(null, null, trueZoom);
    }
    private void OnCenterView(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.Inpaint) MaskCanvas.CenterView();
        else if (_currentMode == AppMode.Inspect) FitInspectPreviewToScreen();
        else if (_currentMode == AppMode.Upscale) FitUpscalePreviewToScreen();
        else if (_currentMode == AppMode.Effects) CenterEffectsPreview();
        else FitGenPreviewToScreen();
    }

    private Microsoft.UI.Windowing.OverlappedPresenterState _lastPresenterState;
    private void OnMaskCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_currentMode != AppMode.Inpaint) return;
        if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            var state = p.State;
            if (state != _lastPresenterState)
            {
                _lastPresenterState = state;
                MaskCanvas.CenterView();
            }
        }
        QueueThumbnailRender();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.Inpaint) MaskCanvas.ZoomIn();
        else if (_currentMode == AppMode.Inspect)
            InspectImageScroller.ChangeView(null, null, InspectImageScroller.ZoomFactor * 1.25f);
        else if (_currentMode == AppMode.Upscale)
            UpscaleImageScroller.ChangeView(null, null, UpscaleImageScroller.ZoomFactor * 1.25f);
        else if (_currentMode == AppMode.Effects)
            EffectsImageScroller.ChangeView(null, null, EffectsImageScroller.ZoomFactor * 1.25f);
        else GenImageScroller.ChangeView(null, null, GenImageScroller.ZoomFactor * 1.25f);
    }
    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.Inpaint) MaskCanvas.ZoomOut();
        else if (_currentMode == AppMode.Inspect)
            InspectImageScroller.ChangeView(null, null, InspectImageScroller.ZoomFactor / 1.25f);
        else if (_currentMode == AppMode.Upscale)
            UpscaleImageScroller.ChangeView(null, null, UpscaleImageScroller.ZoomFactor / 1.25f);
        else if (_currentMode == AppMode.Effects)
            EffectsImageScroller.ChangeView(null, null, EffectsImageScroller.ZoomFactor / 1.25f);
        else GenImageScroller.ChangeView(null, null, GenImageScroller.ZoomFactor / 1.25f);
    }

    private void SetupPreviewScrollZoomAndDrag()
    {
        GenImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        InspectImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        EffectsImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);
        UpscaleImageScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPreviewWheelZoom), true);

        GenPreviewImage.PointerPressed += OnPreviewDragStart;
        GenPreviewImage.PointerMoved += OnPreviewDragMove;
        GenPreviewImage.PointerReleased += OnPreviewDragEnd;
        GenPreviewImage.PointerCanceled += OnPreviewDragEnd;
        GenPreviewImage.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnGenPreviewPointerPressed), true);

        InspectPreviewImage.PointerPressed += OnPreviewDragStart;
        InspectPreviewImage.PointerMoved += OnPreviewDragMove;
        InspectPreviewImage.PointerReleased += OnPreviewDragEnd;
        InspectPreviewImage.PointerCanceled += OnPreviewDragEnd;

        EffectsPreviewImage.PointerPressed += OnPreviewDragStart;
        EffectsPreviewImage.PointerMoved += OnPreviewDragMove;
        EffectsPreviewImage.PointerReleased += OnPreviewDragEnd;
        EffectsPreviewImage.PointerCanceled += OnPreviewDragEnd;

        UpscalePreviewImage.PointerPressed += OnPreviewDragStart;
        UpscalePreviewImage.PointerMoved += OnPreviewDragMove;
        UpscalePreviewImage.PointerReleased += OnPreviewDragEnd;
        UpscalePreviewImage.PointerCanceled += OnPreviewDragEnd;
    }

    private void OnPreviewWheelZoom(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        var point = e.GetCurrentPoint(sv);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        float factor = delta > 0 ? 1.15f : (1f / 1.15f);
        float newZoom = Math.Clamp(sv.ZoomFactor * factor, sv.MinZoomFactor, sv.MaxZoomFactor);

        double mouseX = point.Position.X;
        double mouseY = point.Position.Y;
        double contentX = (sv.HorizontalOffset + mouseX) / sv.ZoomFactor;
        double contentY = (sv.VerticalOffset + mouseY) / sv.ZoomFactor;
        double newOffsetX = contentX * newZoom - mouseX;
        double newOffsetY = contentY * newZoom - mouseY;

        sv.ChangeView(Math.Max(0, newOffsetX), Math.Max(0, newOffsetY), newZoom, false);
        e.Handled = true;
    }

    private void OnPreviewDragStart(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement el) return;
        var props = e.GetCurrentPoint(el).Properties;
        if (!props.IsLeftButtonPressed) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        var sv = el switch
        {
            var _ when el == GenPreviewImage => GenImageScroller,
            var _ when el == InspectPreviewImage => InspectImageScroller,
            var _ when el == EffectsPreviewImage || el == EffectsOverlayCanvas => EffectsImageScroller,
            var _ when el == UpscalePreviewImage => UpscaleImageScroller,
            _ => InspectImageScroller,
        };
        _imgDragging = true;
        _imgDragScroller = sv;
        _imgDragStart = e.GetCurrentPoint(sv).Position;
        _imgDragStartH = sv.HorizontalOffset;
        _imgDragStartV = sv.VerticalOffset;
        el.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPreviewDragMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_imgDragging || _imgDragScroller == null) return;
        var pos = e.GetCurrentPoint(_imgDragScroller).Position;
        double dx = _imgDragStart.X - pos.X;
        double dy = _imgDragStart.Y - pos.Y;
        _imgDragScroller.ChangeView(_imgDragStartH + dx, _imgDragStartV + dy, null, true);
        e.Handled = true;
    }

    private void OnPreviewDragEnd(object sender, PointerRoutedEventArgs e)
    {
        if (!_imgDragging) return;
        _imgDragging = false;
        _imgDragScroller = null;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private async void OnGenPreviewPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(sender as UIElement);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        if (!ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        if (_currentGenImageBytes == null) return;

        await ApplyDroppedImageMetadata(_currentGenImageBytes, "预览图片", skipSeed: true);
        e.Handled = true;
    }

    private void OnAlign(object sender, RoutedEventArgs e)
    {
        if (_currentMode != AppMode.Inpaint || MaskCanvas.IsInPreviewMode) return;
        if (sender is MenuFlyoutItem item && item.Tag is string tag)
        {
            MaskCanvas.AlignImage(tag);
            TxtStatus.Text = "图像已对齐";
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  外观主题
    // ═══════════════════════════════════════════════════════════

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item && item.Tag is string mode)
        {
            ApplyTheme(mode);
            SyncThemeMenuChecks(mode);
            _settings.Settings.ThemeMode = mode;
            _settings.Save();
            TxtStatus.Text = mode switch
            {
                "Light" => "已切换为亮色模式",
                "Dark" => "已切换为暗色模式",
                _ => "已切换为跟随系统",
            };
        }
    }

    private void ApplyTheme(string mode)
    {
        if (this.Content is FrameworkElement root)
        {
            root.RequestedTheme = mode switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => GetSystemTheme(),
            };
            UpdateTitleBarColors(root.RequestedTheme == ElementTheme.Dark ||
                (root.RequestedTheme == ElementTheme.Default && root.ActualTheme == ElementTheme.Dark));
            UpdateSizeWarningVisuals();

            if (_advParamsWindow?.Content is FrameworkElement advRoot)
                advRoot.RequestedTheme = root.RequestedTheme;
            if (_advParamsWindow != null)
                ApplyWindowChrome(_advParamsWindow,
                    root.RequestedTheme == ElementTheme.Dark ||
                    (root.RequestedTheme == ElementTheme.Default && root.ActualTheme == ElementTheme.Dark),
                    _advTitleBarGrid, _advRootPanel);

            RefreshEffectsPanel();
            RefreshEffectsOverlay();
        }
    }

    private void UpdateTitleBarColors(bool isDark)
    {
        ApplyWindowChrome(this, isDark, null, null);
    }

    private static ElementTheme GetSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 1 ? ElementTheme.Light : ElementTheme.Dark;
        }
        catch { }
        return ElementTheme.Default;
    }

    private async void OnHelpOverview(object sender, RoutedEventArgs e)
    {
        var pages = new (string Title, string Body, string Glyph)[]
        {
            (
                "生图模式",
                "1. 在左侧栏选择模型、输入提示词、调整参数。\n" +
                "2. 点击“生成”或按 Ctrl+Enter 发送请求。\n" +
                "3. 生成结果会自动保存到 output/ 文件夹。\n" +
                "4. 可将结果继续发送到重绘或效果工作区。",
                "\uE768"
            ),
            (
                "重绘模式",
                "1. 导入图像后，使用笔刷、橡皮擦或矩形工具编辑遮罩。\n" +
                "2. 点击“生成”发送重绘请求。\n" +
                "3. 在结果栏中可选择应用、对比、重做或舍弃当前结果。",
                "\uE70F"
            ),
            (
                "绘图工具",
                "B：笔刷\n" +
                "E：橡皮擦\n" +
                "R：矩形选区\n" +
                "滚轮：调整笔刷大小",
                "\uEDFB"
            ),
            (
                "视图操作",
                "鼠标中键拖动：平移视口\n" +
                "Ctrl+滚轮：缩放视口\n" +
                "右键拖动 / Alt+左键拖动：移动底图位置\n" +
                "A+方向键：对齐图像\n" +
                "A+Num0：图像居中",
                "\uE7C3"
            ),
            (
                "抽卡器语法",
                "1. `__name__`：抽取一项\n" +
                "2. `__folder/name__`：抽取子目录条目\n" +
                "3. `__name@3__`：一次抽取 3 项并用逗号拼接\n" +
                "4. `__name@var__`：抽取一项并保存到变量 var\n" +
                "5. `__@var__`：引用当前请求里已经抽到的变量\n" +
                "6. `.txt` 里写成 `内容|1.2` 时，会按当前模型转成对应权重语法\n" +
                "7. 若在“使用设置”中关闭双下划线触发，则可直接使用条目名本体触发抽卡器",
                "\uE74C"
            ),
            (
                "快捷键",
                "Ctrl+Z / Ctrl+Y：撤销 / 重做遮罩\n" +
                "Ctrl+I：反转遮罩\n" +
                "Ctrl+Shift+I：填充空白区域\n" +
                "Ctrl+D：清空遮罩\n" +
                "Ctrl++：扩展遮罩\n" +
                "Ctrl+-：收缩遮罩\n" +
                "Ctrl+Enter：发送生成请求\n" +
                "Ctrl+S：保存\n" +
                "Ctrl+Shift+S：另存为",
                "\uE765"
            ),
        };

        var pageTitleText = new TextBlock
        {
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var pageBodyText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var contentPanel = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(20, 18, 20, 18),
        };
        contentPanel.Children.Add(pageTitleText);
        contentPanel.Children.Add(pageBodyText);

        var contentScrollViewer = new ScrollViewer
        {
            Content = contentPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        void ShowOverviewPage(int index)
        {
            var page = pages[index];
            pageTitleText.Text = page.Title;
            pageBodyText.Text = page.Body;
            contentScrollViewer.ChangeView(null, 0, null, true);
        }

        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            IsBackEnabled = false,
            IsSettingsVisible = false,
            AlwaysShowHeader = false,
            SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Disabled,
            CompactModeThresholdWidth = 0,
            ExpandedModeThresholdWidth = 0,
            OpenPaneLength = 190,
            Width = 760,
            Height = 460,
            Content = contentScrollViewer,
        };

        for (int i = 0; i < pages.Length; i++)
        {
            nav.MenuItems.Add(new NavigationViewItem
            {
                Content = pages[i].Title,
                Tag = i,
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = pages[i].Glyph }
            });
        }

        nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItemContainer?.Tag is int index)
                ShowOverviewPage(index);
        };

        if (nav.MenuItems[0] is NavigationViewItem firstItem)
            nav.SelectedItem = firstItem;
        ShowOverviewPage(0);

        var dialog = new ContentDialog
        {
            Title = "操作概览",
            Content = nav,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 860.0;

        await dialog.ShowAsync();
    }

    private async void OnHelpHighlights(object sender, RoutedEventArgs e)
    {
        var pages = new (string Title, string Body)[]
        {
            (
                "自动化生图",
                "支持常规单次生成，也支持自动化连续出图。\n\n" +
                "你可以先固定基础提示词与参数，再让工具自动循环发送请求，并配合已有的随机风格提示词功能持续产出变体，适合批量试风格、试构图和刷灵感。"
            ),
            (
                "提示词标准化",
                "内置提示词标准化与整理能力，可以统一标签写法、清理格式细节，减少提示词越写越乱的问题。\n\n" +
                "在长期使用中，这能明显降低重复编辑成本，也更方便把不同来源的提示词整理成稳定可复用的格式。"
            ),
            (
                "随机风格提示词",
                "工具可以基于内置标签表，自动抽取随机风格提示词，帮助你快速探索画风方向。\n\n" +
                "当你没有明确风格目标，或者想让同一主题快速扩散出更多审美路线时，这个功能会很高效。"
            ),
            (
                "快捷提示词",
                "支持维护自己的快捷提示词库，把常用片段保存下来，随时插入。\n\n" +
                "无论是人物模板、质量词、镜头语言还是常用负面词，都可以沉淀成个人工作流的一部分。"
            ),
            (
                "提示词抽卡器",
                "支持把常用可变提示词片段整理为 `user/wildcards/` 下的 txt 条目，并在发送前自动按 seed 展开。\n\n" +
                "它可以和现有自动补全联动，也支持变量复用与权重写法，适合做服装、发色、构图元素、风格碎片等可复用抽卡模板。"
            ),
            (
                "权重转换",
                "工具菜单中提供独立的权重转换窗口，可在 Stable Diffusion、NovelAI 1~3 经典括号权重，以及 NovelAI 4+ 数字双冒号权重之间互相转换。\n\n" +
                "窗口采用左右对照结构，方便你先预览输入输出，再决定是否发送回当前工作区，特别适合整理旧 Prompt 或在不同生态之间迁移提示词格式。"
            ),
            (
                "动作交互",
                "用户可以在主 Prompt 和角色 Prompt 中通过右键菜单里的“动作交互”快速插入交互前缀。\n\n" +
                "发起方会在当前选区前加入 source#，被动方加入 target#，对等交互加入 mutual#；如果没有框选内容，则会直接插入到当前光标位置。这是 NAI4 之后版本的标准格式，适合在角色互动、视线关系、动作指向等场景里快速标注不同角色的交互方式。"
            ),
            (
                "大图免费重绘",
                "工具利用局部裁剪思路处理大图重绘，把需要修改的区域尽可能放在合理的生成范围内。\n\n" +
                "这样能在不直接整张放大重绘的前提下，对大尺寸作品进行更精细、更节省成本的修改。"
            ),
            (
                "本地超分",
                "内置本地超分工作区，使用 ONNX 模型（如 Real-ESRGAN Anime 6B）在本地对图片进行放大。\n\n" +
                "支持多种倍率选择，无需联网，不消耗 Anlas。处理完成后可自适应缩放预览，或直接发送到其他工作区继续效果处理。"
            ),
            (
                "效果滤镜",
                "内置效果处理工作区，可以在生成完成后继续做轻量修整。\n\n" +
                "适合做一些统一风格、增强观感或快速试效果的操作，让整个流程不用频繁切到外部软件。"
            ),
            (
                "检视 NAI / SD 图片",
                "支持读取 NovelAI 和 Stable Diffusion 图片里的常见参数与提示词信息。\n\n" +
                "这让你可以直接从已有图片回看生成设置，快速复现、继续修改，或把参数重新送回当前工作流。"
            ),
            (
                "图片反推",
                "当图片没有可用生成元数据时，工具可以调用外部反推模型，从图像内容反向推测标签。\n\n" +
                "结果会直接回填到检视工作区，便于继续转到生图模式中微调，也支持按需附加角色 tag、作品 tag 和阈值控制。"
            ),
        };

        var pageIndexText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
        };
        var pageTitleText = new TextBlock
        {
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var pageBodyText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var contentPanel = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(18, 12, 24, 16),
        };
        contentPanel.Children.Add(pageIndexText);
        contentPanel.Children.Add(pageTitleText);
        contentPanel.Children.Add(pageBodyText);

        var contentScrollViewer = new ScrollViewer
        {
            Content = contentPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        void ShowPage(int index)
        {
            var page = pages[index];
            pageIndexText.Text = $"第 {index + 1} / {pages.Length} 项";
            pageTitleText.Text = page.Title;
            pageBodyText.Text = page.Body;
            contentScrollViewer.ChangeView(null, 0, null, true);
        }

        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            IsBackEnabled = false,
            IsSettingsVisible = false,
            AlwaysShowHeader = false,
            SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Disabled,
            CompactModeThresholdWidth = 0,
            ExpandedModeThresholdWidth = 0,
            OpenPaneLength = 190,
            Width = 760,
            Height = 460,
            Content = contentScrollViewer,
        };

        for (int i = 0; i < pages.Length; i++)
        {
            nav.MenuItems.Add(new NavigationViewItem
            {
                Content = pages[i].Title,
                Tag = i,
            });
        }

        nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItemContainer?.Tag is int index)
                ShowPage(index);
        };

        if (nav.MenuItems[0] is NavigationViewItem firstItem)
            nav.SelectedItem = firstItem;
        ShowPage(0);

        var dialog = new ContentDialog
        {
            Title = "工具亮点",
            Content = nav,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 860.0;

        await dialog.ShowAsync();
    }

    private async void OnHelpUsefulLinks(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel
        {
            Spacing = 10,
            MinWidth = 560,
        };

        panel.Children.Add(new TextBlock
        {
            Text = "点击下方按钮即可在系统默认浏览器中打开对应网站。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });

        var links = new (string Name, string Url, string Description)[]
        {
            ("NovelAI 官方网站", "https://novelai.net/", "NovelAI 官方主页"),
            ("Danbooru", "https://danbooru.donmai.us/", "标签检索与参考图库"),
            ("Google AI Studio", "https://aistudio.google.com/", "Google AI Studio 官方入口"),
            ("GitHub 官网", "https://github.com/", "代码托管与开源项目平台"),
            ("HuggingFace 官网", "https://huggingface.co/", "模型与数据集社区"),
        };

        foreach (var link in links)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textPanel = new StackPanel { Spacing = 2 };
            textPanel.Children.Add(new TextBlock
            {
                Text = link.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = $"{link.Description}\n{link.Url}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });

            var openBtn = new Button
            {
                Content = "打开",
                MinWidth = 76,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = link.Url,
            };
            openBtn.Click += (_, _) => OpenExternalUrl(link.Url);

            Grid.SetColumn(textPanel, 0);
            Grid.SetColumn(openBtn, 1);
            row.Children.Add(textPanel);
            row.Children.Add(openBtn);
            panel.Children.Add(row);
        }

        var dialog = new ContentDialog
        {
            Title = "常用网址",
            Content = panel,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 700.0;

        await dialog.ShowAsync();
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"打开网址失败：{ex.Message}";
        }
    }

    private async void OnAbout(object sender, RoutedEventArgs e)
    {
        var aboutPanel = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(4, 8, 4, 8),
        };

        aboutPanel.Children.Add(new TextBlock
        {
            Text = "NAI Utility Tool",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        });
        aboutPanel.Children.Add(new TextBlock
        {
            Text = "版本 Pre-Release 13",
            Opacity = 0.7
        });
        aboutPanel.Children.Add(new TextBlock
        {
            Text = "本工具完全免费，目前位于开发阶段，若付费购得此工具请举报商家。\n\n本工具在特定参数下发送生成请求可能会消耗Opus订阅账户的 Anlas，此类操作会以黄色高亮显示，Anlas 由 NovelAI 官方计算扣除，与本工具及作者无关。\n\n本工具是基于 Opus 订阅用户设计习惯进行开发的，注意，低档位订阅用户及订阅过期用户的任何生成请求都会扣除账号 Anlas。\n\n本工具仅与 NovelAI 官方 API 对接，不会收集、上传或保存任何用户数据至作者或任何第三方。\n\n注意：程序仍为开发阶段，为方便调试，API Key 会以明文形式储存在 user/config/apiconfig.json 中，若分享给他人使用，请在打包前移除该文件。",
            TextWrapping = TextWrapping.Wrap
        });

        bool aboutIsDark = ((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark;
        var cardBg = new SolidColorBrush(aboutIsDark
            ? Windows.UI.Color.FromArgb(28, 255, 255, 255)
            : Windows.UI.Color.FromArgb(16, 0, 0, 0));
        var cardBorder = new SolidColorBrush(aboutIsDark
            ? Windows.UI.Color.FromArgb(20, 255, 255, 255)
            : Windows.UI.Color.FromArgb(14, 0, 0, 0));
        var accentDim = new SolidColorBrush(aboutIsDark
            ? Windows.UI.Color.FromArgb(40, 96, 165, 250)
            : Windows.UI.Color.FromArgb(35, 59, 130, 246));

        var thanksItems = new (string Name, string Description, string Glyph)[]
        {
            ("Aeka", "NAI Utility Tool 项目作者", "\uE77B"),
            ("Dominik Reh", "tag 自动补全功能作者", "\uE8D2"),
            ("SmilingWolf", "WD14 tagger 反推模型及范式作者", "\uE8BA"),
            ("pixai-labs 团队", "pixai-tagger 反推模型作者", "\uE8BA"),
            ("DeepGHS 团队", "ONNX 模型量化支持", "\uE835"),
            ("xinntao", "超分模型作者", "\uE740"),
            ("EinAeffchen", "Wildcards 原作者", "\uE74C"),
            ("青龙圣者", "Wildcards 维护与贡献者", "\uE74C"),
            ("jiarandiana0307", "图片混淆算法源码提供者", "\uF404"),
        };

        var thanksHeader = new TextBlock
        {
            Text = "以下项目、作者与团队为本工具的相关能力提供了直接或间接支持：",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            Margin = new Thickness(4, 8, 4, 4),
        };

        const int rowsPerCol = 2;
        int colCount = (thanksItems.Length + rowsPerCol - 1) / rowsPerCol;
        var columnsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Padding = new Thickness(4, 0, 24, 0),
        };

        for (int col = 0; col < colCount; col++)
        {
            var column = new StackPanel { Spacing = 16 };

            for (int row = 0; row < rowsPerCol; row++)
            {
                int idx = col * rowsPerCol + row;
                if (idx >= thanksItems.Length) break;
                var item = thanksItems[idx];

                var iconBorder = new Border
                {
                    Width = 44, Height = 44,
                    CornerRadius = new CornerRadius(22),
                    Background = accentDim,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                iconBorder.Child = new FontIcon
                {
                    FontFamily = SymbolFontFamily, Glyph = item.Glyph,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var textCol = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                textCol.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 15,
                });
                textCol.Children.Add(new TextBlock
                {
                    Text = item.Description,
                    Opacity = 0.65,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 310,
                });

                var cardInner = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                };
                cardInner.Children.Add(iconBorder);
                cardInner.Children.Add(textCol);

                var card = new Border
                {
                    Background = cardBg,
                    BorderBrush = cardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(18, 16, 20, 16),
                    MinWidth = 340,
                    MinHeight = 108,
                };
                card.Child = cardInner;
                column.Children.Add(card);
            }

            columnsPanel.Children.Add(column);
        }

        var thanksCardScroller = new ScrollViewer
        {
            Content = columnsPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };

        var thanksPanel = new StackPanel { Spacing = 8 };
        thanksPanel.Children.Add(thanksHeader);
        thanksPanel.Children.Add(thanksCardScroller);

        var aboutScrollViewer = new ScrollViewer
        {
            Content = aboutPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 380,
        };
        var thanksScrollViewer = new ScrollViewer
        {
            Content = thanksPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 380,
        };

        var contentHost = new ContentControl
        {
            Height = 380,
            Content = aboutScrollViewer,
        };

        var selectorBar = new SelectorBar
        {
            Margin = new Thickness(0, 0, 0, 8),
        };
        selectorBar.Items.Add(new SelectorBarItem { Text = "工具说明" });
        selectorBar.Items.Add(new SelectorBarItem { Text = "特别鸣谢" });
        selectorBar.SelectionChanged += (_, _) =>
        {
            int selectedIndex = selectorBar.Items.IndexOf(selectorBar.SelectedItem);
            contentHost.Content = selectedIndex == 1 ? thanksScrollViewer : aboutScrollViewer;
        };
        selectorBar.SelectedItem = selectorBar.Items[0];

        var panel = new StackPanel
        {
            Spacing = 8,
            Width = 680,
        };
        panel.Children.Add(selectorBar);
        panel.Children.Add(contentHost);

        var dialog = new ContentDialog
        {
            Title = "关于",
            Content = panel,
            PrimaryButtonText = "确定",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1040.0;
        await dialog.ShowAsync();
    }

    private void SyncThemeMenuChecks(string mode)
    {
        MenuThemeSystem.IsChecked = mode == "System";
        MenuThemeLight.IsChecked = mode == "Light";
        MenuThemeDark.IsChecked = mode == "Dark";
    }

    // ═══════════════════════════════════════════════════════════
    //  工具栏（重绘模式）
    // ═══════════════════════════════════════════════════════════

    private void SetToolSelection(StrokeTool tool)
    {
        MaskCanvas.Brush.CurrentTool = tool;
        BtnBrush.IsChecked = tool == StrokeTool.Brush;
        BtnEraser.IsChecked = tool == StrokeTool.Eraser;
        BtnRect.IsChecked = tool == StrokeTool.Rectangle;
    }

    private void OnToolBrush(object sender, RoutedEventArgs e) => SetToolSelection(StrokeTool.Brush);
    private void OnToolEraser(object sender, RoutedEventArgs e) => SetToolSelection(StrokeTool.Eraser);
    private void OnToolRect(object sender, RoutedEventArgs e) => SetToolSelection(StrokeTool.Rectangle);

    private void OnBrushSizeChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (MaskCanvas == null) return;
        MaskCanvas.Brush.BrushSize = (float)e.NewValue;
        if (TxtBrushSize != null) TxtBrushSize.Text = $"{(int)e.NewValue}";
    }

    private void OnTogglePreviewMask(object sender, RoutedEventArgs e)
    {
        MaskCanvas.PreviewMaskOnly = ChkPreviewMask.IsChecked == true;
    }

    // ═══════════════════════════════════════════════════════════
    //  键盘快捷键
    // ═══════════════════════════════════════════════════════════

    private void OnGlobalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control);
            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                OnGenerate(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        if (FocusManager.GetFocusedElement(this.Content.XamlRoot) is TextBox or PasswordBox)
            return;

        if (_currentMode == AppMode.Inpaint)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control);
            bool ctrlDown = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (ctrlDown && e.Key == (Windows.System.VirtualKey)187)
            {
                OnExpandMask(this, new RoutedEventArgs());
                e.Handled = true; return;
            }
            if (ctrlDown && e.Key == (Windows.System.VirtualKey)189)
            {
                OnShrinkMask(this, new RoutedEventArgs());
                e.Handled = true; return;
            }

            var aKeyState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.A);
            bool aKeyDown = aKeyState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (aKeyDown && !MaskCanvas.IsInPreviewMode && TryAlignByArrowKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Windows.System.VirtualKey.B:
                    SetToolSelection(StrokeTool.Brush); e.Handled = true; break;
                case Windows.System.VirtualKey.E:
                    SetToolSelection(StrokeTool.Eraser); e.Handled = true; break;
                case Windows.System.VirtualKey.R:
                    SetToolSelection(StrokeTool.Rectangle); e.Handled = true; break;
            }
        }
    }

    private bool TryAlignByArrowKey(Windows.System.VirtualKey key)
    {
        bool IsDown(Windows.System.VirtualKey k) =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (key == Windows.System.VirtualKey.NumberPad0)
        {
            MaskCanvas.AlignImage("CC");
            TxtStatus.Text = "图像已对齐: 居中";
            return true;
        }

        bool up = key == Windows.System.VirtualKey.Up || IsDown(Windows.System.VirtualKey.Up);
        bool down = key == Windows.System.VirtualKey.Down || IsDown(Windows.System.VirtualKey.Down);
        bool left = key == Windows.System.VirtualKey.Left || IsDown(Windows.System.VirtualKey.Left);
        bool right = key == Windows.System.VirtualKey.Right || IsDown(Windows.System.VirtualKey.Right);

        if (!up && !down && !left && !right) return false;
        if (up && down) down = false;
        if (left && right) right = false;

        char row = up ? 'T' : down ? 'B' : 'C';
        char col = left ? 'L' : right ? 'R' : 'C';

        var tag = $"{row}{col}";
        MaskCanvas.AlignImage(tag);
        TxtStatus.Text = "图像已对齐";
        return true;
    }

    private void OnPromptPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control);
            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                OnGenerate(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        if (!AutoCompletePopup.IsOpen) return;
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Down:
                    if (AutoCompleteList.Items.Count > 0)
                    {
                        int next = AutoCompleteList.SelectedIndex + 1;
                        if (next >= AutoCompleteList.Items.Count) next = 0;
                        AutoCompleteList.SelectedIndex = next;
                        AutoCompleteList.ScrollIntoView(AutoCompleteList.SelectedItem);
                    }
                    e.Handled = true;
                break;
                case Windows.System.VirtualKey.Up:
                    if (AutoCompleteList.Items.Count > 0)
                    {
                        int prev = AutoCompleteList.SelectedIndex - 1;
                        if (prev < 0) prev = AutoCompleteList.Items.Count - 1;
                        AutoCompleteList.SelectedIndex = prev;
                        AutoCompleteList.ScrollIntoView(AutoCompleteList.SelectedItem);
                    }
                    e.Handled = true;
                break;
                case Windows.System.VirtualKey.Tab:
                    if (AutoCompleteList.SelectedItem is AutoCompleteItem tabSel)
                        InsertAutoCompleteTag(tabSel.InsertText);
                else if (AutoCompleteList.Items.Count > 0 &&
                         AutoCompleteList.Items[0] is AutoCompleteItem tabFirst)
                    InsertAutoCompleteTag(tabFirst.InsertText);
                    e.Handled = true;
                break;
                case Windows.System.VirtualKey.Enter:
                    if (AutoCompleteList.SelectedIndex >= 0 &&
                        AutoCompleteList.SelectedItem is AutoCompleteItem enterSel)
                        InsertAutoCompleteTag(enterSel.InsertText);
                else if (AutoCompleteList.Items.Count > 0 &&
                         AutoCompleteList.Items[0] is AutoCompleteItem enterFirst)
                    InsertAutoCompleteTag(enterFirst.InsertText);
                else
                    CloseAutoComplete();
                e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Escape:
                    CloseAutoComplete();
                    e.Handled = true;
                break;
            }
        }

    private void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control);
            if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                OnGenerate(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  自动补全
    // ═══════════════════════════════════════════════════════════

    private async Task LoadTagServiceAsync()
    {
        var dir = Path.Combine(AppRootDir, "assets", "tagsheet");
        try { await _tagService.LoadFromDirectoryAsync(dir); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Tags] Load failed: {ex.Message}"); }
    }

    private void TriggerAutoComplete(TextBox textBox)
    {
        if (!_settings.Settings.AutoComplete) return;
        if (!_tagService.IsLoaded && !_wildcardService.IsLoaded) return;

        _acTargetTextBox = textBox;
        string token = ExtractCurrentToken(textBox);
        if (token.Length < 1)
        {
            CloseAutoComplete();
            return;
        }

        _acVersion++;
        int version = _acVersion;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (version != _acVersion) return;
            PerformAutoCompleteSearch(textBox, token);
        });
    }

    private static int FindTokenStart(string text, int caret)
    {
        int start = caret - 1;
        while (start > 0)
        {
            if (text[start - 1] == ',') break;
            if (start >= 2 && text[start - 1] == ':' && text[start - 2] == ':') break;
            start--;
        }
        return start;
    }

    private static string ExtractCurrentToken(TextBox textBox)
    {
        string text = textBox.Text;
        int caret = textBox.SelectionStart;
        if (string.IsNullOrEmpty(text) || caret <= 0) return "";

        int start = FindTokenStart(text, caret);
        string token = text.Substring(start, caret - start).TrimStart();
        return token;
    }

    private static (int Start, int End) GetCurrentTokenRange(TextBox textBox)
    {
        string text = textBox.Text;
        int caret = textBox.SelectionStart;
        if (string.IsNullOrEmpty(text) || caret <= 0) return (0, 0);

        int start = FindTokenStart(text, caret);
        while (start < text.Length && text[start] == ' ') start++;

        int end = caret;
        while (end < text.Length && text[end] != ',' && !(end + 1 < text.Length && text[end] == ':' && text[end + 1] == ':'))
            end++;

        return (start, end);
    }

    private string ExtractWildcardSearchPrefix(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "";

        string normalized = token.Trim();
        if (normalized.StartsWith("__", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.EndsWith("__", StringComparison.Ordinal))
            normalized = normalized[..^2];

        int atIndex = normalized.LastIndexOf('@');
        if (atIndex > 0)
            normalized = normalized[..atIndex];

        if (normalized.StartsWith("@", StringComparison.Ordinal))
            return "";

        return normalized.Trim();
    }

    private string BuildWildcardInsertText(string wildcardName) =>
        _settings.Settings.WildcardsRequireExplicitSyntax
            ? $"__{wildcardName}__"
            : wildcardName;

    private void PerformAutoCompleteSearch(TextBox textBox, string token)
    {
        int? catFilter = (textBox == TxtStylePrompt) ? 1 : null;
        var results = _tagService.IsLoaded
            ? _tagService.Search(token, 15, catFilter)
            : new List<TagMatch>();

        if (textBox == TxtPrompt && _isSplitPrompt && _isPositiveTab)
            results = results.Where(r => r.Entry.Category != 1).Take(15).ToList();

        string wildcardPrefix = ExtractWildcardSearchPrefix(token);
        var wildcardResults = _settings.Settings.WildcardsEnabled && _wildcardService.IsLoaded && wildcardPrefix.Length > 0
            ? _wildcardService.Search(wildcardPrefix, 8)
            : new List<WildcardSearchResult>();

        if (results.Count == 0 && wildcardResults.Count == 0)
        {
            CloseAutoComplete();
            return;
        }

        var items = new List<AutoCompleteItem>(results.Count + wildcardResults.Count);
        foreach (var r in results)
        {
            items.Add(new AutoCompleteItem
            {
                TagName = r.Entry.Tag.Replace('_', ' '),
                InsertText = r.Entry.Tag.Replace('_', ' '),
                Category = r.Entry.Category,
                CountText = TagCompleteService.FormatCount(r.Entry.Count),
                AliasText = r.MatchedAlias?.Replace('_', ' ') ?? "",
                AliasVisibility = r.MatchedAlias != null
                    ? Visibility.Visible : Visibility.Collapsed,
                CategoryBrush = GetCategoryBrush(r.Entry.Category),
            });
        }

        foreach (var r in wildcardResults)
        {
            items.Add(new AutoCompleteItem
            {
                TagName = r.Entry.Name,
                InsertText = BuildWildcardInsertText(r.Entry.Name),
                Category = -1,
                CountText = "抽卡器",
                AliasText = $"{r.Entry.OptionCount} 条 · {r.Entry.RelativePath.Replace('\\', '/')}",
                AliasVisibility = Visibility.Visible,
                CategoryBrush = GetCategoryBrush(-1),
            });
        }

        bool wasOpen = AutoCompletePopup.IsOpen;
        AutoCompleteList.ItemsSource = items;
        AutoCompleteList.SelectedIndex = -1;
        if (!wasOpen)
        {
            PositionAutoCompletePopup(textBox);
            AutoCompletePopup.IsOpen = true;
        }
    }

    private void PositionAutoCompletePopup(TextBox textBox)
    {
        try
        {
            int caret = textBox.SelectionStart;
            if (caret <= 0) caret = 0;
            var rect = textBox.GetRectFromCharacterIndex(caret > 0 ? caret - 1 : 0, true);

            var popupParent = AutoCompletePopup.Parent as UIElement ?? this.Content as UIElement;
            var transform = textBox.TransformToVisual(popupParent);
            double lineBottom = rect.Y + rect.Height;
            var point = transform.TransformPoint(new Point(
                textBox.Padding.Left,
                lineBottom + 2));
            AutoCompletePopup.HorizontalOffset = point.X;
            AutoCompletePopup.VerticalOffset = point.Y;
        }
        catch { }
    }

    private void InsertAutoCompleteTag(string tag)
    {
        if (_acTargetTextBox == null) return;
        var textBox = _acTargetTextBox;

        _acInserting = true;
        try
        {
            var (start, end) = GetCurrentTokenRange(textBox);
            string text = textBox.Text;

            string suffix = ", ";
            if (end < text.Length && text[end] == ',') suffix = "";

            string newText = text.Substring(0, start) + tag + suffix + text.Substring(end);
            textBox.Text = newText;
            textBox.SelectionStart = start + tag.Length + suffix.Length;
        }
        finally
        {
            _acInserting = false;
        }

        CloseAutoComplete();
        textBox.Focus(FocusState.Programmatic);
    }

    private void CloseAutoComplete()
    {
        if (!AutoCompletePopup.IsOpen) return;
        AutoCompletePopup.IsOpen = false;
        AutoCompleteList.ItemsSource = null;
    }

    private void OnAutoCompleteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AutoCompleteItem item)
            InsertAutoCompleteTag(item.InsertText);
    }

    private static SolidColorBrush GetCategoryBrush(int category) => category switch
    {
        -1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 160, 90, 220)),
        0 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 180, 120)),
        1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 60, 60)),
        3 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 120, 220)),
        4 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 160, 40)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 140, 140, 140)),
    };

    // ═══════════════════════════════════════════════════════════
    //  设置对话框
    // ═══════════════════════════════════════════════════════════

    private async void OnUsageSettings(object sender, RoutedEventArgs e)
    {
        var chkWeightHighlight = new CheckBox
        {
            Content = "权重高亮",
            IsChecked = _settings.Settings.WeightHighlight,
        };
        var chkAutoComplete = new CheckBox
        {
            Content = "自动补全",
            IsChecked = _settings.Settings.AutoComplete,
        };
        var chkRememberPromptAndParameters = new CheckBox
        {
            Content = "记住上次的提示词和参数",
            IsChecked = _settings.Settings.RememberPromptAndParameters,
        };
        var chkWildcardsEnabled = new CheckBox
        {
            Content = "启用抽卡器（正向 / 负向 / 角色提示词统一生效）",
            IsChecked = _settings.Settings.WildcardsEnabled,
        };
        var chkWildcardExplicitSyntax = new CheckBox
        {
            Content = "抽卡器需要使用 __name__ 语法触发",
            IsChecked = _settings.Settings.WildcardsRequireExplicitSyntax,
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(chkWeightHighlight);
        panel.Children.Add(chkAutoComplete);
        panel.Children.Add(chkRememberPromptAndParameters);
        panel.Children.Add(chkWildcardsEnabled);
        panel.Children.Add(chkWildcardExplicitSyntax);

        var dialog = new ContentDialog
        {
            Title = "使用设置",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.Settings.WeightHighlight = chkWeightHighlight.IsChecked == true;
            _settings.Settings.AutoComplete = chkAutoComplete.IsChecked == true;
            _settings.Settings.RememberPromptAndParameters = chkRememberPromptAndParameters.IsChecked == true;
            _settings.Settings.WildcardsEnabled = chkWildcardsEnabled.IsChecked == true;
            _settings.Settings.WildcardsRequireExplicitSyntax = chkWildcardExplicitSyntax.IsChecked == true;
            if (!_settings.Settings.AutoComplete) CloseAutoComplete();
            if (_settings.Settings.RememberPromptAndParameters)
            {
                SaveCurrentPromptToBuffer();
                SyncUIToParams();
                SyncRememberedPromptAndParameterState();
            }
            else
            {
                ClearRememberedPromptState();
            }
            _settings.Save();
            UpdatePromptHighlights();
            TxtStatus.Text = "使用设置已保存";
        }
    }

    private void AutoDetectTaggerModel()
    {
        var taggerSettings = _settings.Settings.ReverseTagger;
        if (!string.IsNullOrWhiteSpace(taggerSettings.ModelPath) && Directory.Exists(taggerSettings.ModelPath))
            return;

        var taggerDir = Path.Combine(ModelsDir, "tagger");
        if (!Directory.Exists(taggerDir)) return;

        string? found = FindValidTaggerDir(taggerDir);
        if (found == null) return;

        taggerSettings.ModelPath = found;
        _settings.Save();
        System.Diagnostics.Debug.WriteLine($"[ReverseTagger] Auto-detected tagger model: {found}");
    }

    private static string? FindValidTaggerDir(string searchRoot)
    {
        if (IsValidTaggerDirectory(searchRoot))
            return searchRoot;

        try
        {
            foreach (var subDir in Directory.GetDirectories(searchRoot))
            {
                if (IsValidTaggerDirectory(subDir))
                    return subDir;
            }
        }
        catch { }
        return null;
    }

    private static bool IsValidTaggerDirectory(string dir)
    {
        bool hasOnnx = Directory.GetFiles(dir, "*.onnx", SearchOption.TopDirectoryOnly).Length > 0;
        bool hasCsv = File.Exists(Path.Combine(dir, "selected_tags.csv"));
        return hasOnnx && hasCsv;
    }

    private async void OnReverseTaggerSettings(object sender, RoutedEventArgs e)
        => await ShowReverseTaggerSettingsDialogAsync();

    private async Task ShowReverseTaggerSettingsDialogAsync()
    {
        var settings = _settings.Settings.ReverseTagger;

        var pathBox = new TextBox
        {
            Text = settings.ModelPath ?? "",
            PlaceholderText = "选择包含 .onnx 与 selected_tags.csv 的模型目录",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 300,
        };

        var browseButton = new Button
        {
            Content = "选择文件夹",
        };

        var addCharacterCheck = new CheckBox
        {
            Content = "添加角色 tag",
            IsChecked = settings.AddCharacterTags,
        };
        var addCopyrightCheck = new CheckBox
        {
            Content = "添加作品 tag",
            IsChecked = settings.AddCopyrightTags,
        };
        var replaceUnderscoreCheck = new CheckBox
        {
            Content = "空格代替下划线",
            IsChecked = settings.ReplaceUnderscoresWithSpaces,
        };
        var unloadAfterInferenceCheck = new CheckBox
        {
            Content = "反推结束后从内存/显存卸载模型",
            IsChecked = settings.UnloadModelAfterInference,
        };

        var generalSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.01,
            SmallChange = 0.01,
            LargeChange = 0.05,
            Value = Math.Clamp(settings.GeneralThreshold, 0, 1),
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var generalValue = new TextBlock
        {
            MinWidth = 52,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Text = generalSlider.Value.ToString("0.00"),
        };
        generalSlider.ValueChanged += (_, args) => generalValue.Text = args.NewValue.ToString("0.00");

        var characterSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.01,
            SmallChange = 0.01,
            LargeChange = 0.05,
            Value = Math.Clamp(settings.CharacterThreshold, 0, 1),
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var characterValue = new TextBlock
        {
            MinWidth = 52,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Text = characterSlider.Value.ToString("0.00"),
        };
        characterSlider.ValueChanged += (_, args) => characterValue.Text = args.NewValue.ToString("0.00");

        browseButton.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
                pathBox.Text = folder.Path;
        };

        var pathPanel = new Grid { ColumnSpacing = 8 };
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(browseButton, 1);
        pathPanel.Children.Add(pathBox);
        pathPanel.Children.Add(browseButton);

        StackPanel BuildSliderRow(Slider slider, TextBlock valueText)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(slider);
            row.Children.Add(valueText);
            return row;
        }

        var panel = new StackPanel
        {
            Spacing = 12,
            MinWidth = 480,
            Padding = new Thickness(0, 0, 4, 0),
        };
        panel.Children.Add(new TextBlock { Text = "反推模型路径" });
        panel.Children.Add(pathPanel);
        panel.Children.Add(new TextBlock
        {
            Text = "请手动下载pixai-tagger-v0.9-onnx后将目录指向模型文件夹，或将模型放入 models/tagger 目录下自动检测。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        });
        var tagOptionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
        };
        tagOptionRow.Children.Add(addCharacterCheck);
        tagOptionRow.Children.Add(addCopyrightCheck);
        panel.Children.Add(tagOptionRow);
        panel.Children.Add(replaceUnderscoreCheck);
        panel.Children.Add(new TextBlock { Text = "通用置信度阈值" });
        panel.Children.Add(BuildSliderRow(generalSlider, generalValue));
        panel.Children.Add(new TextBlock { Text = "角色置信度阈值" });
        panel.Children.Add(BuildSliderRow(characterSlider, characterValue));
        panel.Children.Add(unloadAfterInferenceCheck);
        panel.Children.Add(new TextBlock
        {
            Text = "启用后，每次反推完成后都会释放模型，以回收内存及显存空间。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Margin = new Thickness(28, -8, 0, 0),
        });

        var dialog = new ContentDialog
        {
            Title = "反推模型设置",
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 520,
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            string modelPath = pathBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(modelPath) && !Directory.Exists(modelPath))
            {
                args.Cancel = true;
                TxtStatus.Text = "反推模型路径不存在，请重新选择";
                return;
            }

            settings.ModelPath = modelPath;
            settings.AddCharacterTags = addCharacterCheck.IsChecked == true;
            settings.AddCopyrightTags = addCopyrightCheck.IsChecked == true;
            settings.ReplaceUnderscoresWithSpaces = replaceUnderscoreCheck.IsChecked == true;
            settings.GeneralThreshold = Math.Round(generalSlider.Value, 2);
            settings.CharacterThreshold = Math.Round(characterSlider.Value, 2);
            settings.UnloadModelAfterInference = unloadAfterInferenceCheck.IsChecked == true;
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.Save();
            TxtStatus.Text = "反推模型设置已保存";
        }
    }

    private async void OnDevSettings(object sender, RoutedEventArgs e)
    {
        var chkLog = new CheckBox
        {
            Content = "日志记录",
            IsChecked = _settings.Settings.DevLogEnabled,
        };
        var hintText = new TextBlock
        {
            Text = "启用后，程序运行日志会保存到程序目录下的 logs 文件夹中。\n会记录每次生图/重绘请求的详细参数、响应状态和返回图像元数据，便于实测接口响应。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };
        hintText.SetValue(TextBlock.ForegroundProperty,
            new SolidColorBrush(((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 160, 160, 160)
                : Windows.UI.Color.FromArgb(255, 100, 100, 100)));

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(chkLog);
        panel.Children.Add(hintText);

        var dialog = new ContentDialog
        {
            Title = "开发者选项",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.Settings.DevLogEnabled = chkLog.IsChecked == true;
            _settings.Save();
            TxtStatus.Text = _settings.Settings.DevLogEnabled
                ? "已启用日志记录" : "已关闭日志记录";
        }
    }

    private async void OnNetworkSettings(object sender, RoutedEventArgs e)
    {
        var tokenBox = new PasswordBox
        {
            PlaceholderText = "Bearer Token",
            Password = _settings.Settings.ApiToken ?? "", Width = 360,
        };

        var maxModeCheck = new CheckBox { Content = "启用 Max 模式", IsChecked = _settings.Settings.MaxMode };
        var maxModeHint = new TextBlock
        {
            Text = "允许可能会扣除 Opus Tier 订阅账户的 Anlas 的操作。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, -8, 0, 0),
        };

        var proxyCheck = new CheckBox { Content = "自定义代理", IsChecked = _settings.Settings.UseProxy };
        var proxyHint = new TextBlock
        {
            Text = "该程序默认跟随系统http代理，一般无需特别设置。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, -8, 0, 0),
        };
        var proxyPortBox = new TextBox { PlaceholderText = "代理端口", Text = _settings.Settings.ProxyPort, Width = 120 };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "API Token:" });
        panel.Children.Add(tokenBox);
        panel.Children.Add(maxModeCheck);
        panel.Children.Add(maxModeHint);
        panel.Children.Add(proxyCheck);
        panel.Children.Add(proxyHint);
        var pp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pp.Children.Add(new TextBlock { Text = "端口:", VerticalAlignment = VerticalAlignment.Center });
        pp.Children.Add(proxyPortBox);
        panel.Children.Add(pp);

        var dialog = new ContentDialog
        {
            Title = "网络/API设置", Content = panel,
            PrimaryButtonText = "保存", SecondaryButtonText = "测试连接",
            CloseButtonText = "取消", XamlRoot = this.Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            bool maxModeChanged = _settings.Settings.MaxMode != (maxModeCheck.IsChecked == true);
            _settings.Settings.ApiToken = tokenBox.Password;
            _settings.Settings.MaxMode = maxModeCheck.IsChecked == true;
            _settings.Settings.UseProxy = proxyCheck.IsChecked == true;
            _settings.Settings.ProxyPort = proxyPortBox.Text;
            _settings.Save();

            if (maxModeChanged)
            {
                RefreshSizeComboBox();
                RefreshPromptModeUiForAccountModeChange();
            }
            UpdateBtnGenerateForApiKey();
            _ = RefreshAnlasInfoAsync();

            if (result == ContentDialogResult.Secondary)
            {
                TxtStatus.Text = "正在测试连接...";
                var (success, msg) = await _naiService.TestConnectionAsync(tokenBox.Password);
                TxtStatus.Text = msg;
                if (success)
                    _ = RefreshAnlasInfoAsync(forceRefresh: true);
            }
            else TxtStatus.Text = "网络/API设置已保存";
        }
    }

    private void RefreshSizeComboBox()
    {
        int prevIdx = CboSize.SelectedIndex;
        CboSize.Items.Clear();
        foreach (var p in MaskCanvasControl.CanvasPresets)
            CboSize.Items.Add(CreateTextComboBoxItem(p.Label));
        CboSize.SelectedIndex = prevIdx >= 0 && prevIdx < CboSize.Items.Count ? prevIdx : 0;

        if (IsAdvancedWindowOpen)
        {
            _advCboSize.Items.Clear();
            foreach (var p in MaskCanvasControl.CanvasPresets)
                _advCboSize.Items.Add(CreateTextComboBoxItem(p.Label));
            _advCboSize.SelectedIndex = CboSize.SelectedIndex;
            UpdateAdvSizeControlMode();
            UpdateAdvSizeWarningVisuals();
            _advNbSteps.Maximum = _settings.Settings.MaxMode ? 50 : 28;
            UpdateAdvStepsWarning();
        }

        UpdateSizeControlMode();
        UpdateSizeWarningVisuals();
        UpdateGenerateButtonWarning();
    }

    private void RefreshPromptModeUiForAccountModeChange()
    {
        UpdateModelDependentUI();
        RecheckVibeTransferCacheState();
        RefreshVibeTransferPanel();
        RefreshPreciseReferencePanel();
        UpdateReferenceButtonAndPanelState();
        UpdateGenerateButtonWarning();
        UpdateDynamicMenuStates();
    }

    // ═══════════════════════════════════════════════════════════
    //  生成（根据模式分流）
    // ═══════════════════════════════════════════════════════════

    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (_autoGenRunning) { StopAutoGeneration(); return; }
        if (_continuousGenRunning) { StopContinuousGeneration(); return; }

        if (string.IsNullOrEmpty(_settings.Settings.ApiToken))
        {
            OnNetworkSettings(sender, e);
            return;
        }

        SyncUIToParams();
        if (IsAdvancedWindowOpen)
            SaveAdvancedPanelToSettings();

        if (GetSizeWarningLevel() == SizeWarningLevel.Red)
        {
            long limit = _settings.Settings.MaxMode ? 2048L : 1024L;
            TxtStatus.Text = $"尺寸总像素超过 {limit}²，已禁止生成。请减小宽度或高度。";
            return;
        }

        _settings.Save();

        await ExecuteCurrentGenerationAsync();
    }

    private Task<bool> ExecuteCurrentGenerationAsync(bool forceRandomSeed = false) =>
        _currentMode == AppMode.ImageGeneration
            ? DoImageGenerationAsync(forceRandomSeed)
            : DoInpaintGenerateAsync(forceRandomSeed);

    // ═══════════════════════════════════════════════════════════
    //  生图模式生成
    // ═══════════════════════════════════════════════════════════

    private async Task<bool> DoImageGenerationAsync(bool forceRandomSeed = false)
    {
        var autoContext = _autoGenRunning ? _automationRunContext : null;
        var (w, h) = autoContext?.CurrentSizeOverride ?? GetSelectedSize();
        bool keepGenerateButtonInteractive = _autoGenRunning || _continuousGenRunning;
        if (!keepGenerateButtonInteractive) BtnGenerate.IsEnabled = false;
        _generateRequestRunning = true;
        UpdateBtnGenerateForApiKey();
        TxtStatus.Text = "正在生成...";
        var p = _settings.Settings.GenParameters;
        int origSeed = p.Seed;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;

            int actualSeed = (!forceRandomSeed && p.Seed > 0) ? p.Seed : Random.Shared.Next(1, int.MaxValue);
            p.Seed = actualSeed;
            var wildcardContext = CreateWildcardContext(actualSeed, p.Model);
            SaveCurrentPromptToBuffer();
            string automationPrompt = autoContext?.CurrentPromptOverride ?? _genPositivePrompt;
            string positiveRaw = MergeStyleAndMain(_genStylePrompt, automationPrompt);
            string negativeRaw = _genNegativePrompt;
            if (!TryValidateReferenceRequest(out string referenceError))
            {
                TxtStatus.Text = referenceError;
                return false;
            }
            if (_autoGenRunning && _activeAutomationSettings?.Randomization.RandomizeStyleTags == true)
            {
                string? stylePrefix = BuildRandomStylePrefixForRequest();
                if (string.IsNullOrWhiteSpace(stylePrefix))
                    return false;
                positiveRaw = MergeStyleAndMain(_genStylePrompt, MergeStyleAndMain(stylePrefix, automationPrompt));
            }
            string prompt = ExpandPromptFeatures(positiveRaw, wildcardContext);
            string negPrompt = ExpandPromptFeatures(negativeRaw, wildcardContext, isNegativeText: true);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                TxtStatus.Text = "请输入提示词";
                return false;
            }

            DebugLog($"[Generate] Start | {w}x{h} | Model={p.Model} | Seed={actualSeed}");

            if (_genCharacters.Count > 0) ApplyCharCountPrefixStrip();
            var chars = (_genCharacters.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
            var vibes = autoContext?.CurrentVibeOverride ?? GetVibeTransferData();
            var preciseReferences = GetPreciseReferenceData();
            var (imageBytes, error) = await _naiService.GenerateAsync(
                w, h, prompt, negPrompt,
                chars, vibes, preciseReferences, ct);
            _lastUsedSeed = actualSeed;

            if (error != null) { DebugLog($"[Generate] API error: {error}"); TxtStatus.Text = error; return false; }
            if (imageBytes == null) { DebugLog("[Generate] API returned no image"); TxtStatus.Text = "API 返回为空"; return false; }

            byte[] finalBytes = imageBytes;
            string? originalSavedPath = await SaveToOutputAsync(imageBytes);
            string? finalSavedPath = originalSavedPath;
            string postSummary = "";

            if (_autoGenRunning && _activeAutomationSettings?.Effects.Enabled == true)
            {
                var postResult = await RunAutomationEffectsProcessAsync(imageBytes, _activeAutomationSettings.Effects, ct);
                finalBytes = postResult.Bytes;
                finalSavedPath = await SaveToOutputAsync(finalBytes, "auto");
                postSummary = postResult.Summary;
            }

            _currentGenImageBytes = finalBytes;
            _currentGenImagePath = finalSavedPath;

            await ShowGenPreviewAsync(finalBytes);

            if (finalSavedPath != null)
                AddHistoryItem(finalSavedPath);

            if (!_autoGenRunning)
            {
                GenResultBarTranslate.X = 0;
                GenResultBarTranslate.Y = 0;
                GenResultBar.Visibility = Visibility.Visible;
            }
            _ = RefreshAnlasInfoAsync(forceRefresh: true);
            UpdateDynamicMenuStates();
            DebugLog($"[Generate] Completed | Seed={actualSeed} | Saved={finalSavedPath}");
            TxtStatus.Text = string.IsNullOrWhiteSpace(postSummary)
                ? $"生成完成！已保存到: {finalSavedPath}"
                : $"生成完成！已执行 {postSummary}，保存到: {finalSavedPath}";
            return true;
        }
        catch (OperationCanceledException) { DebugLog("[Generate] Cancelled"); TxtStatus.Text = "生成已取消"; return false; }
        catch (Exception ex) { DebugLog($"[Generate] Failed: {ex}"); TxtStatus.Text = $"生成失败: {ex.Message}"; return false; }
        finally
        {
            _generateRequestRunning = false;
            UpdateBtnGenerateForApiKey();
            p.Seed = origSeed;
        }
    }

    private async Task ShowGenPreviewAsync(byte[] imageBytes)
    {
        var bitmapImage = new BitmapImage();
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(ms);
        writer.WriteBytes(imageBytes);
        await writer.StoreAsync();
        writer.DetachStream();
        ms.Seek(0);
        await bitmapImage.SetSourceAsync(ms);
        GenPreviewImage.Source = bitmapImage;
        GenPlaceholder.Visibility = Visibility.Collapsed;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => FitGenPreviewToScreen());
    }

    private void FitGenPreviewToScreen()
    {
        if (GenPreviewImage.Source is not BitmapImage bmp) return;
        double imgW = bmp.PixelWidth;
        double imgH = bmp.PixelHeight;
        if (imgW <= 0 || imgH <= 0) return;

        double viewW = GenImageScroller.ViewportWidth;
        double viewH = GenImageScroller.ViewportHeight;
        if (viewW <= 0 || viewH <= 0) return;

        float zoom = (float)Math.Min(viewW / imgW, viewH / imgH);
        zoom = Math.Min(zoom, 1.0f);
        GenImageScroller.ChangeView(0, 0, zoom);
    }

    private static async Task<string?> SaveToOutputAsync(byte[] imageBytes, string prefix = "gen")
    {
        var dateDir = Path.Combine(OutputBaseDir, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateDir);
        var fileName = $"{prefix}_{DateTime.Now:HHmmss_fff}.png";
        var filePath = Path.Combine(dateDir, fileName);
        await File.WriteAllBytesAsync(filePath, imageBytes);
        return filePath;
    }

    // ═══ 生图模式浮动操作窗 ═══

    private void OnSendToInpaint(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        { TxtStatus.Text = "没有生成结果可发送"; return; }

        GenResultBar.Visibility = Visibility.Collapsed;
        SendImageToInpaint(_currentGenImageBytes);
    }

    private async void OnSendToEffectsFromGen(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        {
            TxtStatus.Text = "没有生成结果可发送";
            return;
        }

        GenResultBar.Visibility = Visibility.Collapsed;
        await SendBytesToEffectsAsync(_currentGenImageBytes, _currentGenImagePath);
    }

    private async void OnSendToEffectsFromInpaint(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[]? bytesToSend;
            if (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null)
            {
                await ApplyInpaintResultAsync();
                bytesToSend = _lastGeneratedImageBytes ?? await CreateCurrentFullImageBytes();
            }
            else
            {
                bytesToSend = await CreateCurrentFullImageBytes();
            }

            if (bytesToSend == null || bytesToSend.Length == 0)
            {
                TxtStatus.Text = "没有图像可发送到效果";
                return;
            }

            await SendBytesToEffectsAsync(bytesToSend, MaskCanvas.LoadedFilePath);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"发送到效果失败: {ex.Message}";
        }
    }

    private async void OnGenSendToInspect(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        { TxtStatus.Text = "没有生成结果可发送"; return; }
        GenResultBar.Visibility = Visibility.Collapsed;
        SwitchMode(AppMode.Inspect);
        await LoadInspectImageFromBytesAsync(_currentGenImageBytes, _currentGenImagePath != null ? Path.GetFileName(_currentGenImagePath) : null);
    }

    private async void OnDeleteGenResult(object sender, RoutedEventArgs e)
    {
        GenResultBar.Visibility = Visibility.Collapsed;

        string? deletedPath = _currentGenImagePath;
        if (!string.IsNullOrEmpty(deletedPath) && File.Exists(deletedPath))
        {
            try
            {
                int idx = _historyFiles.IndexOf(deletedPath);
                File.Delete(deletedPath);
                var delDateStr = GetDateFromFilePath(deletedPath);
                if (delDateStr != null && _historyByDate.ContainsKey(delDateStr))
                {
                    _historyByDate[delDateStr].Remove(deletedPath);
                    if (_historyByDate[delDateStr].Count == 0)
                    {
                        _historyByDate.Remove(delDateStr);
                        _historyAvailableDates.Remove(delDateStr);
                        _historyAvailableDateSet.Remove(delDateStr);
                    }
                }
                _historyFiles.Remove(deletedPath);
                _historyLoadedCount = Math.Min(_historyLoadedCount, _historyFiles.Count);
                RefreshHistoryPanel();

                string? nextPath = null;
                if (idx >= 0 && _historyFiles.Count > 0)
                    nextPath = _historyFiles[Math.Min(idx, _historyFiles.Count - 1)];

                if (nextPath != null)
                {
                    await ShowHistoryImageAsync(nextPath);
                    TxtStatus.Text = "已删除图片";
                    return;
                }
                TxtStatus.Text = "已删除生成结果";
            }
            catch (Exception ex) { TxtStatus.Text = $"删除失败: {ex.Message}"; }
        }

        _currentGenImageBytes = null;
        _currentGenImagePath = null;
        GenPreviewImage.Source = null;
        GenPlaceholder.Visibility = Visibility.Visible;
        UpdateDynamicMenuStates();
    }

    private void CopyImageToClipboard(byte[] imageBytes)
    {
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var writer = new Windows.Storage.Streams.DataWriter(stream);
            writer.WriteBytes(imageBytes);
            _ = writer.StoreAsync().AsTask().ContinueWith(_ =>
            {
                stream.Seek(0);
                DispatcherQueue.TryEnqueue(() =>
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                    TxtStatus.Text = "已复制图像到剪贴板";
                });
            });
        }
        catch (Exception ex) { TxtStatus.Text = $"复制失败: {ex.Message}"; }
    }

    private async void OnHistoryCopyImage(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                CopyImageToClipboard(bytes);
            }
            catch (Exception ex) { TxtStatus.Text = $"复制失败: {ex.Message}"; }
        }
    }

    private void OnCloseGenResultBar(object sender, RoutedEventArgs e)
    {
        GenResultBar.Visibility = Visibility.Collapsed;
    }

    private void OnGenResultBarDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        GenResultBarTranslate.X += e.Delta.Translation.X;
        GenResultBarTranslate.Y += e.Delta.Translation.Y;
    }

    // ═══════════════════════════════════════════════════════════
    //  生图预览区右键菜单 & 拖放
    // ═══════════════════════════════════════════════════════════

    private void SetupGenPreviewContextMenu()
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            bool hasImage = _currentGenImageBytes != null;

            var copyItem = new MenuFlyoutItem
            {
                Text = "复制",
                Icon = new SymbolIcon(Symbol.Copy),
                IsEnabled = hasImage,
            };
            copyItem.Click += (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    CopyImageToClipboard(_currentGenImageBytes);
            };
            flyout.Items.Add(copyItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var readerItem = new MenuFlyoutItem
            {
                Text = "发送到检视",
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEE6F" },
                IsEnabled = hasImage,
            };
            readerItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes == null) return;
                SwitchMode(AppMode.Inspect);
                await LoadInspectImageFromBytesAsync(_currentGenImageBytes,
                    _currentGenImagePath != null ? Path.GetFileName(_currentGenImagePath) : null);
            };
            flyout.Items.Add(readerItem);

            var postItem = new MenuFlyoutItem
            {
                Text = "发送到效果",
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" },
                IsEnabled = hasImage,
            };
            postItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes == null) return;
                await SendBytesToEffectsAsync(_currentGenImageBytes, _currentGenImagePath);
            };
            flyout.Items.Add(postItem);

            var inpaintItem = new MenuFlyoutItem
            {
                Text = "发送到重绘",
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" },
                IsEnabled = hasImage,
            };
            inpaintItem.Click += (_, _) =>
            {
                if (_currentGenImageBytes != null) SendImageToInpaint(_currentGenImageBytes);
            };
            flyout.Items.Add(inpaintItem);

            var upscaleItem = new MenuFlyoutItem
            {
                Text = "发送到超分",
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9" },
                IsEnabled = hasImage,
            };
            upscaleItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes == null) return;
                await SendBytesToUpscaleAsync(_currentGenImageBytes, _currentGenImagePath);
            };
            flyout.Items.Add(upscaleItem);

            if (!string.IsNullOrEmpty(_currentGenImagePath))
            {
                var folderItem = new MenuFlyoutItem
                {
                    Text = "打开所在文件夹",
                    Icon = new SymbolIcon(Symbol.OpenLocal),
                };
                folderItem.Click += (_, _) =>
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(_currentGenImagePath);
                        if (dir != null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_currentGenImagePath}\"");
                    }
                    catch { }
                };
                flyout.Items.Add(folderItem);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            var useParamsItem = new MenuFlyoutItem
            {
                Text = "使用参数",
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B6" },
                IsEnabled = hasImage,
            };
            useParamsItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await ApplyDroppedImageMetadata(_currentGenImageBytes, "预览图片");
            };
            flyout.Items.Add(useParamsItem);

            var useParamsNoSeedItem = new MenuFlyoutItem
            {
                Text = "使用参数（不包含种子）",
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B5" },
                IsEnabled = hasImage,
            };
            useParamsNoSeedItem.Click += async (_, _) =>
            {
                if (_currentGenImageBytes != null)
                    await ApplyDroppedImageMetadata(_currentGenImageBytes, "预览图片", skipSeed: true);
            };
            flyout.Items.Add(useParamsNoSeedItem);
            foreach (var item in flyout.Items)
                ApplyMenuTypography(item);
        };
        GenPreviewArea.ContextFlyout = flyout;
    }

    private void OnGenPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void OnGenPreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file &&
                (file.FileType.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".webp", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = await File.ReadAllBytesAsync(file.Path);
                await ApplyDroppedImageMetadata(bytes, file.Name);
                return;
            }
        }
    }

    private async Task ApplyDroppedImageMetadata(byte[] bytes, string fileName, bool skipSeed = false)
    {
        var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
        if (meta == null || !meta.IsNaiParsed)
        {
            TxtStatus.Text = $"已拖入 {fileName}，但未找到可用的 NAI 元数据";
            return;
        }

        bool maxMode = _settings.Settings.MaxMode;
        var skipped = new List<string>();

        string positivePrompt = meta.PositivePrompt;
        bool strippedQuality = false;
        if (positivePrompt.Contains(QualityTagBlock))
        {
            positivePrompt = positivePrompt.Replace(QualityTagBlock, "");
            positivePrompt = System.Text.RegularExpressions.Regex.Replace(positivePrompt, @",\s*,", ",");
            positivePrompt = positivePrompt.Trim(' ', ',');
            strippedQuality = true;
        }

        _genPositivePrompt = positivePrompt;
        _genNegativePrompt = meta.NegativePrompt;
        _genStylePrompt = "";

        var p = _settings.Settings.GenParameters;

        if (strippedQuality)
        {
            p.QualityToggle = true;
            if (IsAdvancedWindowOpen) _advCboQuality.SelectedIndex = 0;
        }

        if (meta.Steps > 0)
        {
            if (!maxMode && meta.Steps > 28)
                skipped.Add($"步数 ({meta.Steps}>28)");
            else
                p.Steps = meta.Steps;
        }
        if (!skipSeed && meta.Seed > 0 && meta.Seed <= int.MaxValue) p.Seed = (int)meta.Seed;
        if (meta.Scale > 0) p.Scale = meta.Scale;
        p.CfgRescale = meta.CfgRescale;
        if (!string.IsNullOrEmpty(meta.Sampler)) p.Sampler = meta.Sampler;
        if (!string.IsNullOrEmpty(meta.NoiseSchedule)) p.Schedule = meta.NoiseSchedule;
        p.Variety = meta.SmDyn || meta.Sm;

        if (meta.Width > 0 && meta.Height > 0)
        {
            if (!maxMode && (long)meta.Width * meta.Height > 1024L * 1024)
                skipped.Add($"尺寸 ({meta.Width}×{meta.Height} 超过 1024²)");
            else
            {
                _customWidth = meta.Width;
                _customHeight = meta.Height;
            }
        }

        _isUpdatingMaxSize = true;
        NbMaxWidth.Value = _customWidth;
        NbMaxHeight.Value = _customHeight;
        _isUpdatingMaxSize = false;
        if (!skipSeed) NbSeed.Value = p.Seed;
        ChkVariety.IsChecked = p.Variety;

        if (meta.CharacterPrompts.Count > 0)
            SetGenCharactersFromMetadata(meta);
        else
            _genCharacters.Clear();
        ApplyReferenceDataFromMetadata(meta);
        RefreshCharacterPanel();

        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        UpdateSizeWarningVisuals();

        if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();

        var notes = new List<string>();
        if (strippedQuality) notes.Add("已提取质量词并启用「添加质量词」");
        if (skipSeed) notes.Add("已跳过种子");
        if (skipped.Count > 0) notes.Add($"已跳过: {string.Join(", ", skipped)}");
        AppendReferenceImportNotes(meta, notes);

        TxtStatus.Text = notes.Count > 0
            ? $"已从 {fileName} 应用元数据（{string.Join("; ", notes)}）"
            : $"已从 {fileName} 应用元数据";
    }

    // ═══════════════════════════════════════════════════════════
    //  发送到重绘
    // ═══════════════════════════════════════════════════════════

    private async void SendImageToInpaint(byte[] imageBytes)
    {
        try
        {
            SaveCurrentPromptToBuffer();
            var (sendW, sendH) = GetSelectedSize();

            var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(imageBytes));

            string sendPos, sendNeg, sendStyle;
            if (meta != null && (meta.IsNaiParsed || meta.IsSdFormat))
            {
                sendPos = meta.PositivePrompt;
                sendNeg = meta.NegativePrompt;
                sendStyle = "";

                if (meta.IsNaiParsed)
                {
                    if (meta.CharacterPrompts.Count > 0)
                        SetGenCharactersFromMetadata(meta);
                    ApplyReferenceDataFromMetadata(meta);
                    RefreshCharacterPanel();
                }
            }
            else
            {
                sendPos = _genPositivePrompt;
                sendNeg = _genNegativePrompt;
                sendStyle = _genStylePrompt;
            }

            var device = MaskCanvas.GetDevice() ?? CanvasDevice.GetSharedDevice();

            CanvasBitmap bitmap;
            using (var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                using var writer = new Windows.Storage.Streams.DataWriter(ms);
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                writer.DetachStream();
                ms.Seek(0);
                bitmap = await CanvasBitmap.LoadAsync(device, ms, 96f);
            }

            int imgW = (int)bitmap.SizeInPixels.Width;
            int imgH = (int)bitmap.SizeInPixels.Height;

            _inpaintPositivePrompt = sendPos;
            _inpaintNegativePrompt = sendNeg;
            _inpaintStylePrompt = sendStyle;

            bool imgMatchesPreset = Array.Exists(MaskCanvasControl.CanvasPresets,
                p => p.W == imgW && p.H == imgH);

            int canvasW, canvasH;
            bool sizeApplied;

            if (_settings.Settings.MaxMode)
            {
                canvasW = imgW;
                canvasH = imgH;
                _customWidth = imgW;
                _customHeight = imgH;
                NbMaxWidth.Value = imgW;
                NbMaxHeight.Value = imgH;
                sizeApplied = true;
            }
            else if (imgMatchesPreset)
            {
                canvasW = imgW;
                canvasH = imgH;
                _customWidth = imgW;
                _customHeight = imgH;
                int idx = Array.FindIndex(MaskCanvasControl.CanvasPresets,
                    p => p.W == imgW && p.H == imgH);
                    if (idx >= 0) CboSize.SelectedIndex = idx;
                sizeApplied = true;
                }
                else
                {
                if (CboSize.SelectedIndex >= 0 &&
                    CboSize.SelectedIndex < MaskCanvasControl.CanvasPresets.Length)
                {
                    var preset = MaskCanvasControl.CanvasPresets[CboSize.SelectedIndex];
                    canvasW = preset.W;
                    canvasH = preset.H;
                }
                else
                {
                    canvasW = sendW;
                    canvasH = sendH;
                }
                sizeApplied = false;
            }

            SwitchMode(AppMode.Inpaint);
            MaskCanvas.InitializeCanvas(canvasW, canvasH);
            MaskCanvas.LoadImageFromBitmap(bitmap);
            MaskCanvas.FitToScreen();

            UpdatePromptHighlights();

            TxtStatus.Text = sizeApplied
                ? $"已发送到重绘 ({imgW} × {imgH})，已同步提示词与尺寸"
                : $"已发送到重绘 ({imgW} × {imgH})，画布 {canvasW}×{canvasH}，已同步提示词";
        }
        catch (Exception ex) { TxtStatus.Text = $"发送到重绘失败: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════════
    //  重绘模式生成
    // ═══════════════════════════════════════════════════════════

    private async Task<bool> DoInpaintGenerateAsync(bool forceRandomSeed = false)
    {
        if (MaskCanvas.IsInPreviewMode)
        {
            return await RedoInpaintGenerateAsync(forceRandomSeed);
        }
        if (MaskCanvas.Document.MaskTarget == null || MaskCanvas.GetDevice() == null)
        { TxtStatus.Text = "画布未初始化"; return false; }
        if (!MaskCanvas.HasMaskContent())
        { TxtStatus.Text = "未绘制遮罩，无法发送请求"; return false; }

        bool keepGenerateButtonInteractive = _continuousGenRunning;
        if (!keepGenerateButtonInteractive)
            BtnGenerate.IsEnabled = false;
        _generateRequestRunning = true;
        UpdateBtnGenerateForApiKey();
        TxtStatus.Text = "正在生成...";
        var ip = _settings.Settings.InpaintParameters;
        int origSeed = ip.Seed;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;
            var device = MaskCanvas.GetDevice()!;

            var exportImage = MaskCanvas.Document.CreateCompositeForExport(device);
            if (exportImage == null) { TxtStatus.Text = "无法创建导出图片"; BtnGenerate.IsEnabled = true; return false; }

            var imageBase64 = await NovelAIService.EncodeRenderTargetAsync(exportImage, isMask: false);
            var maskBase64 = await NovelAIService.EncodeRenderTargetAsync(MaskCanvas.Document.MaskTarget!, isMask: true);

            exportImage.Dispose();

            _cachedImageBase64 = imageBase64;
            _cachedMaskBase64 = maskBase64;
            int actualSeed = (!forceRandomSeed && ip.Seed > 0) ? ip.Seed : Random.Shared.Next(1, int.MaxValue);
            ip.Seed = actualSeed;
            var wildcardContext = CreateWildcardContext(actualSeed, ip.Model);
            var (prompt, negPrompt) = GetPrompts(wildcardContext);
            _cachedPrompt = prompt;
            _cachedNegPrompt = negPrompt;

            DebugLog($"[Inpaint] Start | Model={ip.Model} | Seed={actualSeed}");

            var resultBitmap = await SendInpaintRequestAsync(imageBase64, maskBase64, prompt, negPrompt, wildcardContext, ct);
            _lastUsedSeed = actualSeed;

            if (resultBitmap == null) return false;

            MaskCanvas.SetPreview(resultBitmap);
            ShowResultBar();
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            _ = RefreshAnlasInfoAsync(forceRefresh: true);
            DebugLog($"[Inpaint] Completed | Seed={actualSeed}");
            TxtStatus.Text = "生成完成！";
            return true;
        }
        catch (OperationCanceledException)
        {
            DebugLog("[Inpaint] Cancelled");
            TxtStatus.Text = "生成已取消";
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            return false;
        }
        catch (Exception ex)
        {
            DebugLog($"[Inpaint] Failed: {ex}");
            TxtStatus.Text = $"生成失败: {ex.Message}";
            if (!keepGenerateButtonInteractive)
                BtnGenerate.IsEnabled = true;
            return false;
        }
        finally
        {
            _generateRequestRunning = false;
            UpdateBtnGenerateForApiKey();
            ip.Seed = origSeed;
        }
    }

    private async Task<CanvasBitmap?> SendInpaintRequestAsync(
        string imageBase64, string maskBase64,
        string prompt, string negPrompt, WildcardExpandContext wildcardContext, CancellationToken ct)
    {
        var device = MaskCanvas.GetDevice()!;
        if (!TryValidateReferenceRequest(out string referenceError))
        {
            TxtStatus.Text = referenceError;
            BtnGenerate.IsEnabled = true;
            return null;
        }
        if (_genCharacters.Count > 0) ApplyCharCountPrefixStrip();
        var chars = (_genCharacters.Count > 0 && !IsCurrentModelV3()) ? GetCharacterData(wildcardContext) : null;
        var vibes = GetVibeTransferData();
        var preciseReferences = GetPreciseReferenceData();
        var (imageBytes, error) = await _naiService.InpaintAsync(
            imageBase64, maskBase64,
            MaskCanvas.CanvasW, MaskCanvas.CanvasH,
            prompt, negPrompt, chars, vibes, preciseReferences, ct);

        if (error != null) { DebugLog($"[Inpaint] API error: {error}"); TxtStatus.Text = error; BtnGenerate.IsEnabled = true; return null; }
        if (imageBytes == null) { DebugLog("[Inpaint] API returned no image"); TxtStatus.Text = "API 返回为空"; BtnGenerate.IsEnabled = true; return null; }

        _pendingResultBytes = imageBytes;
        _pendingResultBitmap?.Dispose();

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(stream);
        writer.WriteBytes(imageBytes);
        await writer.StoreAsync();
        stream.Seek(0);
        _pendingResultBitmap = await CanvasBitmap.LoadAsync(device, stream, 96f);
        return _pendingResultBitmap;
    }

    // ═══════════════════════════════════════════════════════════
    //  重绘预览操作：应用 / 重做 / 舍弃
    // ═══════════════════════════════════════════════════════════

    private async void OnApplyResult(object sender, RoutedEventArgs e)
    {
        if (_pendingResultBitmap == null) return;
        try
        {
            await ApplyInpaintResultAsync();
            TxtStatus.Text = "已应用重绘结果。文件→另存为 可保存完整图片。";
        }
        catch (Exception ex) { TxtStatus.Text = $"应用失败: {ex.Message}"; }
    }

    private async Task<bool> RedoInpaintGenerateAsync(bool forceRandomSeed = false)
    {
        if (_cachedImageBase64 == null || _cachedMaskBase64 == null) return false;
        BtnGenerate.IsEnabled = false;
        _generateRequestRunning = true;
        UpdateBtnGenerateForApiKey();
        SetResultBarEnabled(false);
        TxtStatus.Text = "正在重新生成...";
        var ip = _settings.Settings.InpaintParameters;
        int origSeed = ip.Seed;

        try
        {
            _generateCts?.Cancel();
            _generateCts = new CancellationTokenSource();
            var ct = _generateCts.Token;

            int actualSeed = (!forceRandomSeed && ip.Seed > 0) ? ip.Seed : Random.Shared.Next(1, int.MaxValue);
            ip.Seed = actualSeed;
            var wildcardContext = CreateWildcardContext(actualSeed, ip.Model);
            var (prompt, negPrompt) = GetPrompts(wildcardContext);
            _cachedPrompt = prompt;
            _cachedNegPrompt = negPrompt;

            var resultBitmap = await SendInpaintRequestAsync(
                _cachedImageBase64, _cachedMaskBase64,
                prompt, negPrompt, wildcardContext, ct);
            _lastUsedSeed = actualSeed;

            if (resultBitmap != null)
            {
                MaskCanvas.SetPreview(resultBitmap);
                _ = RefreshAnlasInfoAsync(forceRefresh: true);
                TxtStatus.Text = "重新生成完成！请选择：应用 / 重做 / 舍弃";
                return true;
            }
        }
        catch (OperationCanceledException) { TxtStatus.Text = "生成已取消"; }
        catch (Exception ex) { TxtStatus.Text = $"重新生成失败: {ex.Message}"; }
        finally
        {
            _generateRequestRunning = false;
            UpdateBtnGenerateForApiKey();
            ip.Seed = origSeed;
            SetResultBarEnabled(true);
        }
        return false;
    }

    private async void OnRedoGenerate(object sender, RoutedEventArgs e)
    {
        if (_cachedImageBase64 == null || _cachedMaskBase64 == null) return;
        await RedoInpaintGenerateAsync();
    }

    private void OnDiscardResult(object sender, RoutedEventArgs e)
    {
        ExitPreviewMode();
        UpdateInpaintRedoButtonWarning();
        TxtStatus.Text = "已舍弃生成结果";
    }

    private void ExitPreviewMode()
    {
        MaskCanvas.ClearPreview();
        _pendingResultBitmap?.Dispose();
        _pendingResultBitmap = null;
        _pendingResultBytes = null;
        _cachedImageBase64 = null;
        _cachedMaskBase64 = null;
        ResultActionBar.Visibility = Visibility.Collapsed;
        BtnGenerate.IsEnabled = true;
    }

    private void SetResultBarEnabled(bool enabled)
    {
        foreach (var child in ((StackPanel)((Border)ResultActionBar.Children[0]).Child).Children)
        {
            if (child is Button btn) btn.IsEnabled = enabled;
        }
    }

    private void ShowResultBar()
    {
        ResultBarTranslate.X = 0;
        ResultBarTranslate.Y = 0;
        ResultActionBar.Visibility = Visibility.Visible;
        UpdateInpaintRedoButtonWarning();
    }

    private void OnResultBarDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        ResultBarTranslate.X += e.Delta.Translation.X;
        ResultBarTranslate.Y += e.Delta.Translation.Y;
    }

    private void OnComparePressed(object sender, PointerRoutedEventArgs e)
    {
        MaskCanvas.IsComparing = true;
        (sender as UIElement)?.CapturePointer(e.Pointer);
    }

    private void OnCompareReleased(object sender, PointerRoutedEventArgs e)
    {
        MaskCanvas.IsComparing = false;
    }

    private async Task ApplyInpaintResultAsync()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null || _pendingResultBitmap == null) return;

        var doc = MaskCanvas.Document;
        var offset = doc.PixelAlignedImageOffset;
        int canvasW = MaskCanvas.CanvasW;
        int canvasH = MaskCanvas.CanvasH;

        if (doc.OriginalImage == null)
        {
            MaskCanvas.ClearPreview();
            doc.SetOriginalImage(_pendingResultBitmap);
            _lastGeneratedImageBytes = _pendingResultBytes;
            _pendingResultBitmap = null;
        }
        else
        {
            float origW = doc.OriginalImage.SizeInPixels.Width;
            float origH = doc.OriginalImage.SizeInPixels.Height;

            float canvasInOrigX = -offset.X;
            float canvasInOrigY = -offset.Y;

            float minX = Math.Min(0, canvasInOrigX);
            float minY = Math.Min(0, canvasInOrigY);
            float maxX = Math.Max(origW, canvasInOrigX + canvasW);
            float maxY = Math.Max(origH, canvasInOrigY + canvasH);

            int compositeW = (int)Math.Ceiling(maxX - minX);
            int compositeH = (int)Math.Ceiling(maxY - minY);

            using var composite = new CanvasRenderTarget(device, compositeW, compositeH, 96f);
            using (var ds = composite.CreateDrawingSession())
            {
                ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                ds.DrawImage(doc.OriginalImage, -minX, -minY);
                ds.DrawImage(_pendingResultBitmap, canvasInOrigX - minX, canvasInOrigY - minY);
            }

            byte[] compBytes;
            using (var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                await composite.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
                saveStream.Seek(0);
                compBytes = new byte[saveStream.Size];
                using var reader = new Windows.Storage.Streams.DataReader(saveStream);
                await reader.LoadAsync((uint)saveStream.Size);
                reader.ReadBytes(compBytes);
            }
            _lastGeneratedImageBytes = compBytes;

            CanvasBitmap newOriginal;
            using (var loadStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                using var writer = new Windows.Storage.Streams.DataWriter(loadStream);
                writer.WriteBytes(compBytes);
                await writer.StoreAsync();
                writer.DetachStream();
                loadStream.Seek(0);
                newOriginal = await CanvasBitmap.LoadAsync(device, loadStream, 96f);
            }

            var newOffset = new Vector2(offset.X + minX, offset.Y + minY);

            MaskCanvas.ClearPreview();
            doc.SetOriginalImage(newOriginal);
            doc.ImageOffset = newOffset;

            _pendingResultBitmap.Dispose();
            _pendingResultBitmap = null;
        }

        _pendingResultBytes = null;
        _cachedImageBase64 = null;
        _cachedMaskBase64 = null;
        doc.ClearMask();
        if (MaskCanvas.IsInPreviewMode) MaskCanvas.ClearPreview();
        MaskCanvas.UndoMgr.Clear();
        ResultActionBar.Visibility = Visibility.Collapsed;
        BtnGenerate.IsEnabled = true;
        MaskCanvas.RefreshCanvas();
    }

    // ═══════════════════════════════════════════════════════════
    //  检视模式
    // ═══════════════════════════════════════════════════════════

    private void SetInspectPrimaryAction(InspectPrimaryAction action, bool isEnabled)
    {
        _inspectPrimaryAction = action;
        BtnSendToGen.IsEnabled = isEnabled;

        switch (action)
        {
            case InspectPrimaryAction.InferTags:
                BtnSendToGenIcon.Symbol = Symbol.Tag;
                BtnSendToGenText.Text = "使用模型进行反推";
                break;
            case InspectPrimaryAction.DisabledSend:
            case InspectPrimaryAction.SendMetadata:
            default:
                BtnSendToGenIcon.Symbol = Symbol.Send;
                BtnSendToGenText.Text = "发送生成参数";
                break;
        }
    }

    private static string FormatInspectValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string FormatInspectNumber(double value)
        => value > 0 ? value.ToString("G") : "-";

    private async Task<byte[]?> CreateCurrentCanvasImageBytesAsync()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null) return null;

        var doc = MaskCanvas.Document;
        if (doc.OriginalImage == null) return null;

        int canvasW = MaskCanvas.CanvasW;
        int canvasH = MaskCanvas.CanvasH;
        var offset = doc.PixelAlignedImageOffset;

        using var composite = new CanvasRenderTarget(device, canvasW, canvasH, 96f);
        using (var ds = composite.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawImage(doc.OriginalImage, offset.X, offset.Y);
        }

        using var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await composite.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
        saveStream.Seek(0);
        var bytes = new byte[saveStream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(saveStream);
        await reader.LoadAsync((uint)saveStream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task<byte[]?> GetInpaintPromptInferenceImageBytesAsync(bool canvasOnly)
    {
        if (MaskCanvas.IsInPreviewMode && _pendingResultBytes != null)
            return canvasOnly ? _pendingResultBytes : await CreatePreviewCompositeBytes();

        return canvasOnly
            ? await CreateCurrentCanvasImageBytesAsync()
            : await CreateCurrentFullImageBytes();
    }

    private async Task RunInpaintPromptInferenceAsync(bool canvasOnly)
    {
        SaveCurrentPromptToBuffer();

        var imageBytes = await GetInpaintPromptInferenceImageBytesAsync(canvasOnly);
        if (imageBytes == null || imageBytes.Length == 0)
        {
            TxtStatus.Text = canvasOnly ? "没有可用于画布推理的图像" : "没有可用于全局推理的图像";
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Settings.ReverseTagger.ModelPath))
        {
            TxtStatus.Text = "请先在设置中配置反推模型路径";
            await ShowReverseTaggerSettingsDialogAsync();
            return;
        }

        string modeLabel = canvasOnly ? "画布推理" : "全局推理";
        TxtStatus.Text = $"正在执行{modeLabel}...";
        DebugLog($"[InpaintPromptInfer] Start | Mode={modeLabel}");

        try
        {
            var result = await _reverseTaggerService.InferAsync(imageBytes, _settings.Settings.ReverseTagger);
            var artistTagSet = LoadReverseTaggerArtistTagSet();
            var preservedArtistTags = ExtractArtistTags(_inpaintPositivePrompt, artistTagSet);
            _inpaintPositivePrompt = MergePromptTagsPreservingArtists(preservedArtistTags, result.PositivePrompt);

            LoadPromptFromBuffer();
            UpdateSplitVisibility();
            UpdatePromptHighlights();
            UpdateStyleHighlights();

            int totalTags = result.GeneralTags.Count +
                            (_settings.Settings.ReverseTagger.AddCharacterTags ? result.CharacterTags.Count : 0) +
                            (_settings.Settings.ReverseTagger.AddCopyrightTags ? result.CopyrightTags.Count : 0);
            DebugLog($"[InpaintPromptInfer] Completed | Mode={modeLabel} | PreservedStyleTags={preservedArtistTags.Count} | TagCount={totalTags}");
            TxtStatus.Text = $"{modeLabel}完成，已替换主提示词中的非风格标签";
        }
        catch (Exception ex)
        {
            DebugLog($"[InpaintPromptInfer] Failed: {ex}");
            TxtStatus.Text = $"{modeLabel}失败: {ex.Message}";
        }
        finally
        {
            if (_settings.Settings.ReverseTagger.UnloadModelAfterInference)
                _reverseTaggerService.Dispose();
        }
    }

    private static string MergePromptTagsPreservingArtists(IReadOnlyList<string> artistTags, string inferredPrompt)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in artistTags)
        {
            string normalized = NormalizePromptTagForMatch(tag);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                merged.Add(tag.Trim());
        }

        foreach (var tag in SplitPromptTags(inferredPrompt))
        {
            string normalized = NormalizePromptTagForMatch(tag);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                merged.Add(tag);
        }

        return string.Join(", ", merged);
    }

    private List<string> ExtractArtistTags(string prompt, HashSet<string> artistTagSet)
    {
        var result = new List<string>();
        foreach (var tag in SplitPromptTags(prompt))
        {
            if (IsArtistPromptTag(tag, artistTagSet))
                result.Add(tag);
        }
        return result;
    }

    private static List<string> SplitPromptTags(string prompt) =>
        (prompt ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

    private static bool IsArtistPromptTag(string tag, HashSet<string> artistTagSet)
    {
        string normalized = NormalizePromptTagForMatch(tag);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized.StartsWith("artist:", StringComparison.OrdinalIgnoreCase) ||
               artistTagSet.Contains(normalized);
    }

    private static string NormalizePromptTagForMatch(string tag)
    {
        string normalized = (tag ?? "").Trim();
        normalized = Regex.Replace(normalized, @"^[\(\[\{<\s]+|[\)\]\}>\s]+$", "");
        normalized = Regex.Replace(normalized, @":\s*-?\d+(\.\d+)?$", "");
        normalized = normalized.Replace('_', ' ');
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized.ToLowerInvariant();
    }

    private HashSet<string> LoadReverseTaggerArtistTagSet()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string modelDir = _settings.Settings.ReverseTagger.ModelPath?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(modelDir))
            return result;

        string csvPath = Path.Combine(Path.GetFullPath(modelDir), "selected_tags.csv");
        if (!File.Exists(csvPath))
            return result;

        bool isFirstLine = true;
        foreach (var line in File.ReadLines(csvPath, Encoding.UTF8))
        {
            if (isFirstLine)
            {
                isFirstLine = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseSimpleCsvLine(line);
            if (fields.Count < 4)
                continue;

            if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int category) ||
                category != 1)
                continue;

            string tagName = fields[2];
            if (!string.IsNullOrWhiteSpace(tagName))
                result.Add(NormalizePromptTagForMatch(tagName));
        }

        return result;
    }

    private static List<string> ParseSimpleCsvLine(string line)
    {
        var fields = new List<string>();
        var builder = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                fields.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(ch);
        }

        fields.Add(builder.ToString());
        return fields;
    }

    private async Task RunInspectReverseTagAsync()
    {
        if (_inspectImageBytes == null)
        {
            TxtStatus.Text = "没有可用于反推的图片";
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Settings.ReverseTagger.ModelPath))
        {
            TxtStatus.Text = "请先在设置中配置反推模型路径";
            await ShowReverseTaggerSettingsDialogAsync();
            return;
        }

        SetInspectPrimaryAction(InspectPrimaryAction.InferTags, false);
        BtnSendToGenText.Text = "正在反推...";
        TxtStatus.Text = "正在使用反推模型分析图片...";
        DebugLog($"[ReverseTagger] Start | Model={_settings.Settings.ReverseTagger.ModelPath}");

        try
        {
            var result = await _reverseTaggerService.InferAsync(_inspectImageBytes, _settings.Settings.ReverseTagger);

            _inspectMetadata = new ImageMetadata
            {
                PositivePrompt = result.PositivePrompt,
                NegativePrompt = "",
                Width = result.ImageWidth,
                Height = result.ImageHeight,
                Source = result.ExecutionProvider,
                IsModelInference = true,
            };

            DisplayInspectMetadata(_inspectMetadata);

            int totalTags = result.GeneralTags.Count +
                            (_settings.Settings.ReverseTagger.AddCharacterTags ? result.CharacterTags.Count : 0) +
                            (_settings.Settings.ReverseTagger.AddCopyrightTags ? result.CopyrightTags.Count : 0);
            DebugLog($"[ReverseTagger] Completed | Provider={result.ExecutionProvider} | TagCount={totalTags}");
            TxtStatus.Text = $"反推完成，已使用 {result.ExecutionProvider}，共识别 {totalTags} 个标签";
        }
        catch (Exception ex)
        {
            DebugLog($"[ReverseTagger] Failed: {ex}");
            SetInspectPrimaryAction(InspectPrimaryAction.InferTags, _inspectImageBytes != null);
            TxtStatus.Text = $"反推失败: {ex.Message}";
        }
        finally
        {
            if (_settings.Settings.ReverseTagger.UnloadModelAfterInference)
                _reverseTaggerService.Dispose();
        }
    }

    private async void RunInspectImageScrambleAsync(ImageScrambleService.ProcessType processType)
    {
        if (_inspectImageBytes == null) return;

        try
        {
            TxtStatus.Text = processType == ImageScrambleService.ProcessType.Encrypt
                ? "正在混淆图片..."
                : "正在反混淆图片...";

            byte[]? resultBytes = await Task.Run(() =>
            {
                using var bitmap = SKBitmap.Decode(_inspectImageBytes);
                if (bitmap == null) return null;

                using var processed = ImageScrambleService.Process(bitmap, processType);
                using var image = SKImage.FromBitmap(processed);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data?.ToArray();
            });

            if (resultBytes == null)
            {
                TxtStatus.Text = "处理图片失败";
                return;
            }

            _inspectImageBytes = resultBytes;
            _inspectRawModified = true;

            using var ms = new MemoryStream(resultBytes);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            InspectPreviewImage.Source = bmp;

            TxtStatus.Text = processType == ImageScrambleService.ProcessType.Encrypt
                ? "图片混淆完成"
                : "图片反混淆完成";

            UpdateFileMenuState();
            if (_currentMode == AppMode.Inspect) ReplaceEditMenu();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"图片处理失败: {ex.Message}";
        }
    }

    private async Task LoadInspectImageAsync(string filePath)
    {
        try
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(filePath));
            _inspectImageBytes = bytes;
            _inspectImagePath = filePath;
            _inspectRawModified = false;

            var bitmapImage = new BitmapImage();
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using var writer = new Windows.Storage.Streams.DataWriter(ms);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
            ms.Seek(0);
            await bitmapImage.SetSourceAsync(ms);
            InspectPreviewImage.Source = bitmapImage;
            InspectImagePlaceholder.Visibility = Visibility.Collapsed;

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FitInspectPreviewToScreen());

            var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
            _inspectMetadata = meta;
            DisplayInspectMetadata(meta);
            UpdateInspectSaveState();
            if (_currentMode == AppMode.Inspect)
            {
                ReplaceEditMenu();
                ReplaceToolMenu();
            }

            TxtStatus.Text = meta != null
                ? $"已加载: {Path.GetFileName(filePath)} (含元数据)"
                : $"已加载: {Path.GetFileName(filePath)} (无元数据)";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"加载失败: {ex.Message}";
        }
    }

    private async Task LoadInspectImageFromBytesAsync(byte[] bytes, string? sourceName = null)
    {
        try
        {
            _inspectImageBytes = bytes;
            _inspectImagePath = null;
            _inspectRawModified = false;

            var bitmapImage = new BitmapImage();
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using var writer = new Windows.Storage.Streams.DataWriter(ms);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
            ms.Seek(0);
            await bitmapImage.SetSourceAsync(ms);
            InspectPreviewImage.Source = bitmapImage;
            InspectImagePlaceholder.Visibility = Visibility.Collapsed;

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FitInspectPreviewToScreen());

            var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
            _inspectMetadata = meta;
            DisplayInspectMetadata(meta);
            UpdateInspectSaveState();
            if (_currentMode == AppMode.Inspect) ReplaceEditMenu();

            TxtStatus.Text = meta != null
                ? $"已加载图像 (含元数据)"
                : $"已加载图像 (无元数据)";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"加载失败: {ex.Message}";
        }
    }

    private void DisplayInspectMetadata(ImageMetadata? meta)
    {
        TxtInspectRawMeta.Visibility = Visibility.Collapsed;
        InspectCharPanel.Children.Clear();
        InspectCharPanel.Visibility = Visibility.Collapsed;
        InspectCharNegPanel.Children.Clear();
        InspectCharNegPanel.Visibility = Visibility.Collapsed;
        TxtInspectPositive.Text = "";
        TxtInspectNegative.Text = "";
        TxtInspectSize.Text = "-";
        TxtInspectSteps.Text = "-";
        TxtInspectSampler.Text = "-";
        TxtInspectSchedule.Text = "-";
        TxtInspectScale.Text = "-";
        TxtInspectCfgRescale.Text = "-";
        TxtInspectSeed.Text = "-";
        TxtInspectVariety.Text = "-";

        if (meta == null)
        {
            InspectPlaceholder.Text = "此图片不包含可识别的元数据";
            InspectPlaceholder.Visibility = Visibility.Visible;
            InspectContent.Visibility = Visibility.Collapsed;
            SetInspectPrimaryAction(InspectPrimaryAction.InferTags, _inspectImageBytes != null);
            BtnSendInspectToInpaint.IsEnabled = _inspectImageBytes != null;
            UpdateDynamicMenuStates();
            return;
        }

        if (!meta.IsNaiParsed && !meta.IsSdFormat && !meta.IsModelInference)
        {
            InspectPlaceholder.Visibility = Visibility.Collapsed;
            InspectContent.Visibility = Visibility.Collapsed;
            TxtInspectRawMeta.Visibility = Visibility.Visible;
            TxtInspectRawMeta.Text = meta.RawJson;
            SetInspectPrimaryAction(InspectPrimaryAction.InferTags, _inspectImageBytes != null);
            BtnSendInspectToInpaint.IsEnabled = _inspectImageBytes != null;
            UpdateDynamicMenuStates();
            return;
        }

        InspectPlaceholder.Visibility = Visibility.Collapsed;
        InspectContent.Visibility = Visibility.Visible;
        SetInspectPrimaryAction(InspectPrimaryAction.SendMetadata, true);
        BtnSendInspectToInpaint.IsEnabled = true;

        TxtInspectPositive.Text = FormatInspectValue(meta.PositivePrompt);
        TxtInspectNegative.Text = FormatInspectValue(meta.NegativePrompt);

        if (!meta.IsModelInference && meta.CharacterPrompts.Count > 0)
        {
            InspectCharPanel.Visibility = Visibility.Visible;
            InspectCharPanel.Children.Add(CreateThemedCaption("角色提示词"));
            for (int i = 0; i < meta.CharacterPrompts.Count; i++)
            {
                InspectCharPanel.Children.Add(CreateThemedSubLabel($"角色 {i + 1}"));
                InspectCharPanel.Children.Add(new TextBox
                {
                    Text = meta.CharacterPrompts[i],
                    IsReadOnly = true, AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 100,
                });
            }
        }
        else
        {
            InspectCharPanel.Visibility = Visibility.Collapsed;
        }

        if (!meta.IsModelInference && meta.CharacterNegativePrompts.Count > 0)
        {
            InspectCharNegPanel.Visibility = Visibility.Visible;
            InspectCharNegPanel.Children.Add(CreateThemedCaption("角色负面提示词"));
            for (int i = 0; i < meta.CharacterNegativePrompts.Count; i++)
            {
                InspectCharNegPanel.Children.Add(CreateThemedSubLabel($"角色 {i + 1}"));
                InspectCharNegPanel.Children.Add(new TextBox
                {
                    Text = meta.CharacterNegativePrompts[i],
                    IsReadOnly = true, AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 80,
                });
            }
        }
        else
        {
            InspectCharNegPanel.Visibility = Visibility.Collapsed;
        }

        TxtInspectSize.Text = meta.Width > 0 && meta.Height > 0 ? $"{meta.Width} × {meta.Height}" : "-";
        TxtInspectSteps.Text = meta.Steps > 0 ? meta.Steps.ToString() : "-";
        TxtInspectSampler.Text = FormatInspectValue(meta.Sampler);
        TxtInspectSchedule.Text = FormatInspectValue(meta.NoiseSchedule);
        TxtInspectScale.Text = FormatInspectNumber(meta.Scale);
        TxtInspectCfgRescale.Text = meta.IsSdFormat || meta.IsModelInference ? "-" : FormatInspectNumber(meta.CfgRescale);
        TxtInspectSeed.Text = meta.Seed > 0 ? meta.Seed.ToString() : "-";
        TxtInspectVariety.Text = meta.IsSdFormat || meta.IsModelInference ? "-" : ((meta.SmDyn || meta.Sm) ? "是" : "否");
        UpdateDynamicMenuStates();
    }

    private TextBlock CreateThemedCaption(string text)
    {
        var rootGrid = (Grid)this.Content;
        return new TextBlock
        {
            Text = text,
            Style = (Style)rootGrid.Resources["InspectCaptionStyle"],
        };
    }

    private TextBlock CreateThemedSubLabel(string text)
    {
        var rootGrid = (Grid)this.Content;
        return new TextBlock
        {
            Text = text,
            Style = (Style)rootGrid.Resources["InspectSubLabelStyle"],
        };
    }

    private async void OnEditRawMetadata(object sender, RoutedEventArgs e)
    {
        if (_inspectImageBytes == null) return;

        if (_inspectMetadata == null)
            _inspectMetadata = new ImageMetadata();

        string prettyJson = string.IsNullOrWhiteSpace(_inspectMetadata.RawJson)
            ? ""
            : ImageMetadataService.PrettyPrintJson(_inspectMetadata.RawJson);

        var textBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = false,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinWidth = 560,
            MinHeight = 400,
            MaxHeight = 600,
        };
        textBox.Text = prettyJson;

        var dialog = new ContentDialog
        {
            Title = "编辑 Raw 元数据",
            Content = textBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            string compactJson = ImageMetadataService.CompactJson(textBox.Text);
            if (compactJson == ImageMetadataService.CompactJson(_inspectMetadata.RawJson)) return;

            var newMeta = ImageMetadataService.TryParseJson(compactJson);
            if (newMeta != null)
            {
                _inspectMetadata = newMeta;
                _inspectRawModified = true;
                DisplayInspectMetadata(newMeta);
                UpdateInspectSaveState();
                ReplaceEditMenu();
                TxtStatus.Text = "Raw 数据已更新";
            }
            else
            {
                _inspectMetadata.RawJson = compactJson;
                _inspectRawModified = true;
                UpdateInspectSaveState();
                TxtStatus.Text = "Raw 数据已保存（JSON 解析失败，参数显示可能不准确）";
            }
        }
    }

    private void UpdateInspectSaveState()
    {
        UpdateFileMenuState();
    }

    private byte[]? GetInspectSaveBytes(bool stripMetadata)
    {
        if (_inspectImageBytes == null) return null;
        if (stripMetadata)
            return ImageMetadataService.StripPngMetadata(_inspectImageBytes);
        if (_inspectRawModified && _inspectMetadata != null)
            return ImageMetadataService.ReplacePngComment(_inspectImageBytes, _inspectMetadata.RawJson);
        return _inspectImageBytes;
    }

    private async Task SaveInspectOverwriteAsync()
    {
        if (!_inspectRawModified)
        { TxtStatus.Text = "Raw 数据未修改，无需保存"; return; }

        var bytesToSave = GetInspectSaveBytes(stripMetadata: false);
        if (bytesToSave == null)
        { TxtStatus.Text = "没有可保存的图片"; return; }

        if (!string.IsNullOrEmpty(_inspectImagePath) && File.Exists(_inspectImagePath))
        {
            try
            {
                await File.WriteAllBytesAsync(_inspectImagePath, bytesToSave);
                _inspectImageBytes = bytesToSave;
                _inspectRawModified = false;
                UpdateInspectSaveState();
                TxtStatus.Text = $"已保存: {_inspectImagePath}";
            }
            catch (Exception ex) { TxtStatus.Text = $"保存失败: {ex.Message}"; }
        }
        else
        {
            await SaveAsInternal(stripMetadata: false);
        }
    }

    private async Task SaveEffectsOverwriteAsync()
    {
        var bytesToSave = await GetEffectsSaveBytesAsync();
        if (bytesToSave == null)
        {
            TxtStatus.Text = "没有可保存的图片";
            return;
        }

        if (!string.IsNullOrEmpty(_effectsImagePath) && File.Exists(_effectsImagePath))
        {
            try
            {
                await File.WriteAllBytesAsync(_effectsImagePath, bytesToSave);
                _effectsImageBytes = bytesToSave;
                _effectsPreviewImageBytes = bytesToSave;
                ReplaceEffectsSourceBitmap(bytesToSave);
                UpdateFileMenuState();
                TxtStatus.Text = $"已保存: {_effectsImagePath}";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"保存失败: {ex.Message}";
            }
        }
        else
        {
            await SaveAsInternal(stripMetadata: false);
        }
    }

    private void FitInspectPreviewToScreen()
    {
        if (InspectPreviewImage.Source is not BitmapImage bmp) return;
        double imgW = bmp.PixelWidth;
        double imgH = bmp.PixelHeight;
        if (imgW <= 0 || imgH <= 0) return;

        double viewW = InspectImageScroller.ViewportWidth;
        double viewH = InspectImageScroller.ViewportHeight;
        if (viewW <= 0 || viewH <= 0) return;

        float zoom = (float)Math.Min(viewW / imgW, viewH / imgH);
        zoom = Math.Min(zoom, 1.0f);
        InspectImageScroller.ChangeView(0, 0, zoom);
    }

    private static readonly string QualityTagBlock = "rating:general, best quality, very aesthetic, absurdres";

    private void OnSendInspectToInpaint(object sender, RoutedEventArgs e)
    {
        if (_inspectImageBytes == null) return;

        var savedPos = _genPositivePrompt;
        var savedNeg = _genNegativePrompt;
        var savedStyle = _genStylePrompt;

        if (_inspectMetadata != null)
        {
            _genPositivePrompt = _inspectMetadata.PositivePrompt;
            _genNegativePrompt = _inspectMetadata.NegativePrompt;
            _genStylePrompt = "";
        }

        SendImageToInpaint(_inspectImageBytes);

        _genPositivePrompt = savedPos;
        _genNegativePrompt = savedNeg;
        _genStylePrompt = savedStyle;
    }

    private async void OnSendMetadataToGen(object sender, RoutedEventArgs e)
    {
        if (_inspectPrimaryAction == InspectPrimaryAction.InferTags)
        {
            await RunInspectReverseTagAsync();
            return;
        }

        if (_inspectMetadata == null) return;
        var meta = _inspectMetadata;
        bool maxMode = _settings.Settings.MaxMode;
        var skipped = new List<string>();
        var notes = new List<string>();

        string positivePrompt = meta.PositivePrompt;
        string negativePrompt = meta.NegativePrompt;

        if (meta.IsSdFormat)
        {
            positivePrompt = ImageMetadataService.ConvertSdPromptToNai(positivePrompt);
            negativePrompt = ImageMetadataService.ConvertSdPromptToNai(negativePrompt);
            notes.Add("已转换 SD 格式提示词");
        }

        bool strippedQuality = false;
        if (positivePrompt.Contains(QualityTagBlock))
        {
            positivePrompt = positivePrompt.Replace(QualityTagBlock, "");
            positivePrompt = System.Text.RegularExpressions.Regex.Replace(positivePrompt, @",\s*,", ",");
            positivePrompt = positivePrompt.Trim(' ', ',');
            strippedQuality = true;
        }

        _genPositivePrompt = positivePrompt;
        _genNegativePrompt = negativePrompt;
        _genStylePrompt = "";

        if (meta.IsModelInference)
        {
            _genCharacters.Clear();
            ClearReferenceFeatures();
            SwitchMode(AppMode.ImageGeneration);
            RefreshCharacterPanel();
            LoadPromptFromBuffer();
            UpdateSplitVisibility();
            UpdateSizeWarningVisuals();
            if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();
            TxtStatus.Text = "已将模型反推结果发送到生图模式";
            return;
        }

        var p = _settings.Settings.GenParameters;

        if (strippedQuality)
        {
            p.QualityToggle = true;
            if (IsAdvancedWindowOpen) _advCboQuality.SelectedIndex = 0;
        }

        if (meta.Steps > 0)
        {
            if (!maxMode && meta.Steps > 28)
                skipped.Add($"步数 ({meta.Steps}>28)");
            else
                p.Steps = meta.Steps;
        }
        if (meta.Seed > 0 && meta.Seed <= int.MaxValue) p.Seed = (int)meta.Seed;
        if (meta.Scale > 0) p.Scale = meta.Scale;
        if (!meta.IsSdFormat) p.CfgRescale = meta.CfgRescale;
        if (!string.IsNullOrEmpty(meta.Sampler)) p.Sampler = meta.Sampler;
        if (!string.IsNullOrEmpty(meta.NoiseSchedule)) p.Schedule = meta.NoiseSchedule;
        if (!meta.IsSdFormat) p.Variety = meta.SmDyn || meta.Sm;

        if (meta.Width > 0 && meta.Height > 0)
        {
            if (!maxMode && (long)meta.Width * meta.Height > 1024L * 1024)
                skipped.Add($"尺寸 ({meta.Width}×{meta.Height} 超过 1024²)");
            else
            {
                _customWidth = meta.Width;
                _customHeight = meta.Height;
            }
        }

        if (meta.CharacterPrompts.Count > 0)
            SetGenCharactersFromMetadata(meta);
        else
            _genCharacters.Clear();
        ApplyReferenceDataFromMetadata(meta);

        SwitchMode(AppMode.ImageGeneration);
        RefreshCharacterPanel();

        NbMaxWidth.Value = _customWidth;
        NbMaxHeight.Value = _customHeight;
        NbSeed.Value = p.Seed;
        ChkVariety.IsChecked = p.Variety;
        if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();

        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        UpdateSizeWarningVisuals();

        if (strippedQuality) notes.Add("已提取质量词并启用「添加质量词」");
        if (skipped.Count > 0) notes.Add($"已跳过不兼容项: {string.Join(", ", skipped)}");
        if (meta.CharacterPrompts.Count > 0) notes.Add($"已导入 {meta.CharacterPrompts.Count} 个角色");
        AppendReferenceImportNotes(meta, notes);

        TxtStatus.Text = notes.Count > 0
            ? $"已发送生成参数（{string.Join("; ", notes)}）"
            : "已将生成参数发送到生图模式";
    }

    private void OnInspectDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void OnInspectDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file &&
                (file.FileType.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".webp", StringComparison.OrdinalIgnoreCase)))
            {
                await LoadInspectImageAsync(file.Path);
                return;
            }
        }
        TxtStatus.Text = "不支持的文件格式，请拖入 PNG/JPG/WebP 图片";
    }

    private void OnEffectsDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void OnEffectsDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file &&
                (file.FileType.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".webp", StringComparison.OrdinalIgnoreCase)))
            {
                await LoadEffectsImageAsync(file.Path);
                return;
            }
        }
        TxtStatus.Text = "不支持的文件格式，请拖入 PNG/JPG/WebP/BMP 图片";
    }

    private void OnEffectsOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var effect = GetSelectedEffect();
        if (effect == null || !IsRegionEffect(effect.Type) || EffectsPreviewImage.Source is not BitmapImage bmp)
            return;

        var pos = e.GetCurrentPoint(EffectsOverlayCanvas).Position;
        GetEffectRegionValues(effect, out double centerX, out double centerY, out double widthPct, out double heightPct);
        GetEffectRect(bmp.PixelWidth, bmp.PixelHeight, centerX, centerY, widthPct, heightPct,
            out int left, out int top, out int right, out int bottom);

        bool inResize = Math.Abs(pos.X - right) <= 12 && Math.Abs(pos.Y - bottom) <= 12;
        bool inRect = pos.X >= left && pos.X <= right && pos.Y >= top && pos.Y <= bottom;
        if (!inResize && !inRect)
        {
            OnPreviewDragStart(sender, e);
            return;
        }

        PushEffectsUndoState();
        _effectsRegionDragging = inRect && !inResize;
        _effectsRegionResizing = inResize;
        _effectsRegionDragStart = pos;
        _effectsRegionStartCenterX = centerX;
        _effectsRegionStartCenterY = centerY;
        _effectsRegionStartWidth = widthPct;
        _effectsRegionStartHeight = heightPct;
        EffectsOverlayCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnEffectsOverlayPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_effectsRegionDragging && !_effectsRegionResizing)
        {
            OnPreviewDragMove(sender, e);
            return;
        }
        if (EffectsPreviewImage.Source is not BitmapImage bmp) return;

        var effect = GetSelectedEffect();
        if (effect == null) return;

        var pos = e.GetCurrentPoint(EffectsOverlayCanvas).Position;
        double dxPct = (pos.X - _effectsRegionDragStart.X) / Math.Max(1, bmp.PixelWidth) * 100.0;
        double dyPct = (pos.Y - _effectsRegionDragStart.Y) / Math.Max(1, bmp.PixelHeight) * 100.0;

        double centerX = _effectsRegionStartCenterX;
        double centerY = _effectsRegionStartCenterY;
        double widthPct = _effectsRegionStartWidth;
        double heightPct = _effectsRegionStartHeight;

        if (_effectsRegionDragging)
        {
            centerX = Math.Clamp(_effectsRegionStartCenterX + dxPct, 0, 100);
            centerY = Math.Clamp(_effectsRegionStartCenterY + dyPct, 0, 100);
        }
        else if (_effectsRegionResizing)
        {
            widthPct = Math.Clamp(_effectsRegionStartWidth + dxPct * 2.0, 1, 100);
            heightPct = Math.Clamp(_effectsRegionStartHeight + dyPct * 2.0, 1, 100);
        }

        SetEffectRegionValues(effect, centerX, centerY, widthPct, heightPct);
        RefreshEffectsOverlay();
        QueueEffectsPreviewRefresh();
        UpdateFileMenuState();
        e.Handled = true;
    }

    private void OnEffectsOverlayPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_effectsRegionDragging && !_effectsRegionResizing)
        {
            OnPreviewDragEnd(sender, e);
            return;
        }
        _effectsRegionDragging = false;
        _effectsRegionResizing = false;
        EffectsOverlayCanvas.ReleasePointerCapture(e.Pointer);
        RefreshEffectsPanel();
        RefreshEffectsOverlay();
        QueueEffectsPreviewRefresh(immediate: true);
        e.Handled = true;
    }

    private async void OnHistorySendToInspect(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            SwitchMode(AppMode.Inspect);
            await LoadInspectImageAsync(filePath);
        }
    }

    private async void OnHistorySendToEffects(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            SwitchMode(AppMode.Effects);
            await LoadEffectsImageAsync(filePath);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  历史记录
    // ═══════════════════════════════════════════════════════════

    private async void LoadHistoryAsync()
    {
        await Task.Run(() =>
        {
            lock (_historyFiles)
            {
                _historyByDate.Clear();
                _historyAvailableDates.Clear();
                _historyAvailableDateSet.Clear();
                _historyFiles.Clear();
            }

            if (!Directory.Exists(OutputBaseDir)) return;

            var dateDirs = Directory.GetDirectories(OutputBaseDir)
                .Select(d => new DirectoryInfo(d))
                .Where(d => DateTime.TryParseExact(d.Name, "yyyy-MM-dd", null,
                    DateTimeStyles.None, out _))
                .OrderByDescending(d => d.Name)
                .ToList();

            foreach (var dir in dateDirs)
            {
                var files = Directory.GetFiles(dir.FullName, "*.png")
                    .OrderByDescending(f => new FileInfo(f).CreationTime)
                    .ToList();
                if (files.Count > 0)
                {
                    lock (_historyFiles)
                    {
                        _historyByDate[dir.Name] = files;
                        _historyAvailableDates.Add(dir.Name);
                        _historyAvailableDateSet.Add(dir.Name);
                    }
                }
            }
        });

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_historyAvailableDates.Count > 0)
            {
                _selectedHistoryDate = _historyAvailableDates[0];
                BuildHistoryFileList();
                _historyLoadedCount = Math.Min(HistoryPageSize, _historyFiles.Count);
                RefreshHistoryPanel();

                var date = DateTimeOffset.ParseExact(_selectedHistoryDate, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
                HistoryDatePicker.Date = date;
            }
            else
            {
                RefreshHistoryPanel();
            }
        });
    }

    private void AddHistoryItem(string filePath)
    {
        var dateStr = GetDateFromFilePath(filePath);
        if (dateStr == null) return;

        if (!_historyByDate.ContainsKey(dateStr))
        {
            _historyByDate[dateStr] = new List<string>();
            int insertIdx = _historyAvailableDates.FindIndex(
                d => string.Compare(d, dateStr, StringComparison.Ordinal) < 0);
            if (insertIdx < 0) insertIdx = _historyAvailableDates.Count;
            _historyAvailableDates.Insert(insertIdx, dateStr);
            _historyAvailableDateSet.Add(dateStr);
        }
        _historyByDate[dateStr].Insert(0, filePath);

        if (_selectedHistoryDate == null)
        {
            _selectedHistoryDate = dateStr;
            var date = DateTimeOffset.ParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            HistoryDatePicker.Date = date;
        }

        BuildHistoryFileList();
        if (_selectedHistoryDate != null &&
            string.Compare(dateStr, _selectedHistoryDate, StringComparison.Ordinal) <= 0)
        {
            _historyLoadedCount = Math.Min(
                Math.Max(_historyLoadedCount + 1, HistoryPageSize), _historyFiles.Count);
            RefreshHistoryPanel();
        }
    }

    private void BuildHistoryFileList()
    {
        _historyFiles.Clear();
        if (_selectedHistoryDate == null || _historyAvailableDates.Count == 0) return;

        int startIdx = _historyAvailableDates.IndexOf(_selectedHistoryDate);
        if (startIdx < 0) startIdx = 0;

        for (int i = startIdx; i < _historyAvailableDates.Count; i++)
        {
            if (_historyByDate.TryGetValue(_historyAvailableDates[i], out var files))
                _historyFiles.AddRange(files);
        }
    }

    private static string? GetDateFromFilePath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        return dir == null ? null : new DirectoryInfo(dir).Name;
    }

    private static UIElement CreateDateSeparator(string dateStr)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var line1 = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Opacity = 0.4,
        };
        Grid.SetColumn(line1, 0);

        var text = new TextBlock
        {
            Text = dateStr,
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        };
        Grid.SetColumn(text, 1);

        var line2 = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Opacity = 0.4,
        };
        Grid.SetColumn(line2, 2);

        grid.Children.Add(line1);
        grid.Children.Add(text);
        grid.Children.Add(line2);
        return grid;
    }

    private void OnHistoryDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!args.NewDate.HasValue) return;
        var dateStr = args.NewDate.Value.ToString("yyyy-MM-dd");
        if (dateStr == _selectedHistoryDate) return;
        if (!IsHistoryDateSelectable(dateStr)) return;

        _selectedHistoryDate = dateStr;
        BuildHistoryFileList();
        _historyLoadedCount = Math.Min(HistoryPageSize, _historyFiles.Count);
        RefreshHistoryPanel();
        HistoryScroller.ChangeView(null, 0, null);
    }

    private static string GetTodayHistoryDateString() => DateTime.Now.ToString("yyyy-MM-dd");

    private bool IsHistoryDateSelectable(string dateStr) =>
        _historyAvailableDateSet.Contains(dateStr) ||
        string.Equals(dateStr, GetTodayHistoryDateString(), StringComparison.Ordinal);

    private void OnHistoryCalendarDayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        if (args.Item == null) return;
        var dateStr = args.Item.Date.ToString("yyyy-MM-dd");
        bool hasHistory = _historyAvailableDateSet.Contains(dateStr);
        bool isToday = string.Equals(dateStr, GetTodayHistoryDateString(), StringComparison.Ordinal);
        bool isSelectable = hasHistory || isToday;
        args.Item.IsBlackout = false;
        args.Item.IsEnabled = isSelectable;
        args.Item.Opacity = isSelectable ? 1.0 : 0.4;
    }

    private void RefreshHistoryPanel()
    {
        HistoryPanel.Children.Clear();
        _historyLoadedCount = Math.Min(_historyLoadedCount, _historyFiles.Count);

        if (_historyFiles.Count == 0)
        {
            HistoryPanel.Children.Add(new TextBlock
            {
                Text = "暂无历史记录",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0),
            });
            return;
        }

        string? lastDate = null;
        int count = Math.Min(_historyLoadedCount, _historyFiles.Count);
        for (int i = 0; i < count; i++)
        {
            var filePath = _historyFiles[i];
            var fileDate = GetDateFromFilePath(filePath);
            if (fileDate != lastDate)
            {
                if (lastDate != null)
                    HistoryPanel.Children.Add(CreateDateSeparator(fileDate ?? "未知"));
                lastDate = fileDate;
            }
            var border = CreateHistoryThumbnail(filePath);
            HistoryPanel.Children.Add(border);
        }
    }

    private async void OnHistoryScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;
        if (_historyLoadingMore) return;
        if (_historyLoadedCount >= _historyFiles.Count) return;
        if (scroller.ScrollableHeight <= 0) return;

        if (scroller.VerticalOffset < scroller.ScrollableHeight - 160)
            return;

        _historyLoadingMore = true;
        try
        {
            await Task.Yield();
            int target = Math.Min(_historyLoadedCount + HistoryPageSize, _historyFiles.Count);
            string? lastDate = _historyLoadedCount > 0
                ? GetDateFromFilePath(_historyFiles[_historyLoadedCount - 1])
                : null;

            for (int i = _historyLoadedCount; i < target; i++)
            {
                var filePath = _historyFiles[i];
                var fileDate = GetDateFromFilePath(filePath);
                if (fileDate != lastDate)
                {
                    HistoryPanel.Children.Add(CreateDateSeparator(fileDate ?? "未知"));
                    lastDate = fileDate;
                }
                var border = CreateHistoryThumbnail(filePath);
                HistoryPanel.Children.Add(border);
            }
            _historyLoadedCount = target;
        }
        finally
        {
            _historyLoadingMore = false;
        }
    }

    private Border CreateHistoryThumbnail(string filePath)
    {
        var img = new Image
        {
            Height = 140,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _ = LoadThumbnailAsync(img, filePath);

        var border = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            Tag = filePath,
        };
        border.Child = img;
        border.PointerPressed += OnHistoryItemClick;

        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem
        {
            Text = "复制", Tag = filePath,
            Icon = new SymbolIcon(Symbol.Copy),
        };
        copyItem.Click += OnHistoryCopyImage;
        menu.Items.Add(copyItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        var readerItem = new MenuFlyoutItem
        {
            Text = "发送到检视", Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEE6F" },
        };
        readerItem.Click += OnHistorySendToInspect;
        menu.Items.Add(readerItem);
        var postItem = new MenuFlyoutItem
        {
            Text = "发送到效果", Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" },
        };
        postItem.Click += OnHistorySendToEffects;
        menu.Items.Add(postItem);
        var sendItem = new MenuFlyoutItem
        {
            Text = "发送到重绘", Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" },
        };
        sendItem.Click += OnHistorySendToInpaint;
        menu.Items.Add(sendItem);
        var upscaleItem = new MenuFlyoutItem
        {
            Text = "发送到超分", Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9" },
        };
        upscaleItem.Click += OnHistorySendToUpscale;
        menu.Items.Add(upscaleItem);
        var openFolderItem = new MenuFlyoutItem
        {
            Text = "打开所在文件夹", Tag = filePath,
            Icon = new SymbolIcon(Symbol.OpenLocal),
        };
        openFolderItem.Click += OnHistoryOpenFolder;
        menu.Items.Add(openFolderItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        var useParamsItem = new MenuFlyoutItem
        {
            Text = "使用参数", Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B6" },
        };
        useParamsItem.Click += OnHistoryUseParams;
        menu.Items.Add(useParamsItem);
        var useParamsNoSeedItem = new MenuFlyoutItem
        {
            Text = "使用参数（不包含种子）", Tag = filePath,
            Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B5" },
        };
        useParamsNoSeedItem.Click += OnHistoryUseParamsNoSeed;
        menu.Items.Add(useParamsNoSeedItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除", Tag = filePath,
            Icon = new SymbolIcon(Symbol.Delete),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)),
        };
        deleteItem.Click += OnHistoryDelete;
        menu.Items.Add(deleteItem);
        foreach (var item in menu.Items)
            ApplyMenuTypography(item);
        border.ContextFlyout = menu;

        return border;
    }

    private static async Task LoadThumbnailAsync(Image img, string filePath)
    {
        try
        {
            var bitmapImage = new BitmapImage
            {
                DecodePixelHeight = 140,
            };
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var stream = await file.OpenReadAsync();
            await bitmapImage.SetSourceAsync(stream);
            img.Source = bitmapImage;
        }
        catch { }
    }

    private void OnHistoryItemClick(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string filePath)
        {
            var pt = e.GetCurrentPoint(border);
            if (pt.Properties.IsLeftButtonPressed)
            {
                var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    Windows.System.VirtualKey.Control);
                if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                    _ = ApplyHistoryParamsNoSeedAsync(filePath);
                else
                _ = ShowHistoryImageAsync(filePath);
                e.Handled = true;
            }
        }
    }

    private async Task ApplyHistoryParamsNoSeedAsync(string filePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            await ApplyDroppedImageMetadata(bytes, Path.GetFileName(filePath), skipSeed: true);
        }
        catch (Exception ex) { TxtStatus.Text = $"读取失败: {ex.Message}"; }
    }

    private async Task ShowHistoryImageAsync(string filePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            _currentGenImageBytes = bytes;
            _currentGenImagePath = filePath;
            GenResultBar.Visibility = Visibility.Collapsed;
            await ShowGenPreviewAsync(bytes);
            UpdateDynamicMenuStates();
        }
        catch (Exception ex) { TxtStatus.Text = $"加载失败: {ex.Message}"; }
    }

    private void OnHistorySendToInpaint(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            _ = SendFileToInpaintAsync(filePath);
        }
    }

    private async Task SendFileToInpaintAsync(string filePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            SendImageToInpaint(bytes);
        }
        catch (Exception ex) { TxtStatus.Text = $"发送到重绘失败: {ex.Message}"; }
    }

    private void OnHistoryOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath && File.Exists(filePath))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
    }

    private async void OnHistoryUseParams(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                await ApplyDroppedImageMetadata(bytes, Path.GetFileName(filePath));
            }
            catch (Exception ex) { TxtStatus.Text = $"读取失败: {ex.Message}"; }
        }
    }

    private async void OnHistoryUseParamsNoSeed(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                await ApplyDroppedImageMetadata(bytes, Path.GetFileName(filePath), skipSeed: true);
            }
            catch (Exception ex) { TxtStatus.Text = $"读取失败: {ex.Message}"; }
        }
    }

    private async void OnHistoryDelete(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                int idx = _historyFiles.IndexOf(filePath);
                if (File.Exists(filePath)) File.Delete(filePath);
                var delDateStr = GetDateFromFilePath(filePath);
                if (delDateStr != null && _historyByDate.ContainsKey(delDateStr))
                {
                    _historyByDate[delDateStr].Remove(filePath);
                    if (_historyByDate[delDateStr].Count == 0)
                    {
                        _historyByDate.Remove(delDateStr);
                        _historyAvailableDates.Remove(delDateStr);
                        _historyAvailableDateSet.Remove(delDateStr);
                    }
                }
                _historyFiles.Remove(filePath);
                _historyLoadedCount = Math.Min(_historyLoadedCount, _historyFiles.Count);
                RefreshHistoryPanel();

                if (_currentGenImagePath == filePath)
                {
                    string? nextPath = null;
                    if (idx >= 0 && _historyFiles.Count > 0)
                        nextPath = _historyFiles[Math.Min(idx, _historyFiles.Count - 1)];

                    if (nextPath != null)
                    {
                        await ShowHistoryImageAsync(nextPath);
                        TxtStatus.Text = "已删除，已切换到相邻图片";
                    }
                    else
                    {
                        _currentGenImageBytes = null;
                        _currentGenImagePath = null;
                        GenPreviewImage.Source = null;
                        GenPlaceholder.Visibility = Visibility.Visible;
                        GenResultBar.Visibility = Visibility.Collapsed;
                        UpdateDynamicMenuStates();
                TxtStatus.Text = "已删除";
                    }
                }
                else
                {
                    TxtStatus.Text = "已删除";
                }
            }
            catch (Exception ex) { TxtStatus.Text = $"删除失败: {ex.Message}"; }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  超分工作区
    // ═══════════════════════════════════════════════════════════

    private List<UpscaleService.UpscaleModelInfo> _upscaleModelInfos = new();

    private void PopulateUpscaleModelList()
    {
        CboUpscaleModel.Items.Clear();
        var modelsDir = Path.Combine(ModelsDir, "upscaler");
        _upscaleModelInfos = UpscaleService.ScanModels(modelsDir);

        if (_upscaleModelInfos.Count == 0)
        {
            CboUpscaleModel.Items.Add(CreateTextComboBoxItem("（未找到模型）"));
            CboUpscaleModel.SelectedIndex = 0;
            CboUpscaleModel.IsEnabled = false;
            BtnStartUpscale.IsEnabled = false;
            TxtStatus.Text = $"请将超分模型 (.onnx) 放入 {modelsDir}";
            return;
        }

        CboUpscaleModel.IsEnabled = true;
        foreach (var m in _upscaleModelInfos)
            CboUpscaleModel.Items.Add(CreateTextComboBoxItem(m.DisplayName));

        CboUpscaleModel.SelectedIndex = 0;
        ApplyMenuTypography(CboUpscaleModel);
    }

    private void OnUpscaleModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtUpscaleInputRes == null) return;
        if (CboUpscaleModel.SelectedIndex < 0 || _upscaleModelInfos.Count == 0) return;
        UpdateUpscaleResolutionDisplay();
    }

    private void OnUpscaleScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtUpscaleInputRes == null) return;
        UpdateUpscaleResolutionDisplay();
    }

    private int GetSelectedUpscaleScale()
    {
        if (CboUpscaleScale.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && int.TryParse(tag, out int scale))
            return scale;
        return 4;
    }

    private void UpdateUpscaleResolutionDisplay()
    {
        if (_upscaleSourceWidth <= 0 || _upscaleSourceHeight <= 0)
        {
            TxtUpscaleInputRes.Text = "—";
            TxtUpscaleOutputRes.Text = "—";
            return;
        }

        TxtUpscaleInputRes.Text = $"{_upscaleSourceWidth} × {_upscaleSourceHeight}";
        int scale = GetSelectedUpscaleScale();
        int outW = _upscaleSourceWidth * scale;
        int outH = _upscaleSourceHeight * scale;
        TxtUpscaleOutputRes.Text = $"{outW} × {outH}";
    }

    private void OnUpscaleDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void OnUpscaleDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file &&
                (file.FileType.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                 file.FileType.Equals(".webp", StringComparison.OrdinalIgnoreCase)))
            {
                await LoadUpscaleImageAsync(file.Path);
                return;
            }
        }
        TxtStatus.Text = "不支持的文件格式，请拖入 PNG/JPG/WebP/BMP 图片";
    }

    private async Task LoadUpscaleImageAsync(string filePath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            _upscaleInputImageBytes = bytes;

            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap == null)
            {
                TxtStatus.Text = "无法解码图片";
                return;
            }

            _upscaleSourceWidth = bitmap.Width;
            _upscaleSourceHeight = bitmap.Height;

            await ShowUpscalePreviewAsync(bytes);
            UpdateUpscaleResolutionDisplay();
            BtnStartUpscale.IsEnabled = _upscaleModelInfos.Count > 0;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FitUpscalePreviewToScreen());
            TxtStatus.Text = $"已加载: {Path.GetFileName(filePath)} ({_upscaleSourceWidth}×{_upscaleSourceHeight})";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"加载失败: {ex.Message}";
        }
    }

    private async Task ShowUpscalePreviewAsync(byte[] bytes)
    {
        var bitmapImage = new BitmapImage();
        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(ms);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        writer.DetachStream();
        ms.Seek(0);
        await bitmapImage.SetSourceAsync(ms);
        UpscalePreviewImage.Source = bitmapImage;
        UpscalePlaceholder.Visibility = Visibility.Collapsed;
    }

    private void FitUpscalePreviewToScreen()
    {
        if (UpscalePreviewImage.Source is not BitmapImage bmp) return;
        double imgW = bmp.PixelWidth;
        double imgH = bmp.PixelHeight;
        if (imgW <= 0 || imgH <= 0) return;

        double viewW = UpscaleImageScroller.ViewportWidth;
        double viewH = UpscaleImageScroller.ViewportHeight;
        if (viewW <= 0 || viewH <= 0) return;

        float zoom = (float)Math.Min(viewW / imgW, viewH / imgH);
        zoom = Math.Min(zoom, 1.0f);
        UpscaleImageScroller.ChangeView(0, 0, zoom);
    }

    private async void OnStartUpscale(object sender, RoutedEventArgs e)
    {
        if (_upscaleInputImageBytes == null || _upscaleInputImageBytes.Length == 0)
        {
            TxtStatus.Text = "请先拖入图片";
            return;
        }

        if (_upscaleRunning) return;

        int modelIdx = CboUpscaleModel.SelectedIndex;
        if (modelIdx < 0 || modelIdx >= _upscaleModelInfos.Count) return;

        var modelInfo = _upscaleModelInfos[modelIdx];
        _upscaleRunning = true;
        BtnStartUpscale.IsEnabled = false;
        SetUpscaleButtonText("超分中…");
        UpscaleProgressBar.Visibility = Visibility.Visible;
        TxtStatus.Text = "正在加载超分模型…";

        try
        {
            _upscaleService ??= new UpscaleService();
            var inputBytes = _upscaleInputImageBytes;
            bool preferCpu = CboUpscaleDevice.SelectedIndex == 1;

            DebugLog($"[Upscale] Start | Model={modelInfo.DisplayName} | Device={(preferCpu ? "CPU" : "Prefer GPU")} | Input={_upscaleSourceWidth}x{_upscaleSourceHeight}");

            await Task.Run(() => _upscaleService.LoadModel(modelInfo.FilePath, preferCpu));
            DebugLog($"[Upscale] Model loaded | Provider={_upscaleService.ExecutionProvider} | Scale={_upscaleService.ModelScale}x");
            TxtStatus.Text = "正在超分…";

            var progress = new Progress<double>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpscaleProgressBar.IsIndeterminate = false;
                    UpscaleProgressBar.Value = p * 100;
                });
            });

            var resultBytes = await _upscaleService.UpscaleAsync(inputBytes, progress);

            using var resultBitmap = SKBitmap.Decode(resultBytes);
            if (resultBitmap != null)
            {
                _upscaleSourceWidth = resultBitmap.Width;
                _upscaleSourceHeight = resultBitmap.Height;
                _upscaleInputImageBytes = resultBytes;
            }

            await ShowUpscalePreviewAsync(resultBytes);
            UpdateUpscaleResolutionDisplay();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FitUpscalePreviewToScreen());
            DebugLog($"[Upscale] Completed | Output={_upscaleSourceWidth}x{_upscaleSourceHeight} | Provider={_upscaleService.ExecutionProvider}");
            TxtStatus.Text = $"超分完成 ({_upscaleSourceWidth}×{_upscaleSourceHeight}) | {_upscaleService.ExecutionProvider}";

            await PromptSaveUpscaleResultAsync(resultBytes);
        }
        catch (Exception ex)
        {
            DebugLog($"[Upscale] Failed: {ex}");
            TxtStatus.Text = $"超分失败: {ex.Message}";
        }
        finally
        {
            _upscaleRunning = false;
            BtnStartUpscale.IsEnabled = true;
            SetUpscaleButtonText("开始超分");
            UpscaleProgressBar.Visibility = Visibility.Collapsed;
            UpscaleProgressBar.IsIndeterminate = true;
            UpscaleProgressBar.Value = 0;
        }
    }

    private void SetUpscaleButtonText(string text)
    {
        BtnStartUpscale.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9", FontSize = 16 },
                new TextBlock { Text = text },
            }
        };
    }

    private async Task PromptSaveUpscaleResultAsync(byte[] resultBytes)
    {
        var savePicker = new FileSavePicker();
        savePicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        savePicker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
        savePicker.SuggestedFileName = $"upscaled_{DateTime.Now:yyyyMMdd_HHmmss}";

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(savePicker, hwnd);

        var file = await savePicker.PickSaveFileAsync();
        if (file != null)
        {
            await File.WriteAllBytesAsync(file.Path, resultBytes);
            TxtStatus.Text = $"已保存: {file.Path}";
        }
    }

    private async Task SendBytesToUpscaleAsync(byte[] bytes, string? sourcePath = null)
    {
        SwitchMode(AppMode.Upscale);

        _upscaleInputImageBytes = bytes;
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap != null)
        {
            _upscaleSourceWidth = bitmap.Width;
            _upscaleSourceHeight = bitmap.Height;
        }

        await ShowUpscalePreviewAsync(bytes);
        UpdateUpscaleResolutionDisplay();
        BtnStartUpscale.IsEnabled = _upscaleModelInfos.Count > 0;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => FitUpscalePreviewToScreen());
        TxtStatus.Text = sourcePath != null
            ? $"已发送到超分: {Path.GetFileName(sourcePath)}"
            : "已发送到超分";
    }

    private async void OnSendToUpscaleFromGen(object sender, RoutedEventArgs e)
    {
        if (_currentGenImageBytes == null)
        {
            TxtStatus.Text = "没有生成结果可发送";
            return;
        }
        GenResultBar.Visibility = Visibility.Collapsed;
        await SendBytesToUpscaleAsync(_currentGenImageBytes, _currentGenImagePath);
    }

    private async void OnSendToUpscaleFromInpaint(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[]? bytesToSend;
            if (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null)
            {
                await ApplyInpaintResultAsync();
                bytesToSend = _lastGeneratedImageBytes ?? await CreateCurrentFullImageBytes();
            }
            else
            {
                bytesToSend = await CreateCurrentFullImageBytes();
            }

            if (bytesToSend == null || bytesToSend.Length == 0)
            {
                TxtStatus.Text = "没有图像可发送到超分";
                return;
            }

            await SendBytesToUpscaleAsync(bytesToSend, MaskCanvas.LoadedFilePath);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"发送到超分失败: {ex.Message}";
        }
    }

    private void OnSendToInpaintFromUpscale(object sender, RoutedEventArgs e)
    {
        if (_upscaleInputImageBytes == null)
        {
            TxtStatus.Text = "没有图像可发送";
            return;
        }
        SendImageToInpaint(_upscaleInputImageBytes);
    }

    private async void OnSendToEffectsFromUpscale(object sender, RoutedEventArgs e)
    {
        if (_upscaleInputImageBytes == null)
        {
            TxtStatus.Text = "没有图像可发送";
            return;
        }
        await SendBytesToEffectsAsync(_upscaleInputImageBytes);
    }

    private async void OnHistorySendToUpscale(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string filePath)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                await SendBytesToUpscaleAsync(bytes, filePath);
            }
            catch (Exception ex) { TxtStatus.Text = $"发送到超分失败: {ex.Message}"; }
        }
    }
}

public class AutoCompleteItem
{
    public string TagName { get; set; } = "";
    public string InsertText { get; set; } = "";
    public int Category { get; set; }
    public string CountText { get; set; } = "";
    public string AliasText { get; set; } = "";
    public Visibility AliasVisibility { get; set; } = Visibility.Collapsed;
    public SolidColorBrush CategoryBrush { get; set; } = new(Microsoft.UI.Colors.Gray);
}

public sealed record RandomStyleOptions(int TagCount, int MinCount, bool UseWeight);
