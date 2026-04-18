using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Services;
using SkiaSharp;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NAITool;

public sealed partial class MainWindow
{
    private bool _promptAreaHeightUpdateQueued;

    private void UpdateReferenceButtonAndPanelState()
    {
        if (BtnAddCharacter == null || BtnAddVibeTransfer == null || BtnAddPreciseReference == null)
            return;

        BtnAddCharacter.Visibility = SupportsCharacterFeature()
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnAddCharacter.IsEnabled = SupportsCharacterFeature() && _genCharacters.Count < MaxCharacters;

        BtnAddVibeTransfer.Visibility = SupportsVibeTransferFeature() && ActivePreciseReferenceCount() == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        int maxVibeTransfers = GetMaxAllowedVibeTransfers();
        bool vibeLimitReached = ActiveVibeTransferCount() >= maxVibeTransfers;
        BtnAddVibeTransfer.IsEnabled = CanEditVibeTransferFeature() && !vibeLimitReached;

        string vibeToolTip = vibeLimitReached && IsAssetProtectionPaidFeatureLimitEnabled()
            ? Lf("references.error.asset_protection_vibe_count_limit", AssetProtectionFreeVibeLimit)
            : RequiresEncodedVibeFileOnly()
                ? L("references.error.asset_protection_requires_encoded_vibe")
                : L("references.tooltips.vibe");
        ToolTipService.SetToolTip(BtnAddVibeTransfer, vibeToolTip);

        BtnAddPreciseReference.Visibility = SupportsPreciseReferenceFeature() && ActiveVibeTransferCount() == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnAddPreciseReference.IsEnabled = CanEditPreciseReferenceFeature() && ActivePreciseReferenceCount() < MaxPreciseReferences;
        ToolTipService.SetToolTip(BtnAddPreciseReference, L("references.tooltips.precise"));

        CharacterPanel.Visibility = SupportsCharacterFeature()
            ? Visibility.Visible
            : Visibility.Collapsed;

        VibeTransferPanel.Visibility = ShouldShowVibeTransferPanel()
            ? Visibility.Visible
            : Visibility.Collapsed;
        TxtVibeTransferHint.Visibility = IsAssetProtectionPaidFeatureLimitEnabled() && ActiveVibeTransferCount() > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        TxtVibeTransferHint.Text = L("references.hint.asset_protection_vibe");

        PreciseReferencePanel.Visibility = ShouldShowPreciseReferencePanel()
            ? Visibility.Visible
            : Visibility.Collapsed;
        TxtPreciseReferenceHint.Visibility = Visibility.Collapsed;

        UpdateReferenceButtonRowLayout();
        UpdatePromptAreaHeight();
        QueuePromptAreaHeightUpdate();
    }

    private void UpdateReferenceButtonRowLayout()
    {
        if (ReferenceButtonRow == null)
            return;

        var columns = new[] { ReferenceButtonCol0, ReferenceButtonCol1, ReferenceButtonCol2 };
        foreach (var column in columns)
            column.Width = new GridLength(0);

        var visibleButtons = new List<Button>();
        if (BtnAddCharacter.Visibility == Visibility.Visible) visibleButtons.Add(BtnAddCharacter);
        if (BtnAddVibeTransfer.Visibility == Visibility.Visible) visibleButtons.Add(BtnAddVibeTransfer);
        if (BtnAddPreciseReference.Visibility == Visibility.Visible) visibleButtons.Add(BtnAddPreciseReference);

        ReferenceButtonRow.Visibility = visibleButtons.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        for (int i = 0; i < visibleButtons.Count; i++)
        {
            columns[i].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(visibleButtons[i], i);
        }

        UpdateReferenceButtonText(visibleButtons.Count);
    }

    private void UpdateReferenceButtonText(int visibleCount)
    {
        bool useCompact = false;
        if (visibleCount == 3)
        {
            double availableWidth = ReferenceButtonRow.ActualWidth;
            if (availableWidth < 1)
                availableWidth = (PanelLeftMain?.ActualWidth ?? 300) - 24;
            double perButton = (availableWidth - (visibleCount - 1) * 6) / visibleCount;
            useCompact = (perButton - 34) < 50;
        }
        if (TxtAddCharacterButton != null) TxtAddCharacterButton.Text = useCompact ? L("references.compact.character") : L("button.add_character");
        if (TxtAddVibeTransferButton != null) TxtAddVibeTransferButton.Text = useCompact ? L("references.compact.vibe") : L("button.add_vibe");
        if (TxtAddPreciseReferenceButton != null) TxtAddPreciseReferenceButton.Text = useCompact ? L("references.compact.precise") : L("button.add_precise_reference");
    }

    private void OnReferenceButtonRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        int visibleCount = 0;
        if (BtnAddCharacter?.Visibility == Visibility.Visible) visibleCount++;
        if (BtnAddVibeTransfer?.Visibility == Visibility.Visible) visibleCount++;
        if (BtnAddPreciseReference?.Visibility == Visibility.Visible) visibleCount++;
        UpdateReferenceButtonText(visibleCount);
    }

    private void OnLeftPanelScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePromptTabText();
        UpdatePromptAreaHeight(e.NewSize.Height);
        QueuePromptAreaHeightUpdate();
    }

    private void OnBottomContentPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePromptAreaHeight();
        QueuePromptAreaHeightUpdate();
    }

    private void UpdatePromptAreaHeight(double? viewportOverride = null)
    {
        if (LeftPanelScrollViewer == null || PromptAreaGrid == null || BottomContentPanel == null)
            return;

        double viewport = viewportOverride ?? LeftPanelScrollViewer.ActualHeight;
        if (viewport <= 0 || double.IsNaN(viewport) || double.IsInfinity(viewport))
            return;

        double availableWidth = Math.Max(0, LeftPanelScrollViewer.ActualWidth - 24);
        var availableSize = new Windows.Foundation.Size(availableWidth, double.PositiveInfinity);
        double modelH = MeasureVisibleHeight(ModelHeaderPanel, availableSize);
        double tabH = MeasureVisibleHeight(PromptTabRow, availableSize);
        double bottomH = MeasureVisibleHeight(BottomContentPanel, availableSize);
        const double overhead = 24 + 30; // Grid Padding (12*2) + RowSpacing (10*3)

        double desired = viewport - modelH - tabH - bottomH - overhead;
        double height = Math.Max(80, desired);
        PromptAreaGrid.MinHeight = 80;
        PromptAreaGrid.Height = height;
    }

    private void QueuePromptAreaHeightUpdate()
    {
        if (_promptAreaHeightUpdateQueued)
            return;

        _promptAreaHeightUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _promptAreaHeightUpdateQueued = false;
            UpdatePromptAreaHeight();
        }))
        {
            _promptAreaHeightUpdateQueued = false;
            UpdatePromptAreaHeight();
        }
    }

    private static double MeasureVisibleHeight(FrameworkElement? element, Windows.Foundation.Size availableSize)
    {
        if (element == null || element.Visibility != Visibility.Visible)
            return 0;

        element.Measure(availableSize);
        double desiredHeight = element.DesiredSize.Height;
        if (desiredHeight > 0 && !double.IsNaN(desiredHeight) && !double.IsInfinity(desiredHeight))
            return desiredHeight;

        double actualHeight = element.ActualHeight;
        return actualHeight > 0 && !double.IsNaN(actualHeight) && !double.IsInfinity(actualHeight)
            ? actualHeight
            : 0;
    }
}
