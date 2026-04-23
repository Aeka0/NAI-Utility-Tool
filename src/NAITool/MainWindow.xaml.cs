using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

public enum AppMode { ImageGeneration, I2I, Upscale, Effects, Inspect }
public enum I2IEditMode { Inpaint, Denoise }
public enum PromptWeightFormat { StableDiffusion, NaiClassic, NaiNumeric }
public enum PromptGeneratorOutputMode { BooruTags, BooruTagsWithNaturalLanguage, NaturalLanguage }
public enum SuperDropAction { GeneratePrompt, GenerateVibe, GeneratePrecise, I2IPrompt, I2IVibe, I2IPrecise, Upscale, Effects, Inspect }

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
    private const string MenuCommandSendToI2I = "send_to_i2i";
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
    private const string MenuCommandVibeManager = "vibe_manager";
    private const string MenuCommandWildcard = "wildcard";
    private const string MenuCommandPromptGenerator = "prompt_generator";
    private const string MenuCommandAutomation = "automation";
    private const string MenuCommandExpandMask = "expand_mask";
    private const string MenuCommandContractMask = "contract_mask";
    private const string MenuCommandAlignImage = "align_image";
    private const string MenuCommandPromptInference = "prompt_inference";
    private const string MenuCommandMaskOps = "mask_ops";
    private const string MenuCommandReloadImage = "reload_image";

    // ═══ 模式 ═══
    private AppMode _currentMode = AppMode.ImageGeneration;
    private I2IEditMode _i2iEditMode = I2IEditMode.Inpaint;
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
    private string _i2iPositivePrompt = "";
    private string _i2iNegativePrompt = "";
    private string _i2iStylePrompt = "";
    private bool _isPositiveTab = true;
    private bool _isSplitPrompt;
    private bool _promptBufferLoaded;
    private bool? _promptTabsUsingCompact;
    private string _promptTabLanguageCode = "";

    // ═══ 角色提示词（生图与重绘独立） ═══
    private readonly List<CharacterEntry> _genCharacters = new();
    private readonly List<CharacterEntry> _i2iCharacters = new();
    private const int MaxCharacters = 6;
    private readonly List<PromptShortcutEntry> _promptShortcuts = new();

    // ═══ 生成 ═══
    private CancellationTokenSource? _generateCts;
    private byte[]? _lastGeneratedImageBytes;
    private int _lastUsedSeed;
    private int _customWidth = 832;
    private int _customHeight = 1216;
    private bool _isUpdatingMaxSize;

    // ═══ 高级参数 Flyout ═══
    private Flyout? _advParamsFlyout;
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

    // ═══ 权重高亮 ═══
    private int _promptHighlightVer;
    private int _styleHighlightVer;

    // ═══ 自动生成 ═══
    private bool _autoGenRunning;
    private CancellationTokenSource? _autoGenCts;
    private int _autoGenRemaining;
    private bool _continuousGenRunning;
    private bool _generateRequestRunning;
    private CancellationTokenSource? _continuousGenCts;
    private int _continuousGenRemaining;
    private bool _continuousStopRequested;
    private int? _anlasBalance;
    private bool _isOpusSubscriber;
    private bool _hasActiveSubscription;
    private bool _anlasRefreshRunning;
    private bool _anlasInitialFetchDone;
    private bool _isWildcardDialogOpen;

    // ═══ 重绘预览工作流 ═══
    private CanvasBitmap? _pendingResultBitmap;
    private byte[]? _pendingResultBytes;
    private Dictionary<string, string>? _pendingResultTextChunks;
    private Dictionary<string, string>? _i2iImageTextChunks;

    // ═══ 生图结果 ═══
    private byte[]? _currentGenImageBytes;
    private string? _currentGenImagePath;
    private bool _genResultBarRequested;

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
    private string? _upscaleImagePath;
    private int _upscaleSourceWidth, _upscaleSourceHeight;
    private bool _upscaleRunning;
    private UpscaleService? _upscaleService;

    // ═══ 历史记录 ═══
    private readonly List<string> _historyFiles = new();
    private readonly Dictionary<string, List<string>> _historyByDate = new();
    private readonly List<string> _historyAvailableDates = new();
    private readonly HashSet<string> _historyAvailableDateSet = new();
    private readonly ObservableCollection<HistoryListItem> _historyListItems = [];
    private readonly List<HistoryListItem> _historyPendingItems = [];
    private readonly Dictionary<string, HistoryListItem> _historyFileItemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapImage> _historyThumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _historyThumbnailCacheLru = new();
    private readonly object _historyThumbnailCacheLock = new();
    private readonly object _historyThumbnailRequestLock = new();
    private readonly List<HistoryThumbnailQueueEntry> _historyThumbnailQueue = [];
    private readonly Dictionary<string, List<WeakReference<Image>>> _historyThumbnailWaiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _historyThumbnailQueuedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _historyThumbnailInFlightPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _historyThumbnailRevealPendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private ScrollViewer? _historyListScrollViewer;
    private string? _selectedHistoryDate;
    private const double HistoryThumbnailHeight = 140;
    private const int HistoryThumbnailCacheLimit = 96;
    private const int HistoryThumbnailMaxConcurrentLoads = 2;
    private int _historyItemsVersion;
    private int _historyPendingSequence;
    private int _historyThumbnailActiveLoads;
    private int _historyThumbnailRequestSequence;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _historyDateRefreshTimer;
    private string _historyTodayDateMarker = DateTime.Now.ToString("yyyy-MM-dd");
    private bool _superDropOverlayVisible;
    private bool _superDropOverlayOpening;
    private bool _superDropWindowRaisedTopmost;
    private bool _superDropWindowWasTopmost;
    private int _superDropDragVersion;
    private int _superDropBackdropVersion;

    private sealed class HistoryLoadSnapshot
    {
        public Dictionary<string, List<string>> ByDate { get; init; } = new();
        public List<string> AvailableDates { get; init; } = [];
        public HashSet<string> AvailableDateSet { get; init; } = [];
    }

    private sealed class HistoryThumbnailQueueEntry
    {
        public required string FilePath { get; init; }
        public required int ItemsVersion { get; init; }
        public required int Sequence { get; init; }
        public int Priority { get; set; }
    }

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
        "nai-diffusion-4-curated-preview",
        "nai-diffusion-3",
    ];
    private static readonly string[] I2IModels =
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
    private OnnxPerformanceSettings OnnxPerformance => _settings.Settings.OnnxPerformance;
    private bool PreferCpuForOnnxInference => OnnxPerformance.PreferCpu;
    private bool ShouldUnloadOnnxModelsAfterInference => OnnxPerformance.UnloadModelAfterInference;

    // ═══ 自动补全 ═══
    private readonly TagCompleteService _tagService = new();
    private int _acVersion;
    private PromptTextBox? _acTargetTextBox;
    private bool _acInserting;

    // ═══════════════════════════════════════════════════════════
    //  构造
    // ═══════════════════════════════════════════════════════════

    public MainWindow()
    {
        System.Diagnostics.Debug.WriteLine($"[Startup] App root: {AppRootDir}");
        System.Diagnostics.Debug.WriteLine($"[Startup] BaseDirectory: {AppContext.BaseDirectory}");

        _settings.Load();
        try
        {
            bool persistDetectedLanguage = string.IsNullOrWhiteSpace(_settings.Settings.LanguageCode);
            _settings.Settings.LanguageCode = _loc.Initialize(_settings.Settings.LanguageCode);
            if (persistDetectedLanguage)
                _settings.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Localization] MainWindow initialization failed: {ex.Message}");
            _settings.Settings.LanguageCode = LocalizationService.NormalizeLanguageCode(_settings.Settings.LanguageCode);
        }

        _loc.LanguageChanged += OnAppLanguageChanged;

        this.InitializeComponent();
        HistoryListView.ItemsSource = _historyListItems;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new DesktopAcrylicBackdrop();

        if (AppWindow != null)
        {
            AppWindow.Resize(new SizeInt32(1400, 900));
            AppWindow.SetIcon("NAIT.ico");
        }
        SetupCloseConfirmation();
        this.Activated += (_, _) => ApplyWindowChrome(this, IsDarkTheme(), null, null);
        Closed += (_, _) =>
        {
            CloseAdvancedParamsWindow();
            _historyDateRefreshTimer?.Stop();
            if (IsPromptMode(_currentMode))
            {
                SaveCurrentPromptToBuffer();
                SyncUIToParams();
            }
            SyncRememberedPromptAndParameterState();
            _settings.Save();
            _reverseTaggerService.Dispose();
        };

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
        ApplyTransparency(_settings.Settings.AppearanceTransparency);
        SyncTransparencyMenuChecks(_settings.Settings.AppearanceTransparency);

        MaskCanvas.ZoomChanged += z => TxtZoomInfo.Text = Lf("status.zoom", z * 100);
        MaskCanvas.UseAssetProtectionCanvasSizing = IsAssetProtectionSizeLimitEnabled();
        MaskCanvas.ContentChanged += () =>
        {
            if (_currentMode == AppMode.I2I &&
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
        MaskCanvas.ImageFileLoaded += OnMaskCanvasImageFileLoaded;
        ApplyDragDropModeSetting();

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
        SetupHistoryDateRefreshTimer();
        RefreshHistoryDatePickerRange();
        LoadHistoryAsync();
    }

    private string L(string key) => _loc.GetString(key);

    private string Lf(string key, params object?[] args) => _loc.Format(key, args);
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
