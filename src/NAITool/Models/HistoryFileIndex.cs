using System.Collections;

namespace NAITool;

/// <summary>A cheap snapshot over immutable, newest-first daily path arrays. No image I/O or flattened copy.</summary>
public sealed class HistoryFileIndex : IReadOnlyList<string>
{
    public sealed record Day(string Date, string[] Files, int Start);
    public static HistoryFileIndex Empty { get; } = new([]);
    public IReadOnlyList<Day> Days { get; }
    private readonly Dictionary<string, Day> _daysByDate = new(StringComparer.Ordinal);
    public int Count { get; }

    public HistoryFileIndex(IEnumerable<KeyValuePair<string, string[]>> days)
    {
        var result = new List<Day>();
        int count = 0;
        foreach (var pair in days)
        {
            if (pair.Value.Length == 0) continue;
            var day = new Day(pair.Key, pair.Value, count);
            result.Add(day);
            _daysByDate.Add(day.Date, day);
            count = checked(count + day.Files.Length);
        }
        Days = result;
        Count = count;
    }

    public string this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int lo = 0, hi = Days.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Days[mid].Start <= index) lo = mid;
                else hi = mid - 1;
            }
            var day = Days[lo];
            return day.Files[index - day.Start];
        }
    }

    public int IndexOf(string path)
    {
        string? date = Path.GetFileName(Path.GetDirectoryName(path));
        if (date == null || !_daysByDate.TryGetValue(date, out var day)) return -1;
        int local = Array.FindIndex(day.Files, file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase));
        return local < 0 ? -1 : day.Start + local;
    }

    public IEnumerator<string> GetEnumerator() => Days.SelectMany(day => day.Files).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
