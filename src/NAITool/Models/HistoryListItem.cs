using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NAITool;

public sealed class HistoryListItem
{
    private HistoryListItem(bool isSeparator, string? dateLabel, string? filePath)
    {
        IsSeparator = isSeparator;
        DateLabel = dateLabel;
        FilePath = filePath;
    }

    public bool IsSeparator { get; }
    public string? DateLabel { get; }
    public string? FilePath { get; }

    public bool HasSameIdentity(HistoryListItem? other) =>
        other != null &&
        IsSeparator == other.IsSeparator &&
        string.Equals(DateLabel, other.DateLabel, StringComparison.Ordinal) &&
        string.Equals(FilePath, other.FilePath, StringComparison.OrdinalIgnoreCase);

    public static HistoryListItem CreateSeparator(string dateLabel) =>
        new(true, dateLabel, null);

    public static HistoryListItem CreateThumbnail(string filePath) =>
        new(false, null, filePath);
}

public sealed class HistoryItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SeparatorTemplate { get; set; }
    public DataTemplate? ThumbnailTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is HistoryListItem historyItem && historyItem.IsSeparator)
            return SeparatorTemplate ?? base.SelectTemplateCore(item);

        return ThumbnailTemplate ?? base.SelectTemplateCore(item);
    }
}
