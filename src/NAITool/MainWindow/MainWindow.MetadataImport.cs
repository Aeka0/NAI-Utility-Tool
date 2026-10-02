using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private bool _metadataImportDialogOpen;

    private async Task ImportMetadataAsync(ImageMetadata meta, bool toI2I = false, string fileName = "", bool chooseTarget = false,
        byte[]? sourceImage = null, string? sourcePath = null)
    {
        if (_metadataImportDialogOpen) return;
        _metadataImportDialogOpen = true;
        try
        {
            var selection = await ShowMetadataImportDialogAsync(meta, toI2I, chooseTarget, sourceImage != null);
            if (selection == null) return;
            if (selection.ToI2I)
            {
                if (selection.Image && sourceImage != null &&
                    !await SendImageToI2IAsync(sourceImage, sourcePath, importMetadata: false))
                    return;
                if (_currentMode != AppMode.I2I) SwitchMode(AppMode.I2I);
                ApplyMetadataToI2I(meta, fileName, selection);
            }
            else
                ApplyMetadataToGeneration(meta, selection);
        }
        finally
        {
            _metadataImportDialogOpen = false;
        }
    }

    private async Task<MetadataImportSelection?> ShowMetadataImportDialogAsync(ImageMetadata meta, bool toI2I, bool chooseTarget, bool hasImage)
    {
        bool hasCharacters = meta.CharacterPrompts.Count > 0;
        bool hasReferences = meta.VibeTransfers.Count > 0 || meta.PreciseReferences.Count > 0;
        bool hasActualPrompt = meta.ActualPositivePrompt != null || meta.ActualNegativePrompt != null ||
            meta.ActualCharacterPrompts.Any(x => x != null) || meta.ActualCharacterNegativePrompts.Any(x => x != null);
        bool CanImportModel(bool targetIsI2I) => !meta.IsModelInference &&
            ImportedImageModel.ResolveForTarget(meta.ModelDisplayName,
                targetIsI2I && _i2iEditMode != I2IEditMode.Denoise ? I2IModels : GenerationModels) != null;
        int ExistingCharacterCount(bool targetIsI2I) => (targetIsI2I ? _i2iCharacters : _genCharacters).Count;

        CheckBox Option(string key, bool available, bool selected = true) => new()
        {
            Content = new TextBlock { Text = L(key), TextWrapping = TextWrapping.Wrap },
            IsChecked = available && selected,
            IsEnabled = available,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var prompt = Option("metadata.import.prompt", !string.IsNullOrWhiteSpace(meta.PositivePrompt) || meta.ActualPositivePrompt != null);
        var image = Option("metadata.import.image_for_i2i", hasImage);
        var negative = Option("metadata.import.negative", !string.IsNullOrWhiteSpace(meta.NegativePrompt) || meta.ActualNegativePrompt != null);
        var characters = Option("metadata.import.characters", hasCharacters);
        ((TextBlock)characters.Content).Text = Lf("metadata.import.characters", meta.CharacterPrompts.Count);
        var append = Option("metadata.import.append_characters", hasCharacters, false);
        append.Margin = new Thickness(24, 0, 0, 0);
        var model = Option("panel.model", chooseTarget ? CanImportModel(false) || CanImportModel(true) : CanImportModel(toI2I));
        var settings = Option("metadata.import.settings", !meta.IsModelInference && (meta.IsNaiParsed || meta.IsSdFormat));
        var size = Option("metadata.import.size", (chooseTarget || !toI2I) && meta.Width > 0 && meta.Height > 0);
        var seed = Option("metadata.import.seed", !SeedValue.IsRandom(meta.Seed), false);
        var references = Option("metadata.import.references", hasReferences);
        var actual = Option("metadata.import.actual_prompt", hasActualPrompt, false);

        var stack = new StackPanel { Spacing = 2, MaxWidth = 420 };
        stack.Children.Add(new TextBlock
        {
            Text = Lf("metadata.import.summary", meta.IsModelInference || string.IsNullOrWhiteSpace(meta.ModelDisplayName)
                    ? L("metadata.import.model_unknown") : meta.ModelDisplayName,
                meta.Width > 0 && meta.Height > 0 ? $"{meta.Width} × {meta.Height}" : "—",
                meta.CharacterPrompts.Count, !SeedValue.IsRandom(meta.Seed) ? meta.Seed : "—"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var item in new[] { image, prompt, negative, characters, append, model, settings, size, seed, references, actual })
            stack.Children.Add(item);

        var dialog = new ContentDialog
        {
            Title = L("metadata.import.title"),
            Content = new ScrollViewer
            {
                Content = stack,
                MaxHeight = Math.Max(160, Math.Min(470, this.Content.XamlRoot.Size.Height - 220)),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            PrimaryButtonText = L(chooseTarget ? "metadata.import.send_to_generation" : "metadata.import.apply"),
            SecondaryButtonText = chooseTarget ? L("action.send_to_i2i") : "",
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };

        MetadataImportSelection ReadSelection(bool targetIsI2I) => new()
        {
            ToI2I = targetIsI2I,
            Image = targetIsI2I && image.IsEnabled && image.IsChecked == true,
            Prompt = prompt.IsEnabled && prompt.IsChecked == true,
            NegativePrompt = negative.IsEnabled && negative.IsChecked == true,
            Characters = characters.IsEnabled && characters.IsChecked == true,
            AppendCharacters = append.IsEnabled && append.IsChecked == true && ExistingCharacterCount(targetIsI2I) > 0,
            Model = model.IsEnabled && model.IsChecked == true && CanImportModel(targetIsI2I),
            Settings = settings.IsEnabled && settings.IsChecked == true,
            Size = !targetIsI2I && size.IsEnabled && size.IsChecked == true,
            Seed = seed.IsEnabled && seed.IsChecked == true,
            References = references.IsEnabled && references.IsChecked == true,
            ActualPrompt = actual.IsEnabled && actual.IsChecked == true,
        };
        void UpdateSelectionState()
        {
            bool hasExistingCharacters = chooseTarget
                ? ExistingCharacterCount(false) > 0 || ExistingCharacterCount(true) > 0
                : ExistingCharacterCount(toI2I) > 0;
            append.IsEnabled = characters.IsChecked == true && hasExistingCharacters;
            actual.IsEnabled = hasActualPrompt &&
                (prompt.IsChecked == true || negative.IsChecked == true || characters.IsChecked == true);
            dialog.IsPrimaryButtonEnabled = ReadSelection(!chooseTarget && toI2I).HasSelection;
            dialog.IsSecondaryButtonEnabled = chooseTarget && ReadSelection(true).HasSelection;
        }
        foreach (var item in new[] { image, prompt, negative, characters, model, settings, size, seed, references })
        {
            item.Checked += (_, _) => UpdateSelectionState();
            item.Unchecked += (_, _) => UpdateSelectionState();
        }
        UpdateSelectionState();
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return null;
        bool targetIsI2I = chooseTarget ? result == ContentDialogResult.Secondary : toI2I;
        var selection = ReadSelection(targetIsI2I);
        return selection.HasSelection ? selection : null;
    }

    private void ImportSelectedCharacters(ImageMetadata meta, MetadataImportSelection selection, bool toI2I, List<string> notes)
    {
        if (!selection.Characters) return;
        SaveAllCharacterPrompts();
        int count = ImportCharactersFromMetadata(toI2I ? _i2iCharacters : _genCharacters, meta, selection);
        if (meta.CharacterPrompts.Count > 0)
        {
            notes.Add(Lf("metadata.note.characters_imported", count));
            if (count < meta.CharacterPrompts.Count)
                notes.Add(Lf("metadata.note.characters_capacity", count, meta.CharacterPrompts.Count, CharacterPromptRules.MaxRetainedCount));
        }
    }
}
