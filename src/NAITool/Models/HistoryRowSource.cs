using System.Collections;
using System.Collections.Specialized;

namespace NAITool;

public sealed record HistoryRow(int Version, int Index, string DateLabel, bool IsSeparator, IReadOnlyList<HistoryListItem> Items);

/// <summary>
/// Random-access data virtualization for the single shared ListView. Only requested rows are
/// materialized; Count covers the whole catalog, so dragging the thumb can jump to any date.
/// The gallery uses several cells per virtualized row, keeping full-width date separators.
/// All mutation and access is owned by the UI thread.
/// </summary>
public sealed class HistoryRowSource : IList, INotifyCollectionChanged
{
    private sealed record Group(string Date, string[] Files, HistoryListItem[] Pending, int Start, int Count);
    public const int CacheLimit = 256;
    private readonly List<Group> _groups = [];
    private readonly Dictionary<string, Group> _groupsByDate = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HistoryRow> _cache = [];
    private readonly Queue<int> _cacheOrder = [];
    private int _version;
    private int _columns = 1;
    private double _cellWidth = 220;
    public int Count { get; private set; }
    public int CachedRowCount => _cache.Count;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public void Reset(HistoryFileIndex files, IEnumerable<HistoryListItem> pending, int columns, double cellWidth)
    {
        _version++;
        _columns = Math.Max(1, columns);
        _cellWidth = Math.Max(24, cellWidth);
        _groups.Clear();
        _groupsByDate.Clear();
        _cache.Clear();
        _cacheOrder.Clear();
        Count = 0;
        var pendingByDate = pending.GroupBy(item => item.DateKey!).ToDictionary(group => group.Key, group => group.ToArray());
        var filesByDate = files.Days.ToDictionary(day => day.Date, day => day.Files);
        var dates = filesByDate.Keys.Union(pendingByDate.Keys).OrderByDescending(date => date, StringComparer.Ordinal);
        foreach (var date in dates)
        {
            var paths = filesByDate.GetValueOrDefault(date) ?? [];
            var waiting = pendingByDate.GetValueOrDefault(date) ?? [];
            int items = checked(paths.Length + waiting.Length);
            int rows = 1 + (items - 1) / _columns;
            var group = new Group(date, paths, waiting, Count, rows + 1);
            _groups.Add(group);
            _groupsByDate.Add(date, group);
            Count = checked(Count + group.Count);
        }
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public HistoryRow GetRow(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_cache.TryGetValue(index, out var cached)) return cached;
        int lo = 0, hi = _groups.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_groups[mid].Start <= index) lo = mid;
            else hi = mid - 1;
        }
        var group = _groups[lo];
        int row = index - group.Start - 1;
        var cells = new List<HistoryListItem>(_columns);
        if (row >= 0)
        {
            int start = row * _columns;
            int end = Math.Min(start + _columns, group.Pending.Length + group.Files.Length);
            for (int i = start; i < end; i++)
            {
                // Snapshot pending state so resizing never mutates items in still-realized old rows.
                var item = i < group.Pending.Length
                    ? HistoryListItem.CreatePending(group.Pending[i].PendingId!, group.Date, _cellWidth, 140)
                    : HistoryListItem.CreateThumbnail(group.Files[i - group.Pending.Length], _cellWidth, 140);
                cells.Add(item);
            }
        }
        var result = new HistoryRow(_version, index, group.Date, row < 0, cells);
        _cache.Add(index, result);
        _cacheOrder.Enqueue(index);
        while (_cache.Count > CacheLimit) _cache.Remove(_cacheOrder.Dequeue());
        return result;
    }

    public int FindRow(string? path, string? pendingId = null, string? date = null)
    {
        date ??= path == null ? null : Path.GetFileName(Path.GetDirectoryName(path));
        if (date == null || !_groupsByDate.TryGetValue(date, out var group)) return -1;
        if (pendingId != null)
        {
            int pendingIndex = Array.FindIndex(group.Pending, item => item.PendingId == pendingId);
            if (pendingIndex >= 0) return group.Start + 1 + pendingIndex / _columns;
            if (path == null) return -1;
        }
        if (path == null) return group.Start;
        int index = Array.FindIndex(group.Files, file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? -1 : group.Start + 1 + (group.Pending.Length + index) / _columns;
    }

    public object? this[int index] { get => GetRow(index); set => throw new NotSupportedException(); }
    public int IndexOf(object? value) => value is HistoryRow row && row.Version == _version ? row.Index : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public IEnumerator GetEnumerator() { for (int i = 0; i < Count; i++) yield return GetRow(i); }
    public void CopyTo(Array array, int index) { for (int i = 0; i < Count; i++) array.SetValue(GetRow(i), index + i); }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
