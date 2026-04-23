using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NAITool;

public sealed class HistoryListItem : INotifyPropertyChanged
{
    private bool _isPending;
    private string? _filePath;

    private HistoryListItem(
        bool isSeparator,
        bool isPending,
        string? dateLabel,
        string? filePath,
        string? pendingId,
        string? dateKey,
        double thumbnailWidth,
        double thumbnailHeight)
    {
        IsSeparator = isSeparator;
        _isPending = isPending;
        DateLabel = dateLabel;
        _filePath = filePath;
        PendingId = pendingId;
        DateKey = dateKey;
        ThumbnailWidth = thumbnailWidth;
        ThumbnailHeight = thumbnailHeight;
    }

    public bool IsSeparator { get; }
    public bool IsPending
    {
        get => _isPending;
        private set => SetField(ref _isPending, value);
    }
    public string? DateLabel { get; }
    public string? FilePath
    {
        get => _filePath;
        private set => SetField(ref _filePath, value);
    }
    public string? PendingId { get; }
    public string? DateKey { get; }
    public double ThumbnailWidth { get; }
    public double ThumbnailHeight { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasSameIdentity(HistoryListItem? other) =>
        other != null &&
        IsSeparator == other.IsSeparator &&
        IsPending == other.IsPending &&
        string.Equals(PendingId, other.PendingId, StringComparison.Ordinal) &&
        string.Equals(DateKey, other.DateKey, StringComparison.Ordinal) &&
        string.Equals(DateLabel, other.DateLabel, StringComparison.Ordinal) &&
        ThumbnailWidth.Equals(other.ThumbnailWidth) &&
        string.Equals(FilePath, other.FilePath, StringComparison.OrdinalIgnoreCase);

    public static HistoryListItem CreateSeparator(string dateLabel) =>
        new(true, false, dateLabel, null, null, null, 0, 0);

    public static HistoryListItem CreateThumbnail(
        string filePath,
        double thumbnailWidth = 140,
        double thumbnailHeight = 140) =>
        new(false, false, null, filePath, null, null, thumbnailWidth, thumbnailHeight);

    public static HistoryListItem CreatePending(
        string pendingId,
        string dateKey,
        double thumbnailWidth,
        double thumbnailHeight) =>
        new(false, true, null, null, pendingId, dateKey, thumbnailWidth, thumbnailHeight);

    public void Resolve(string filePath)
    {
        FilePath = filePath;
        IsPending = false;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
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
