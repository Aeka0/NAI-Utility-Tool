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
    private async Task ApplySuperDropGenerationPromptAsync(StorageFile file)
    {
        byte[] bytes = await File.ReadAllBytesAsync(file.Path);
        var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
        if (meta != null && (meta.IsNaiParsed || meta.IsSdFormat || meta.IsModelInference))
            ApplyMetadataToGeneration(meta);
        else
            TxtStatus.Text = Lf("metadata.no_usable_generation_metadata", file.Name);
    }

    private async Task ApplySuperDropI2IPromptAsync(StorageFile file)
    {
        byte[] bytes = await File.ReadAllBytesAsync(file.Path);
        var meta = await Task.Run(() => ImageMetadataService.ReadFromBytes(bytes));
        if (meta != null && (meta.IsNaiParsed || meta.IsSdFormat))
            ApplyMetadataToI2I(meta, file.Name);
        else
            TxtStatus.Text = Lf("metadata.no_usable_generation_metadata", file.Name);
    }

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
        if (IsPromptMode(_currentMode))
            SaveCurrentPromptToBuffer();

        ApplyLocalization();
        RefreshLocalizedDynamicInterface();
        ReplaceEditMenu();
        ReplaceToolMenu();
        ApplyStaticMenuAndComboTypography();
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
        RootGrid.Language = UiLanguageTag;
        if (_advRootPanel != null)
            _advRootPanel.Language = UiLanguageTag;

        Title = L("app.title");
        AppTitleText.Text = L("app.title");

        MenuFile.Title = L("menu.file");
        MenuOpenImage.Text = L("menu.file.open_image");
        MenuSave.Text = L("menu.file.save");
        MenuSaveAs.Text = L("menu.file.save_as");
        MenuSaveStripped.Text = L("menu.file.save_as_stripped");
        MenuExportCanvasMask.Text = L("menu.file.export_canvas_mask");
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
        MenuQuotaSettings.Text = L("menu.settings.quota");
        MenuNetworkSettings.Text = L("menu.settings.network");
        MenuReverseTaggerSettings.Text = L("menu.settings.reverse_tagger");
        MenuAppearance.Text = L("menu.settings.appearance");
        MenuThemeSystem.Text = L("menu.settings.theme.system");
        MenuThemeLight.Text = L("menu.settings.theme.light");
        MenuThemeDark.Text = L("menu.settings.theme.dark");
        MenuTransparencyStandard.Text = L("menu.settings.transparency.standard");
        MenuTransparencyLesser.Text = L("menu.settings.transparency.lesser");
        MenuTransparencyOpaque.Text = L("menu.settings.transparency.opaque");
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
        TabI2I.Content = L("mode.i2i");
        TabUpscale.Content = L("mode.upscale");
        TabEffects.Content = L("mode.post");
        TabInspect.Content = L("mode.inspect");
        UpdatePromptTabText();

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
        SetInspectPrimaryAction(_inspectPrimaryAction, BtnSendToGen.IsEnabled);
        TxtSendInspectToI2I.Text = L("button.send_whole_to_i2i");

        TxtUpscaleModelLabel.Text = L("upscale.model");
        TxtUpscaleScaleLabel.Text = L("upscale.scale");
        TxtUpscaleDeviceLabel.Text = L("upscale.device");
        CboUpscaleDeviceGpu.Content = L("upscale.device_gpu");
        CboUpscaleDeviceCpu.Content = L("upscale.device_cpu");
        TxtUpscaleBeforeLabel.Text = L("upscale.before");
        TxtUpscaleAfterLabel.Text = L("upscale.after_estimated");
        TxtStartUpscaleButton.Text = _upscaleRunning ? L("button.upscaling") : L("button.start_upscale");

        GenPlaceholder.Text = L("placeholder.generate");
        TxtSendGenToI2I.Text = L("button.send_to_i2i");
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
        TxtSuperDropGeneratePrompt.Text = L("superdrop.generate_prompt");
        TxtSuperDropGenerateVibe.Text = L("superdrop.generate_vibe");
        TxtSuperDropGeneratePrecise.Text = L("superdrop.generate_precise");
        TxtSuperDropI2IPrompt.Text = L("superdrop.i2i_prompt");
        TxtSuperDropI2IVibe.Text = L("superdrop.i2i_vibe");
        TxtSuperDropI2IPrecise.Text = L("superdrop.i2i_precise");
        TxtSuperDropUpscale.Text = L("superdrop.upscale");
        TxtSuperDropEffects.Text = L("superdrop.effects");
        TxtSuperDropInspect.Text = L("superdrop.inspect");
        TxtHistoryTitle.Text = L("history.title");
        HistoryDatePicker.PlaceholderText = L("history.select_date");

        TxtI2IPreviewLabel.Text = L("inpaint.preview");
        TxtZoomInfo.Text = Lf("status.zoom", 100d);
        TxtI2IModeLabel.Text = L("i2i.mode");
        BtnI2IInpaintMode.Content = L("i2i.mode.inpaint");
        BtnI2IDenoiseMode.Content = L("i2i.mode.denoise");
        ChkPreviewMask.Content = L("inpaint.preview_mask");
        TxtI2IToolsLabel.Text = L("inpaint.tools");
        TxtBrushSizeLabel.Text = L("inpaint.brush_size");
        TxtDenoiseStrengthLabel.Text = L("i2i.denoise_strength");
        TxtDenoiseNoiseLabel.Text = L("i2i.denoise_noise");
        UpdateI2IEditModeUI();

        TxtStatus.Text = L("status.ready");
    }

    private static TextBlock CreateTabHeaderText(string text, double fontSize = 13)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void UpdatePromptTabText()
    {
        if (TabPositive == null || TabNegative == null)
            return;

        string code = LocalizationService.NormalizeLanguageCode(_settings.Settings.LanguageCode);
        bool useCompact = ShouldUseCompactPromptTabText();
        if (_promptTabsUsingCompact == useCompact && _promptTabLanguageCode == code)
            return;

        TabPositive.Content = CreateTabHeaderText(L(useCompact ? "prompt.positive_compact" : "prompt.positive"));
        TabNegative.Content = CreateTabHeaderText(L(useCompact ? "prompt.negative_compact" : "prompt.negative"));
        _promptTabsUsingCompact = useCompact;
        _promptTabLanguageCode = code;
    }

    private bool ShouldUseCompactPromptTabText()
    {
        string code = LocalizationService.NormalizeLanguageCode(_settings.Settings.LanguageCode);
        double threshold = code switch
        {
            "en_us" => 116,
            "ja_jp" => 136,
            _ => 0,
        };

        if (threshold <= 0)
            return false;

        double availableWidth = PromptTabRow?.ActualWidth ?? 0;
        if (availableWidth < 1)
            availableWidth = (PanelLeftMain?.ActualWidth ?? MainContentGrid?.ColumnDefinitions[0].ActualWidth ?? 300) - 24;

        double reservedRight = BtnSplitPrompt?.Visibility == Visibility.Visible ? 42 : 0;
        double perTabWidth = Math.Max(0, (availableWidth - reservedRight) / 2.0);
        return perTabWidth < threshold;
    }

    private void RefreshLocalizedDynamicInterface()
    {
        if (IsPromptMode(_currentMode))
        {
            LoadPromptFromBuffer();
            UpdateSplitVisibility();
        }

        RefreshCharacterPanel();
        SetInspectPrimaryAction(_inspectPrimaryAction, BtnSendToGen.IsEnabled);
        SetUpscaleButtonText(_upscaleRunning ? L("button.upscaling") : L("button.start_upscale"));
        UpdatePromptTabText();
        UpdateAutoGenUI();
        UpdateGenerateButtonWarning();
        UpdateI2IRedoButtonWarning();
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

}
