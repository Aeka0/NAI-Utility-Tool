using System.Collections.Concurrent;
using System.Globalization;

namespace NAITool.Services;

public static class HistoryCatalogScanner
{
    public static Task<Dictionary<string, string[]>> ScanAsync(string directory, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var result = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return new Dictionary<string, string[]>(StringComparer.Ordinal);
        var dates = Directory.EnumerateDirectories(directory).Where(path =>
            DateTime.TryParseExact(Path.GetFileName(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        Parallel.ForEach(dates, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
        }, path =>
        {
            try
            {
                // FileInfo from enumeration already contains timestamps; avoid a stat for every sort comparison.
                var files = new List<(string Path, DateTime Created)>();
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*.png"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add((file.FullName, file.CreationTimeUtc));
                }
                var sorted = files.OrderByDescending(file => file.Created).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(file => file.Path).ToArray();
                if (sorted.Length > 0) result[Path.GetFileName(path)] = sorted;
            }
            catch (IOException) { /* A date directory can disappear during a rescan. */ }
            catch (UnauthorizedAccessException) { /* Other dates remain browsable. */ }
        });
        cancellationToken.ThrowIfCancellationRequested();
        return new Dictionary<string, string[]>(result, StringComparer.Ordinal);
    }, cancellationToken);
}
