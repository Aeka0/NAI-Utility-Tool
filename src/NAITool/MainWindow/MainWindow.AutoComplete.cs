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
    // ═══════════════════════════════════════════════════════════
    //  自动补全
    // ═══════════════════════════════════════════════════════════

    private async Task LoadTagServiceAsync()
    {
        var dir = Path.Combine(AppRootDir, "assets", "tagsheet");
        try { await _tagService.LoadFromDirectoryAsync(dir); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Tags] Load failed: {ex.Message}"); }
    }

    private void TriggerAutoComplete(PromptTextBox textBox)
    {
        if (!_settings.Settings.AutoComplete) return;
        if (!_tagService.IsLoaded && !_wildcardService.IsLoaded) return;

        _acTargetTextBox = textBox;
        string token = ExtractCurrentToken(textBox);
        if (token.Length < 1)
        {
            CloseAutoComplete();
            return;
        }

        _acVersion++;
        int version = _acVersion;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (version != _acVersion) return;
            PerformAutoCompleteSearch(textBox, token);
        });
    }

    private static int FindTokenStart(string text, int caret)
    {
        int start = caret - 1;
        while (start > 0)
        {
            if (text[start - 1] == ',') break;
            if (IsAutoCompleteLineBreak(text[start - 1])) break;
            if (start >= 2 && text[start - 1] == ':' && text[start - 2] == ':') break;
            start--;
        }
        return start;
    }

    private static bool IsAutoCompleteLineBreak(char ch) => ch is '\r' or '\n';

    private static string ExtractCurrentToken(PromptTextBox textBox)
    {
        string text = textBox.Text;
        int caret = textBox.SelectionStart;
        if (string.IsNullOrEmpty(text) || caret <= 0) return "";

        int start = FindTokenStart(text, caret);
        string token = text.Substring(start, caret - start).TrimStart();
        return token;
    }

    private static (int Start, int End) GetCurrentTokenRange(PromptTextBox textBox)
    {
        string text = textBox.Text;
        int caret = textBox.SelectionStart;
        if (string.IsNullOrEmpty(text) || caret <= 0) return (0, 0);

        int start = FindTokenStart(text, caret);
        while (start < text.Length && text[start] == ' ') start++;

        int end = caret;
        while (end < text.Length
            && text[end] != ','
            && !IsAutoCompleteLineBreak(text[end])
            && !(end + 1 < text.Length && text[end] == ':' && text[end + 1] == ':'))
        {
            end++;
        }

        return (start, end);
    }

    private string ExtractWildcardSearchPrefix(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "";

        string normalized = token.Trim();
        if (normalized.StartsWith("__", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.EndsWith("__", StringComparison.Ordinal))
            normalized = normalized[..^2];

        int atIndex = normalized.LastIndexOf('@');
        if (atIndex > 0)
            normalized = normalized[..atIndex];

        if (normalized.StartsWith("@", StringComparison.Ordinal))
            return "";

        return normalized.Trim();
    }

    private string BuildWildcardInsertText(string wildcardName) =>
        _settings.Settings.WildcardsRequireExplicitSyntax
            ? $"__{wildcardName}__"
            : wildcardName;

    private void PerformAutoCompleteSearch(PromptTextBox textBox, string token)
    {
        int? catFilter = (textBox == TxtStylePrompt) ? 1 : null;
        var results = _tagService.IsLoaded
            ? _tagService.Search(token, 15, catFilter)
            : new List<TagMatch>();

        if (textBox == TxtPrompt && _isSplitPrompt && _isPositiveTab)
            results = results.Where(r => r.Entry.Category != 1).Take(15).ToList();

        string wildcardPrefix = ExtractWildcardSearchPrefix(token);
        var wildcardResults = _settings.Settings.WildcardsEnabled && _wildcardService.IsLoaded && wildcardPrefix.Length > 0
            ? _wildcardService.Search(wildcardPrefix, 8)
            : new List<WildcardSearchResult>();

        if (results.Count == 0 && wildcardResults.Count == 0)
        {
            CloseAutoComplete();
            return;
        }

        var items = new List<AutoCompleteItem>(results.Count + wildcardResults.Count);
        foreach (var r in results)
        {
            items.Add(new AutoCompleteItem
            {
                TagName = r.Entry.Tag.Replace('_', ' '),
                InsertText = r.Entry.Tag.Replace('_', ' '),
                Category = r.Entry.Category,
                CountText = TagCompleteService.FormatCount(r.Entry.Count),
                AliasText = r.MatchedAlias?.Replace('_', ' ') ?? "",
                AliasVisibility = r.MatchedAlias != null
                    ? Visibility.Visible : Visibility.Collapsed,
                CategoryBrush = GetCategoryBrush(r.Entry.Category),
            });
        }

        foreach (var r in wildcardResults)
        {
            items.Add(new AutoCompleteItem
            {
                TagName = r.Entry.Name,
                InsertText = BuildWildcardInsertText(r.Entry.Name),
                Category = -1,
                CountText = L("wildcards.title"),
                AliasText = Lf("wildcards.autocomplete_meta", r.Entry.OptionCount, r.Entry.RelativePath.Replace('\\', '/')),
                AliasVisibility = Visibility.Visible,
                CategoryBrush = GetCategoryBrush(-1),
            });
        }

        bool wasOpen = AutoCompletePopup.IsOpen;
        AutoCompleteList.ItemsSource = items;
        AutoCompleteList.SelectedIndex = -1;
        if (!wasOpen)
        {
            PositionAutoCompletePopup(textBox);
            AutoCompletePopup.IsOpen = true;
        }
    }

    private void PositionAutoCompletePopup(PromptTextBox textBox)
    {
        try
        {
            int caret = textBox.SelectionStart;
            if (caret <= 0) caret = 0;
            var rect = textBox.GetRectFromCharacterIndex(caret > 0 ? caret - 1 : 0, true);

            var popupParent = AutoCompletePopup.Parent as UIElement ?? this.Content as UIElement;
            var transform = textBox.TransformToVisual(popupParent);
            double lineBottom = rect.Y + rect.Height;
            var point = transform.TransformPoint(new Point(
                textBox.Padding.Left,
                lineBottom + 2));
            AutoCompletePopup.HorizontalOffset = point.X;
            AutoCompletePopup.VerticalOffset = point.Y;
        }
        catch { }
    }

    private void InsertAutoCompleteTag(string tag)
    {
        if (_acTargetTextBox == null) return;
        var textBox = _acTargetTextBox;

        _acInserting = true;
        try
        {
            var (start, end) = GetCurrentTokenRange(textBox);
            string text = textBox.Text;

            string suffix = ", ";
            if (end < text.Length && text[end] == ',') suffix = "";

            string newText = text.Substring(0, start) + tag + suffix + text.Substring(end);
            textBox.Text = newText;
            textBox.SelectionStart = start + tag.Length + suffix.Length;
        }
        finally
        {
            _acInserting = false;
        }

        CloseAutoComplete();
        textBox.Focus(FocusState.Programmatic);
    }

    private void CloseAutoComplete()
    {
        if (!AutoCompletePopup.IsOpen) return;
        AutoCompletePopup.IsOpen = false;
        AutoCompleteList.ItemsSource = null;
    }

    private void OnAutoCompleteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AutoCompleteItem item)
            InsertAutoCompleteTag(item.InsertText);
    }

    private static SolidColorBrush GetCategoryBrush(int category) => category switch
    {
        -1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 160, 90, 220)),
        0 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 180, 120)),
        1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 60, 60)),
        3 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 120, 220)),
        4 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 160, 40)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 140, 140, 140)),
    };

    // ═══════════════════════════════════════════════════════════
    //  设置对话框
    // ═══════════════════════════════════════════════════════════
}
