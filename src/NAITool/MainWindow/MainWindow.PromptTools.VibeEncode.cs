using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    /// <summary>
    /// 氛围预编码二级窗口的上下文。null = 标准模式（用户自选图）；非 null = 再编码模式（锁定 ImageHash，图源由上下文提供）。
    /// </summary>
    private sealed record VibeEncodeContext(
        string? FixedImageHash,
        byte[]? PresetImageBytes,
        string? PresetImagePath,
        string? PresetFileName,
        bool OriginalMissingFallback);

    /// <summary>
    /// 展示氛围预编码二级窗口。context 不为 null 时图片锁定且禁用选图按钮。
    /// </summary>
    private async Task<bool> ShowVibeEncodeDialog(VibeEncodeContext? context = null)
    {
        if (_isVibeEncodeDialogOpen) return false;
        _isVibeEncodeDialogOpen = true;

        bool didEncode = false;

        try
        {
            bool isReencodeMode = context != null;
            byte[]? selectedImageBytes = context?.PresetImageBytes;
            string? selectedFileName = context?.PresetFileName;
            string? selectedImagePath = context?.PresetImagePath;

            string[] vibeModels = ["nai-diffusion-4-5-curated", "nai-diffusion-4-5-full", "nai-diffusion-4-curated-preview", "nai-diffusion-4-full"];

            var modelCombo = new ComboBox
            {
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                ItemsSource = vibeModels,
                SelectedIndex = 0,
            };
            ApplyMenuTypography(modelCombo);

            var fileNameBlock = new TextBlock
            {
                Text = selectedFileName ?? L("dialog.vibe_encode.no_image_selected"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260,
                Opacity = selectedFileName != null ? 1.0 : 0.6,
            };

            var browseBtn = new Button
            {
                Content = L("dialog.vibe_encode.select_image"),
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
                Content = CreateAnlasActionButtonContent(L("dialog.vibe_encode.start_encoding"), 2),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                IsEnabled = selectedImageBytes != null && selectedImageBytes.Length > 0,
                Margin = new Thickness(0, 4, 0, 0),
            };
            ApplyGoldAccentButtonStyle(encodeBtn);

            async Task LoadThumbFromBytesAsync(byte[] bytes)
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                    thumbImage.Source = bmp;
                    thumbImage.Visibility = Visibility.Visible;
                }
                catch
                {
                    thumbImage.Visibility = Visibility.Collapsed;
                }
            }

            if (isReencodeMode)
            {
                browseBtn.IsEnabled = false;
                browseBtn.Visibility = Visibility.Collapsed;

                if (selectedImageBytes != null && selectedImageBytes.Length > 0)
                    await LoadThumbFromBytesAsync(selectedImageBytes);
            }

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
                    statusBlock.Text = Lf("dialog.vibe_encode.read_failed", file.Name);
                    return;
                }

                selectedImageBytes = pngBytes;
                selectedFileName = file.Name;
                selectedImagePath = string.IsNullOrWhiteSpace(file.Path) ? null : file.Path;
                fileNameBlock.Text = file.Name;
                fileNameBlock.Opacity = 1.0;
                encodeBtn.IsEnabled = true;
                statusBlock.Text = "";

                await LoadThumbFromBytesAsync(pngBytes);
            };

            encodeBtn.Click += async (_, _) =>
            {
                if (selectedImageBytes == null)
                {
                    statusBlock.Text = L("dialog.vibe_encode.select_image_first");
                    return;
                }

                if (IsAssetProtectionPaidFeatureLimitEnabled())
                {
                    var warnDialog = new ContentDialog
                    {
                        Title = L("dialog.vibe_encode.asset_protection_enabled"),
                        Content = L("dialog.vibe_encode.asset_protection_enabled_message"),
                        CloseButtonText = L("button.close"),
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
                string imageHash = VibeCacheService.ComputeImageHash(selectedImageBytes);

                if (isReencodeMode &&
                    !string.IsNullOrWhiteSpace(context!.FixedImageHash) &&
                    !string.Equals(imageHash, context.FixedImageHash, StringComparison.Ordinal))
                {
                    statusBlock.Text = Lf("dialog.vibe_encode.hash_mismatch",
                        context.FixedImageHash, imageHash);
                    return;
                }

                string? cached = VibeCacheService.TryGetCachedVibeByLookup(
                    cacheDir, imageHash, ie, model);
                if (cached != null)
                {
                    statusBlock.Text = Lf("dialog.vibe_encode.cache_hit", ie, cacheDir);
                    didEncode = true;
                    return;
                }

                encodeBtn.IsEnabled = false;
                browseBtn.IsEnabled = false;
                statusBlock.Text = L("dialog.vibe_encode.encoding");

                string imageBase64 = Convert.ToBase64String(selectedImageBytes);
                DebugLog($"[VibeEncode] 开始编码 | 模型={model} | IE={ie:0.00} | 再编码模式={isReencodeMode}");
                var (vibeData, error) = await _naiService.EncodeVibeAsync(imageBase64, model, ie);

                if (vibeData != null && vibeData.Length > 0)
                {
                    string savePath = VibeCacheService.SaveVibe(
                        cacheDir, selectedImageBytes, selectedImageBytes, vibeData, ie, model,
                        originalImagePath: selectedImagePath);
                    DebugLog($"[VibeEncode] 完成 | 保存={savePath}");
                    statusBlock.Text = Lf("dialog.vibe_encode.success", savePath);
                    didEncode = true;
                    _ = RefreshAnlasInfoAsync(forceRefresh: true);
                }
                else
                {
                    DebugLog($"[VibeEncode] 失败: {error ?? "未知错误"}");
                    statusBlock.Text = Lf("dialog.vibe_encode.failed", error ?? L("dialog.vibe_encode.unknown_error"));
                }

                encodeBtn.IsEnabled = true;
                browseBtn.IsEnabled = !isReencodeMode;
            };

            var panel = new StackPanel { Spacing = 10, MinWidth = 400 };

            panel.Children.Add(CreateThemedSubLabel(L("panel.model")));
            panel.Children.Add(modelCombo);

            var fileRow = new Grid { ColumnSpacing = 8 };
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(fileNameBlock, 0);
            Grid.SetColumn(browseBtn, 1);
            fileRow.Children.Add(fileNameBlock);
            fileRow.Children.Add(browseBtn);

            panel.Children.Add(CreateThemedSubLabel(L("dialog.vibe_encode.reference_image")));
            panel.Children.Add(fileRow);
            panel.Children.Add(thumbImage);

            if (isReencodeMode)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = L("dialog.vibe_encode.locked_image_hint"),
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.WrapWholeWords,
                });

                if (!string.IsNullOrWhiteSpace(context!.PresetImagePath))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = Lf("dialog.vibe_encode.using_original_path", context.PresetImagePath),
                        FontSize = 11,
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.WrapWholeWords,
                    });
                }

                if (context.OriginalMissingFallback)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = L("dialog.vibe_encode.using_thumbnail_warning"),
                        FontSize = 12,
                        Opacity = 0.9,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 160, 60)),
                    });
                }
            }

            panel.Children.Add(CreateThemedSubLabel(L("dialog.vibe_encode.ie_label")));
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
                Text = L("dialog.vibe_encode.cache_hint"),
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.5,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
            };
            panel.Children.Add(hintBlock);

            string dialogTitle = isReencodeMode
                ? L("dialog.vibe_encode.reencode_title")
                : L("dialog.vibe_encode.title");

            var dialog = new ContentDialog
            {
                Title = dialogTitle,
                Content = panel,
                CloseButtonText = L("button.close"),
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

        return didEncode;
    }
}
