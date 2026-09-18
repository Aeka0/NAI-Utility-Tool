using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using NAITool;
using NAITool.Services;

int checks = 0;
void Check(bool condition, string scenario)
{
    checks++;
    if (!condition) throw new Exception(scenario);
}

void ThrowsOutOfRange(Action action)
{
    try { action(); }
    catch (ArgumentOutOfRangeException) { checks++; return; }
    throw new Exception("Expected ArgumentOutOfRangeException");
}

string ImagePath(string date, int index) => Path.Combine("output", date, $"{index:D6}.png");

var rows = new HistoryRowSource();
int resets = 0;
rows.CollectionChanged += (_, args) =>
{
    Check(args.Action == NotifyCollectionChangedAction.Reset, "Bulk update must be a single reset");
    resets++;
};
rows.Reset(HistoryFileIndex.Empty, [], 1, 200);
Check(rows.Count == 0 && rows.CachedRowCount == 0, "Empty history has no rows or cached objects");
ThrowsOutOfRange(() => rows.GetRow(0));
ThrowsOutOfRange(() => _ = HistoryFileIndex.Empty[0]);

const int days = 100;
const int filesPerDay = 2000;
var newest = new DateTime(2026, 9, 18);
var catalog = Enumerable.Range(0, days).ToDictionary(
    day => newest.AddDays(-day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    day => Enumerable.Range(0, filesPerDay).Select(i => ImagePath(
        newest.AddDays(-day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), i)).ToArray());
var watch = Stopwatch.StartNew();
long before = GC.GetAllocatedBytesForCurrentThread();
var files = new HistoryFileIndex(catalog);
rows.Reset(files, [], 1, 200);
long indexBytes = GC.GetAllocatedBytesForCurrentThread() - before;
Check(files.Count == 200_000, "Full path count is available before any row is realized");
Check(rows.Count == 200_100 && rows.CachedRowCount == 0, "Full scroll range without creating per-file view models");
Check(indexBytes < 1_000_000, "Index reset must not allocate a full flattened path copy or per-image models");
Check(ReferenceEquals(files.Days[0].Files, catalog["2026-09-18"]), "Snapshots share immutable daily arrays");
Check(files[0] == ImagePath("2026-09-18", 0), "First file");
Check(files[filesPerDay] == ImagePath("2026-09-17", 0), "Index boundary between days");
Check(files.IndexOf(files[^1].ToUpperInvariant()) == files.Count - 1, "Last file lookup is case-insensitive");
Check(files.IndexOf(ImagePath("1900-01-01", 0)) == -1, "Unknown date lookup");
ThrowsOutOfRange(() => _ = files[-1]);
ThrowsOutOfRange(() => _ = files[files.Count]);
var last = rows.GetRow(rows.Count - 1);
Check(last.Items.Single().FilePath == files[^1] && rows.CachedRowCount == 1, "Scrollbar can jump directly to last row");
Check(((IList)rows).IndexOf(last) == rows.Count - 1, "Native list can locate a materialized row without enumeration");
for (int i = 0; i < 10_000; i++) rows.GetRow((i * 97) % rows.Count);
Check(rows.CachedRowCount == HistoryRowSource.CacheLimit, "Row cache stays bounded after 10000 random jumps");
Check(rows.GetRow(0).IsSeparator && rows.GetRow(2001).DateLabel == "2026-09-17", "Daily separators and offsets");

rows.Reset(files, [], 7, 210);
Check(rows.IndexOf(last) == -1, "Recycled rows from an earlier reset are not valid scroll targets");
Check(rows.Count == days * (1 + (filesPerDay + 6) / 7), "Gallery row count includes partial rows per day");
Check(rows.GetRow(rows.Count - 1).Items.Count == 5, "Partial final gallery row");
Check(rows.GetRow(rows.Count - 1).Items[^1].FilePath == files[^1], "Last gallery image is reachable");
foreach (int index in new[] { 0, 6, 7, 1999, 2000, 199999 })
{
    int row = rows.FindRow(files[index]);
    Check(row >= 0 && rows.GetRow(row).Items.Any(item => item.FilePath == files[index]), "Gallery file-to-row mapping");
}

var pending = HistoryListItem.CreatePending("pending-1", "2026-09-18", 140, 140);
rows.Reset(files, [pending], 7, 220);
var pendingRow = rows.GetRow(rows.FindRow(null, "pending-1", "2026-09-18"));
Check(pendingRow.Items[0].PendingId == pending.PendingId, "Pending row is first in its date");
Check(pendingRow.Items[1].FilePath == files[0], "Pending count shifts file positions");
Check(pendingRow.Items[0].ThumbnailWidth == 220 && pending.ThumbnailWidth == 140, "Layout does not mutate pending source");
string anchor = files[1999];
string generated = Path.Combine("output", "2026-09-18", "generated.png");
catalog["2026-09-18"] = [generated, .. catalog["2026-09-18"]];
var updated = new HistoryFileIndex(catalog);
rows.Reset(updated, [], 3, 250);
Check(rows.GetRow(rows.FindRow(generated)).Items[0].FilePath == generated, "Resolved pending maps to saved image");
Check(rows.FindRow(null, "pending-1", "2026-09-18") == -1, "Removed pending anchor falls back to its nearest row");
Check(rows.GetRow(rows.FindRow(anchor)).Items.Any(item => item.FilePath == anchor), "Anchor survives insertion and column change");
Check(files[0] != generated && files.Count == 200_000, "Previous snapshot remains unchanged after insertion");
catalog["2026-09-18"] = catalog["2026-09-18"].Where(path => path != anchor).ToArray();
rows.Reset(new HistoryFileIndex(catalog), [], 3, 250);
Check(rows.FindRow(anchor) == -1, "Deleted anchor is not a stale scroll target");
Check(pendingRow.Items[0].ThumbnailWidth == 220, "Previously realized rows remain immutable after resizing");
rows.Reset(HistoryFileIndex.Empty, [pending], 4, 220);
Check(rows.Count == 2 && rows.GetRow(1).Items[0].PendingId == "pending-1", "Pending-only date is visible with empty disk history");
rows.Reset(new HistoryFileIndex(catalog.Where(pair => string.CompareOrdinal(pair.Key, "2026-09-10") <= 0)), [], 4, 220);
Check(rows.GetRow(0).DateLabel == "2026-09-10", "Date selection excludes newer days");
Check(rows.FindRow(generated) == -1, "Newer file cannot scroll into excluded date");
Check(resets == 8, "Exactly one collection notification for each reset");
Console.WriteLine($"200000-file index and random-access checks: {watch.ElapsedMilliseconds} ms; reset allocation: {indexBytes:N0} bytes.");

// Isolated temporary directory: never scan, modify, or remove real user history.
var fixture = Directory.CreateTempSubdirectory("NAITool-history-tests-");
try
{
    var date = Directory.CreateDirectory(Path.Combine(fixture.FullName, "2026-09-18"));
    var old = Path.Combine(date.FullName, "old.png");
    var recent = Path.Combine(date.FullName, "recent.png");
    await File.WriteAllBytesAsync(old, []);
    await File.WriteAllBytesAsync(recent, []);
    File.SetCreationTimeUtc(old, new DateTime(2026, 9, 18, 1, 0, 0, DateTimeKind.Utc));
    File.SetCreationTimeUtc(recent, new DateTime(2026, 9, 18, 2, 0, 0, DateTimeKind.Utc));
    await File.WriteAllTextAsync(Path.Combine(date.FullName, "not-an-image.txt"), "ignored");
    Directory.CreateDirectory(Path.Combine(fixture.FullName, "2026-09-17"));
    var invalid = Directory.CreateDirectory(Path.Combine(fixture.FullName, "2026-02-30"));
    await File.WriteAllBytesAsync(Path.Combine(invalid.FullName, "invalid.png"), []);
    var nested = Directory.CreateDirectory(Path.Combine(date.FullName, "nested"));
    await File.WriteAllBytesAsync(Path.Combine(nested.FullName, "nested.png"), []);
    var snapshot = await HistoryCatalogScanner.ScanAsync(fixture.FullName, CancellationToken.None);
    Check(snapshot.Count == 1 && snapshot[date.Name].Length == 2, "Scan excludes invalid/empty dates and unrelated/nested files");
    Check(snapshot[date.Name][0] == recent, "Scan uses newest creation timestamp first");
    Check((await HistoryCatalogScanner.ScanAsync(Path.Combine(fixture.FullName, "missing"), CancellationToken.None)).Count == 0,
        "Missing output directory is empty history");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await HistoryCatalogScanner.ScanAsync(fixture.FullName, cancellation.Token);
        throw new Exception("Canceled scan unexpectedly completed");
    }
    catch (OperationCanceledException) { checks++; }
}
finally { fixture.Delete(recursive: true); }
Console.WriteLine($"Passed {checks} history checks.");
