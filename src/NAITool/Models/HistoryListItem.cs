using System.ComponentModel;

namespace NAITool;

/// <summary>Stable cell identity with independently observable layout dimensions.</summary>
public sealed class HistoryListItem(string? filePath, string? pendingId, string? dateKey, double width, double height) : INotifyPropertyChanged
{
    public string? FilePath { get; } = filePath;
    public string? PendingId { get; } = pendingId;
    public string? DateKey { get; } = dateKey;
    public double ThumbnailWidth { get; private set; } = width;
    public double ThumbnailHeight { get; private set; } = height;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Resize(double width, double height)
    {
        if (ThumbnailWidth != width)
        {
            ThumbnailWidth = width;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailWidth)));
        }
        if (ThumbnailHeight != height)
        {
            ThumbnailHeight = height;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailHeight)));
        }
    }

    public static HistoryListItem CreateThumbnail(string filePath, double thumbnailWidth, double thumbnailHeight) =>
        new(filePath, null, null, thumbnailWidth, thumbnailHeight);

    public static HistoryListItem CreatePending(string pendingId, string dateKey, double thumbnailWidth, double thumbnailHeight) =>
        new(null, pendingId, dateKey, thumbnailWidth, thumbnailHeight);
}
