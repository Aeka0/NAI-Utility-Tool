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
        if (!CanEditVibeTransferFeature() || _genVibeTransfers.Count >= maxVibeTransfers)
        {
            if (IsAssetProtectionPaidFeatureLimitEnabled() && _genVibeTransfers.Count >= maxVibeTransfers)
                TxtStatus.Text = Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit);
            return;
        }

        var newEntry = await CreateVibeTransferEntryAsync();
        if (newEntry == null)
            return;

        if (_genVibeTransfers.Count >= maxVibeTransfers)
        {
            if (IsAssetProtectionPaidFeatureLimitEnabled())
                TxtStatus.Text = Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit);
            return;
        }

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
            IsCachedEncoding = picked.IsCachedHit,
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

        return await CreateVibeTransferPickResultAsync(file);
    }

    private async Task<VibePickResult?> CreateVibeTransferPickResultAsync(StorageFile file)
    {
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

        string? cachedEncoding = VibeCacheService.TryGetCachedVibeByLookup(
            cacheDir, imageHash, 1.0, currentModel);
        if (cachedEncoding != null)
        {
            TxtStatus.Text = Lf("references.status.cached_vibe_loaded", file.Name, currentModel);
            return new VibePickResult(file.Name, cachedEncoding, IsEncodedFile: true,
                ImageHash: imageHash, ThumbnailHash: thumbnailHash, OriginalBase64: originalBase64, IsCachedHit: true);
        }

        var (thumbnailMatchedEncoding, matchedImageHash) = VibeCacheService.TryGetCachedVibeByThumbnailHash(
            cacheDir, thumbnailHash, 1.0, currentModel);
        if (thumbnailMatchedEncoding != null && matchedImageHash != null)
        {
            TxtStatus.Text = Lf("references.status.cached_vibe_loaded", file.Name, currentModel);
            return new VibePickResult(file.Name, thumbnailMatchedEncoding, IsEncodedFile: true,
                ImageHash: matchedImageHash, ThumbnailHash: thumbnailHash, OriginalBase64: originalBase64, IsCachedHit: true);
        }

        return new VibePickResult(file.Name, originalBase64, IsEncodedFile: false,
            ImageHash: imageHash, ThumbnailHash: thumbnailHash, OriginalBase64: originalBase64);
    }

    private async Task AddDroppedVibeTransferAsync(StorageFile file)
    {
        int maxVibeTransfers = GetMaxAllowedVibeTransfers();
        if (!CanEditVibeTransferFeature() || _genVibeTransfers.Count >= maxVibeTransfers)
        {
            if (IsAssetProtectionPaidFeatureLimitEnabled() && _genVibeTransfers.Count >= maxVibeTransfers)
                TxtStatus.Text = Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit);
            return;
        }

        var newEntry = CreateVibeTransferEntry(await CreateVibeTransferPickResultAsync(file));
        if (newEntry == null)
            return;

        if (_genVibeTransfers.Count >= maxVibeTransfers)
        {
            if (IsAssetProtectionPaidFeatureLimitEnabled())
                TxtStatus.Text = Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit);
            return;
        }

        _genVibeTransfers.Add(newEntry);
        RefreshVibeTransferPanel();
        UpdateReferenceButtonAndPanelState();
        UpdateGenerateButtonWarning();
        TxtStatus.Text = Lf("references.status.added_vibe", newEntry.FileName);
    }

    private async Task AddDroppedPreciseReferenceAsync(StorageFile file)
    {
        if (IsPreciseReferenceBlockedByAssetProtection())
        {
            TxtStatus.Text = L("superdrop.error.asset_protection_precise_reference");
            return;
        }

        if (!CanEditPreciseReferenceFeature() || _genPreciseReferences.Count >= MaxPreciseReferences)
            return;

        var newEntry = CreatePreciseReferenceEntry(await CreateReferenceImagePickResultAsync(file));
        if (newEntry == null)
            return;

        _genPreciseReferences.Add(newEntry);
        RefreshPreciseReferencePanel();
        UpdateReferenceButtonAndPanelState();
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
