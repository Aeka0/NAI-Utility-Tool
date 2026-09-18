namespace NAITool;

/// <summary>An immutable cell in a realized history row. Separators belong to the row source.</summary>
public sealed record HistoryListItem(
    string? FilePath,
    string? PendingId,
    string? DateKey,
    double ThumbnailWidth,
    double ThumbnailHeight)
{
    public static HistoryListItem CreateThumbnail(string filePath, double thumbnailWidth, double thumbnailHeight) =>
        new(filePath, null, null, thumbnailWidth, thumbnailHeight);

    public static HistoryListItem CreatePending(string pendingId, string dateKey, double thumbnailWidth, double thumbnailHeight) =>
        new(null, pendingId, dateKey, thumbnailWidth, thumbnailHeight);
}
