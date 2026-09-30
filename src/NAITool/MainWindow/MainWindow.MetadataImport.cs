using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private sealed class MetadataImportSelection
    {
        public bool Prompt { get; init; } = true;
        public bool NegativePrompt { get; init; } = true;
        public bool Characters { get; init; } = true;
        public bool AppendCharacters { get; init; }
        public bool Settings { get; init; } = true;
        public bool Size { get; init; } = true;
        public bool Seed { get; init; } = true;
        public bool References { get; init; } = true;
        public bool ActualPrompt { get; init; }
    }

    private async Task<MetadataImportSelection?> ShowMetadataImportDialogAsync(ImageMetadata meta)
    {
        if (meta.IsModelInference)
            return new MetadataImportSelection();

        bool hasCharacters = meta.CharacterPrompts.Count > 0;
        bool hasReferences = meta.VibeTransfers.Count > 0 || meta.PreciseReferences.Count > 0;
        bool hasActualPrompt = meta.ActualPositivePrompt != null ||
            meta.ActualNegativePrompt != null || meta.ActualCharacterPrompts.Count > 0;

        var summary = new TextBlock
        {
            Text = Lf("metadata.import.summary", meta.ModelKey ?? L("metadata.import.model_unknown"),
                meta.Width > 0 && meta.Height > 0 ? $"{meta.Width} × {meta.Height}" : "—",
                meta.CharacterPrompts.Count, !SeedValue.IsRandom(meta.Seed) ? meta.Seed : "—"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var prompt = new CheckBox { Content = L("metadata.import.prompt"), IsChecked = true,
            IsEnabled = !string.IsNullOrWhiteSpace(meta.PositivePrompt) };
        var negative = new CheckBox { Content = L("metadata.import.negative"), IsChecked = true,
            IsEnabled = !string.IsNullOrWhiteSpace(meta.NegativePrompt) };
        var characters = new CheckBox { Content = Lf("metadata.import.characters", meta.CharacterPrompts.Count),
            IsChecked = hasCharacters, IsEnabled = hasCharacters };
        var append = new CheckBox { Content = L("metadata.import.append_characters"),
            IsChecked = false, IsEnabled = hasCharacters && _genCharacters.Count > 0,
            Margin = new Thickness(24, 0, 0, 0) };
        characters.Checked += (_, _) => append.IsEnabled = _genCharacters.Count > 0;
        characters.Unchecked += (_, _) => append.IsEnabled = false;
        var settings = new CheckBox { Content = L("metadata.import.settings"), IsChecked = true };
        var size = new CheckBox { Content = L("metadata.import.size"),
            IsChecked = meta.Width > 0 && meta.Height > 0,
            IsEnabled = meta.Width > 0 && meta.Height > 0 };
        var seed = new CheckBox { Content = L("metadata.import.seed"),
            IsChecked = false, IsEnabled = !SeedValue.IsRandom(meta.Seed) };
        var references = new CheckBox { Content = L("metadata.import.references"),
            IsChecked = hasReferences, IsEnabled = hasReferences };
        var actual = new CheckBox { Content = L("metadata.import.actual_prompt"),
            IsChecked = false, IsEnabled = hasActualPrompt };

        var stack = new StackPanel { Spacing = 2, Width = 400 };
        stack.Children.Add(summary);
        foreach (var item in new UIElement[] { prompt, negative, characters, append, settings,
            size, seed, references, actual })
            stack.Children.Add(item);

        var dialog = new ContentDialog
        {
            Title = L("metadata.import.title"),
            Content = new ScrollViewer { Content = stack, MaxHeight = 470 },
            PrimaryButtonText = L("metadata.import.apply"),
            CloseButtonText = L("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var selection = new MetadataImportSelection
        {
            Prompt = prompt.IsChecked == true && prompt.IsEnabled,
            NegativePrompt = negative.IsChecked == true && negative.IsEnabled,
            Characters = characters.IsChecked == true && characters.IsEnabled,
            AppendCharacters = append.IsChecked == true && append.IsEnabled,
            Settings = settings.IsChecked == true,
            Size = size.IsChecked == true && size.IsEnabled,
            Seed = seed.IsChecked == true && seed.IsEnabled,
            References = references.IsChecked == true && references.IsEnabled,
            ActualPrompt = actual.IsChecked == true && actual.IsEnabled,
        };
        if (!selection.Prompt && !selection.NegativePrompt && !selection.Characters &&
            !selection.Settings && !selection.Size && !selection.Seed && !selection.References)
            return null;
        return selection;
    }
}
