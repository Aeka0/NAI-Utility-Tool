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
    private sealed class WeightedSpan
    {
        public WeightedSpan(string text, double weight)
        {
            Text = text;
            Weight = weight;
        }

        public string Text { get; }
        public double Weight { get; }
    }

    private bool _isWeightDialogOpen;
    private bool _isVibeEncodeDialogOpen;
    private bool _isPromptGeneratorDialogOpen;

    private static readonly Regex PromptGeneratorFenceRegex =
        new(@"\*{4}\s*(.*?)\s*\*{4}", RegexOptions.Singleline | RegexOptions.Compiled);

    private bool IsKnownOpusSubscriber() =>
        _isOpusSubscriber || (_settings.CachedApiConfig.SubscriptionTierLevel ?? -1) >= 3;

    private static string ExtractGeneratedPromptText(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return "";

        var match = PromptGeneratorFenceRegex.Match(responseText);
        string prompt = match.Success ? match.Groups[1].Value : responseText;
        return prompt
            .Replace('_', ' ')
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim()
            .Trim('"', '\'', '*', ',', ' ');
    }

    private static string BuildPromptGeneratorInstruction(string userInput, PromptGeneratorOutputMode outputMode)
    {
        string outputRule = outputMode switch
        {
            PromptGeneratorOutputMode.BooruTagsWithNaturalLanguage =>
                "Output format: Booru-style tags (short keywords/phrases like '1girl, solo, outdoors') first, followed by a short natural-language English image description at the end. Separate every tag and the final description with an English comma and one space. Use spaces instead of underscores inside tags.",
            PromptGeneratorOutputMode.NaturalLanguage =>
                "Output format: natural-language English only. Write one concise image prompt sentence or phrase, and do not use Booru-style tag lists.",
            _ =>
                "Output format: Booru-style tags only (short keywords/phrases like '1girl, solo, outdoors', NOT natural language sentences), separated by an English comma and one space. Use spaces instead of underscores inside tags.",
        };

        return
            "You are a prompt writer for NovelAI image generation. Convert the user idea inside <idea> into one polished image prompt.\n" +
            "Rules:\n" +
            "- Preserve the user's concrete subject, action, setting, mood, and composition.\n" +
            "- Add only helpful visual details when the user leaves gaps.\n" +
            "- Use English only in the final prompt.\n" +
            "- STRICTLY do not include quality tags (e.g., 'best quality', 'masterpiece', 'highres'), negative prompt text, ratings, artist names, model names, explanations, or markdown other than the required wrapper.\n" +
            "- Do not add trailing commas or spaces at the end of the prompt.\n" +
            "- Output exactly one prompt wrapped with four asterisks on both sides.\n" +
            $"- {outputRule}\n" +
            "<idea>\n" +
            userInput.Trim() +
            "\n</idea>";
    }

    private static PromptWeightFormat IndexToPromptWeightFormat(int index) => index switch
    {
        0 => PromptWeightFormat.StableDiffusion,
        1 => PromptWeightFormat.NaiClassic,
        _ => PromptWeightFormat.NaiNumeric,
    };

    private static int PromptWeightFormatToIndex(PromptWeightFormat format) => format switch
    {
        PromptWeightFormat.StableDiffusion => 0,
        PromptWeightFormat.NaiClassic => 1,
        _ => 2,
    };

    private async void ShowPromptGeneratorDialog()
    {
        if (_isPromptGeneratorDialogOpen) return;
        _isPromptGeneratorDialogOpen = true;

        try
        {
            var inputBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 140,
                MaxHeight = 220,
                PlaceholderText = L("dialog.prompt_generator.input_placeholder"),
                IsSpellCheckEnabled = false,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(inputBox, ScrollBarVisibility.Auto);

            var outputBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 140,
                MaxHeight = 220,
                PlaceholderText = L("dialog.prompt_generator.output_placeholder"),
                IsReadOnly = true,
                IsSpellCheckEnabled = false,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);

            var generateBtn = new Button
            {
                Content = L("dialog.prompt_generator.generate"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                IsEnabled = false,
            };

            var outputModeBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            outputModeBox.Items.Add(new ComboBoxItem
            {
                Content = L("dialog.prompt_generator.mode_booru_tags"),
                Tag = PromptGeneratorOutputMode.BooruTags,
            });
            outputModeBox.Items.Add(new ComboBoxItem
            {
                Content = L("dialog.prompt_generator.mode_booru_tags_natural"),
                Tag = PromptGeneratorOutputMode.BooruTagsWithNaturalLanguage,
            });
            outputModeBox.Items.Add(new ComboBoxItem
            {
                Content = L("dialog.prompt_generator.mode_natural"),
                Tag = PromptGeneratorOutputMode.NaturalLanguage,
            });
            outputModeBox.SelectedIndex = 0;

            var statusBlock = new TextBlock
            {
                Text = "",
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.75,
                FontSize = 12,
            };

            inputBox.TextChanged += (_, _) =>
            {
                generateBtn.IsEnabled = !string.IsNullOrWhiteSpace(inputBox.Text);
            };

            generateBtn.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_settings.Settings.ApiToken))
                {
                    statusBlock.Text = L("api.error.token_missing_network_api");
                    return;
                }

                generateBtn.IsEnabled = false;
                inputBox.IsEnabled = false;
                outputModeBox.IsEnabled = false;
                outputBox.Text = "";
                var outputMode = outputModeBox.SelectedItem is ComboBoxItem { Tag: PromptGeneratorOutputMode selectedMode }
                    ? selectedMode
                    : PromptGeneratorOutputMode.BooruTags;
                string instruction = BuildPromptGeneratorInstruction(inputBox.Text, outputMode);
                if (!_anlasInitialFetchDone)
                {
                    var accountInfo = await _naiService.GetAccountInfoAsync();
                    if (accountInfo != null)
                    {
                        _anlasBalance = accountInfo.AnlasBalance;
                        _isOpusSubscriber = accountInfo.IsOpus;
                        _hasActiveSubscription = accountInfo.HasActiveSubscription;
                        _anlasInitialFetchDone = true;
                        _settings.UpdateCachedAccountInfo(
                            accountInfo.AnlasBalance,
                            accountInfo.TierName,
                            accountInfo.TierLevel,
                            accountInfo.HasActiveSubscription,
                            accountInfo.ExpiresAt);
                        UpdateAnlasBalanceText();
                    }
                }

                if (!_hasActiveSubscription)
                {
                    statusBlock.Text = L("dialog.prompt_generator.no_subscription");
                    inputBox.IsEnabled = true;
                    outputModeBox.IsEnabled = true;
                    generateBtn.IsEnabled = !string.IsNullOrWhiteSpace(inputBox.Text);
                    return;
                }

                string[] modelCandidates = IsKnownOpusSubscriber()
                    ? ["xialong-v1", "glm-4-6"]
                    : ["glm-4-6"];

                statusBlock.Text = Lf("dialog.prompt_generator.generating_with_model", modelCandidates[0]);

                string? lastError = null;
                foreach (string model in modelCandidates)
                {
                    var (text, error) = await _naiService.GeneratePromptTextAsync(instruction, model);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        outputBox.Text = ExtractGeneratedPromptText(text);
                        statusBlock.Text = Lf("dialog.prompt_generator.generated_with_model", model);
                        lastError = null;
                        break;
                    }

                    lastError = error;
                    if (modelCandidates.Length > 1 && model == modelCandidates[0])
                        statusBlock.Text = Lf("dialog.prompt_generator.retrying_with_model", modelCandidates[1]);
                }

                if (lastError != null)
                    statusBlock.Text = Lf("dialog.prompt_generator.failed", lastError);

                inputBox.IsEnabled = true;
                outputModeBox.IsEnabled = true;
                generateBtn.IsEnabled = !string.IsNullOrWhiteSpace(inputBox.Text);
            };

            var panel = new StackPanel { Spacing = 10, Width = 560 };
            panel.Children.Add(CreateThemedSubLabel(L("dialog.prompt_generator.output_mode_label")));
            panel.Children.Add(outputModeBox);
            panel.Children.Add(CreateThemedSubLabel(L("dialog.prompt_generator.input_label")));
            panel.Children.Add(inputBox);
            panel.Children.Add(generateBtn);
            panel.Children.Add(CreateThemedSubLabel(L("dialog.prompt_generator.output_label")));
            panel.Children.Add(outputBox);
            panel.Children.Add(statusBlock);

            bool canSendToPrompt = IsPromptMode(_currentMode);

            var dialog = new ContentDialog
            {
                Title = L("dialog.prompt_generator.title"),
                Content = panel,
                PrimaryButtonText = L("dialog.prompt_generator.send_to_prompt"),
                SecondaryButtonText = L("dialog.prompt_generator.copy_result"),
                IsPrimaryButtonEnabled = false,
                IsSecondaryButtonEnabled = false,
                CloseButtonText = L("button.close"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 620.0;

            void UpdateDialogButtons()
            {
                bool hasOutput = !string.IsNullOrWhiteSpace(outputBox.Text);
                dialog.IsPrimaryButtonEnabled = canSendToPrompt && hasOutput;
                dialog.IsSecondaryButtonEnabled = hasOutput;
            }

            outputBox.TextChanged += (_, _) => UpdateDialogButtons();

            dialog.PrimaryButtonClick += (_, _) =>
            {
                if (!canSendToPrompt) return;

                string generatedPrompt = outputBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(generatedPrompt)) return;

                TxtPrompt.Text = generatedPrompt;

                SaveCurrentPromptToBuffer();
                UpdatePromptHighlights();
            };

            dialog.SecondaryButtonClick += (_, args) =>
            {
                string generatedPrompt = outputBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(generatedPrompt))
                {
                    args.Cancel = true;
                    return;
                }

                try
                {
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.SetText(generatedPrompt);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                    statusBlock.Text = L("dialog.prompt_generator.copied_to_clipboard");
                }
                catch (Exception ex)
                {
                    statusBlock.Text = Lf("common.copy_failed", ex.Message);
                }

                args.Cancel = true;
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _isPromptGeneratorDialogOpen = false;
        }
    }
}
