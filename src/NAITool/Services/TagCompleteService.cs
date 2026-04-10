using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NAITool.Services;

public sealed record TagEntry(string Tag, int Category, int Count, string[] Aliases);

public sealed class TagCompleteService
{
    private List<TagEntry> _tags = new();
    public bool IsLoaded => _tags.Count > 0;

    /// <summary>
    /// Load all CSV files from the given directory, merging duplicates:
    /// same tag+category → keep highest count, union aliases.
    /// Different categories for the same tag → kept as separate entries.
    /// </summary>
    public async Task LoadFromDirectoryAsync(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return;

        var csvFiles = Directory.GetFiles(dirPath, "*.csv");
        if (csvFiles.Length == 0) return;

        // Key: (tag, category) → merged entry data
        var merged = new Dictionary<(string Tag, int Cat), (int Count, HashSet<string> Aliases)>();

        foreach (var csvPath in csvFiles)
        {
            using var reader = new StreamReader(csvPath);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                var entry = ParseLine(line);
                if (entry == null) continue;

                var key = (entry.Tag, entry.Category);
                if (merged.TryGetValue(key, out var existing))
                {
                    int bestCount = Math.Max(existing.Count, entry.Count);
                    foreach (var a in entry.Aliases) existing.Aliases.Add(a);
                    merged[key] = (bestCount, existing.Aliases);
                }
                else
                {
                    merged[key] = (entry.Count, new HashSet<string>(entry.Aliases, StringComparer.OrdinalIgnoreCase));
                }
            }
        }

        var list = new List<TagEntry>(merged.Count);
        foreach (var kv in merged)
        {
            list.Add(new TagEntry(kv.Key.Tag, kv.Key.Cat, kv.Value.Count,
                kv.Value.Aliases.Count > 0 ? kv.Value.Aliases.ToArray() : Array.Empty<string>()));
        }

        list.Sort((a, b) => b.Count.CompareTo(a.Count));
        _tags = list;
    }

    private static TagEntry? ParseLine(string line)
    {
        int col = 0;
        int pos = 0;
        string tag = "", aliasRaw = "";
        int category = 0, count = 0;

        while (col < 4 && pos <= line.Length)
        {
            string field;
            if (pos < line.Length && line[pos] == '"')
            {
                int close = line.IndexOf('"', pos + 1);
                if (close < 0) close = line.Length;
                field = line.Substring(pos + 1, close - pos - 1);
                pos = close + 2;
            }
            else
            {
                int comma = line.IndexOf(',', pos);
                if (comma < 0) comma = line.Length;
                field = line.Substring(pos, comma - pos);
                pos = comma + 1;
            }

            switch (col)
            {
                case 0: tag = field; break;
                case 1: int.TryParse(field, out category); break;
                case 2: int.TryParse(field, out count); break;
                case 3: aliasRaw = field; break;
            }
            col++;
        }

        if (string.IsNullOrEmpty(tag)) return null;

        var aliases = string.IsNullOrEmpty(aliasRaw)
            ? Array.Empty<string>()
            : aliasRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new TagEntry(tag, category, count, aliases);
    }

    public List<TagMatch> Search(string prefix, int maxResults = 15, int? categoryFilter = null)
    {
        if (string.IsNullOrWhiteSpace(prefix) || _tags.Count == 0)
            return new List<TagMatch>();

        var results = new List<TagMatch>(maxResults);
        var prefixLower = prefix.ToLowerInvariant();
        var prefixUnderscore = prefixLower.Replace(' ', '_');

        foreach (var entry in _tags)
        {
            if (categoryFilter.HasValue && entry.Category != categoryFilter.Value)
                continue;

            string? matchedAlias = null;

            if (entry.Tag.StartsWith(prefixUnderscore, StringComparison.OrdinalIgnoreCase) ||
                entry.Tag.StartsWith(prefixLower, StringComparison.OrdinalIgnoreCase))
            {
                // direct tag match
            }
            else
            {
                bool aliasHit = false;
                foreach (var alias in entry.Aliases)
                {
                    if (alias.StartsWith(prefixLower, StringComparison.OrdinalIgnoreCase) ||
                        alias.StartsWith(prefixUnderscore, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedAlias = alias;
                        aliasHit = true;
                        break;
                    }
                }
                if (!aliasHit) continue;
            }

            results.Add(new TagMatch(entry, matchedAlias));
            if (results.Count >= maxResults) break;
        }

        return results;
    }

    /// <summary>
    /// Get random tags from a specific category that meet minimum count.
    /// </summary>
    public List<TagEntry> GetRandomTags(int count, int category, int minCount)
    {
        var candidates = _tags.Where(t => t.Category == category && t.Count >= minCount).ToList();
        if (candidates.Count == 0) return new List<TagEntry>();

        var rng = Random.Shared;
        var selected = new List<TagEntry>(count);
        var used = new HashSet<int>();

        int limit = Math.Min(count, candidates.Count);
        while (selected.Count < limit)
        {
            int idx = rng.Next(candidates.Count);
            if (used.Add(idx))
                selected.Add(candidates[idx]);
        }
        return selected;
    }

    public static string FormatCount(int count)
    {
        if (count >= 1_000_000) return $"{count / 1_000_000.0:F1}M";
        if (count >= 1_000) return $"{count / 1_000.0:F1}K";
        return count.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed record TagMatch(TagEntry Entry, string? MatchedAlias);
