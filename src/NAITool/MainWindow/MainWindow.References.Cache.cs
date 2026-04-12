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
}
