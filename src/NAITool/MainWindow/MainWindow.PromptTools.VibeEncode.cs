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
    private async void ShowVibeEncodeDialog()
    {
        if (_isVibeEncodeDialogOpen) return;
        _isVibeEncodeDialogOpen = true;

        try
        {
            byte[]? selectedImageBytes = null;
            string? selectedFileName = null;

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
                Text = L("dialog.vibe_encode.no_image_selected"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260,
                Opacity = 0.6,
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
                    statusBlock.Text = Lf("dialog.vibe_encode.read_failed", file.Name);
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

                string? cached = VibeCacheService.TryGetCachedVibeByLookup(
                    cacheDir, imageHash, ie, model);
                if (cached != null)
                {
                    statusBlock.Text = Lf("dialog.vibe_encode.cache_hit", ie, cacheDir);
                    return;
                }

                encodeBtn.IsEnabled = false;
                browseBtn.IsEnabled = false;
                statusBlock.Text = L("dialog.vibe_encode.encoding");

                string imageBase64 = Convert.ToBase64String(selectedImageBytes);
                DebugLog($"[VibeEncode] Start | Model={model} | IE={ie:0.00}");
                var (vibeData, error) = await _naiService.EncodeVibeAsync(imageBase64, model, ie);

                if (vibeData != null && vibeData.Length > 0)
                {
                    string savePath = VibeCacheService.SaveVibe(
                        cacheDir, selectedImageBytes, selectedImageBytes, vibeData, ie, model);
                    DebugLog($"[VibeEncode] Completed | Saved={savePath}");
                    statusBlock.Text = Lf("dialog.vibe_encode.success", savePath);
                    _ = RefreshAnlasInfoAsync(forceRefresh: true);
                }
                else
                {
                    DebugLog($"[VibeEncode] Failed: {error ?? "Unknown error"}");
                    statusBlock.Text = Lf("dialog.vibe_encode.failed", error ?? L("dialog.vibe_encode.unknown_error"));
                }

                encodeBtn.IsEnabled = true;
                browseBtn.IsEnabled = true;
            };

            var rootGrid = (Grid)this.Content;

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

            var dialog = new ContentDialog
            {
                Title = L("dialog.vibe_encode.title"),
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
    }
}
