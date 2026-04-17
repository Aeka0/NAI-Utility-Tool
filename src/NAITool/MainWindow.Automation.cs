using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Controls;
using NAITool.Models;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private readonly AutomationPresetService _automationPresetService = new(AppRootDir);
    private AutomationSettings? _activeAutomationSettings;
    private AutomationRunContext? _automationRunContext;
    private int? _lastGenerationFailureStatusCode;

    private sealed class AutomationRunContext
    {
        public required AutomationSettings SettingsSnapshot { get; init; }
        public required List<PromptShortcutEntry> PromptPool { get; init; }
        public required List<VibeTransferEntry> VibePool { get; init; }
        public string? CurrentPromptOverride { get; set; }
        public (int W, int H)? CurrentSizeOverride { get; set; }
        public List<VibeTransferInfo>? CurrentVibeOverride { get; set; }
        public string LastSummary { get; set; } = "";
        public bool MissingPromptPoolNotified { get; set; }
        public bool MissingVibePoolNotified { get; set; }
    }

    private sealed class AutomationEffectsProcessResult
    {
        public required byte[] Bytes { get; init; }
        public string Summary { get; init; } = "";
    }

    private AutomationSettings GetAutomationSettings()
    {
        _settings.Settings.Automation ??= new AutomationSettings();
        _settings.Settings.Automation.Normalize();
        return _settings.Settings.Automation;
    }

}
