using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Services;
using SkiaSharp;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NAITool;

public sealed partial class MainWindow
{
    private const int MaxVibeTransfers = 16;
    private const int MaxPreciseReferences = 16;

    private readonly List<VibeTransferEntry> _genVibeTransfers = new();
    private readonly List<PreciseReferenceEntry> _genPreciseReferences = new();

    private sealed class VibeTransferEntry
    {
        public string FileName { get; set; } = "";
        public string ImageBase64 { get; set; } = "";
        public double Strength { get; set; } = 0.6;
        public double InformationExtracted { get; set; } = 1.0;
        public bool IsEncodedFile { get; set; }
        public bool IsCollapsed { get; set; }
        /// <summary>原始图片 SHA256 前缀（仅原始图片条目）</summary>
        public string? OriginalImageHash { get; set; }
        /// <summary>原始图片 Base64（缓存失效时回退）</summary>
        public string? OriginalImageBase64 { get; set; }
        /// <summary>当前使用的是本地缓存编码数据</summary>
        public bool IsCachedEncoding { get; set; }
    }

    private sealed class PreciseReferenceEntry
    {
        public string FileName { get; set; } = "";
        public string ImageBase64 { get; set; } = "";
        public PreciseReferenceType ReferenceType { get; set; } = PreciseReferenceType.CharacterAndStyle;
        public double Strength { get; set; } = 1.0;
        public double Fidelity { get; set; }
        public bool IsCollapsed { get; set; }
    }

    private string GetCurrentModelKey() => GetSelectedComboText(CboModel) ?? CurrentParams.Model;

    private static bool IsV3ModelKey(string model) =>
        model == "nai-diffusion-3" || model == "nai-diffusion-3-inpainting";

    private static bool IsV4PlusModelKey(string model) =>
        model.Contains("-4", StringComparison.Ordinal);

    private static bool IsV45ModelKey(string model) =>
        model.Contains("4-5", StringComparison.Ordinal);

    private bool SupportsCharacterFeature()
    {
        if (!IsPromptMode(_currentMode)) return false;
        return IsV4PlusModelKey(GetCurrentModelKey());
    }

    private bool SupportsVibeTransferFeature()
    {
        if (!IsPromptMode(_currentMode)) return false;
        return true;
    }

    private bool RequiresEncodedVibeFileOnly() =>
        IsV4PlusModelKey(GetCurrentModelKey()) && !_settings.Settings.MaxMode;

    private bool SupportsPreciseReferenceFeature()
    {
        if (!IsPromptMode(_currentMode)) return false;
        return IsV45ModelKey(GetCurrentModelKey()) && _settings.Settings.MaxMode;
    }

    private bool CanEditVibeTransferFeature() =>
        SupportsVibeTransferFeature() &&
        _genPreciseReferences.Count == 0;

    private bool CanEditPreciseReferenceFeature() =>
        SupportsPreciseReferenceFeature() &&
        _genVibeTransfers.Count == 0;

    private bool ShouldShowVibeTransferPanel() =>
        SupportsVibeTransferFeature() && _genVibeTransfers.Count > 0;

    private bool ShouldShowPreciseReferencePanel() =>
        SupportsPreciseReferenceFeature() && _genPreciseReferences.Count > 0;

    private int EstimateCurrentRequestAnlasCost()
    {
        if (!IsPromptMode(_currentMode) || string.IsNullOrWhiteSpace(_settings.Settings.ApiToken))
            return 0;

        int steps = IsAdvancedWindowOpen ? (int)_advNbSteps.Value : CurrentParams.Steps;
        int width;
        int height;
        if (_currentMode == AppMode.ImageGeneration)
        {
            (width, height) = GetSelectedSize();
        }
        else
        {
            width = MaskCanvas.CanvasW > 0 ? MaskCanvas.CanvasW : _customWidth;
            height = MaskCanvas.CanvasH > 0 ? MaskCanvas.CanvasH : _customHeight;
        }

        long pixelCount = (long)width * height;
        bool isSmEnabled = CurrentParams.Sm;
        bool isSmDynamic = false;

        long dimension = Math.Max(pixelCount, 65536);
        int accountTier = _isOpusSubscriber ? 3 : 1;

        // ── 基础生图费用 ──
        // Opus 免费条件: <=28步, 总像素 <= 1024x1024
        int baseCost = 0;
        bool opusFree = steps <= 28 && dimension <= 1024L * 1024L && accountTier >= 3;
        if (!opusFree)
        {
            double factor = isSmDynamic ? 1.4 : isSmEnabled ? 1.2 : 1.0;
            double raw = Math.Ceiling(2951823174884865e-21 * pixelCount +
                                      5.753298233447344e-7 * pixelCount * steps) * factor;
            baseCost = Math.Clamp((int)Math.Ceiling(raw), 2, 140);
        }

        // ── 氛围迁移 / 精确参考额外费用 ──
        int refCost = 0;
        bool isV4Plus = IsV4PlusModelKey(GetCurrentModelKey());

        if (isV4Plus && _settings.Settings.MaxMode && _genVibeTransfers.Count > 0)
        {
            int encodingCost = _genVibeTransfers.Count(v => !v.IsEncodedFile) * 2;
            int slotCost = Math.Max(_genVibeTransfers.Count - 4, 0) * 2;
            refCost += encodingCost + slotCost;
        }

        if (SupportsPreciseReferenceFeature() && _genPreciseReferences.Count > 0)
        {
            refCost += _genPreciseReferences.Count * 5;
        }

        return baseCost + refCost;
    }

    private bool CurrentRequestUsesAnlas() => EstimateCurrentRequestAnlasCost() > 0;

    private bool TryValidateReferenceRequest(out string error)
    {
        error = "";

        if (!IsPromptMode(_currentMode))
            return true;

        if (_genVibeTransfers.Count > 0 &&
            _genPreciseReferences.Count > 0 &&
            _settings.Settings.MaxMode &&
            IsV45ModelKey(GetCurrentModelKey()))
        {
            error = L("references.validation.mixed_reference_types");
            return false;
        }

        if (RequiresEncodedVibeFileOnly() &&
            _genVibeTransfers.Any(x => !x.IsEncodedFile))
        {
            error = L("references.error.non_max_requires_encoded_vibe");
            return false;
        }

        return true;
    }

    private void ClearReferenceFeatures()
    {
        _genVibeTransfers.Clear();
        _genPreciseReferences.Clear();
    }

    private void ApplyReferenceDataFromMetadata(ImageMetadata? meta)
    {
        _genVibeTransfers.Clear();
        _genPreciseReferences.Clear();

        if (meta == null)
            return;

        foreach (var vibe in meta.VibeTransfers.Take(MaxVibeTransfers))
        {
            if (string.IsNullOrWhiteSpace(vibe.ImageBase64))
                continue;

            _genVibeTransfers.Add(new VibeTransferEntry
            {
                FileName = string.IsNullOrWhiteSpace(vibe.FileName) ? L("references.imported.vibe_label") : vibe.FileName,
                ImageBase64 = vibe.ImageBase64,
                Strength = Math.Clamp(vibe.Strength, 0, 1),
                InformationExtracted = Math.Clamp(vibe.InformationExtracted, 0, 1),
                IsEncodedFile = true,
            });
        }

        foreach (var reference in meta.PreciseReferences.Take(MaxPreciseReferences))
        {
            if (string.IsNullOrWhiteSpace(reference.ImageBase64))
                continue;

            _genPreciseReferences.Add(new PreciseReferenceEntry
            {
                FileName = string.IsNullOrWhiteSpace(reference.FileName) ? L("references.imported.precise_label") : reference.FileName,
                ImageBase64 = reference.ImageBase64,
                ReferenceType = reference.ReferenceType,
                Strength = Math.Clamp(reference.Strength, -1, 1),
                Fidelity = Math.Clamp(reference.Fidelity, -1, 1),
            });
        }
    }

    private void AppendReferenceImportNotes(ImageMetadata? meta, List<string> notes)
    {
        if (meta == null) return;
        if (meta.VibeTransfers.Count > 0)
            notes.Add(Lf("references.imported.vibe_count", meta.VibeTransfers.Count));
        if (meta.PreciseReferences.Count > 0)
            notes.Add(Lf("references.imported.precise_count", meta.PreciseReferences.Count));
    }

    private List<VibeTransferInfo>? GetVibeTransferData()
    {
        if (!SupportsVibeTransferFeature()) return null;
        if (_genPreciseReferences.Count > 0) return null;

        var result = _genVibeTransfers
            .Where(x => !string.IsNullOrWhiteSpace(x.ImageBase64))
            .Select(x => new VibeTransferInfo
            {
                FileName = x.FileName,
                ImageBase64 = x.ImageBase64,
                Strength = Math.Clamp(x.Strength, 0, 1),
                InformationExtracted = Math.Clamp(x.InformationExtracted, 0, 1),
            })
            .ToList();

        return result.Count > 0 ? result : null;
    }

    private List<PreciseReferenceInfo>? GetPreciseReferenceData()
    {
        if (!SupportsPreciseReferenceFeature()) return null;
        if (_genVibeTransfers.Count > 0) return null;

        var result = _genPreciseReferences
            .Where(x => !string.IsNullOrWhiteSpace(x.ImageBase64))
            .Select(x => new PreciseReferenceInfo
            {
                FileName = x.FileName,
                ImageBase64 = x.ImageBase64,
                ReferenceType = x.ReferenceType,
                Strength = Math.Clamp(x.Strength, -1, 1),
                Fidelity = Math.Clamp(x.Fidelity, -1, 1),
            })
            .ToList();

        return result.Count > 0 ? result : null;
    }

    private void UpdateReferenceButtonAndPanelState()
    {
        if (BtnAddCharacter == null || BtnAddVibeTransfer == null || BtnAddPreciseReference == null)
            return;

        BtnAddCharacter.Visibility = SupportsCharacterFeature()
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnAddCharacter.IsEnabled = SupportsCharacterFeature() && _genCharacters.Count < MaxCharacters;

        BtnAddVibeTransfer.Visibility = SupportsVibeTransferFeature() && _genPreciseReferences.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnAddVibeTransfer.IsEnabled = CanEditVibeTransferFeature() && _genVibeTransfers.Count < MaxVibeTransfers;

        string vibeToolTip = RequiresEncodedVibeFileOnly()
            ? L("references.error.non_max_requires_encoded_vibe")
            : L("references.tooltips.vibe");
        ToolTipService.SetToolTip(BtnAddVibeTransfer, vibeToolTip);

        BtnAddPreciseReference.Visibility = SupportsPreciseReferenceFeature() && _genVibeTransfers.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnAddPreciseReference.IsEnabled = CanEditPreciseReferenceFeature() && _genPreciseReferences.Count < MaxPreciseReferences;
        ToolTipService.SetToolTip(BtnAddPreciseReference, L("references.tooltips.precise"));

        CharacterPanel.Visibility = SupportsCharacterFeature()
            ? Visibility.Visible
            : Visibility.Collapsed;

        VibeTransferPanel.Visibility = ShouldShowVibeTransferPanel()
            ? Visibility.Visible
            : Visibility.Collapsed;
        TxtVibeTransferHint.Visibility = RequiresEncodedVibeFileOnly() && _genVibeTransfers.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        TxtVibeTransferHint.Text = L("references.hint.non_max_vibe");

        PreciseReferencePanel.Visibility = ShouldShowPreciseReferencePanel()
            ? Visibility.Visible
            : Visibility.Collapsed;
        TxtPreciseReferenceHint.Visibility = Visibility.Collapsed;

        UpdateReferenceButtonRowLayout();
    }

    private void UpdateReferenceButtonRowLayout()
    {
        if (ReferenceButtonRow == null)
            return;

        var columns = new[] { ReferenceButtonCol0, ReferenceButtonCol1, ReferenceButtonCol2 };
        foreach (var column in columns)
            column.Width = new GridLength(0);

        var visibleButtons = new List<Button>();
        if (BtnAddCharacter.Visibility == Visibility.Visible) visibleButtons.Add(BtnAddCharacter);
        if (BtnAddVibeTransfer.Visibility == Visibility.Visible) visibleButtons.Add(BtnAddVibeTransfer);
        if (BtnAddPreciseReference.Visibility == Visibility.Visible) visibleButtons.Add(BtnAddPreciseReference);

        ReferenceButtonRow.Visibility = visibleButtons.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        for (int i = 0; i < visibleButtons.Count; i++)
        {
            columns[i].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(visibleButtons[i], i);
        }

        UpdateReferenceButtonText(visibleButtons.Count);
    }

    private void UpdateReferenceButtonText(int visibleCount)
    {
        bool useCompact = false;
        if (visibleCount == 3)
        {
            double availableWidth = ReferenceButtonRow.ActualWidth;
            if (availableWidth < 1)
                availableWidth = (PanelLeftMain?.ActualWidth ?? 300) - 24;
            double perButton = (availableWidth - (visibleCount - 1) * 6) / visibleCount;
            useCompact = (perButton - 34) < 50;
        }
        if (TxtAddCharacterButton != null) TxtAddCharacterButton.Text = useCompact ? L("references.compact.character") : L("button.add_character");
        if (TxtAddVibeTransferButton != null) TxtAddVibeTransferButton.Text = useCompact ? L("references.compact.vibe") : L("button.add_vibe");
        if (TxtAddPreciseReferenceButton != null) TxtAddPreciseReferenceButton.Text = useCompact ? L("references.compact.precise") : L("button.add_precise_reference");
    }

    private void OnReferenceButtonRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        int visibleCount = 0;
        if (BtnAddCharacter?.Visibility == Visibility.Visible) visibleCount++;
        if (BtnAddVibeTransfer?.Visibility == Visibility.Visible) visibleCount++;
        if (BtnAddPreciseReference?.Visibility == Visibility.Visible) visibleCount++;
        UpdateReferenceButtonText(visibleCount);
    }

    private void OnLeftPanelScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePromptTabText();
        UpdatePromptAreaHeight();
    }

    private void OnBottomContentPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePromptAreaHeight();
    }

    private void UpdatePromptAreaHeight()
    {
        if (LeftPanelScrollViewer == null || PromptAreaGrid == null || BottomContentPanel == null)
            return;

        double viewport = LeftPanelScrollViewer.ActualHeight;
        double modelH = ModelHeaderPanel?.ActualHeight ?? CboModel?.ActualHeight ?? 0;
        double tabH = PromptTabRow?.ActualHeight ?? 0;
        double bottomH = BottomContentPanel.ActualHeight;
        const double overhead = 24 + 30; // Grid Padding (12*2) + RowSpacing (10*3)

        double desired = viewport - modelH - tabH - bottomH - overhead;
        PromptAreaGrid.MinHeight = Math.Max(80, desired);
    }

    private async void OnAddVibeTransfer(object sender, RoutedEventArgs e)
    {
        if (!CanEditVibeTransferFeature() || _genVibeTransfers.Count >= MaxVibeTransfers)
            return;

        var newEntry = await CreateVibeTransferEntryAsync();
        if (newEntry == null)
            return;

        _genVibeTransfers.Add(newEntry);
        RefreshVibeTransferPanel();
        UpdateReferenceButtonAndPanelState();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_vibe", newEntry.FileName);
    }

    private async void OnAddPreciseReference(object sender, RoutedEventArgs e)
    {
        if (!CanEditPreciseReferenceFeature() || _genPreciseReferences.Count >= MaxPreciseReferences)
            return;

        var newEntry = await CreatePreciseReferenceEntryAsync();
        if (newEntry == null)
            return;

        _genPreciseReferences.Add(newEntry);
        RefreshPreciseReferencePanel();
        UpdateReferenceButtonAndPanelState();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_precise", newEntry.FileName);
    }

    private async Task<VibeTransferEntry?> CreateVibeTransferEntryAsync()
    {
        var picked = await PickVibeTransferSourceAsync();
        if (picked == null)
            return null;

        return new VibeTransferEntry
        {
            FileName = picked.FileName,
            ImageBase64 = picked.ImageBase64,
            IsEncodedFile = picked.IsEncodedFile,
            OriginalImageHash = picked.ImageHash,
            OriginalImageBase64 = picked.OriginalBase64,
            IsCachedEncoding = picked.IsCachedHit,
        };
    }

    private async Task<PreciseReferenceEntry?> CreatePreciseReferenceEntryAsync()
    {
        var picked = await PickReferenceImageAsync();
        if (picked == null)
            return null;

        return new PreciseReferenceEntry
        {
            FileName = picked.Value.FileName,
            ImageBase64 = picked.Value.ImageBase64,
        };
    }

    private async Task<(string FileName, string ImageBase64)?> PickReferenceImageAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return null;

        byte[]? pngBytes = await ReadImageFileAsPngAsync(file);
        if (pngBytes == null || pngBytes.Length == 0)
        {
            TxtStatus.Text = Lf("references.error.cannot_read_reference_image", file.Name);
            return null;
        }

        return (file.Name, Convert.ToBase64String(pngBytes));
    }

    private sealed record VibePickResult(
        string FileName,
        string ImageBase64,
        bool IsEncodedFile,
        string? ImageHash = null,
        string? OriginalBase64 = null,
        bool IsCachedHit = false);

    private async Task<VibePickResult?> PickVibeTransferSourceAsync()
    {
        var picker = new FileOpenPicker();

        if (RequiresEncodedVibeFileOnly())
        {
            picker.FileTypeFilter.Add(".naiv4vibe");
        }
        else
        {
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".naiv4vibe");
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return null;

        if (file.FileType.Equals(".naiv4vibe", StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes = await File.ReadAllBytesAsync(file.Path);
            if (bytes.Length == 0)
            {
                TxtStatus.Text = Lf("references.error.cannot_read_encoded_vibe", file.Name);
                return null;
            }

            return new VibePickResult(file.Name, Convert.ToBase64String(bytes), IsEncodedFile: true);
        }

        if (RequiresEncodedVibeFileOnly())
        {
            TxtStatus.Text = L("references.error.non_max_requires_encoded_vibe");
            return null;
        }

        byte[]? pngBytes = await ReadImageFileAsPngAsync(file);
        if (pngBytes == null || pngBytes.Length == 0)
        {
            TxtStatus.Text = Lf("references.error.cannot_read_reference_image", file.Name);
            return null;
        }

        string imageHash = VibeCacheService.ComputeImageHash(pngBytes);
        string originalBase64 = Convert.ToBase64String(pngBytes);
        string currentModel = GetCurrentModelKey();
        string cacheDir = VibeCacheService.GetCacheDir(AppRootDir);

        string? cachedEncoding = VibeCacheService.TryGetCachedVibeByHash(
            cacheDir, imageHash, 1.0, currentModel);
        if (cachedEncoding != null)
        {
            TxtStatus.Text = Lf("references.status.cached_vibe_loaded", file.Name, currentModel);
            return new VibePickResult(file.Name, cachedEncoding, IsEncodedFile: true,
                ImageHash: imageHash, OriginalBase64: originalBase64, IsCachedHit: true);
        }

        return new VibePickResult(file.Name, originalBase64, IsEncodedFile: false,
            ImageHash: imageHash, OriginalBase64: originalBase64);
    }

    private static async Task<byte[]?> ReadImageFileAsPngAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        using var source = stream.AsStreamForRead();
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory);
        byte[] bytes = memory.ToArray();

        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap == null)
            return null;

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    /// <summary>
    /// 重新检测所有原始图片条目的缓存命中状态（基于当前模型 + 各条目 IE）。
    /// </summary>
    private void RecheckVibeTransferCacheState(bool refreshPanel = true)
    {
        string cacheDir = VibeCacheService.GetCacheDir(AppRootDir);
        string currentModel = GetCurrentModelKey();
        bool anyChanged = false;

        foreach (var entry in _genVibeTransfers)
        {
            if (entry.OriginalImageHash == null)
                continue;

            string? cachedEncoding = VibeCacheService.TryGetCachedVibeByHash(
                cacheDir, entry.OriginalImageHash, entry.InformationExtracted, currentModel);

            bool wasCached = entry.IsCachedEncoding;

            if (cachedEncoding != null)
            {
                entry.ImageBase64 = cachedEncoding;
                entry.IsEncodedFile = true;
                entry.IsCachedEncoding = true;
            }
            else
            {
                if (entry.OriginalImageBase64 != null)
                    entry.ImageBase64 = entry.OriginalImageBase64;
                entry.IsEncodedFile = false;
                entry.IsCachedEncoding = false;
            }

            if (wasCached != entry.IsCachedEncoding)
                anyChanged = true;
        }

        if (anyChanged)
        {
            if (refreshPanel)
                RefreshVibeTransferPanel();
            UpdateGenerateButtonWarning();
        }
    }

    private void RefreshVibeTransferPanel()
    {
        if (VibeTransferContainer == null)
            return;

        VibeTransferContainer.Children.Clear();
        for (int i = 0; i < _genVibeTransfers.Count; i++)
            VibeTransferContainer.Children.Add(BuildVibeTransferUI(_genVibeTransfers[i], i));

        UpdateReferenceButtonAndPanelState();
    }

    private void RefreshPreciseReferencePanel()
    {
        if (PreciseReferenceContainer == null)
            return;

        PreciseReferenceContainer.Children.Clear();
        for (int i = 0; i < _genPreciseReferences.Count; i++)
            PreciseReferenceContainer.Children.Add(BuildPreciseReferenceUI(_genPreciseReferences[i], i));

        UpdateReferenceButtonAndPanelState();
    }

    private UIElement BuildVibeTransferUI(VibeTransferEntry entry, int index)
    {
        var root = new StackPanel { Spacing = 6 };

        string vibeTitle = entry.IsCachedEncoding
            ? Lf("references.vibe.cached_title", index + 1)
            : Lf("references.vibe.title", index + 1);
        var header = BuildReferenceHeader(
            vibeTitle,
            entry.IsCollapsed,
            canMoveUp: index > 0,
            canMoveDown: index < _genVibeTransfers.Count - 1,
            onMoveUp: () => MoveVibeTransfer(index, -1),
            onMoveDown: () => MoveVibeTransfer(index, 1),
            onDelete: () =>
            {
                _genVibeTransfers.Remove(entry);
                RefreshVibeTransferPanel();
                UpdateGenerateButtonWarning();
            },
            onCollapse: () =>
            {
                entry.IsCollapsed = !entry.IsCollapsed;
                RefreshVibeTransferPanel();
            });
        root.Children.Add(header);

        if (!entry.IsCollapsed)
        {
            bool canEdit = CanEditVibeTransferFeature();

            var infoGrid = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 0) };
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var thumbImage = new Image
            {
                Width = 64, Height = 64,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var thumbBorder = new Border
            {
                Width = 64, Height = 64,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)),
                Child = thumbImage,
            };
            thumbBorder.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, 64, 64),
            };
            _ = LoadVibeThumbAsync(entry, thumbImage);
            Grid.SetColumn(thumbBorder, 0);
            infoGrid.Children.Add(thumbBorder);

            var rightCol = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Top };

            var fileNameBlock = new TextBlock
            {
                Text = entry.FileName,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Opacity = 0.85,
            };
            rightCol.Children.Add(fileNameBlock);

            if (entry.IsCachedEncoding)
            {
                rightCol.Children.Add(new TextBlock
                {
                    Text = Lf("references.vibe.cached_badge", GetCurrentModelKey()),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 200, 120)),
                    Opacity = 0.85,
                });
            }
            else if (entry.IsEncodedFile && entry.OriginalImageHash == null)
            {
                rightCol.Children.Add(new TextBlock
                {
                    Text = L("references.vibe.encoded_file"),
                    FontSize = 11,
                    Opacity = 0.5,
                });
            }
            else
            {
                rightCol.Children.Add(new TextBlock
                {
                    Text = L("references.vibe.unencoded_cost"),
                    FontSize = 11,
                    Opacity = 0.5,
                });
            }

            var replaceBtn = new Button
            {
                Content = L("references.vibe.replace"),
                FontSize = 12,
                MinWidth = 52, MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 0),
            };
            replaceBtn.Click += async (_, _) =>
            {
                var picked = await PickVibeTransferSourceAsync();
                if (picked == null) return;
                entry.FileName = picked.FileName;
                entry.ImageBase64 = picked.ImageBase64;
                entry.IsEncodedFile = picked.IsEncodedFile;
                entry.OriginalImageHash = picked.ImageHash;
                entry.OriginalImageBase64 = picked.OriginalBase64;
                entry.IsCachedEncoding = picked.IsCachedHit;
                RefreshVibeTransferPanel();
                UpdateGenerateButtonWarning();
                TxtStatus.Text = Lf("references.vibe.updated", entry.FileName);
            };
            replaceBtn.IsEnabled = canEdit;
            rightCol.Children.Add(replaceBtn);

            Grid.SetColumn(rightCol, 1);
            infoGrid.Children.Add(rightCol);
            infoGrid.IsHitTestVisible = canEdit;
            infoGrid.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(infoGrid);

            var strengthRow = BuildReferenceSliderRow(
                L("references.vibe.reference_strength"),
                0, 1, entry.Strength,
                value => entry.Strength = Math.Round(value, 2));
            strengthRow.IsHitTestVisible = canEdit;
            strengthRow.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(strengthRow);

            var infoRow = BuildReferenceSliderRow(
                L("references.vibe.info_extracted"),
                0, 1, entry.InformationExtracted,
                value =>
                {
                    entry.InformationExtracted = Math.Round(value, 2);
                    RecheckVibeTransferCacheState(refreshPanel: false);
                });
            infoRow.IsHitTestVisible = canEdit;
            infoRow.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(infoRow);
        }

        if (index < _genVibeTransfers.Count - 1)
            root.Children.Add(CreateReferenceSeparator());
        root.Opacity = CanEditVibeTransferFeature() ? 1.0 : 0.72;
        return root;
    }

    private async Task LoadVibeThumbAsync(VibeTransferEntry entry, Image target)
    {
        try
        {
            byte[]? thumbBytes = null;

            if (entry.OriginalImageBase64 != null)
            {
                thumbBytes = Convert.FromBase64String(entry.OriginalImageBase64);
            }
            else if (entry.OriginalImageHash != null)
            {
                string cacheDir = VibeCacheService.GetCacheDir(AppRootDir);
                string? thumbPath = VibeCacheService.GetThumbnailPath(cacheDir, entry.OriginalImageHash);
                if (thumbPath != null)
                    thumbBytes = await Task.Run(() => File.ReadAllBytes(thumbPath));
            }

            if (thumbBytes != null && thumbBytes.Length > 0)
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(thumbBytes);
                await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                target.Source = bmp;
            }
            else
            {
                target.Source = null;
            }
        }
        catch
        {
            target.Source = null;
        }
    }

    private UIElement BuildPreciseReferenceUI(PreciseReferenceEntry entry, int index)
    {
        var root = new StackPanel { Spacing = 6 };

        var header = BuildReferenceHeader(
            Lf("references.precise.cached", index + 1),
            entry.IsCollapsed,
            canMoveUp: index > 0,
            canMoveDown: index < _genPreciseReferences.Count - 1,
            onMoveUp: () => MovePreciseReference(index, -1),
            onMoveDown: () => MovePreciseReference(index, 1),
            onDelete: () =>
            {
                _genPreciseReferences.Remove(entry);
                RefreshPreciseReferencePanel();
                UpdateGenerateButtonWarning();
            },
            onCollapse: () =>
            {
                entry.IsCollapsed = !entry.IsCollapsed;
                RefreshPreciseReferencePanel();
            });
        root.Children.Add(header);

        if (!entry.IsCollapsed)
        {
            bool canEdit = CanEditPreciseReferenceFeature();

            var infoGrid = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 0) };
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var thumbImage = new Image
            {
                Width = 64, Height = 64,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var thumbBorder = new Border
            {
                Width = 64, Height = 64,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)),
                Child = thumbImage,
            };
            thumbBorder.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, 64, 64),
            };
            _ = LoadPreciseRefThumbAsync(entry, thumbImage);
            Grid.SetColumn(thumbBorder, 0);
            infoGrid.Children.Add(thumbBorder);

            var rightCol = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Top };
            rightCol.Children.Add(new TextBlock
            {
                Text = entry.FileName,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Opacity = 0.85,
            });

            var replaceBtn = new Button
            {
                Content = L("references.precise.replace"),
                FontSize = 12,
                MinWidth = 52, MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 0),
            };
            replaceBtn.Click += async (_, _) =>
            {
                var picked = await PickReferenceImageAsync();
                if (picked == null) return;
                entry.FileName = picked.Value.FileName;
                entry.ImageBase64 = picked.Value.ImageBase64;
                RefreshPreciseReferencePanel();
                TxtStatus.Text = Lf("references.precise.updated", entry.FileName);
            };
            replaceBtn.IsEnabled = canEdit;
            rightCol.Children.Add(replaceBtn);

            Grid.SetColumn(rightCol, 1);
            infoGrid.Children.Add(rightCol);
            infoGrid.IsHitTestVisible = canEdit;
            infoGrid.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(infoGrid);

            var typeCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 32,
                FontFamily = UiTextFontFamily,
            };
            typeCombo.Items.Add(CreateTextComboBoxItem(L("references.precise.type.both")));
            typeCombo.Items.Add(CreateTextComboBoxItem(L("references.precise.type.character")));
            typeCombo.Items.Add(CreateTextComboBoxItem(L("references.precise.type.style")));
            typeCombo.SelectedIndex = entry.ReferenceType switch
            {
                PreciseReferenceType.CharacterAndStyle => 0,
                PreciseReferenceType.Character => 1,
                _ => 2,
            };
            typeCombo.SelectionChanged += (_, _) =>
            {
                entry.ReferenceType = typeCombo.SelectedIndex switch
                {
                    1 => PreciseReferenceType.Character,
                    2 => PreciseReferenceType.Style,
                    _ => PreciseReferenceType.CharacterAndStyle,
                };
            };
            ApplyMenuTypography(typeCombo);
            typeCombo.IsEnabled = canEdit;
            root.Children.Add(typeCombo);

            var strengthRow = BuildReferenceSliderRow(
                L("references.precise.strength"),
                -1, 1, entry.Strength,
                value => entry.Strength = Math.Round(value, 2));
            strengthRow.IsHitTestVisible = canEdit;
            strengthRow.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(strengthRow);

            var fidelityRow = BuildReferenceSliderRow(
                L("references.precise.fidelity"),
                -1, 1, entry.Fidelity,
                value => entry.Fidelity = Math.Round(value, 2));
            fidelityRow.IsHitTestVisible = canEdit;
            fidelityRow.Opacity = canEdit ? 1.0 : 0.72;
            root.Children.Add(fidelityRow);
        }

        if (index < _genPreciseReferences.Count - 1)
            root.Children.Add(CreateReferenceSeparator());
        return root;
    }

    private async Task LoadPreciseRefThumbAsync(PreciseReferenceEntry entry, Image target)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entry.ImageBase64)) return;
            byte[] bytes = Convert.FromBase64String(entry.ImageBase64);
            if (bytes.Length == 0) return;
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            target.Source = bmp;
        }
        catch
        {
            target.Source = null;
        }
    }

    private Border CreateReferenceSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 2, 0, 0),
        Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
    };

    private UIElement BuildReferenceHeader(
        string title,
        bool isCollapsed,
        bool canMoveUp,
        bool canMoveDown,
        Action onMoveUp,
        Action onMoveDown,
        Action onDelete,
        Action onCollapse)
    {
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var collapseBtn = CreateCharacterCollapseButton(isCollapsed);
        collapseBtn.Click += (_, _) => onCollapse();
        Grid.SetColumn(collapseBtn, 0);
        headerGrid.Children.Add(collapseBtn);

        var label = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)((Grid)this.Content).Resources["InspectCaptionStyle"],
        };
        Grid.SetColumn(label, 1);
        headerGrid.Children.Add(label);

        var movePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = isCollapsed ? Visibility.Collapsed : Visibility.Visible,
        };
        var upBtn = CreateCharacterActionButton("\uE70E", L("references.action.move_up"), canMoveUp);
        var downBtn = CreateCharacterActionButton("\uE70D", L("references.action.move_down"), canMoveDown);
        upBtn.Click += (_, _) => onMoveUp();
        downBtn.Click += (_, _) => onMoveDown();
        movePanel.Children.Add(upBtn);
        movePanel.Children.Add(downBtn);
        Grid.SetColumn(movePanel, 2);
        headerGrid.Children.Add(movePanel);

        var delBtn = CreateCharacterActionButton("\uE74D", L("references.action.delete"), true, isDelete: true);
        delBtn.Margin = new Thickness(4, 0, 0, 0);
        delBtn.Click += (_, _) => onDelete();
        delBtn.Visibility = isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(delBtn, 3);
        headerGrid.Children.Add(delBtn);

        return headerGrid;
    }

    // BuildReferenceFileRow removed — replaced by inline thumbnail layout

    private StackPanel BuildReferenceSliderRow(
        string label,
        double min,
        double max,
        double value,
        Action<double> onValueChanged)
    {
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(CreateThemedSubLabel(label));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = 0.01,
            Value = value,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueText = new TextBlock
        {
            Text = value.ToString("0.00"),
            MinWidth = 38,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, args) =>
        {
            double next = Math.Clamp(args.NewValue, min, max);
            valueText.Text = next.ToString("0.00");
            onValueChanged(next);
            UpdateGenerateButtonWarning();
        };

        Grid.SetColumn(slider, 0);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(slider);
        grid.Children.Add(valueText);
        row.Children.Add(grid);
        return row;
    }

    private void MoveVibeTransfer(int index, int direction)
    {
        int newIndex = index + direction;
        if (newIndex < 0 || newIndex >= _genVibeTransfers.Count) return;
        var entry = _genVibeTransfers[index];
        _genVibeTransfers.RemoveAt(index);
        _genVibeTransfers.Insert(newIndex, entry);
        RefreshVibeTransferPanel();
    }

    private void MovePreciseReference(int index, int direction)
    {
        int newIndex = index + direction;
        if (newIndex < 0 || newIndex >= _genPreciseReferences.Count) return;
        var entry = _genPreciseReferences[index];
        _genPreciseReferences.RemoveAt(index);
        _genPreciseReferences.Insert(newIndex, entry);
        RefreshPreciseReferencePanel();
    }
}
