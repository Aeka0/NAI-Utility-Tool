using System;

namespace NAITool;

public sealed partial class MainWindow
{
    private static double NormalizePromptHeight(double height) =>
        double.IsFinite(height) && height > 0 ? Math.Clamp(height, 60, 4096) : 0;

    private void SetupPromptResizing()
    {
        var settings = _settings.Settings;
        settings.PromptEditorHeight = NormalizePromptHeight(settings.PromptEditorHeight);
        settings.StylePromptEditorHeight = NormalizePromptHeight(settings.StylePromptEditorHeight);
        TxtPrompt.EnableResizing(L("prompt.resize_help"));
        TxtStylePrompt.EnableResizing(L("prompt.resize_help"));
        TxtPrompt.ResizeHeightRequested += height =>
        {
            _settings.Settings.PromptEditorHeight = Math.Max(80, height);
            UpdatePromptAreaHeight();
        };
        TxtPrompt.AutoSizeRequested += () =>
        {
            _settings.Settings.PromptEditorHeight = 0;
            UpdatePromptAreaHeight();
            SavePromptEditorSizes();
        };
        TxtPrompt.ResizeCompleted += SavePromptEditorSizes;
        TxtStylePrompt.ResizeHeightRequested += height =>
        {
            _settings.Settings.StylePromptEditorHeight = height;
            ApplyStylePromptHeight();
        };
        TxtStylePrompt.AutoSizeRequested += () =>
        {
            _settings.Settings.StylePromptEditorHeight = 0;
            ApplyStylePromptHeight();
            SavePromptEditorSizes();
        };
        TxtStylePrompt.ResizeCompleted += SavePromptEditorSizes;
        ApplyStylePromptHeight();
    }

    private void ApplyStylePromptHeight()
    {
        double height = NormalizePromptHeight(_settings.Settings.StylePromptEditorHeight);
        TxtStylePrompt.MaxHeight = height > 0 ? double.PositiveInfinity : 120;
        TxtStylePrompt.Height = height > 0 ? height : double.NaN;
        UpdatePromptAreaHeight();
        QueuePromptAreaHeightUpdate();
    }

    private void SavePromptEditorSizes()
    {
        SyncRememberedPromptAndParameterState();
        _settings.Save();
    }
}
