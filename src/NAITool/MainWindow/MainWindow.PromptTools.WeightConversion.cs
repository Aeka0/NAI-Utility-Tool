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
    private bool CanApplyWeightConversionToCurrentWorkspace() =>
        _currentMode switch
        {
            AppMode.ImageGeneration => true,
            AppMode.I2I => true,
            AppMode.Inspect => _inspectMetadata != null &&
                              (_inspectMetadata.IsNaiParsed || _inspectMetadata.IsSdFormat || _inspectMetadata.IsModelInference),
            _ => false,
        };

    private async void ShowWeightConversionDialog()
    {
        if (_isWeightDialogOpen) return;
        _isWeightDialogOpen = true;

        try
        {
            const double panelWidth = 350;
            const double panelGap = 20;
            const double swapColumnWidth = 56;
            string[] formatLabels =
            {
                L("dialog.weight_converter.format.sd_webui"),
                L("dialog.weight_converter.format.nai_classic"),
                L("dialog.weight_converter.format.nai_numeric")
            };

            SaveCurrentPromptToBuffer();
            string initialText = _currentMode switch
            {
                AppMode.ImageGeneration => _genPositivePrompt,
                AppMode.I2I => _i2iPositivePrompt,
                AppMode.Inspect when _inspectMetadata != null => _inspectMetadata.PositivePrompt ?? string.Empty,
                _ => string.Empty,
            };

            var sourceCombo = new ComboBox
            {
                Width = panelWidth,
                MinWidth = panelWidth,
                MaxWidth = panelWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = formatLabels,
                SelectedIndex = PromptWeightFormatToIndex(PromptWeightFormat.StableDiffusion),
            };
            ApplyMenuTypography(sourceCombo);

            var targetCombo = new ComboBox
            {
                Width = panelWidth,
                MinWidth = panelWidth,
                MaxWidth = panelWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = formatLabels,
                SelectedIndex = PromptWeightFormatToIndex(PromptWeightFormat.NaiNumeric),
            };
            ApplyMenuTypography(targetCombo);

            var swapBtn = new Button
            {
                Content = "\u21C4",
                Width = 40,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(swapBtn, L("dialog.weight_converter.swap_formats"));

            var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelWidth) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelGap + swapColumnWidth) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelWidth) });
            Grid.SetColumn(sourceCombo, 0);
            Grid.SetColumn(swapBtn, 1);
            Grid.SetColumn(targetCombo, 2);
            topGrid.Children.Add(sourceCombo);
            topGrid.Children.Add(swapBtn);
            topGrid.Children.Add(targetCombo);

            var inputBox = new TextBox
            {
                Width = panelWidth,
                MinWidth = panelWidth,
                MaxWidth = panelWidth,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                PlaceholderText = L("dialog.weight_converter.input_placeholder"),
                MinHeight = 240,
                MaxHeight = 240,
                VerticalAlignment = VerticalAlignment.Stretch,
                Text = initialText,
            };

            var outputBox = new TextBox
            {
                Width = panelWidth,
                MinWidth = panelWidth,
                MaxWidth = panelWidth,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true,
                PlaceholderText = L("dialog.weight_converter.result_placeholder"),
                MinHeight = 240,
                MaxHeight = 240,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            var textGrid = new Grid();
            textGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelWidth) });
            textGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelGap + swapColumnWidth) });
            textGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelWidth) });
            Grid.SetColumn(inputBox, 0);
            Grid.SetColumn(outputBox, 2);
            textGrid.Children.Add(inputBox);
            textGrid.Children.Add(outputBox);

            var mainPanel = new StackPanel();
            mainPanel.Children.Add(topGrid);
            mainPanel.Children.Add(textGrid);

            void DoConvert()
            {
                var source = IndexToPromptWeightFormat(sourceCombo.SelectedIndex);
                var target = IndexToPromptWeightFormat(targetCombo.SelectedIndex);
                outputBox.Text = ConvertPromptWeightSyntax(inputBox.Text, source, target);
            }

            inputBox.TextChanged += (_, _) => DoConvert();
            sourceCombo.SelectionChanged += (_, _) => DoConvert();
            targetCombo.SelectionChanged += (_, _) => DoConvert();
            swapBtn.Click += (_, _) =>
            {
                int sourceIndex = sourceCombo.SelectedIndex;
                sourceCombo.SelectedIndex = targetCombo.SelectedIndex;
                targetCombo.SelectedIndex = sourceIndex;
                inputBox.Text = outputBox.Text;
            };

            DoConvert();

            bool canApply = CanApplyWeightConversionToCurrentWorkspace();
            var dialog = new ContentDialog
            {
                Title = L("dialog.weight_converter.title"),
                Content = mainPanel,
                CloseButtonText = L("button.close"),
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };
            if (canApply)
            {
                dialog.PrimaryButtonText = L("dialog.weight_converter.send_to_current");
                dialog.DefaultButton = ContentDialogButton.Primary;
            }
            else
            {
                dialog.DefaultButton = ContentDialogButton.Close;
            }
            dialog.Resources["ContentDialogMaxWidth"] = 860.0;

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var source = IndexToPromptWeightFormat(sourceCombo.SelectedIndex);
                var target = IndexToPromptWeightFormat(targetCombo.SelectedIndex);
                ConvertPromptWeightsInCurrentWorkspace(source, target);
            }
        }
        finally
        {
            _isWeightDialogOpen = false;
        }
    }

    private void ConvertPromptWeightsInCurrentWorkspace(PromptWeightFormat source, PromptWeightFormat target)
    {
        if (source == target)
        {
            TxtStatus.Text = L("dialog.weight_converter.same_format");
            return;
        }

        if (_currentMode == AppMode.Effects)
        {
            TxtStatus.Text = L("dialog.weight_converter.no_prompt_current");
            return;
        }

        if (_currentMode == AppMode.Inspect)
        {
            if (_inspectMetadata == null || (!_inspectMetadata.IsNaiParsed && !_inspectMetadata.IsSdFormat && !_inspectMetadata.IsModelInference))
            {
                TxtStatus.Text = L("dialog.weight_converter.no_prompt_inspect");
                return;
            }

            _inspectMetadata.PositivePrompt = ConvertPromptWeightSyntax(_inspectMetadata.PositivePrompt, source, target);
            _inspectMetadata.NegativePrompt = ConvertPromptWeightSyntax(_inspectMetadata.NegativePrompt, source, target);
            _inspectMetadata.CharacterPrompts = _inspectMetadata.CharacterPrompts
                .Select(x => ConvertPromptWeightSyntax(x, source, target))
                .ToList();
            _inspectMetadata.CharacterNegativePrompts = _inspectMetadata.CharacterNegativePrompts
                .Select(x => ConvertPromptWeightSyntax(x, source, target))
                .ToList();
            _inspectMetadata.IsSdFormat = target == PromptWeightFormat.StableDiffusion;

            DisplayInspectMetadata(_inspectMetadata);
            UpdateDynamicMenuStates();
            TxtStatus.Text = Lf("dialog.weight_converter.converted_inspect", GetPromptWeightFormatLabel(source), GetPromptWeightFormatLabel(target));
            return;
        }

        SaveCurrentPromptToBuffer();
        SaveAllCharacterPrompts();

        if (_currentMode == AppMode.ImageGeneration)
        {
            _genPositivePrompt = ConvertPromptWeightSyntax(_genPositivePrompt, source, target);
            _genNegativePrompt = ConvertPromptWeightSyntax(_genNegativePrompt, source, target);
            _genStylePrompt = ConvertPromptWeightSyntax(_genStylePrompt, source, target);
        }
        else
        {
            _i2iPositivePrompt = ConvertPromptWeightSyntax(_i2iPositivePrompt, source, target);
            _i2iNegativePrompt = ConvertPromptWeightSyntax(_i2iNegativePrompt, source, target);
            _i2iStylePrompt = ConvertPromptWeightSyntax(_i2iStylePrompt, source, target);
        }

        foreach (var entry in CurrentCharacterEntries)
        {
            entry.PositivePrompt = ConvertPromptWeightSyntax(entry.PositivePrompt, source, target);
            entry.NegativePrompt = ConvertPromptWeightSyntax(entry.NegativePrompt, source, target);
        }

        LoadPromptFromBuffer();
        UpdateSplitVisibility();
        RefreshCharacterPanel();
        UpdatePromptHighlights();
        UpdateStyleHighlights();
        TxtStatus.Text = Lf("dialog.weight_converter.converted_current", GetPromptWeightFormatLabel(source), GetPromptWeightFormatLabel(target));
    }

    private static string ConvertPromptWeightSyntax(string text, PromptWeightFormat source, PromptWeightFormat target)
    {
        if (string.IsNullOrEmpty(text) || source == target) return text;

        List<WeightedSpan> spans = ParsePromptWeightedSpans(text, source);
        return RenderPromptWeightedSpans(spans, target);
    }

    private static List<WeightedSpan> ParsePromptWeightedSpans(string text, PromptWeightFormat format)
    {
        var spans = new List<WeightedSpan>();
        ParsePromptWeightedSpans(text, format, 1.0, spans);
        return MergeWeightedSpans(spans);
    }

    private static void ParsePromptWeightedSpans(string text, PromptWeightFormat format, double weight, List<WeightedSpan> spans)
    {
        var buffer = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (format == PromptWeightFormat.NaiNumeric &&
                TryReadNaiNumericSegment(text, i, out double numericWeight, out string numericInner, out int numericNext))
            {
                AppendWeightedSpan(spans, buffer, weight);
                ParsePromptWeightedSpans(numericInner, format, weight * numericWeight, spans);
                i = numericNext;
                continue;
            }

            if (TryReadBracketedSegment(text, format, i, out double segmentWeight, out string segmentInner, out int segmentNext))
            {
                AppendWeightedSpan(spans, buffer, weight);
                ParsePromptWeightedSpans(segmentInner, format, weight * segmentWeight, spans);
                i = segmentNext;
                continue;
            }

            buffer.Append(text[i]);
            i++;
        }

        AppendWeightedSpan(spans, buffer, weight);
    }

    private static bool TryReadBracketedSegment(string text, PromptWeightFormat format, int start, out double weight, out string inner, out int next)
    {
        weight = 1.0;
        inner = "";
        next = start;
        if (start < 0 || start >= text.Length) return false;

        char open = text[start];
        char close;
        switch (format)
        {
            case PromptWeightFormat.StableDiffusion when open == '(' || open == '[':
                close = open == '(' ? ')' : ']';
                break;
            case PromptWeightFormat.NaiClassic when open == '{' || open == '[':
            case PromptWeightFormat.NaiNumeric when open == '{' || open == '[':
                close = open == '{' ? '}' : ']';
                break;
            default:
                return false;
        }

        if (!TryReadDelimitedGroup(text, start, open, close, out string rawInner, out next))
            return false;

        if (format == PromptWeightFormat.StableDiffusion)
        {
            if (TryParseTrailingExplicitWeight(rawInner, out string explicitInner, out double explicitWeight))
            {
                inner = explicitInner;
                weight = explicitWeight;
            }
            else
            {
                inner = rawInner;
                weight = open == '(' ? 1.1 : 1.0 / 1.1;
            }
            return true;
        }

        inner = rawInner;
        weight = open == '{' ? 1.05 : 1.0 / 1.05;
        return true;
    }

    private static bool TryReadDelimitedGroup(string text, int start, char open, char close, out string inner, out int next)
    {
        inner = "";
        next = start;
        if (start < 0 || start >= text.Length || text[start] != open) return false;

        int depth = 1;
        for (int i = start + 1; i < text.Length; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    inner = text.Substring(start + 1, i - start - 1);
                    next = i + 1;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseTrailingExplicitWeight(string text, out string inner, out double weight)
    {
        inner = text;
        weight = 1.0;
        int depth = 0;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char ch = text[i];
            if (ch == ')' || ch == ']' || ch == '}') depth++;
            else if (ch == '(' || ch == '[' || ch == '{') depth--;
            else if (ch == ':' && depth == 0)
            {
                string weightText = text[(i + 1)..].Trim();
                if (!double.TryParse(weightText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                    return false;

                string content = text[..i];
                if (string.IsNullOrWhiteSpace(content)) return false;

                inner = content;
                weight = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadNaiNumericSegment(string text, int start, out double weight, out string inner, out int next)
    {
        weight = 1.0;
        inner = "";
        next = start;
        if (start < 0 || start >= text.Length) return false;

        int prefixEnd = text.IndexOf("::", start, StringComparison.Ordinal);
        if (prefixEnd <= start) return false;

        string numberText = text.Substring(start, prefixEnd - start).Trim();
        if (!double.TryParse(numberText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out weight))
            return false;

        int close = text.IndexOf("::", prefixEnd + 2, StringComparison.Ordinal);
        if (close < 0) return false;

        inner = text.Substring(prefixEnd + 2, close - prefixEnd - 2);
        next = close + 2;
        return true;
    }

    private static void AppendWeightedSpan(List<WeightedSpan> spans, StringBuilder buffer, double weight)
    {
        if (buffer.Length == 0) return;
        spans.Add(new WeightedSpan(buffer.ToString(), weight));
        buffer.Clear();
    }

    private static List<WeightedSpan> MergeWeightedSpans(List<WeightedSpan> spans)
    {
        var merged = new List<WeightedSpan>();
        foreach (var span in spans)
        {
            if (string.IsNullOrEmpty(span.Text)) continue;
            if (merged.Count > 0 && Math.Abs(merged[^1].Weight - span.Weight) < 0.0001)
            {
                var last = merged[^1];
                merged[^1] = new WeightedSpan(last.Text + span.Text, last.Weight);
            }
            else
            {
                merged.Add(span);
            }
        }
        return merged;
    }

    private static string RenderPromptWeightedSpans(List<WeightedSpan> spans, PromptWeightFormat target)
    {
        var sb = new StringBuilder();
        foreach (var span in spans)
            sb.Append(RenderWeightedSpan(span, target));
        return sb.ToString();
    }

    private static string RenderWeightedSpan(WeightedSpan span, PromptWeightFormat target)
    {
        if (Math.Abs(span.Weight - 1.0) < 0.0001) return span.Text;

        return WrapWeightedContent(span.Text, core =>
        {
            switch (target)
            {
                case PromptWeightFormat.StableDiffusion:
                    if (span.Weight > 1.0)
                        return $"({core}:{FormatWeightValue(span.Weight)})";
                    if (span.Weight >= 0)
                        return $"[{core}:{FormatWeightValue(span.Weight)}]";
                    return $"({core}:{FormatWeightValue(span.Weight)})";

                case PromptWeightFormat.NaiClassic:
                    if (span.Weight <= 0)
                        return $"{FormatWeightValue(span.Weight)}::{core}::";

                    double ratio = Math.Log(span.Weight) / Math.Log(1.05);
                    int layers = (int)Math.Round(ratio, MidpointRounding.AwayFromZero);
                    if (layers == 0) return core;
                    return layers > 0
                        ? new string('{', layers) + core + new string('}', layers)
                        : new string('[', -layers) + core + new string(']', -layers);

                default:
                    return $"{FormatWeightValue(span.Weight)}::{core}::";
            }
        });
    }

    private static string WrapWeightedContent(string text, Func<string, string> wrapper)
    {
        if (string.IsNullOrEmpty(text)) return text;

        int start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;

        int end = text.Length - 1;
        while (end >= start && char.IsWhiteSpace(text[end])) end--;

        if (start > end) return text;

        string leading = text[..start];
        string core = text.Substring(start, end - start + 1);
        string trailing = text[(end + 1)..];
        return leading + wrapper(core) + trailing;
    }

    private static string FormatWeightValue(double value)
    {
        double rounded = Math.Round(value, 3, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private string GetPromptWeightFormatLabel(PromptWeightFormat format) => format switch
    {
        PromptWeightFormat.StableDiffusion => L("dialog.weight_converter.short.sd"),
        PromptWeightFormat.NaiClassic => L("dialog.weight_converter.short.nai_classic"),
        PromptWeightFormat.NaiNumeric => L("dialog.weight_converter.short.nai_numeric"),
        _ => L("dialog.weight_converter.short.unknown"),
    };
}
