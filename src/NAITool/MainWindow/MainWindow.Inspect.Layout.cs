using System;
using Microsoft.UI.Xaml;

namespace NAITool;

public sealed partial class MainWindow
{
    private bool _inspectPromptLayoutQueued;

    private void OnInspectPromptLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_inspectPromptLayoutQueued) return;
        // Wait for the whole layout pass so the panel and child measurements agree.
        _inspectPromptLayoutQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _inspectPromptLayoutQueued = false;
            if (PanelLeftInspect.Visibility != Visibility.Visible ||
                InspectContent.Visibility != Visibility.Visible ||
                InspectMetadataScroller.ActualHeight <= 0)
                return;

            double otherContentHeight = Math.Max(0, InspectMetadataLayout.ActualHeight
                - InspectMetadataLayout.Padding.Top - InspectMetadataLayout.Padding.Bottom
                - TxtInspectPositive.ActualHeight - TxtInspectNegative.ActualHeight);
            double availableHeight = InspectMetadataScroller.ActualHeight
                - InspectMetadataLayout.Padding.Top - InspectMetadataLayout.Padding.Bottom
                - otherContentHeight;
            double promptHeight = Math.Max(TxtInspectPositive.MinHeight + TxtInspectNegative.MinHeight, availableHeight);
            double positiveHeight = Math.Round(promptHeight * 0.6);
            double negativeHeight = promptHeight - positiveHeight;

            // Keep useful minimums in short windows or with many characters; the outer panel scrolls.
            if (Math.Abs(TxtInspectPositive.Height - positiveHeight) > 0.5)
                TxtInspectPositive.Height = positiveHeight;
            if (Math.Abs(TxtInspectNegative.Height - negativeHeight) > 0.5)
                TxtInspectNegative.Height = negativeHeight;
        });
    }
}
