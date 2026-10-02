using System.Text.Json;

namespace NAITool.Services;

/// <summary>Portable favorites for output images. Published sets are immutable; disk writes are serialized.</summary>
public sealed class HistoryFavoritesService(string outputDirectory, string storagePath)
{
    private readonly string _outputDirectory = Path.GetFullPath(outputDirectory);
    private readonly string _storagePath = Path.GetFullPath(storagePath);
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public bool CanFavorite(string? path) => GetKey(path) != null;

    public bool IsFavorite(string? path) => GetKey(path) is { } key && _keys.Contains(key);

    public IReadOnlySet<string> CapturePaths() => _keys
        .Select(key => Path.GetFullPath(Path.Combine(_outputDirectory, key)))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task LoadAsync()
    {
        await _storageGate.WaitAsync();
        try
        {
            _keys = await Task.Run(async () =>
            {
                string[] entries;
                try
                {
                    await using var stream = File.OpenRead(_storagePath);
                    entries = await JsonSerializer.DeserializeAsync<string[]>(stream) ?? [];
                }
                catch (FileNotFoundException) { entries = []; }
                catch (DirectoryNotFoundException) { entries = []; }

                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry)) continue;
                    var relative = entry.Replace('\\', '/');
                    if (relative.Split('/').Any(part => part is ".." or "." or "")) continue;
                    if (GetKey(Path.Combine(_outputDirectory, relative)) is { } key) keys.Add(key);
                }
                return keys;
            });
        }
        finally { _storageGate.Release(); }
    }

    public async Task SetFavoriteAsync(string path, bool favorite)
    {
        var key = GetKey(path) ?? throw new ArgumentException("Image is outside the output directory.", nameof(path));
        await _storageGate.WaitAsync();
        try
        {
            if (_keys.Contains(key) == favorite) return;
            var next = new HashSet<string>(_keys, StringComparer.OrdinalIgnoreCase);
            if (favorite) next.Add(key);
            else next.Remove(key);

            await Task.Run(async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
                var temporaryPath = _storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                                     FileShare.None, 65536, FileOptions.Asynchronous))
                    {
                        await JsonSerializer.SerializeAsync(stream, next.Order(StringComparer.OrdinalIgnoreCase));
                    }
                    File.Move(temporaryPath, _storagePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            });
            // A failed write leaves both the previous file and the visible favorite state intact.
            _keys = next;
        }
        finally { _storageGate.Release(); }
    }

    private string? GetKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var relative = Path.GetRelativePath(_outputDirectory, Path.GetFullPath(path)).Replace('\\', '/');
            return Path.IsPathRooted(relative) || relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal)
                ? null : relative;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }
}
