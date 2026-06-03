using System;
using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using NAITool.Controls;
using Windows.System;

namespace NAITool;

public sealed partial class MainWindow
{
    private enum PromptWeightSyntax
    {
        NaiClassic,
        NaiNumeric,
    }

    private enum PromptWeightedRegionKind
    {
        Classic,
        Numeric,
    }

    private readonly record struct PromptEditRange(int Start, int End)
    {
        public int Length => End - Start;
    }

    private readonly record struct PromptWeightedRegion(
        PromptWeightedRegionKind Kind,
        int Start,
        int End,
        int InnerStart,
        int InnerEnd,
        char Open,
        double Weight);

    private void OnPromptEditorPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not PromptTextBox textBox || !IsCtrlKeyDown())
            return;

        int delta = e.GetCurrentPoint(textBox).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        if (TryAdjustPromptWeight(textBox, delta > 0))
            e.Handled = true;
    }

    private bool TryHandlePromptWeightShortcut(PromptTextBox textBox, VirtualKey key)
    {
        if (!IsCtrlKeyDown())
            return false;

        return key switch
        {
            VirtualKey.Up or VirtualKey.NumberPad8 => TryAdjustPromptWeight(textBox, increase: true),
            VirtualKey.Down or VirtualKey.NumberPad2 => TryAdjustPromptWeight(textBox, increase: false),
            _ => false,
        };
    }

    private bool TryAdjustPromptWeight(PromptTextBox textBox, bool increase)
    {
        string text = textBox.Text ?? string.Empty;
        if (text.Length == 0 && textBox.SelectionLength <= 0)
            return false;

        int caret = Math.Clamp(textBox.SelectionStart, 0, text.Length);
        int selectionStart = Math.Clamp(textBox.SelectionStart, 0, text.Length);
        int selectionEnd = Math.Clamp(selectionStart + Math.Max(0, textBox.SelectionLength), selectionStart, text.Length);

        _suppressPromptAutoComplete = true;
        try
        {
            if (selectionEnd > selectionStart &&
                TryTrimRange(text, new PromptEditRange(selectionStart, selectionEnd), out var selectedRange))
            {
                ApplyNewPromptWeight(textBox, selectedRange, increase);
                return true;
            }

            if (TryFindWeightedRegionAtCaret(text, caret, out var weightedRegion))
            {
                ApplyExistingPromptWeight(textBox, weightedRegion, increase);
                return true;
            }

            if (TryFindExactTagRangeAtCaret(textBox, caret, out var tagRange))
            {
                ApplyNewPromptWeight(textBox, tagRange, increase);
                return true;
            }

            if (TryFindFallbackPromptRange(text, caret, out var fallbackRange))
            {
                ApplyNewPromptWeight(textBox, fallbackRange, increase);
                return true;
            }

            return false;
        }
        finally
        {
            _suppressPromptAutoComplete = false;
            CloseAutoComplete();
        }
    }

    private PromptWeightSyntax GetPromptWeightSyntaxForCurrentModel() =>
        IsV3ModelKey(GetCurrentModelKey())
            ? PromptWeightSyntax.NaiClassic
            : PromptWeightSyntax.NaiNumeric;

    private bool TryFindExactTagRangeAtCaret(PromptTextBox textBox, int caret, out PromptEditRange range)
    {
        range = default;
        string text = textBox.Text ?? string.Empty;
        if (!_tagService.IsLoaded || text.Length == 0)
            return false;

        var rawRange = GetDelimitedPromptTokenRange(text, caret);
        if (!TryTrimRange(text, rawRange, out range))
            return false;

        string token = text.Substring(range.Start, range.Length);
        int? categoryFilter = ReferenceEquals(textBox, TxtStylePrompt) ? 1 : null;
        return _tagService.ContainsExactTag(token, categoryFilter);
    }

    private static PromptEditRange GetDelimitedPromptTokenRange(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);

        int start = caret;
        while (start > 0 && !IsPromptBoundary(text[start - 1]))
            start--;

        int end = caret;
        while (end < text.Length && !IsPromptBoundary(text[end]))
            end++;

        return new PromptEditRange(start, end);
    }

    private static bool TryFindFallbackPromptRange(string text, int caret, out PromptEditRange range)
    {
        range = default;
        if (string.IsNullOrEmpty(text))
            return false;

        caret = Math.Clamp(caret, 0, text.Length);

        int start = 0;
        for (int i = Math.Max(0, caret - 1); i >= 0; i--)
        {
            if (!IsPromptBoundary(text[i]))
                continue;

            start = i + 1;
            while (start < text.Length && text[start] == ' ')
                start++;
            break;
        }

        int end = caret;
        while (end < text.Length && !IsPromptBoundary(text[end]))
            end++;

        return TryTrimRange(text, new PromptEditRange(start, end), out range);
    }

    private static bool TryFindWeightedRegionAtCaret(string text, int caret, out PromptWeightedRegion region)
    {
        bool hasNumeric = TryFindNaiNumericRegionAtCaret(text, caret, out var numericRegion);
        bool hasClassic = TryFindClassicRegionAtCaret(text, caret, out var classicRegion);

        if (hasNumeric && hasClassic)
        {
            region = numericRegion.End - numericRegion.Start <= classicRegion.End - classicRegion.Start
                ? numericRegion
                : classicRegion;
            return true;
        }

        if (hasNumeric)
        {
            region = numericRegion;
            return true;
        }

        if (hasClassic)
        {
            region = classicRegion;
            return true;
        }

        region = default;
        return false;
    }

    private static bool TryFindNaiNumericRegionAtCaret(string text, int caret, out PromptWeightedRegion region)
    {
        region = default;
        if (string.IsNullOrEmpty(text))
            return false;

        caret = Math.Clamp(caret, 0, text.Length);
        bool found = false;
        int bestLength = int.MaxValue;

        for (int i = 0; i < text.Length; i++)
        {
            if (!CouldStartNumber(text[i]))
                continue;

            if (!IsNumericWeightStart(text, i))
                continue;

            if (!TryReadNaiNumericSegment(text, i, out double weight, out _, out int next))
                continue;

            int prefixEnd = text.IndexOf("::", i, StringComparison.Ordinal);
            if (prefixEnd < 0)
                continue;

            if (caret < i || caret > next)
                continue;

            int length = next - i;
            if (length >= bestLength)
                continue;

            found = true;
            bestLength = length;
            region = new PromptWeightedRegion(
                PromptWeightedRegionKind.Numeric,
                i,
                next,
                prefixEnd + 2,
                next - 2,
                '\0',
                weight);
        }

        return found;
    }

    private static bool TryFindClassicRegionAtCaret(string text, int caret, out PromptWeightedRegion region)
    {
        region = default;
        if (string.IsNullOrEmpty(text))
            return false;

        caret = Math.Clamp(caret, 0, text.Length);
        Span<(char Open, int Start)> stack = stackalloc (char Open, int Start)[128];
        var overflowStack = stack.Length > 0 ? null : new System.Collections.Generic.List<(char Open, int Start)>();
        int stackCount = 0;
        bool found = false;
        int bestLength = int.MaxValue;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch is '{' or '[')
            {
                if (stackCount < stack.Length)
                {
                    stack[stackCount++] = (ch, i);
                }
                else
                {
                    overflowStack ??= new System.Collections.Generic.List<(char Open, int Start)>();
                    overflowStack.Add((ch, i));
                }
                continue;
            }

            if (ch is not '}' and not ']')
                continue;

            char expectedOpen = ch == '}' ? '{' : '[';
            if (!TryPopClassicOpen(expectedOpen, stack, ref stackCount, overflowStack, out int start))
                continue;

            int end = i + 1;
            if (caret < start || caret > end)
                continue;

            int length = end - start;
            if (length >= bestLength)
                continue;

            found = true;
            bestLength = length;
            region = new PromptWeightedRegion(
                PromptWeightedRegionKind.Classic,
                start,
                end,
                start + 1,
                end - 1,
                expectedOpen,
                1.0);
        }

        return found;
    }

    private static bool TryPopClassicOpen(
        char expectedOpen,
        Span<(char Open, int Start)> stack,
        ref int stackCount,
        System.Collections.Generic.List<(char Open, int Start)>? overflowStack,
        out int start)
    {
        start = 0;
        if (overflowStack is { Count: > 0 })
        {
            int last = overflowStack.Count - 1;
            if (overflowStack[last].Open != expectedOpen)
                return false;

            start = overflowStack[last].Start;
            overflowStack.RemoveAt(last);
            return true;
        }

        if (stackCount <= 0 || stack[stackCount - 1].Open != expectedOpen)
            return false;

        start = stack[--stackCount].Start;
        return true;
    }

    private void ApplyExistingPromptWeight(PromptTextBox textBox, PromptWeightedRegion region, bool increase)
    {
        if (region.Kind == PromptWeightedRegionKind.Numeric)
        {
            ApplyExistingNumericPromptWeight(textBox, region, increase);
            return;
        }

        bool shouldAddLayer = (increase && region.Open == '{') || (!increase && region.Open == '[');
        if (shouldAddLayer)
            AddClassicPromptWeightLayer(textBox, region.Start, region.End, increase ? '{' : '[');
        else
            RemoveClassicPromptWeightLayer(textBox, region.Start, region.End);
    }

    private static void ApplyExistingNumericPromptWeight(PromptTextBox textBox, PromptWeightedRegion region, bool increase)
    {
        string text = textBox.Text ?? string.Empty;
        int numberEnd = text.IndexOf("::", region.Start, StringComparison.Ordinal);
        if (numberEnd <= region.Start)
            return;

        double nextWeight = Math.Round(region.Weight + (increase ? 0.1 : -0.1), 1, MidpointRounding.AwayFromZero);
        if (Math.Abs(nextWeight) < 0.0001)
            nextWeight = 0;

        if (Math.Abs(nextWeight - 1.0) < 0.0001)
        {
            string inner = text[region.InnerStart..region.InnerEnd];
            int unwrappedCaret = textBox.SelectionStart;
            if (unwrappedCaret <= region.Start)
                unwrappedCaret = region.Start;
            else if (unwrappedCaret >= region.End)
                unwrappedCaret -= region.End - region.Start - inner.Length;
            else if (unwrappedCaret <= region.InnerStart)
                unwrappedCaret = region.Start;
            else if (unwrappedCaret <= region.InnerEnd)
                unwrappedCaret -= region.InnerStart - region.Start;
            else
                unwrappedCaret = region.Start + inner.Length;

            ReplacePromptText(textBox, region.Start, region.End - region.Start, inner, unwrappedCaret);
            return;
        }

        string weightText = FormatWeightValue(nextWeight);
        int lengthDelta = weightText.Length - (numberEnd - region.Start);

        int caret = textBox.SelectionStart;
        if (caret > numberEnd)
            caret += lengthDelta;
        else if (caret > region.Start)
            caret = region.Start + weightText.Length;

        ReplacePromptText(textBox, region.Start, numberEnd - region.Start, weightText, caret);
    }

    private static void AddClassicPromptWeightLayer(PromptTextBox textBox, int start, int end, char open)
    {
        string text = textBox.Text ?? string.Empty;
        char close = open == '{' ? '}' : ']';
        string replacement = open + text[start..end] + close;

        int caret = textBox.SelectionStart;
        if (caret >= start)
            caret++;

        ReplacePromptText(textBox, start, end - start, replacement, caret);
    }

    private static void RemoveClassicPromptWeightLayer(PromptTextBox textBox, int start, int end)
    {
        string text = textBox.Text ?? string.Empty;
        if (start < 0 || end > text.Length || end - start < 2)
            return;

        string replacement = text[(start + 1)..(end - 1)];
        int caret = textBox.SelectionStart;
        if (caret > start)
            caret--;
        if (caret > end - 1)
            caret--;

        ReplacePromptText(textBox, start, end - start, replacement, caret);
    }

    private void ApplyNewPromptWeight(PromptTextBox textBox, PromptEditRange range, bool increase)
    {
        string text = textBox.Text ?? string.Empty;
        if (range.Start < 0 || range.End > text.Length || range.End <= range.Start)
            return;

        string content = text.Substring(range.Start, range.Length);
        string wrapped;
        int caretPrefixLength;

        if (GetPromptWeightSyntaxForCurrentModel() == PromptWeightSyntax.NaiClassic)
        {
            char open = increase ? '{' : '[';
            char close = increase ? '}' : ']';
            wrapped = $"{open}{content}{close}";
            caretPrefixLength = 1;
        }
        else
        {
            string weightText = increase ? "1.1" : "0.9";
            string prefix = $"{weightText}::";
            wrapped = $"{prefix}{content}::";
            caretPrefixLength = prefix.Length;
        }

        int oldCaret = Math.Clamp(textBox.SelectionStart, range.Start, range.End);
        int contentOffset = oldCaret - range.Start;

        ReplacePromptText(textBox, range.Start, range.Length, wrapped, range.Start + caretPrefixLength + contentOffset);
    }

    private static void ReplacePromptText(PromptTextBox textBox, int start, int length, string replacement, int caret)
    {
        string text = textBox.Text ?? string.Empty;
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);

        textBox.Focus(FocusState.Programmatic);
        textBox.Select(start, length);
        textBox.SelectedText = replacement;
        textBox.SelectionStart = Math.Clamp(caret, 0, (textBox.Text ?? string.Empty).Length);
        textBox.SelectionLength = 0;
    }

    private static bool TryTrimRange(string text, PromptEditRange range, out PromptEditRange trimmed)
    {
        int start = Math.Clamp(range.Start, 0, text.Length);
        int end = Math.Clamp(range.End, start, text.Length);

        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        trimmed = new PromptEditRange(start, end);
        return end > start;
    }

    private static bool IsPromptBoundary(char ch) =>
        ch is ',' or '.' or '\r' or '\n' or '，' or '。';

    private static bool CouldStartNumber(char ch) =>
        char.IsDigit(ch) || ch is '+' or '-' or '.';

    private static bool IsNumericWeightStart(string text, int start)
    {
        if (start <= 0)
            return true;

        char previous = text[start - 1];
        return !char.IsDigit(previous)
            && previous is not '.'
            && previous is not '+'
            && previous is not '-';
    }

    private static bool IsCtrlKeyDown()
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var leftCtrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftControl);
        var rightCtrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightControl);

        return ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || leftCtrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || rightCtrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }
}
