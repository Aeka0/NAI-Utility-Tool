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
    private void OnPromptTextChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PromptTextBox { IsApplyingHighlights: true }) return;
        UpdatePromptHighlights();
        if (!_acInserting) TriggerAutoComplete(TxtPrompt);
    }
    private void OnPromptSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePromptHighlights();
    private void OnPromptLostFocus(object sender, RoutedEventArgs e) => CloseAutoComplete();
    private void OnStylePromptTextChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PromptTextBox { IsApplyingHighlights: true }) return;
        UpdateStyleHighlights();
        if (!_acInserting) TriggerAutoComplete(TxtStylePrompt);
    }
    private void OnStylePromptSizeChanged(object sender, SizeChangedEventArgs e) => UpdateStyleHighlights();

    private sealed class NormalizeOptions
    {
        public bool Lowercase { get; set; } = true;
        public bool HalfWidth { get; set; } = true;
        public bool RemoveSpecial { get; set; } = true;
        public bool UnderscoreToSpace { get; set; } = true;
        public bool RemoveNewlines { get; set; } = true;
        public bool RemoveJunk { get; set; } = true;
        public bool RemoveNonAscii { get; set; } = true;
        public bool PreserveWildcards { get; set; } = true;
    }

    private async void OnNormalizePrompts(object sender, RoutedEventArgs e)
    {
        var options = new NormalizeOptions();
        var chkLower = new CheckBox { Content = L("normalize.lowercase"), IsChecked = options.Lowercase };
        var chkHalf = new CheckBox { Content = L("normalize.half_width"), IsChecked = options.HalfWidth };
        var chkSpecial = new CheckBox { Content = L("normalize.remove_special"), IsChecked = options.RemoveSpecial };
        var chkUnderscore = new CheckBox { Content = L("normalize.underscore_to_space"), IsChecked = options.UnderscoreToSpace };
        var chkNewlines = new CheckBox { Content = L("normalize.newlines_to_commas"), IsChecked = options.RemoveNewlines };
        var chkJunk = new CheckBox { Content = L("normalize.remove_junk"), IsChecked = options.RemoveJunk };
        var chkAscii = new CheckBox { Content = L("normalize.remove_non_ascii"), IsChecked = options.RemoveNonAscii };
        var chkPreserveWild = new CheckBox { Content = L("normalize.preserve_wildcards"), IsChecked = options.PreserveWildcards };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(chkLower);
        panel.Children.Add(chkHalf);
        panel.Children.Add(chkSpecial);
        panel.Children.Add(chkUnderscore);
        panel.Children.Add(chkNewlines);
        panel.Children.Add(chkJunk);
        panel.Children.Add(chkAscii);
        panel.Children.Add(chkPreserveWild);

        var dialog = new ContentDialog
        {
            Title = L("normalize.title"),
            Content = panel,
            PrimaryButtonText = L("button.apply"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        options.Lowercase = chkLower.IsChecked == true;
        options.HalfWidth = chkHalf.IsChecked == true;
        options.RemoveSpecial = chkSpecial.IsChecked == true;
        options.UnderscoreToSpace = chkUnderscore.IsChecked == true;
        options.RemoveNewlines = chkNewlines.IsChecked == true;
        options.RemoveJunk = chkJunk.IsChecked == true;
        options.RemoveNonAscii = chkAscii.IsChecked == true;
        options.PreserveWildcards = chkPreserveWild.IsChecked == true;

        ApplyPromptNormalization(options);
    }

    private void ApplyPromptNormalization(NormalizeOptions options)
    {
        SaveCurrentPromptToBuffer();
        SaveAllCharacterPrompts();

        if (_currentMode == AppMode.ImageGeneration)
        {
            _genPositivePrompt = NormalizeAnnotation(_genPositivePrompt, options);
            _genNegativePrompt = NormalizeAnnotation(_genNegativePrompt, options);
            _genStylePrompt = NormalizeAnnotation(_genStylePrompt, options);
        }
        else if (_currentMode == AppMode.I2I)
        {
            _i2iPositivePrompt = NormalizeAnnotation(_i2iPositivePrompt, options);
            _i2iNegativePrompt = NormalizeAnnotation(_i2iNegativePrompt, options);
            _i2iStylePrompt = NormalizeAnnotation(_i2iStylePrompt, options);
        }

        foreach (var entry in _genCharacters)
        {
            entry.PositivePrompt = NormalizeAnnotation(entry.PositivePrompt, options);
            entry.NegativePrompt = NormalizeAnnotation(entry.NegativePrompt, options);
        }

        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        RefreshCharacterPanel();
        UpdatePromptHighlights();
        UpdateStyleHighlights();
        TxtStatus.Text = L("prompt.normalize.completed");
    }

    private static readonly Regex WildcardTokenPreserveRegex = new(@"__(.+?)__", RegexOptions.Compiled);

    private static string NormalizeAnnotation(string text, NormalizeOptions opts)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var wildcardSlots = new Dictionary<string, string>();
        if (opts.PreserveWildcards)
        {
            int slotIdx = 0;
            text = WildcardTokenPreserveRegex.Replace(text, m =>
            {
                string placeholder = $"\x01WC{slotIdx++}\x01";
                wildcardSlots[placeholder] = m.Value;
                return placeholder;
            });
        }

        if (opts.Lowercase)
            text = text.ToLowerInvariant();

        if (opts.HalfWidth)
            text = text
                .Replace('，', ',')
                .Replace('　', ' ')
                .Replace('（', '(')
                .Replace('）', ')')
                .Replace('、', ',');
        else
            text = text
                .Replace(',', '，')
                .Replace('(', '（')
                .Replace(')', '）');

        if (opts.RemoveSpecial)
            text = Regex.Replace(text, @"[【】]", "");

        if (opts.UnderscoreToSpace)
        {
            if (opts.PreserveWildcards)
            {
                var sb = new StringBuilder(text);
                for (int i = 0; i < sb.Length; i++)
                {
                    if (sb[i] == '_' && !IsInsidePlaceholder(text, i))
                        sb[i] = ' ';
                }
                text = sb.ToString();
            }
            else
            {
                text = text.Replace('_', ' ');
            }
        }

        if (opts.RemoveNewlines)
            text = text.Replace("\r\n", "\n").Replace('\n', ',');

        if (opts.RemoveJunk)
        {
            var phrases = new[] { "artist:", "best quality", "amazing quality", "very aesthetic", "absurdres" };
            string pattern = @"\b(" + string.Join("|", phrases.Select(Regex.Escape)) + @")\b";
            text = Regex.Replace(text, pattern, "", RegexOptions.IgnoreCase);
        }

        if (opts.RemoveNonAscii)
            text = new string(text.Where(c => c <= 127 || c == '\x01').ToArray());

        string tempText = text.Replace('，', ',');
        var tags = tempText.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var uniqueTags = new List<string>();
        var seenTags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            string cleaned = tag.Trim().Replace("/", "");
            if (string.IsNullOrEmpty(cleaned) || !seenTags.Add(cleaned)) continue;
            uniqueTags.Add(cleaned);
        }

        string joinSep = opts.HalfWidth ? ", " : "，";
        string result = string.Join(joinSep, uniqueTags);

        foreach (var kvp in wildcardSlots)
            result = result.Replace(kvp.Key, kvp.Value);

        return result;
    }

    private static bool IsInsidePlaceholder(string text, int index)
    {
        int prevMarker = text.LastIndexOf('\x01', index);
        if (prevMarker < 0) return false;
        int nextMarker = text.IndexOf('\x01', index);
        if (nextMarker < 0) return false;
        string between = text[prevMarker..(nextMarker + 1)];
        return between.Contains("WC");
    }

    private void DebugLog(string msg)
    {
        if (!_settings.Settings.DevLogEnabled) return;
        try
        {
            string dir = System.IO.Path.Combine(AppRootDir, "logs");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir,
                $"debug_{DateTime.Now:yyyy-MM-dd}.txt");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private void UpdatePromptHighlights()
    {
        string text = TxtPrompt.Text;
        bool wh = _settings.Settings.WeightHighlight;
        if (_settings.Settings.DevLogEnabled) DebugLog($"UpdatePromptHighlights: text.len={text?.Length}, WeightHighlight={wh}");
        if (string.IsNullOrEmpty(text) || !wh)
        {
            TxtPrompt.ClearHighlights();
            return;
        }
        _promptHighlightVer++;
        int version = _promptHighlightVer;
        if (_settings.Settings.DevLogEnabled) DebugLog($"  enqueue ver={version}");
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_settings.Settings.DevLogEnabled) DebugLog($"  callback ver={version}, current={_promptHighlightVer}");
            if (version != _promptHighlightVer) return;
            ApplyHighlightsFor(TxtPrompt);
        });
    }

    private void UpdateStyleHighlights()
    {
        if (string.IsNullOrEmpty(TxtStylePrompt.Text) || !_settings.Settings.WeightHighlight)
        {
            TxtStylePrompt.ClearHighlights();
            return;
        }
        _styleHighlightVer++;
        int version = _styleHighlightVer;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (version != _styleHighlightVer) return;
            ApplyHighlightsFor(TxtStylePrompt);
        });
    }

    private static readonly Regex PromptWeightHighlightRegex = new(@"(\d+\.?\d*)::", RegexOptions.Compiled);
    private static readonly Regex WildcardHighlightExplicitRegex = new(@"__(.+?)__", RegexOptions.Compiled);

    private void ApplyHighlightsFor(PromptTextBox textBox)
    {
        var text = textBox.Text;
        if (string.IsNullOrEmpty(text) || !_settings.Settings.WeightHighlight) return;

        bool isDark = ((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark;
        var greenColor = isDark
            ? Windows.UI.Color.FromArgb(50, 16, 185, 129)
            : Windows.UI.Color.FromArgb(70, 16, 185, 129);
        var redColor = isDark
            ? Windows.UI.Color.FromArgb(50, 239, 68, 68)
            : Windows.UI.Color.FromArgb(70, 239, 68, 68);
        var purpleColor = isDark
            ? Windows.UI.Color.FromArgb(55, 168, 85, 247)
            : Windows.UI.Color.FromArgb(65, 147, 51, 234);

        var matches = PromptWeightHighlightRegex.Matches(text);
        if (_settings.Settings.DevLogEnabled)
            DebugLog($"ApplyHighlightsFor: textBox={textBox.Name}, text.len={text.Length}, matches={matches.Count}, textBox.W={textBox.ActualWidth}, textBox.H={textBox.ActualHeight}");

        var highlights = new List<PromptTextHighlight>();
        int drawn = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double w) || w == 1.0)
            {
                if (_settings.Settings.DevLogEnabled)
                    DebugLog($"  match[{i}] '{m.Value}' w={m.Groups[1].Value} -> skip (w=1 or parse fail)");
                continue;
            }

            int contentStart = m.Index + m.Length;
            int searchEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;

            int closingIdx = -1;
            int searchLen = searchEnd - contentStart;
            if (searchLen >= 2)
                closingIdx = text.IndexOf("::", contentStart, searchLen);

            int segEnd = (closingIdx >= 0) ? closingIdx + 2 : searchEnd;
            if (_settings.Settings.DevLogEnabled)
                DebugLog($"  match[{i}] '{m.Value}' w={w}, start={m.Index}, len={segEnd - m.Index}");

            AddSegmentHighlight(highlights, text.Length, m.Index, segEnd - m.Index, w > 1 ? greenColor : redColor);
            drawn++;
        }

        AddWildcardHighlights(highlights, text, purpleColor);
        textBox.ApplyHighlights(highlights);
        if (_settings.Settings.DevLogEnabled) DebugLog($"  drawn={drawn}, highlights={highlights.Count}");
    }

    private void AddWildcardHighlights(List<PromptTextHighlight> highlights, string text, Windows.UI.Color color)
    {
        if (!_settings.Settings.WildcardsEnabled || !_wildcardService.IsLoaded) return;

        bool requireExplicit = _settings.Settings.WildcardsRequireExplicitSyntax;

        var explicitMatches = WildcardHighlightExplicitRegex.Matches(text);
        foreach (Match m in explicitMatches)
        {
            string body = m.Groups[1].Value.Trim();
            string name = body;
            int atIdx = body.LastIndexOf('@');
            if (atIdx > 0) name = body[..atIdx].Trim();
            if (body.StartsWith("@", StringComparison.Ordinal) || _wildcardService.HasEntry(name))
                AddSegmentHighlight(highlights, text.Length, m.Index, m.Length, color);
        }

        if (!requireExplicit)
        {
            int pos = 0;
            while (pos <= text.Length)
            {
                int comma = text.IndexOf(',', pos);
                int tokenEnd = comma >= 0 ? comma : text.Length;
                int start = pos;
                int end = tokenEnd - 1;
                while (start <= end && char.IsWhiteSpace(text[start])) start++;
                while (end >= start && char.IsWhiteSpace(text[end])) end--;
                string trimmed = start <= end ? text.Substring(start, end - start + 1) : "";
                if (trimmed.Length > 0
                    && !(trimmed.StartsWith("__") && trimmed.EndsWith("__"))
                    && _wildcardService.HasEntry(trimmed))
                {
                    AddSegmentHighlight(highlights, text.Length, start, trimmed.Length, color);
                }
                if (comma < 0) break;
                pos = comma + 1;
            }
        }
    }

    private static void AddSegmentHighlight(List<PromptTextHighlight> highlights, int textLength,
        int start, int length, Windows.UI.Color color)
    {
        if (length <= 0 || start >= textLength) return;
        start = Math.Clamp(start, 0, textLength);
        int end = Math.Clamp(start + length, start, textLength);
        if (end <= start) return;
        highlights.Add(new PromptTextHighlight(start, end - start, color));
    }
}
