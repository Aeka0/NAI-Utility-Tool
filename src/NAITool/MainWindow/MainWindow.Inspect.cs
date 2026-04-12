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
    //  检视模式
    // ═══════════════════════════════════════════════════════════

    private void SetInspectPrimaryAction(InspectPrimaryAction action, bool isEnabled)
    {
        _inspectPrimaryAction = action;
        BtnSendToGen.IsEnabled = isEnabled;

        switch (action)
        {
            case InspectPrimaryAction.InferTags:
                BtnSendToGenIcon.Symbol = Symbol.Tag;
                BtnSendToGenText.Text = L("inspect.action.infer");
                break;
            case InspectPrimaryAction.DisabledSend:
            case InspectPrimaryAction.SendMetadata:
            default:
                BtnSendToGenIcon.Symbol = Symbol.Send;
                BtnSendToGenText.Text = L("inspect.action.send_metadata");
                break;
        }
    }

    private static string FormatInspectValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string FormatInspectNumber(double value)
        => value > 0 ? value.ToString("G") : "-";

    private async Task<byte[]?> CreateCurrentCanvasImageBytesAsync()
    {
        var device = MaskCanvas.GetDevice();
        if (device == null) return null;

        var doc = MaskCanvas.Document;
        if (doc.OriginalImage == null) return null;

        int canvasW = MaskCanvas.CanvasW;
        int canvasH = MaskCanvas.CanvasH;
        var offset = doc.PixelAlignedImageOffset;

        using var composite = new CanvasRenderTarget(device, canvasW, canvasH, 96f);
        using (var ds = composite.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawImage(doc.OriginalImage, offset.X, offset.Y);
        }

        using var saveStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await composite.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
        saveStream.Seek(0);
        var bytes = new byte[saveStream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(saveStream);
        await reader.LoadAsync((uint)saveStream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task<byte[]?> GetI2IPromptInferenceImageBytesAsync(bool canvasOnly)
    {
        if (MaskCanvas.IsInPreviewMode && _pendingResultBytes != null)
            return canvasOnly ? _pendingResultBytes : await CreatePreviewCompositeBytes();

        return canvasOnly
            ? await CreateCurrentCanvasImageBytesAsync()
            : await CreateCurrentFullImageBytes();
    }

    private async Task RunI2IPromptInferenceAsync(bool canvasOnly)
    {
        SaveCurrentPromptToBuffer();

        var imageBytes = await GetI2IPromptInferenceImageBytesAsync(canvasOnly);
        if (imageBytes == null || imageBytes.Length == 0)
        {
            TxtStatus.Text = canvasOnly ? L("inspect.infer.no_canvas_image") : L("inspect.infer.no_global_image");
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Settings.ReverseTagger.ModelPath))
        {
            TxtStatus.Text = L("reverse.error.model_path_not_set");
            await ShowReverseTaggerSettingsDialogAsync();
            return;
        }

        string modeLabel = canvasOnly ? L("inspect.infer.canvas_mode") : L("inspect.infer.global_mode");
        TxtStatus.Text = Lf("inspect.infer.running", modeLabel);
        DebugLog($"[InpaintPromptInfer] Start | Mode={modeLabel}");

        try
        {
            var result = await _reverseTaggerService.InferAsync(imageBytes, _settings.Settings.ReverseTagger);
            var artistTagSet = LoadReverseTaggerArtistTagSet();
            var preservedArtistTags = ExtractArtistTags(_i2iPositivePrompt, artistTagSet);
            _i2iPositivePrompt = MergePromptTagsPreservingArtists(preservedArtistTags, result.PositivePrompt);

            LoadPromptFromBuffer();
            UpdateSplitVisibility();
            UpdatePromptHighlights();
            UpdateStyleHighlights();

            int totalTags = result.GeneralTags.Count +
                            (_settings.Settings.ReverseTagger.AddCharacterTags ? result.CharacterTags.Count : 0) +
                            (_settings.Settings.ReverseTagger.AddCopyrightTags ? result.CopyrightTags.Count : 0);
            DebugLog($"[InpaintPromptInfer] Completed | Mode={modeLabel} | PreservedStyleTags={preservedArtistTags.Count} | TagCount={totalTags}");
            TxtStatus.Text = Lf("inspect.infer.completed", modeLabel);
        }
        catch (Exception ex)
        {
            DebugLog($"[InpaintPromptInfer] Failed: {ex}");
            TxtStatus.Text = Lf("inspect.infer.failed", modeLabel, ex.Message);
        }
        finally
        {
            if (_settings.Settings.ReverseTagger.UnloadModelAfterInference)
                _reverseTaggerService.Dispose();
        }
    }

    private static string MergePromptTagsPreservingArtists(IReadOnlyList<string> artistTags, string inferredPrompt)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in artistTags)
        {
            string normalized = NormalizePromptTagForMatch(tag);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                merged.Add(tag.Trim());
        }

        foreach (var tag in SplitPromptTags(inferredPrompt))
        {
            string normalized = NormalizePromptTagForMatch(tag);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                merged.Add(tag);
        }

        return string.Join(", ", merged);
    }

    private List<string> ExtractArtistTags(string prompt, HashSet<string> artistTagSet)
    {
        var result = new List<string>();
        foreach (var tag in SplitPromptTags(prompt))
        {
            if (IsArtistPromptTag(tag, artistTagSet))
                result.Add(tag);
        }
        return result;
    }

    private static List<string> SplitPromptTags(string prompt) =>
        (prompt ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

    private static bool IsArtistPromptTag(string tag, HashSet<string> artistTagSet)
    {
        string normalized = NormalizePromptTagForMatch(tag);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized.StartsWith("artist:", StringComparison.OrdinalIgnoreCase) ||
               artistTagSet.Contains(normalized);
    }

    private static string NormalizePromptTagForMatch(string tag)
    {
        string normalized = (tag ?? "").Trim();
        normalized = Regex.Replace(normalized, @"^[\(\[\{<\s]+|[\)\]\}>\s]+$", "");
        normalized = Regex.Replace(normalized, @":\s*-?\d+(\.\d+)?$", "");
        normalized = normalized.Replace('_', ' ');
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized.ToLowerInvariant();
    }

    private HashSet<string> LoadReverseTaggerArtistTagSet()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string modelDir = _settings.Settings.ReverseTagger.ModelPath?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(modelDir))
            return result;

        string csvPath = Path.Combine(Path.GetFullPath(modelDir), "selected_tags.csv");
        if (!File.Exists(csvPath))
            return result;

        bool isFirstLine = true;
        foreach (var line in File.ReadLines(csvPath, Encoding.UTF8))
        {
            if (isFirstLine)
            {
                isFirstLine = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseSimpleCsvLine(line);
            if (fields.Count < 4)
                continue;

            if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int category) ||
                category != 1)
                continue;

            string tagName = fields[2];
            if (!string.IsNullOrWhiteSpace(tagName))
                result.Add(NormalizePromptTagForMatch(tagName));
        }

        return result;
    }

    private static List<string> ParseSimpleCsvLine(string line)
    {
        var fields = new List<string>();
        var builder = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                fields.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(ch);
        }

        fields.Add(builder.ToString());
        return fields;
    }

    private async Task RunInspectReverseTagAsync()
    {
        if (_inspectImageBytes == null)
        {
            TxtStatus.Text = L("inspect.reverse.no_image");
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Settings.ReverseTagger.ModelPath))
        {
            TxtStatus.Text = L("reverse.error.model_path_not_set");
            await ShowReverseTaggerSettingsDialogAsync();
            return;
        }

        SetInspectPrimaryAction(InspectPrimaryAction.InferTags, false);
        BtnSendToGenText.Text = L("inspect.reverse.running_short");
        TxtStatus.Text = L("inspect.reverse.running");
        DebugLog($"[ReverseTagger] Start | Model={_settings.Settings.ReverseTagger.ModelPath}");

        try
        {
            var result = await _reverseTaggerService.InferAsync(_inspectImageBytes, _settings.Settings.ReverseTagger);

            _inspectMetadata = new ImageMetadata
            {
                PositivePrompt = result.PositivePrompt,
                NegativePrompt = "",
                Width = result.ImageWidth,
                Height = result.ImageHeight,
                Source = result.ExecutionProvider,
                IsModelInference = true,
            };

            DisplayInspectMetadata(_inspectMetadata);

            int totalTags = result.GeneralTags.Count +
                            (_settings.Settings.ReverseTagger.AddCharacterTags ? result.CharacterTags.Count : 0) +
                            (_settings.Settings.ReverseTagger.AddCopyrightTags ? result.CopyrightTags.Count : 0);
            DebugLog($"[ReverseTagger] Completed | Provider={result.ExecutionProvider} | TagCount={totalTags}");
            TxtStatus.Text = Lf("inspect.reverse.completed", result.ExecutionProvider, totalTags);
        }
        catch (Exception ex)
        {
            DebugLog($"[ReverseTagger] Failed: {ex}");
            SetInspectPrimaryAction(InspectPrimaryAction.InferTags, _inspectImageBytes != null);
            TxtStatus.Text = Lf("inspect.reverse.failed", ex.Message);
        }
        finally
        {
            if (_settings.Settings.ReverseTagger.UnloadModelAfterInference)
                _reverseTaggerService.Dispose();
        }
    }

    private async void RunInspectImageScrambleAsync(ImageScrambleService.ProcessType processType)
    {
        if (_inspectImageBytes == null) return;

        try
        {
            TxtStatus.Text = processType == ImageScrambleService.ProcessType.Encrypt
                ? L("inspect.scramble.encrypting")
                : L("inspect.scramble.decrypting");

            byte[]? resultBytes = await Task.Run(() =>
            {
                using var bitmap = SKBitmap.Decode(_inspectImageBytes);
                if (bitmap == null) return null;

                using var processed = ImageScrambleService.Process(bitmap, processType);
                using var image = SKImage.FromBitmap(processed);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data?.ToArray();
            });

            if (resultBytes == null)
            {
                TxtStatus.Text = L("inspect.scramble.process_failed");
                return;
            }

            _inspectImageBytes = resultBytes;
            _inspectRawModified = true;

            using var ms = new MemoryStream(resultBytes);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            InspectPreviewImage.Source = bmp;

            TxtStatus.Text = processType == ImageScrambleService.ProcessType.Encrypt
                ? L("inspect.scramble.encrypt_done")
                : L("inspect.scramble.decrypt_done");

            UpdateFileMenuState();
            if (_currentMode == AppMode.Inspect) ReplaceEditMenu();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Lf("inspect.scramble.failed", ex.Message);
        }
    }
}
