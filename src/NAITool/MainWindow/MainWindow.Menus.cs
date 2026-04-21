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
    private void PopulateModelList()
    {
        CboModel.Items.Clear();
        var models = _currentMode == AppMode.ImageGeneration || _i2iEditMode == I2IEditMode.Denoise
            ? GenerationModels
            : I2IModels;
        foreach (var m in models) CboModel.Items.Add(CreateTextComboBoxItem(m));

        CboModel.SelectedIndex = Array.IndexOf(models, CurrentParams.Model);
        if (CboModel.SelectedIndex < 0) CboModel.SelectedIndex = 0;
        ApplyMenuTypography(CboModel);
    }

    // ═══════════════════════════════════════════════════════════
    //  编辑菜单：替换整个 MenuBarItem 避免 WinUI Clear() bug
    // ═══════════════════════════════════════════════════════════

    private void ReplaceEditMenu()
    {
        int idx = AppMenuBar.Items.IndexOf(MenuEdit);
        if (idx < 0) idx = 1;

        AppMenuBar.Items.Remove(MenuEdit);

        var newEdit = new MenuBarItem { Title = L("menu.edit") };

        if (_currentMode == AppMode.ImageGeneration)
        {
            newEdit.Items.Add(BuildPresetResolutionSubMenu());
            newEdit.Items.Add(new MenuFlyoutSeparator());

            var normalizeItem = CreateLocalizedMenuItem(
                MenuCommandNormalizePrompts,
                "menu.edit.normalize_prompts",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8D2" });
            normalizeItem.Click += OnNormalizePrompts;
            newEdit.Items.Add(normalizeItem);

            var randomStyleItem = CreateLocalizedMenuItem(
                MenuCommandRandomStylePrompt,
                "menu.edit.random_style_prompt",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B1" });
            randomStyleItem.Click += OnRandomStylePrompt;
            newEdit.Items.Add(randomStyleItem);
            newEdit.Items.Add(BuildPromptShortcutsMenuItem());
            newEdit.Items.Add(new MenuFlyoutSeparator());

            var sendItem = CreateLocalizedMenuItem(
                MenuCommandSendToI2I,
                "action.send_to_i2i",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" });
            sendItem.Click += OnSendToI2I;
            newEdit.Items.Add(sendItem);

            var postItem = CreateLocalizedMenuItem(
                MenuCommandSendToPost,
                "action.send_to_post",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" });
            postItem.Click += OnSendToEffectsFromGen;
            newEdit.Items.Add(postItem);

            var upscaleItem = CreateLocalizedMenuItem(
                MenuCommandSendToUpscale,
                "action.send_to_upscale",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9" });
            upscaleItem.Click += OnSendToUpscaleFromGen;
            newEdit.Items.Add(upscaleItem);

            newEdit.Items.Add(new MenuFlyoutSeparator());
            var clearAllItem = CreateLocalizedMenuItem(
                MenuCommandClearAllPrompts,
                "menu.edit.clear_all_prompts",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74D" });
            clearAllItem.Click += OnClearAllPrompts;
            newEdit.Items.Add(clearAllItem);

            var resetParamsItem = CreateLocalizedMenuItem(
                MenuCommandResetGenerationParams,
                "menu.edit.reset_generation_params",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE777" });
            resetParamsItem.Click += OnResetGenParams;
            newEdit.Items.Add(resetParamsItem);
        }
        else if (_currentMode == AppMode.I2I)
        {
            BuildI2IEditMenuItems(newEdit);
        }
        else if (_currentMode == AppMode.Inspect)
        {
            var rawItem = CreateLocalizedMenuItem(
                MenuCommandEditRawMetadata,
                "menu.edit.edit_raw_metadata",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE70F" });
            rawItem.IsEnabled = _inspectImageBytes != null;
            rawItem.Click += OnEditRawMetadata;
            newEdit.Items.Add(rawItem);

            newEdit.Items.Add(new MenuFlyoutSeparator());

            var scrambleMenu = CreateLocalizedSubItem(
                MenuCommandImageScramble,
                "menu.edit.image_scramble",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uF404" });
            scrambleMenu.IsEnabled = _inspectImageBytes != null;
            var encryptItem = CreateLocalizedMenuItem(
                MenuCommandScramble,
                "menu.edit.scramble",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE727" });
            encryptItem.Click += (_, _) => RunInspectImageScrambleAsync(ImageScrambleService.ProcessType.Encrypt);
            var decryptItem = CreateLocalizedMenuItem(
                MenuCommandUnscramble,
                "menu.edit.unscramble",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8D7" });
            decryptItem.Click += (_, _) => RunInspectImageScrambleAsync(ImageScrambleService.ProcessType.Decrypt);

            scrambleMenu.Items.Add(encryptItem);
            scrambleMenu.Items.Add(decryptItem);
            newEdit.Items.Add(scrambleMenu);
        }
        else if (_currentMode == AppMode.Upscale)
        {
            var reloadItem = CreateReloadImageMenuItem();
            reloadItem.Click += OnReloadImage;
            newEdit.Items.Add(reloadItem);
            newEdit.Items.Add(new MenuFlyoutSeparator());

            var sendI2IItem = CreateLocalizedMenuItem(
                MenuCommandSendToI2I,
                "action.send_to_i2i",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" });
            sendI2IItem.Click += OnSendToI2IFromUpscale;
            newEdit.Items.Add(sendI2IItem);

            var sendPostItem = CreateLocalizedMenuItem(
                MenuCommandSendToPost,
                "action.send_to_post",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" });
            sendPostItem.Click += OnSendToEffectsFromUpscale;
            newEdit.Items.Add(sendPostItem);
        }
        else if (_currentMode == AppMode.Effects)
        {
            var undoItem = CreateLocalizedMenuItem(MenuCommandUndo, "menu.edit.undo", new SymbolIcon(Symbol.Undo));
            undoItem.Click += OnUndo;
            undoItem.KeyboardAccelerators.Add(new KeyboardAccelerator
            { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.Z });
            newEdit.Items.Add(undoItem);

            var redoItem = CreateLocalizedMenuItem(MenuCommandRedo, "menu.edit.redo", new SymbolIcon(Symbol.Redo));
            redoItem.Click += OnRedo;
            redoItem.KeyboardAccelerators.Add(new KeyboardAccelerator
            { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.Y });
            newEdit.Items.Add(redoItem);

            newEdit.Items.Add(new MenuFlyoutSeparator());

            var reloadItem = CreateReloadImageMenuItem();
            reloadItem.Click += OnReloadImage;
            newEdit.Items.Add(reloadItem);

            newEdit.Items.Add(new MenuFlyoutSeparator());

            var sendI2IItem = CreateLocalizedMenuItem(
                MenuCommandSendToI2I,
                "action.send_to_i2i",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEDFB" });
            sendI2IItem.Click += OnSendToI2IFromEffects;
            newEdit.Items.Add(sendI2IItem);
            newEdit.Items.Add(new MenuFlyoutSeparator());

            var addPresetItem = CreateLocalizedMenuItem(
                MenuCommandAddPreset,
                "menu.edit.add_preset",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE70F" });
            addPresetItem.Click += OnAddEffectsPreset;
            newEdit.Items.Add(addPresetItem);

            var usePresetItem = CreateLocalizedMenuItem(
                MenuCommandUsePreset,
                "menu.edit.use_preset",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE790" });
            usePresetItem.Click += OnUseEffectsPreset;
            newEdit.Items.Add(usePresetItem);
            newEdit.Items.Add(new MenuFlyoutSeparator());

            var clearEffectsItem = CreateLocalizedMenuItem(
                MenuCommandClearAllEffects,
                "menu.edit.clear_all_effects",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74D" });
            clearEffectsItem.Click += OnClearAllEffects;
            newEdit.Items.Add(clearEffectsItem);

            var applyEffectsItem = CreateLocalizedMenuItem(
                MenuCommandApplyEffects,
                "menu.edit.apply_effects",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8C7" });
            applyEffectsItem.Click += OnApplyEffects;
            newEdit.Items.Add(applyEffectsItem);
        }

        AppMenuBar.Items.Insert(idx, newEdit);
        MenuEdit = newEdit;
        ApplyMenuTypography(newEdit);
        UpdateDynamicMenuStates();
    }

    private void ReplaceToolMenu()
    {
        _menuTools ??= MenuTools;
        int idx = _menuTools != null ? AppMenuBar.Items.IndexOf(_menuTools) : -1;
        if (idx < 0) idx = 2;

        if (_menuTools != null)
            AppMenuBar.Items.Remove(_menuTools);

        var newTools = new MenuBarItem { Title = L("menu.tools") };

        var weightItem = CreateLocalizedMenuItem(
            MenuCommandWeightConverter,
            "menu.tools.weight_converter",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE943" });
        weightItem.Click += (_, _) => ShowWeightConversionDialog();
        newTools.Items.Add(weightItem);

        var vibeManagerItem = CreateLocalizedMenuItem(
            MenuCommandVibeManager,
            "menu.tools.vibe_manager",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE706" });
        vibeManagerItem.Click += (_, _) => ShowVibeManagerDialog();
        newTools.Items.Add(vibeManagerItem);

        var wildcardItem = CreateLocalizedMenuItem(
            MenuCommandWildcard,
            "menu.tools.wildcard",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74C" });
        wildcardItem.Click += (_, _) => ShowWildcardDialog();
        newTools.Items.Add(wildcardItem);

        var promptGeneratorItem = CreateLocalizedMenuItem(
            MenuCommandPromptGenerator,
            "menu.tools.prompt_generator",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uED1E" });
        promptGeneratorItem.Click += (_, _) => ShowPromptGeneratorDialog();
        newTools.Items.Add(promptGeneratorItem);

        if (_currentMode == AppMode.ImageGeneration)
        {
            newTools.Items.Add(new MenuFlyoutSeparator());

            var autoItem = CreateLocalizedMenuItem(
                MenuCommandAutomation,
                "menu.tools.automation",
                new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE768" });
            autoItem.Click += OnAutoGenSettings;
            newTools.Items.Add(autoItem);
        }

        AppMenuBar.Items.Insert(idx, newTools);
        _menuTools = newTools;
        ApplyMenuTypography(newTools);
        UpdateDynamicMenuStates();
    }

    private void UpdateDynamicMenuStates()
    {
        UpdateToolMenuStates();
        if (MenuEdit == null) return;

        if (_currentMode == AppMode.ImageGeneration)
        {
            bool hasImage = _currentGenImageBytes != null;
            foreach (var baseItem in MenuEdit.Items)
            {
                if (baseItem is MenuFlyoutItem item &&
                    (HasMenuCommand(item, MenuCommandSendToI2I) ||
                     HasMenuCommand(item, MenuCommandSendToPost) ||
                     HasMenuCommand(item, MenuCommandSendToUpscale)))
                    item.IsEnabled = hasImage;
            }
        }
        else if (_currentMode == AppMode.Upscale)
        {
            bool hasImage = _upscaleInputImageBytes != null;
            bool canReload = !string.IsNullOrWhiteSpace(_upscaleImagePath) &&
                File.Exists(_upscaleImagePath) &&
                !_upscaleRunning;
            foreach (var baseItem in MenuEdit.Items)
            {
                if (baseItem is not MenuFlyoutItem item) continue;
                if (HasMenuCommand(item, MenuCommandSendToI2I) ||
                    HasMenuCommand(item, MenuCommandSendToPost))
                    item.IsEnabled = hasImage;
                else if (HasMenuCommand(item, MenuCommandReloadImage))
                    item.IsEnabled = canReload;
            }
        }
        else if (_currentMode == AppMode.Effects)
        {
            bool hasImage = _effectsImageBytes != null;
            bool hasEffects = _effects.Count > 0;
            bool canReload = !string.IsNullOrWhiteSpace(_effectsImagePath) &&
                File.Exists(_effectsImagePath);
            foreach (var baseItem in MenuEdit.Items)
            {
                if (baseItem is not MenuFlyoutItem item) continue;
                if (HasMenuCommand(item, MenuCommandSendToI2I))
                    item.IsEnabled = hasImage;
                else if (HasMenuCommand(item, MenuCommandAddPreset))
                    item.IsEnabled = hasEffects;
                else if (HasMenuCommand(item, MenuCommandUsePreset))
                    item.IsEnabled = HasEffectsPresets();
                else if (HasMenuCommand(item, MenuCommandClearAllEffects))
                    item.IsEnabled = hasEffects;
                else if (HasMenuCommand(item, MenuCommandApplyEffects))
                    item.IsEnabled = hasImage && hasEffects;
                else if (HasMenuCommand(item, MenuCommandUndo))
                    item.IsEnabled = _effectsUndoStack.Count > 0;
                else if (HasMenuCommand(item, MenuCommandRedo))
                    item.IsEnabled = _effectsRedoStack.Count > 0;
                else if (HasMenuCommand(item, MenuCommandReloadImage))
                    item.IsEnabled = canReload;
            }
        }
        else if (_currentMode == AppMode.I2I)
        {
            bool hasImageLoaded = MaskCanvas.Document.OriginalImage != null && !MaskCanvas.IsInPreviewMode;
            bool inpaintMode = _i2iEditMode == I2IEditMode.Inpaint;
            bool hasMaskContent = inpaintMode && MaskCanvas.HasMaskContent() && !MaskCanvas.IsInPreviewMode;
            bool hasI2IImage = MaskCanvas.Document.OriginalImage != null ||
                (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null);
            bool canReload = !string.IsNullOrWhiteSpace(MaskCanvas.LoadedFilePath) &&
                File.Exists(MaskCanvas.LoadedFilePath) &&
                !MaskCanvas.IsInPreviewMode;

            foreach (var baseItem in MenuEdit.Items)
            {
                if (baseItem is MenuFlyoutItem item)
                {
                    if (HasMenuCommand(item, MenuCommandUndo) || HasMenuCommand(item, MenuCommandRedo))
                        item.IsEnabled = inpaintMode && !MaskCanvas.IsInPreviewMode;
                    else if (HasMenuCommand(item, MenuCommandExpandMask) || HasMenuCommand(item, MenuCommandContractMask))
                        item.IsEnabled = hasMaskContent;
                    else if (HasMenuCommand(item, MenuCommandSendToPost) || HasMenuCommand(item, MenuCommandSendToUpscale))
                        item.IsEnabled = hasI2IImage;
                    else if (HasMenuCommand(item, MenuCommandReloadImage))
                        item.IsEnabled = canReload;
                }
                else if (baseItem is MenuFlyoutSubItem sub && HasMenuCommand(sub, MenuCommandAlignImage))
                {
                    sub.IsEnabled = hasImageLoaded;
                    foreach (var child in sub.Items)
                    {
                        if (child is MenuFlyoutItem childItem)
                            childItem.IsEnabled = hasImageLoaded;
                    }
                }
                else if (baseItem is MenuFlyoutSubItem inferSub && HasMenuCommand(inferSub, MenuCommandPromptInference))
                {
                    inferSub.IsEnabled = hasI2IImage;
                    foreach (var child in inferSub.Items)
                    {
                        if (child is MenuFlyoutItem childItem)
                            childItem.IsEnabled = hasI2IImage;
                    }
                }
                else if (baseItem is MenuFlyoutSubItem maskSub && HasMenuCommand(maskSub, MenuCommandMaskOps))
                {
                    maskSub.IsEnabled = inpaintMode && !MaskCanvas.IsInPreviewMode;
                    foreach (var child in maskSub.Items)
                    {
                        if (child is not MenuFlyoutItem childItem) continue;
                        if (HasMenuCommand(childItem, MenuCommandExpandMask) || HasMenuCommand(childItem, MenuCommandContractMask))
                            childItem.IsEnabled = hasMaskContent;
                        else
                            childItem.IsEnabled = !MaskCanvas.IsInPreviewMode;
                    }
                }
            }
        }

        UpdateFileMenuState();
    }

    private void UpdateToolMenuStates()
    {
        if (_menuTools == null) return;

        foreach (var baseItem in _menuTools.Items)
        {
            if (baseItem is MenuFlyoutItem item && HasMenuCommand(item, MenuCommandWeightConverter))
                item.IsEnabled = true;
        }
    }

    private void UpdateFileMenuState()
    {
        bool hasGenImage = _currentGenImageBytes != null;
        bool hasI2IImage = MaskCanvas.Document.OriginalImage != null ||
            (MaskCanvas.IsInPreviewMode && _pendingResultBitmap != null);
        bool hasUpscaleImage = _upscaleInputImageBytes != null;
        bool hasPostImage = _effectsImageBytes != null;
        bool hasReaderImage = _inspectImageBytes != null;

        MenuSave.Visibility = _currentMode switch
        {
            AppMode.I2I => Visibility.Visible,
            AppMode.Inspect when _inspectRawModified => Visibility.Visible,
            AppMode.Effects when _effectsImageBytes != null => Visibility.Visible,
            _ => Visibility.Collapsed,
        };
        MenuSave.IsEnabled = _currentMode switch
        {
            AppMode.I2I => hasI2IImage,
            AppMode.Inspect => hasReaderImage && _inspectRawModified,
            AppMode.Effects => hasPostImage,
            _ => false,
        };

        MenuSaveAs.IsEnabled = _currentMode switch
        {
            AppMode.ImageGeneration => hasGenImage,
            AppMode.I2I => hasI2IImage,
            AppMode.Upscale => hasUpscaleImage,
            AppMode.Effects => hasPostImage,
            AppMode.Inspect => hasReaderImage,
            _ => false,
        };

        MenuSaveStripped.Visibility = (_currentMode == AppMode.Inspect || _currentMode == AppMode.ImageGeneration || _currentMode == AppMode.I2I)
            ? Visibility.Visible
            : Visibility.Collapsed;
        MenuExportCanvasMask.Visibility = _currentMode == AppMode.I2I ? Visibility.Visible : Visibility.Collapsed;
        MenuSaveStripped.IsEnabled = _currentMode switch
        {
            AppMode.Inspect => hasReaderImage,
            AppMode.ImageGeneration => hasGenImage,
            AppMode.I2I => hasI2IImage,
            _ => false,
        };
        MenuExportCanvasMask.IsEnabled = _currentMode == AppMode.I2I && hasI2IImage;
    }

    private static FontFamily SymbolFontFamily =>
        (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];

    private string UiLanguageTag => _settings.Settings.LanguageCode switch
    {
        "zh_cn" => "zh-CN",
        "zh_tw" => "zh-TW",
        "ja_jp" => "ja-JP",
        _ => "en-US"
    };

    private FontFamily UiTextFontFamily => new(UiLanguageTag switch
    {
        "zh-CN" => "Microsoft YaHei UI",
        "zh-TW" => "Microsoft JhengHei UI",
        "ja-JP" => "Yu Gothic UI",
        _ => "Segoe UI Variable"
    });

    private ComboBoxItem CreateTextComboBoxItem(string text) => new()
    {
        Content = text,
        FontFamily = UiTextFontFamily,
        Language = UiLanguageTag,
    };

    private void ApplyMenuTypography(object? item)
    {
        switch (item)
        {
            case MenuBarItem menuBarItem:
                menuBarItem.FontFamily = UiTextFontFamily;
                menuBarItem.Language = UiLanguageTag;
                foreach (var child in menuBarItem.Items)
                    ApplyMenuTypography(child);
                break;
            case MenuFlyoutSubItem subItem:
                subItem.FontFamily = UiTextFontFamily;
                subItem.Language = UiLanguageTag;
                foreach (var child in subItem.Items)
                    ApplyMenuTypography(child);
                break;
            case ToggleMenuFlyoutItem toggleItem:
                toggleItem.FontFamily = UiTextFontFamily;
                toggleItem.Language = UiLanguageTag;
                break;
            case MenuFlyoutItem menuItem:
                menuItem.FontFamily = UiTextFontFamily;
                menuItem.Language = UiLanguageTag;
                break;
            case ComboBox comboBox:
                comboBox.FontFamily = UiTextFontFamily;
                comboBox.Language = UiLanguageTag;
                foreach (var child in comboBox.Items)
                    ApplyMenuTypography(child);
                break;
            case ComboBoxItem comboBoxItem:
                comboBoxItem.FontFamily = UiTextFontFamily;
                comboBoxItem.Language = UiLanguageTag;
                break;
        }
    }

    private void ApplyStaticMenuAndComboTypography()
    {
        AppMenuBar.FontFamily = UiTextFontFamily;
        AppMenuBar.Language = UiLanguageTag;
        foreach (var item in AppMenuBar.Items)
            ApplyMenuTypography(item);

        ApplyMenuTypography(CboModel);
        ApplyMenuTypography(CboSize);
    }

    // ═══════════════════════════════════════════════════════════
    //  统一应用 UI 字体到可视树
    //  WinAppSDK 1.8 默认字体 Segoe UI Variable Text 渲染中文时会退化到 font fallback，
    //  line metrics 仍沿用西文字体，造成中文与 Icon 基线错位（视觉上整体偏上）。
    //  这里递归把 Control/TextBlock 的字体设成当前语言对应的 UI 字体，
    //  但保留 FontIcon/SymbolIcon 的符号字体、保留 Consolas 等显式等宽字体、
    //  以及跳过 PromptTextBox（自己管理字体）。
    // ═══════════════════════════════════════════════════════════
    private void ApplyUiFontToVisualTree(DependencyObject? root)
    {
        if (root == null)
            return;

        ApplyUiFontToElement(root);

        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            ApplyUiFontToVisualTree(child);
        }
    }

    private void ApplyUiFontToElement(DependencyObject element)
    {
        switch (element)
        {
            case IconElement:
                return;
            case NAITool.Controls.PromptTextBox:
                return;
            case TextBlock tb:
                if (!IsExplicitNonUiFont(tb.FontFamily))
                {
                    tb.FontFamily = UiTextFontFamily;
                    tb.Language = UiLanguageTag;
                }
                break;
            case Control c:
                if (!IsExplicitNonUiFont(c.FontFamily))
                {
                    c.FontFamily = UiTextFontFamily;
                    c.Language = UiLanguageTag;
                }
                break;
        }
    }

    private static bool IsExplicitNonUiFont(FontFamily? font)
    {
        if (font?.Source is not { Length: > 0 } source)
            return false;

        return source.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Cascadia", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSelectedComboText(ComboBox comboBox) =>
        comboBox.SelectedItem switch
        {
            string text => text,
            ComboBoxItem { Content: string text } => text,
            _ => null,
        };

    private static bool IsWindows11OrGreater() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    private static Windows.UI.Color GetWindowSurfaceColor(bool isDark) =>
        isDark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
            : Windows.UI.Color.FromArgb(255, 243, 243, 243);

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) =>
        Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    private static byte GetBackdropOverlayAlpha(string mode) => mode switch
    {
        "Lesser" => 192,
        "Opaque" => 255,
        _ => 0,
    };

    private static Brush? CreateBackdropOverlayBrush(string mode, bool isDark)
    {
        byte alpha = GetBackdropOverlayAlpha(mode);
        if (alpha == 0)
            return null;

        return new SolidColorBrush(WithAlpha(GetWindowSurfaceColor(isDark), alpha));
    }

    private void ApplyWindowChrome(Window window, bool isDark, Panel? titleBarPanel = null, Panel? rootPanel = null)
    {
        var surfaceColor = GetWindowSurfaceColor(isDark);
        bool isMainWindow = ReferenceEquals(window, this);
        string transparencyMode = _settings.Settings.AppearanceTransparency;
        var titleBarBaseColor = isMainWindow
            ? WithAlpha(surfaceColor, GetBackdropOverlayAlpha(transparencyMode))
            : surfaceColor;

        if (titleBarPanel != null)
            titleBarPanel.Background = new SolidColorBrush(surfaceColor);
        if (rootPanel != null)
        {
            // 主窗口随透明度档位叠加表面层，其它窗口继续保持实色内容面板。
            rootPanel.Background = isMainWindow
                ? CreateBackdropOverlayBrush(transparencyMode, isDark)
                : new SolidColorBrush(surfaceColor);
        }

        var appWindow = isMainWindow ? AppWindow : GetAppWindowForWindow(window);
        if (appWindow == null) return;
        if (!IsWindows11OrGreater() && appWindow.Presenter is OverlappedPresenter presenter)
        {
            try
            {
                // Win10 下保留系统标题栏按钮，但移除边框，规避顶部 1px 线。
                presenter.SetBorderAndTitleBar(false, true);
            }
            catch
            {
                // 某些系统/窗口状态可能不支持，忽略并继续使用默认行为。
            }
        }

        // 通过 WndProc 子类化把客户区向上扩 1px，遮盖 WinUI3+Acrylic 标题栏遗留的
        // 顶端 1px 不透明非客户区线，并在每次 ApplyWindowChrome 调用时确保已安装。
        WindowTopBorderTrim.Install(window);

        if (appWindow.TitleBar == null) return;

        var tb = appWindow.TitleBar;
        tb.ExtendsContentIntoTitleBar = true;
        tb.BackgroundColor = titleBarBaseColor;
        tb.InactiveBackgroundColor = titleBarBaseColor;
        tb.ButtonBackgroundColor = titleBarBaseColor;
        tb.ButtonInactiveBackgroundColor = titleBarBaseColor;

        if (isDark)
        {
            tb.ForegroundColor = Colors.White;
            tb.InactiveForegroundColor = Windows.UI.Color.FromArgb(180, 255, 255, 255);
            tb.ButtonForegroundColor = Colors.White;
            tb.ButtonHoverForegroundColor = Colors.White;
            tb.ButtonPressedForegroundColor = Colors.White;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 255, 255, 255);
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(30, 255, 255, 255);
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(60, 255, 255, 255);
        }
        else
        {
            tb.ForegroundColor = Colors.Black;
            tb.InactiveForegroundColor = Windows.UI.Color.FromArgb(180, 0, 0, 0);
            tb.ButtonForegroundColor = Colors.Black;
            tb.ButtonHoverForegroundColor = Colors.Black;
            tb.ButtonPressedForegroundColor = Colors.Black;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 0, 0, 0);
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 0, 0, 0);
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 0, 0, 0);
        }
    }

    private void OnAppTitleBarDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 禁止双击自定义标题栏触发最大化/还原。
        e.Handled = true;
    }

    private void OnResetGenParams(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            _settings.Settings.GenParameters = CreateDefaultGenerationParameters();
        }
        else if (_currentMode == AppMode.I2I)
        {
            if (_i2iEditMode == I2IEditMode.Denoise)
                _settings.Settings.I2IDenoiseParameters = CreateDefaultI2IDenoiseParameters();
            else
                _settings.Settings.InpaintParameters = CreateDefaultInpaintParameters();
        }
        _settings.Save();
        SyncParamsToUI();
        UpdateModelDependentUI();
        TxtStatus.Text = L("status.generation_params_reset");
    }

    private void OnClearAllPrompts(object sender, RoutedEventArgs e)
    {
        if (_currentMode == AppMode.ImageGeneration)
        {
            _genPositivePrompt = "";
            _genNegativePrompt = "";
            _genStylePrompt = "";
            _genCharacters.Clear();
            ClearReferenceFeatures();
            RefreshCharacterPanel();
        }
        else if (_currentMode == AppMode.I2I)
        {
            _i2iPositivePrompt = "";
            _i2iNegativePrompt = "";
            _i2iStylePrompt = "";
            _i2iCharacters.Clear();
            ClearReferenceFeatures();
            RefreshCharacterPanel();
        }
        TxtPrompt.Text = "";
        TxtStylePrompt.Text = "";
        UpdatePromptHighlights();
        UpdateStyleHighlights();
        TxtStatus.Text = L("status.prompts_cleared");
    }

    private void BuildI2IEditMenuItems(MenuBarItem menu)
    {
        var undoItem = CreateLocalizedMenuItem(MenuCommandUndo, "menu.edit.undo", new SymbolIcon(Symbol.Undo));
        undoItem.Click += OnUndo;
        undoItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.Z });
        menu.Items.Add(undoItem);

        var redoItem = CreateLocalizedMenuItem(MenuCommandRedo, "menu.edit.redo", new SymbolIcon(Symbol.Redo));
        redoItem.Click += OnRedo;
        redoItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.Y });
        menu.Items.Add(redoItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var reloadItem = CreateReloadImageMenuItem();
        reloadItem.Click += OnReloadImage;
        menu.Items.Add(reloadItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var maskSub = CreateLocalizedSubItem(
            MenuCommandMaskOps,
            "menu.inpaint.mask_ops",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE7C3" });
        var fillItem = CreateLocalizedMenuItem(
            "fill_empty",
            "menu.inpaint.fill_empty",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE771" });
        fillItem.Click += OnFillEmpty;
        fillItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, Key = Windows.System.VirtualKey.I });
        maskSub.Items.Add(fillItem);

        var invertItem = CreateLocalizedMenuItem(
            "invert_mask",
            "menu.inpaint.invert_mask",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE895" });
        invertItem.Click += OnInvertMask;
        invertItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.I });
        maskSub.Items.Add(invertItem);

        var expandItem = CreateLocalizedMenuItem(
            MenuCommandExpandMask,
            "menu.inpaint.expand_mask",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE744" });
        expandItem.KeyboardAcceleratorTextOverride = "Ctrl++";
        expandItem.Click += OnExpandMask;
        maskSub.Items.Add(expandItem);

        var shrinkItem = CreateLocalizedMenuItem(
            MenuCommandContractMask,
            "menu.inpaint.contract_mask",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE743" });
        shrinkItem.KeyboardAcceleratorTextOverride = "Ctrl+-";
        shrinkItem.Click += OnShrinkMask;
        maskSub.Items.Add(shrinkItem);

        var clearItem = CreateLocalizedMenuItem("clear_mask", "menu.inpaint.clear_mask", new SymbolIcon(Symbol.Delete));
        clearItem.Click += OnClearMask;
        clearItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.Control, Key = Windows.System.VirtualKey.D });
        maskSub.Items.Add(clearItem);
        menu.Items.Add(maskSub);

        var trimItem = CreateLocalizedMenuItem(
            "trim_canvas",
            "menu.inpaint.trim_canvas",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE7A8" });
        trimItem.Click += OnTrimCanvas;
        menu.Items.Add(trimItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var alignSub = CreateLocalizedSubItem(
            MenuCommandAlignImage,
            "menu.inpaint.align_image",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uF599" });
        (string Key, string Tag, string Glyph)[] alignments =
        {
            ("align.top_left", "TL", "\uF599"), ("align.top_center", "TC", "\uF59E"), ("align.top_right", "TR", "\uF59A"),
            ("-", "", ""), ("align.center_left", "CL", "\uF59C"), ("align.center", "CC", "\uF58E"), ("align.center_right", "CR", "\uF59D"),
            ("-", "", ""), ("align.bottom_left", "BL", "\uF5AE"), ("align.bottom_center", "BC", "\uF59F"), ("align.bottom_right", "BR", "\uF59B"),
        };
        foreach (var alignment in alignments)
        {
            if (alignment.Key == "-")
                alignSub.Items.Add(new MenuFlyoutSeparator());
            else
            {
                var ai = new MenuFlyoutItem
                {
                    Text = L(alignment.Key),
                    Tag = alignment.Tag,
                    Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = alignment.Glyph },
                };
                ai.Click += OnAlign;
                alignSub.Items.Add(ai);
            }
        }
        menu.Items.Add(alignSub);

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(BuildPresetResolutionSubMenu());
        menu.Items.Add(new MenuFlyoutSeparator());

        var inferSub = CreateLocalizedSubItem(
            MenuCommandPromptInference,
            "menu.inpaint.prompt_inference",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8A5" });
        var inferGlobalItem = CreateLocalizedMenuItem(
            "infer_global",
            "menu.inpaint.infer_global",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE9A6" });
        inferGlobalItem.Click += async (_, _) => await RunI2IPromptInferenceAsync(canvasOnly: false);
        inferSub.Items.Add(inferGlobalItem);

        var inferCanvasItem = CreateLocalizedMenuItem(
            "infer_canvas",
            "menu.inpaint.infer_canvas",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE799" });
        inferCanvasItem.Click += async (_, _) => await RunI2IPromptInferenceAsync(canvasOnly: true);
        inferSub.Items.Add(inferCanvasItem);
        menu.Items.Add(inferSub);

        menu.Items.Add(new MenuFlyoutSeparator());

        var postItem = CreateLocalizedMenuItem(
            MenuCommandSendToPost,
            "action.send_to_post",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uEB3C" });
        postItem.Click += OnSendToEffectsFromI2I;
        menu.Items.Add(postItem);

        var upscaleItem = CreateLocalizedMenuItem(
            MenuCommandSendToUpscale,
            "action.send_to_upscale",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uECE9" });
        upscaleItem.Click += OnSendToUpscaleFromI2I;
        menu.Items.Add(upscaleItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var normalizeItem = CreateLocalizedMenuItem(
            MenuCommandNormalizePrompts,
            "menu.edit.normalize_prompts",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8D2" });
        normalizeItem.Click += OnNormalizePrompts;
        menu.Items.Add(normalizeItem);

        var randomStyleItem = CreateLocalizedMenuItem(
            MenuCommandRandomStylePrompt,
            "menu.edit.random_style_prompt",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8B1" });
        randomStyleItem.Click += OnRandomStylePrompt;
        menu.Items.Add(randomStyleItem);
        menu.Items.Add(BuildPromptShortcutsMenuItem());

        menu.Items.Add(new MenuFlyoutSeparator());
        var clearAllItem = CreateLocalizedMenuItem(
            MenuCommandClearAllPrompts,
            "menu.edit.clear_all_prompts",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74D" });
        clearAllItem.Click += OnClearAllPrompts;
        menu.Items.Add(clearAllItem);

        var resetParamsItem = CreateLocalizedMenuItem(
            MenuCommandResetGenerationParams,
            "menu.edit.reset_generation_params",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE777" });
        resetParamsItem.Click += OnResetGenParams;
        menu.Items.Add(resetParamsItem);
    }

    private MenuFlyoutSubItem BuildPresetResolutionSubMenu()
    {
        var presetSub = CreateLocalizedSubItem(
            "preset_resolution",
            "menu.edit.preset_resolution",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE740" });
        foreach (var p in MaskCanvasControl.CanvasPresets)
        {
            string glyph = p.W == p.H
                ? "\uF16B"
                : p.W > p.H
                    ? "\uF5A1"
                    : "\uF599";
            var item = new MenuFlyoutItem
            {
                Text = p.Label,
                Tag = (p.W, p.H),
                Icon = new FontIcon { FontFamily = SymbolFontFamily, Glyph = glyph },
            };
            item.Click += OnPresetResolutionSelected;
            presetSub.Items.Add(item);
        }
        return presetSub;
    }

    private MenuFlyoutItem BuildPromptShortcutsMenuItem()
    {
        var item = CreateLocalizedMenuItem(
            MenuCommandPromptShortcuts,
            "menu.edit.prompt_shortcuts",
            new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE8A7" });
        item.Click += OnPromptShortcuts;
        return item;
    }

    private MenuFlyoutItem CreateReloadImageMenuItem()
    {
        var item = CreateLocalizedMenuItem(
            MenuCommandReloadImage,
            "menu.edit.reload_image",
            new SymbolIcon(Symbol.Refresh));
        item.KeyboardAccelerators.Add(new KeyboardAccelerator
        { Modifiers = Windows.System.VirtualKeyModifiers.None, Key = Windows.System.VirtualKey.F5 });
        return item;
    }
}
