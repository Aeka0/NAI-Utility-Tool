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
                ? Lf("inspect.loaded_with_metadata", Path.GetFileName(filePath))
                : Lf("inspect.loaded_without_metadata", Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("common.load_failed", ex.Message);
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
                ? Lf("inspect.loaded_with_metadata", sourceName ?? L("image.preview_label"))
                : Lf("inspect.loaded_without_metadata", sourceName ?? L("image.preview_label"));
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("common.load_failed", ex.Message);
        }
    }

    private void DisplayInspectMetadata(ImageMetadata? meta)
    {
        UpdateInspectActions();
        TxtInspectModel.Text = meta == null || meta.IsModelInference ? "-" : FormatInspectValue(meta.ModelDisplayName);
        InspectModelPanel.Visibility = meta != null ? Visibility.Visible : Visibility.Collapsed;
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
            InspectPlaceholder.Text = L("inspect.no_recognized_metadata");
            InspectPlaceholder.Visibility = Visibility.Visible;
            InspectContent.Visibility = Visibility.Collapsed;
            UpdateDynamicMenuStates();
            return;
        }

        if (!meta.IsNaiParsed && !meta.IsSdFormat && !meta.IsModelInference)
        {
            InspectPlaceholder.Visibility = Visibility.Collapsed;
            InspectContent.Visibility = Visibility.Collapsed;
            TxtInspectRawMeta.Visibility = Visibility.Visible;
            TxtInspectRawMeta.Text = meta.RawJson;
            UpdateDynamicMenuStates();
            return;
        }

        InspectPlaceholder.Visibility = Visibility.Collapsed;
        InspectContent.Visibility = Visibility.Visible;

        TxtInspectPositive.Text = FormatInspectValue(meta.PositivePrompt);
        TxtInspectNegative.Text = FormatInspectValue(meta.NegativePrompt);

        if (!meta.IsModelInference && meta.CharacterPrompts.Count > 0)
        {
            InspectCharPanel.Visibility = Visibility.Visible;
            InspectCharPanel.Children.Add(CreateThemedCaption(L("inspect.character_prompts")));
            for (int i = 0; i < meta.CharacterPrompts.Count; i++)
            {
                InspectCharPanel.Children.Add(CreateThemedSubLabel(Lf("character.label", i + 1)));
                InspectCharPanel.Children.Add(new TextBox
                {
                    Text = meta.CharacterPrompts[i],
                    IsReadOnly = true, AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 160,
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
            InspectCharNegPanel.Children.Add(CreateThemedCaption(L("inspect.character_negative_prompts")));
            for (int i = 0; i < meta.CharacterNegativePrompts.Count; i++)
            {
                InspectCharNegPanel.Children.Add(CreateThemedSubLabel(Lf("character.label", i + 1)));
                InspectCharNegPanel.Children.Add(new TextBox
                {
                    Text = meta.CharacterNegativePrompts[i],
                    IsReadOnly = true, AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 120,
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
        TxtInspectSeed.Text = !string.IsNullOrEmpty(meta.Seed) ? meta.Seed : "-";
        TxtInspectVariety.Text = meta.IsSdFormat || meta.IsModelInference ? "-" : ((meta.SmDyn || meta.Sm) ? L("common.yes") : L("common.no"));
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
            Width = 720,
            MinHeight = 400,
            MaxHeight = 600,
        };
        textBox.Text = prettyJson;

        var dialog = new ContentDialog
        {
            Title = L("inspect.raw.edit_title"),
            Content = textBox,
            PrimaryButtonText = L("common.ok"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 800.0;

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            string compactJson = ImageMetadataService.CompactJson(textBox.Text);
            if (compactJson == ImageMetadataService.CompactJson(_inspectMetadata.RawJson)) return;

            var newMeta = ImageMetadataService.TryParseJson(compactJson);
            if (newMeta != null)
            {
                newMeta.IsNaiParsed = true;
                _inspectMetadata = newMeta;
                _inspectRawModified = true;
                DisplayInspectMetadata(newMeta);
                UpdateInspectSaveState();
                ReplaceEditMenu();
                TxtStatus.Text = L("inspect.raw.updated");
            }
            else
            {
                _inspectMetadata.RawJson = compactJson;
                _inspectRawModified = true;
                UpdateInspectSaveState();
                TxtStatus.Text = L("inspect.raw.saved_json_parse_failed");
            }
        }
    }

    private void UpdateInspectSaveState()
    {
        UpdateFileMenuState();
    }

    private async Task<byte[]?> GetInspectSaveBytesAsync(bool stripMetadata, bool forcePng = false)
    {
        if (_inspectImageBytes == null) return null;

        var imageBytes = forcePng && !HasPngSignature(_inspectImageBytes)
            ? await Task.Run(() => EnsurePngEncoded(_inspectImageBytes))
            : _inspectImageBytes;

        if (stripMetadata)
        {
            if (!HasPngSignature(imageBytes))
                imageBytes = await Task.Run(() => EnsurePngEncoded(imageBytes));
            return await Task.Run(() => ImageMetadataService.StripPngMetadata(imageBytes));
        }

        if (_inspectRawModified && _inspectMetadata != null)
        {
            if (!HasPngSignature(imageBytes))
                imageBytes = await Task.Run(() => EnsurePngEncoded(imageBytes));
            return await Task.Run(() => ImageMetadataService.ReplacePngComment(imageBytes, _inspectMetadata.RawJson));
        }

        return imageBytes;
    }

    private async Task SaveInspectOverwriteAsync()
    {
        if (!_inspectRawModified)
        { TxtStatus.Text = L("inspect.raw.unchanged"); return; }

        if (!string.IsNullOrEmpty(_inspectImagePath) && File.Exists(_inspectImagePath))
        {
            var savePath = ResolveOverwriteSavePath(_inspectImagePath, out bool redirectedToPng);
            var bytesToSave = await GetInspectSaveBytesAsync(stripMetadata: false, forcePng: redirectedToPng);
            if (bytesToSave == null)
            { TxtStatus.Text = L("file.error.no_image_to_save"); return; }
            bytesToSave = await PrepareImageBytesForSaveAsync(bytesToSave, stripMetadata: false);

            try
            {
                await File.WriteAllBytesAsync(savePath, bytesToSave);
                _inspectImageBytes = bytesToSave;
                _inspectImagePath = savePath;
                _inspectRawModified = false;
                UpdateInspectSaveState();
                TxtStatus.Text = ShouldStripSavedImageMetadata(false)
                    ? Lf("file.saved_path_stripped", savePath)
                    : Lf("file.saved_path", savePath);
            }
            catch (Exception ex) { TxtStatus.Text = Lf("common.save_failed", ex.Message); }
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
            TxtStatus.Text = L("file.error.no_image_to_save");
            return;
        }

        if (!string.IsNullOrEmpty(_effectsImagePath) && File.Exists(_effectsImagePath))
        {
            var savePath = ResolveOverwriteSavePath(_effectsImagePath, out bool redirectedToPng);
            if (redirectedToPng)
                bytesToSave = await Task.Run(() => EnsurePngEncoded(bytesToSave));
            bytesToSave = await PrepareImageBytesForSaveAsync(bytesToSave, stripMetadata: false);

            try
            {
                await File.WriteAllBytesAsync(savePath, bytesToSave);
                _effectsImagePath = savePath;
                MarkEffectsWorkspaceClean();
                RefreshEffectsPanel();
                UpdateFileMenuState();
                TxtStatus.Text = ShouldStripSavedImageMetadata(false)
                    ? Lf("file.saved_path_stripped", savePath)
                    : Lf("file.saved_path", savePath);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = Lf("common.save_failed", ex.Message);
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

    private async void OnInspectTagger(object sender, RoutedEventArgs e)
    {
        await RunInspectReverseTagAsync();
    }

    private async void OnInspectImport(object sender, RoutedEventArgs e)
    {
        if ((_inspectImageBytes == null && !HasImportableInspectContent) || _inspectInferenceRunning) return;
        await ImportMetadataAsync(_inspectMetadata ?? new ImageMetadata(), fileName: _inspectImagePath == null ? L("image.preview_label") : Path.GetFileName(_inspectImagePath),
            chooseTarget: true, sourceImage: _inspectImageBytes, sourcePath: _inspectImagePath);
    }

}
