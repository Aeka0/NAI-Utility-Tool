using System;
using NAITool.Controls;

namespace NAITool;

public sealed partial class MainWindow
{
    private static double NormalizePromptHeight(double height, double minimum = 50) =>
        PromptTextBox.NormalizeEditorHeight(height, minimum);

    private void SetupPromptResizing()
    {
        var settings = _settings.Settings;
        settings.PromptEditorHeight = NormalizePromptHeight(settings.PromptEditorHeight, 80);
        settings.StylePromptEditorHeight = NormalizePromptHeight(settings.StylePromptEditorHeight, 34);
        TxtPrompt.ResizeHeightRequested += height =>
        {
            settings.PromptEditorHeight = NormalizePromptHeight(height, 80);
            UpdatePromptAreaHeight();
        };
        TxtPrompt.AutoSizeRequested += () =>
        {
            settings.PromptEditorHeight = 0;
            UpdatePromptAreaHeight();
            SavePromptEditorSizes();
        };
        TxtPrompt.ResizeCompleted += SavePromptEditorSizes;
        TxtStylePrompt.ResizeHeightRequested += height =>
        {
            settings.StylePromptEditorHeight = NormalizePromptHeight(height, 34);
            ApplyStylePromptHeight();
        };
        TxtStylePrompt.AutoSizeRequested += () =>
        {
            settings.StylePromptEditorHeight = 0;
            ApplyStylePromptHeight();
            SavePromptEditorSizes();
        };
        TxtStylePrompt.ResizeCompleted += SavePromptEditorSizes;
        TxtStylePrompt.SizeChanged += (_, _) => QueuePromptAreaHeightUpdate();
        ApplyStylePromptHeight();
    }

    private void ApplyStylePromptHeight()
    {
        double height = NormalizePromptHeight(_settings.Settings.StylePromptEditorHeight, 34);
        TxtStylePrompt.MaxHeight = height > 0 ? double.PositiveInfinity : 120;
        TxtStylePrompt.Height = height > 0 ? height : double.NaN;
        UpdatePromptAreaHeight();
        QueuePromptAreaHeightUpdate();
    }

    private void ResetPromptEditorHeights()
    {
        _settings.Settings.PromptEditorHeight = 0;
        _settings.Settings.StylePromptEditorHeight = 0;
        foreach (var entry in _genCharacters)
            entry.EditorHeight = 0;
        foreach (var entry in _i2iCharacters)
            entry.EditorHeight = 0;

        RefreshCharacterPanel();
        ApplyStylePromptHeight();
        SavePromptEditorSizes();
        TxtStatus.Text = L("status.prompt_heights_reset");
    }

    private void SavePromptEditorSizes()
    {
        SyncRememberedPromptAndParameterState();
        _settings.Save();
    }
}
