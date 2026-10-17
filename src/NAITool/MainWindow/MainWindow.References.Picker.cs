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
    private async void OnAddVibeTransfer(object sender, RoutedEventArgs e)
    {
        int maxVibeTransfers = GetMaxAllowedVibeTransfers();
        if (TryReportVibeTransferUnavailable(maxVibeTransfers))
            return;

        var newEntry = await CreateVibeTransferEntryAsync();
        if (newEntry == null)
            return;

        if (ActiveVibeTransferCount() >= maxVibeTransfers)
        {
            TxtStatus.Text = IsAssetProtectionPaidFeatureLimitEnabled()
                ? Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit)
                : Lf("references.error.vibe_count_limit", maxVibeTransfers);
            return;
        }

        _genVibeTransfers.Add(newEntry);
        _referenceImportVersion++;
        RecheckVibeTransferCacheState(refreshPanel: false);
        RefreshReferencePanels();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_vibe", newEntry.FileName);
    }

    private async void OnAddPreciseReference(object sender, RoutedEventArgs e)
    {
        if (TryReportPreciseReferenceUnavailable())
            return;

        var newEntry = await CreatePreciseReferenceEntryAsync();
        if (newEntry == null)
            return;

        _genPreciseReferences.Add(newEntry);
        RefreshReferencePanels();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_precise", newEntry.FileName);
    }

    private async Task<VibeTransferEntry?> CreateVibeTransferEntryAsync()
    {
        var picked = await PickVibeTransferSourceAsync();
        return CreateVibeTransferEntry(picked);
    }

    private async Task<PreciseReferenceEntry?> CreatePreciseReferenceEntryAsync()
    {
        var picked = await PickReferenceImageAsync();
        return CreatePreciseReferenceEntry(picked);
    }

    private VibeTransferEntry? CreateVibeTransferEntry(VibePickResult? picked)
    {
        if (picked == null)
            return null;

        return new VibeTransferEntry
        {
            FileName = picked.FileName,
            ImageBase64 = picked.ImageBase64,
            IsEncodedFile = picked.IsEncodedFile,
            OriginalImageHash = picked.ImageHash,
            OriginalThumbnailHash = picked.ThumbnailHash,
            OriginalImageBase64 = picked.OriginalBase64,
            OriginalImagePath = picked.OriginalImagePath,
            IsCachedEncoding = picked.IsCachedHit,
            ImportedVibe = picked.Document,
            InformationExtracted = picked.InformationExtracted,
            Strength = picked.Strength,
        };
    }

    private PreciseReferenceEntry? CreatePreciseReferenceEntry((string FileName, string ImageBase64)? picked)
    {
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

        return await CreateReferenceImagePickResultAsync(file);
    }

    private async Task<(string FileName, string ImageBase64)?> CreateReferenceImagePickResultAsync(StorageFile file)
    {
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
        string? ThumbnailHash = null,
        string? OriginalBase64 = null,
        bool IsCachedHit = false,
        string? OriginalImagePath = null,
        VibeFileService.Document? Document = null,
        double InformationExtracted = 1.0,
        double Strength = 0.6);

    private static VibePickResult ToVibePickResult(string fileName, VibeImportService.Prepared prepared) =>
        new(fileName, prepared.ImageBase64, prepared.IsEncoded, prepared.ImageHash, prepared.ThumbnailHash,
            prepared.OriginalBase64, prepared.IsEncoded && prepared.ImageHash != null, prepared.OriginalPath,
            prepared.Document, prepared.InformationExtracted, prepared.Strength);

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

        return await CreateVibeTransferPickResultAsync(file);
    }

    private async Task<VibePickResult?> CreateVibeTransferPickResultAsync(StorageFile file)
    {
        if (file.FileType.Equals(".naiv4vibe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (new FileInfo(file.Path).Length > VibeFileService.MaxFileBytes)
                    throw new InvalidDataException("Vibe file too large.");
                string model = GetCurrentModelKey();
                string cache = VibeCacheService.GetCacheDir(AppRootDir);
                return await Task.Run(async () =>
                {
                    byte[] bytes = await File.ReadAllBytesAsync(file.Path);
                    return ToVibePickResult(file.Name, VibeImportService.PrepareFile(bytes, model, cache));
                });
            }
            catch (Exception ex)
            {
                DebugLog($"[VibeImport] {ex.Message}");
                TxtStatus.Text = Lf("references.error.cannot_read_encoded_vibe", file.Name);
                return null;
            }
        }

        if (RequiresEncodedVibeFileOnly())
        {
            TxtStatus.Text = L("superdrop.error.asset_protection_vibe_image");
            return null;
        }

        byte[]? pngBytes = await ReadImageFileAsPngAsync(file);
        if (pngBytes == null || pngBytes.Length == 0)
        {
            TxtStatus.Text = Lf("references.error.cannot_read_reference_image", file.Name);
            return null;
        }

        string imageHash = VibeCacheService.ComputeImageHash(pngBytes);
        string thumbnailHash = VibeCacheService.ComputeThumbnailHash(
            VibeCacheService.CreateCanonicalThumbnail(pngBytes));
        string originalBase64 = Convert.ToBase64String(pngBytes);
        string currentModel = GetCurrentModelKey();
        string cacheDir = VibeCacheService.GetCacheDir(AppRootDir);
        string? originalPath = string.IsNullOrWhiteSpace(file.Path) ? null : file.Path;

        string? cachedEncoding = VibeCacheService.TryGetCachedVibeByLookup(
            cacheDir, imageHash, 1.0, currentModel);
        if (cachedEncoding != null)
        {
            TxtStatus.Text = Lf("references.status.cached_vibe_loaded", file.Name, currentModel);
            return new VibePickResult(file.Name, cachedEncoding, IsEncodedFile: true,
                ImageHash: imageHash, ThumbnailHash: thumbnailHash, OriginalBase64: originalBase64, IsCachedHit: true,
                OriginalImagePath: originalPath);
        }

        var (thumbnailMatchedEncoding, matchedImageHash) = VibeCacheService.TryGetCachedVibeByThumbnailHash(
            cacheDir, thumbnailHash, 1.0, currentModel);
        if (thumbnailMatchedEncoding != null && matchedImageHash != null)
        {
            TxtStatus.Text = Lf("references.status.cached_vibe_loaded", file.Name, currentModel);
            return new VibePickResult(file.Name, thumbnailMatchedEncoding, IsEncodedFile: true,
                ImageHash: matchedImageHash, ThumbnailHash: thumbnailHash, OriginalBase64: originalBase64, IsCachedHit: true,
                OriginalImagePath: originalPath);
        }

        return new VibePickResult(file.Name, originalBase64, IsEncodedFile: false,
            ImageHash: imageHash, ThumbnailHash: thumbnailHash, OriginalBase64: originalBase64,
            OriginalImagePath: originalPath);
    }

    private bool TryReportVibeTransferUnavailable(int maxVibeTransfers)
    {
        if (!SupportsVibeTransferFeature())
        {
            TxtStatus.Text = L("references.error.vibe_unsupported_model");
            return true;
        }

        if (ActivePreciseReferenceCount() > 0)
        {
            TxtStatus.Text = L("references.validation.mixed_reference_types");
            return true;
        }

        if (ActiveVibeTransferCount() >= maxVibeTransfers)
        {
            TxtStatus.Text = IsAssetProtectionPaidFeatureLimitEnabled()
                ? Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit)
                : Lf("references.error.vibe_count_limit", maxVibeTransfers);
            return true;
        }

        return false;
    }

    private bool TryReportPreciseReferenceUnavailable()
    {
        if (IsPreciseReferenceBlockedByAssetProtection())
        {
            TxtStatus.Text = L("superdrop.error.asset_protection_precise_reference");
            return true;
        }

        if (!SupportsPreciseReferenceFeature())
        {
            TxtStatus.Text = L("references.error.precise_requires_v45_model");
            return true;
        }

        if (SupportsVibeTransferFeature() && ActiveVibeTransferCount() > 0)
        {
            TxtStatus.Text = L("references.validation.mixed_reference_types");
            return true;
        }

        if (ActivePreciseReferenceCount() >= MaxPreciseReferences)
        {
            TxtStatus.Text = Lf("references.error.precise_count_limit", MaxPreciseReferences);
            return true;
        }

        return false;
    }

    private async Task AddDroppedVibeTransferAsync(StorageFile file)
    {
        if (IsVibeFile(file))
        {
            await ImportDroppedVibeFileAsync(file);
            return;
        }
        int maxVibeTransfers = GetMaxAllowedVibeTransfers();
        if (TryReportVibeTransferUnavailable(maxVibeTransfers))
            return;

        var newEntry = CreateVibeTransferEntry(await CreateVibeTransferPickResultAsync(file));
        if (newEntry == null)
            return;

        if (ActiveVibeTransferCount() >= maxVibeTransfers)
        {
            if (IsAssetProtectionPaidFeatureLimitEnabled())
                TxtStatus.Text = Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit);
            return;
        }

        _genVibeTransfers.Add(newEntry);
        _referenceImportVersion++;
        RecheckVibeTransferCacheState(refreshPanel: false);
        RefreshReferencePanels();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_vibe", newEntry.FileName);
    }

    private async Task ImportDroppedVibeFileAsync(StorageFile file)
    {
        // Validate the source before changing workspace or model. V5 must be allowed here
        // so that an encoded V4/V4.5 file can select its supported model automatically.
        var mode = _currentMode;
        string model = GetCurrentModelKey();
        int version = _referenceImportVersion;
        var picked = await CreateVibeTransferPickResultAsync(file);
        if (picked?.Document == null || mode != _currentMode || model != GetCurrentModelKey() ||
            version != _referenceImportVersion) return;

        string? targetModel = VibeImportService.SelectCompatibleModel(picked.Document,
            mode == AppMode.ImageGeneration ? model : _settings.Settings.GenParameters.Model, GenerationModels);
        if (targetModel == null)
        {
            TxtStatus.Text = L("references.error.vibe_unsupported_model");
            return;
        }
        if (ActivePreciseReferenceCount() > 0)
        {
            TxtStatus.Text = L("references.validation.mixed_reference_types");
            return;
        }
        int maxVibeTransfers = GetMaxAllowedVibeTransfers();
        if (ActiveVibeTransferCount() >= maxVibeTransfers)
        {
            TxtStatus.Text = IsAssetProtectionPaidFeatureLimitEnabled()
                ? Lf("references.error.asset_protection_vibe_count_limit", maxVibeTransfers)
                : Lf("references.error.vibe_count_limit", maxVibeTransfers);
            return;
        }

        if (_currentMode != AppMode.ImageGeneration) SwitchMode(AppMode.ImageGeneration);
        if (GetCurrentModelKey() != targetModel)
            ApplyImportedImageModel(new ImageMetadata { Model = targetModel }, CurrentParams, GenerationModels);
        _genVibeTransfers.Add(CreateVibeTransferEntry(picked)!);
        _referenceImportVersion++;
        RecheckVibeTransferCacheState(refreshPanel: false);
        RefreshReferencePanels();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_vibe", file.Name);
    }

    private async Task AddDroppedPreciseReferenceAsync(StorageFile file)
    {
        if (TryReportPreciseReferenceUnavailable())
            return;

        var newEntry = CreatePreciseReferenceEntry(await CreateReferenceImagePickResultAsync(file));
        if (newEntry == null)
            return;

        _genPreciseReferences.Add(newEntry);
        RefreshReferencePanels();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_precise", newEntry.FileName);
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
}
