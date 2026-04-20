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
            SetInspectPrimaryAction(InspectPrimaryAction.InferTags, _inspectImageBytes != null);
            BtnSendInspectToI2I.IsEnabled = _inspectImageBytes != null;
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
            BtnSendInspectToI2I.IsEnabled = _inspectImageBytes != null;
            UpdateDynamicMenuStates();
            return;
        }

        InspectPlaceholder.Visibility = Visibility.Collapsed;
        InspectContent.Visibility = Visibility.Visible;
        SetInspectPrimaryAction(InspectPrimaryAction.SendMetadata, true);
        BtnSendInspectToI2I.IsEnabled = true;

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
            InspectCharNegPanel.Children.Add(CreateThemedCaption(L("inspect.character_negative_prompts")));
            for (int i = 0; i < meta.CharacterNegativePrompts.Count; i++)
            {
                InspectCharNegPanel.Children.Add(CreateThemedSubLabel(Lf("character.label", i + 1)));
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

            try
            {
                await File.WriteAllBytesAsync(savePath, bytesToSave);
                _inspectImageBytes = bytesToSave;
                _inspectImagePath = savePath;
                _inspectRawModified = false;
                UpdateInspectSaveState();
                TxtStatus.Text = Lf("file.saved_path", savePath);
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

            try
            {
                await File.WriteAllBytesAsync(savePath, bytesToSave);
                _effectsImagePath = savePath;
                MarkEffectsWorkspaceClean();
                RefreshEffectsPanel();
                UpdateFileMenuState();
                TxtStatus.Text = Lf("file.saved_path", savePath);
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

    private void OnSendInspectToI2I(object sender, RoutedEventArgs e)
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

        SendImageToI2I(_inspectImageBytes, _inspectImagePath);

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
        ApplyMetadataToGeneration(_inspectMetadata);
    }

    private void ApplyMetadataToGeneration(ImageMetadata meta)
    {
        SwitchMode(AppMode.ImageGeneration);

        bool blockOversizedSteps = IsAssetProtectionStepLimitEnabled();
        bool blockOversizedDimensions = IsAssetProtectionSizeLimitEnabled();
        var skipped = new List<string>();
        var notes = new List<string>();

        string positivePrompt = meta.PositivePrompt;
        string negativePrompt = meta.NegativePrompt;

        if (meta.IsSdFormat)
        {
            positivePrompt = ImageMetadataService.ConvertSdPromptToNai(positivePrompt);
            negativePrompt = ImageMetadataService.ConvertSdPromptToNai(negativePrompt);
            notes.Add(L("metadata.note.sd_converted"));
        }

        var p = _settings.Settings.GenParameters;
        var presetMatch = ExtractImportedPromptPresetMatch(positivePrompt, negativePrompt, p.Model);
        positivePrompt = presetMatch.PositivePrompt;
        negativePrompt = presetMatch.NegativePrompt;

        _genPositivePrompt = positivePrompt;
        _genNegativePrompt = negativePrompt;
        _genStylePrompt = "";

        if (meta.IsModelInference)
        {
            _genCharacters.Clear();
            ClearReferenceFeatures();
            RefreshCharacterPanel();
            LoadPromptFromBuffer();
            UpdateSplitVisibility();
            UpdateSizeWarningVisuals();
            if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();
            TxtStatus.Text = L("inspect.sent_reverse_result_to_generate");
            return;
        }

        p.QualityToggle = presetMatch.QualityMatched;
        p.UcPreset = presetMatch.UcPresetMatched ?? 2;

        if (meta.Steps > 0)
        {
            if (blockOversizedSteps && meta.Steps > 28)
                skipped.Add(Lf("metadata.skipped.steps", meta.Steps));
            else
                p.Steps = meta.Steps;
        }
        if (meta.Seed > 0 && meta.Seed <= int.MaxValue) p.Seed = (int)meta.Seed;
        if (meta.Scale > 0) p.Scale = meta.Scale;
        if (!meta.IsSdFormat) p.CfgRescale = meta.CfgRescale;
        if (!string.IsNullOrEmpty(meta.Sampler)) p.Sampler = NormalizeSamplerForModel(meta.Sampler, p.Model);
        if (!string.IsNullOrEmpty(meta.NoiseSchedule)) p.Schedule = NormalizeScheduleForModel(meta.NoiseSchedule, p.Model, p.Schedule);
        if (!meta.IsSdFormat) p.Variety = meta.SmDyn || meta.Sm;

        p.Sampler = NormalizeSamplerForModel(p.Sampler, p.Model);
        p.Schedule = NormalizeScheduleForModel(p.Schedule, p.Model);

        if (meta.Width > 0 && meta.Height > 0)
        {
            if (blockOversizedDimensions && (long)meta.Width * meta.Height > 1024L * 1024)
                skipped.Add(Lf("metadata.skipped.size", meta.Width, meta.Height));
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

        RefreshCharacterPanel();

        SetSizeInputsSilently(_customWidth, _customHeight);
        NbSeed.Value = p.Seed;
        ChkVariety.IsChecked = p.Variety;
        if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();

        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        UpdateSizeWarningVisuals();

        if (presetMatch.QualityMatched) notes.Add(L("metadata.note.quality_extracted"));
        if (presetMatch.UcPresetMatched.HasValue)
            notes.Add(Lf("metadata.note.negative_quality_extracted", GetUcPresetDisplayName(presetMatch.UcPresetMatched.Value)));
        if (skipped.Count > 0) notes.Add(Lf("metadata.note.incompatible_skipped", string.Join(", ", skipped)));
        if (meta.CharacterPrompts.Count > 0) notes.Add(Lf("metadata.note.characters_imported", meta.CharacterPrompts.Count));
        AppendReferenceImportNotes(meta, notes);

        TxtStatus.Text = notes.Count > 0
            ? Lf("inspect.sent_parameters_with_notes", string.Join("; ", notes))
            : L("inspect.sent_parameters_to_generate");
    }

    private void ApplyMetadataToI2I(ImageMetadata meta, string fileName)
    {
        var notes = new List<string>();
        var skipped = new List<string>();
        bool blockOversizedSteps = IsAssetProtectionStepLimitEnabled();

        string positivePrompt = meta.PositivePrompt;
        string negativePrompt = meta.NegativePrompt;

        if (meta.IsSdFormat)
        {
            positivePrompt = ImageMetadataService.ConvertSdPromptToNai(positivePrompt);
            negativePrompt = ImageMetadataService.ConvertSdPromptToNai(negativePrompt);
            notes.Add(L("metadata.note.sd_converted"));
        }

        var p = _settings.Settings.InpaintParameters;
        var presetMatch = ExtractImportedPromptPresetMatch(positivePrompt, negativePrompt, p.Model);
        positivePrompt = presetMatch.PositivePrompt;
        negativePrompt = presetMatch.NegativePrompt;

        _i2iPositivePrompt = positivePrompt;
        _i2iNegativePrompt = negativePrompt;
        _i2iStylePrompt = "";

        p.QualityToggle = presetMatch.QualityMatched;
        p.UcPreset = presetMatch.UcPresetMatched ?? 2;

        if (meta.Steps > 0)
        {
            if (blockOversizedSteps && meta.Steps > 28)
                skipped.Add(Lf("metadata.skipped.steps", meta.Steps));
            else
                p.Steps = meta.Steps;
        }
        if (meta.Seed > 0 && meta.Seed <= int.MaxValue) p.Seed = (int)meta.Seed;
        if (meta.Scale > 0) p.Scale = meta.Scale;
        if (!meta.IsSdFormat) p.CfgRescale = meta.CfgRescale;
        if (!string.IsNullOrEmpty(meta.Sampler)) p.Sampler = NormalizeSamplerForModel(meta.Sampler, p.Model);
        if (!string.IsNullOrEmpty(meta.NoiseSchedule)) p.Schedule = NormalizeScheduleForModel(meta.NoiseSchedule, p.Model, p.Schedule);
        if (!meta.IsSdFormat) p.Variety = meta.SmDyn || meta.Sm;

        p.Sampler = NormalizeSamplerForModel(p.Sampler, p.Model);
        p.Schedule = NormalizeScheduleForModel(p.Schedule, p.Model);

        NbSeed.Value = p.Seed;
        ChkVariety.IsChecked = p.Variety;

        if (meta.IsNaiParsed)
        {
            if (meta.CharacterPrompts.Count > 0)
                SetGenCharactersFromMetadata(meta);
            else
                _genCharacters.Clear();
            ApplyReferenceDataFromMetadata(meta);
        }

        RefreshCharacterPanel();
        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        if (IsAdvancedWindowOpen) SyncSidebarToAdvanced();

        if (presetMatch.QualityMatched) notes.Add(L("metadata.note.quality_extracted"));
        if (presetMatch.UcPresetMatched.HasValue)
            notes.Add(Lf("metadata.note.negative_quality_extracted", GetUcPresetDisplayName(presetMatch.UcPresetMatched.Value)));
        if (skipped.Count > 0) notes.Add(Lf("metadata.note.skipped", string.Join(", ", skipped)));
        if (meta.IsNaiParsed && meta.CharacterPrompts.Count > 0)
            notes.Add(Lf("metadata.note.characters_imported", meta.CharacterPrompts.Count));
        AppendReferenceImportNotes(meta, notes);

        TxtStatus.Text = notes.Count > 0
            ? Lf("metadata.applied_with_notes", fileName, string.Join("; ", notes))
            : Lf("metadata.applied", fileName);
    }

}
