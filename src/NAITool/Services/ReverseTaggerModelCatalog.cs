using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NAITool.Services;

internal static class ReverseTaggerModelCatalog
{
    private const string TagsFileName = "selected_tags.csv";

    public static bool HasUsableModelDirectory(string modelDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
                return false;

            bool hasOnnx = Directory.GetFiles(modelDirectory, "*.onnx", SearchOption.AllDirectories).Length > 0;
            return hasOnnx && File.Exists(GetTagsCsvPath(modelDirectory));
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<ReverseTagDefinition> LoadTagDefinitions(string modelDirectory)
    {
        var csvPath = GetTagsCsvPath(modelDirectory);
        if (!File.Exists(csvPath))
            throw new FileNotFoundException(L("reverse.error.tags_csv_missing"));

        using var enumerator = File.ReadLines(csvPath, Encoding.UTF8).GetEnumerator();
        if (!enumerator.MoveNext())
            throw new InvalidOperationException(L("reverse.error.tags_csv_empty"));

        var header = ParseCsvLine(enumerator.Current)
            .Select(NormalizeHeader)
            .ToArray();

        var layout = ReverseTagCsvLayout.FromHeader(header);
        var tags = new List<ReverseTagDefinition>();
        while (enumerator.MoveNext())
        {
            var line = enumerator.Current;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseCsvLine(line);
            if (!layout.TryRead(fields, tags.Count, out var tag))
                continue;

            tags.Add(tag);
        }

        if (tags.Count == 0)
            throw new InvalidOperationException(L("reverse.error.tags_csv_empty"));

        if (!HasContiguousIndexes(tags))
        {
            if (!layout.UsesExplicitModelIndex)
                throw new InvalidOperationException(L("reverse.error.tags_csv_non_contiguous"));

            tags = tags
                .Select((tag, index) => tag with { Index = index })
                .ToList();
        }

        return tags.OrderBy(tag => tag.Index).ToArray();
    }

    public static HashSet<string> LoadArtistTagSet(string modelDirectory)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!HasUsableModelDirectory(modelDirectory))
            return result;

        foreach (var tag in LoadTagDefinitions(modelDirectory))
        {
            if (tag.Category == ArtistTagCategory && !string.IsNullOrWhiteSpace(tag.Name))
                result.Add(NormalizePromptTagForMatch(tag.Name));
        }

        return result;
    }

    private static string GetTagsCsvPath(string modelDirectory)
        => Path.Combine(Path.GetFullPath(modelDirectory), TagsFileName);

    private static string NormalizeHeader(string field)
        => (field ?? "").Trim().TrimStart('\uFEFF').ToLowerInvariant();

    private static int ParseRequiredInt(string text, string fieldName)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return value;
        throw new InvalidOperationException(Lf("reverse.error.tags_csv_parse_failed", fieldName));
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var builder = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                fields.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(ch);
        }

        fields.Add(builder.ToString());
        return fields;
    }

    private static IReadOnlyList<string> ParseIps(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "[]")
            return Array.Empty<string>();

        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(raw);
            if (values != null)
                return values;
            return Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string NormalizePromptTagForMatch(string tag)
    {
        string normalized = (tag ?? "").Trim();
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"^[\(\[\{<\s]+|[\)\]\}>\s]+$", "");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @":\s*-?\d+(\.\d+)?$", "");
        normalized = normalized.Replace('_', ' ');
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized.ToLowerInvariant();
    }

    private static string L(string key) => LocalizationService.Instance.GetString(key);
    private static string Lf(string key, params object?[] args) => LocalizationService.Instance.Format(key, args);

    private const int ArtistTagCategory = 1;

    private static bool HasContiguousIndexes(IReadOnlyList<ReverseTagDefinition> tags)
    {
        int expectedIndex = 0;
        foreach (var tag in tags.OrderBy(tag => tag.Index))
        {
            if (tag.Index != expectedIndex)
                return false;
            expectedIndex++;
        }

        return true;
    }

    private sealed class ReverseTagCsvLayout
    {
        private readonly int? _modelIndexColumn;
        private readonly int _nameColumn;
        private readonly int _categoryColumn;
        private readonly int? _intellectualPropertiesColumn;

        private ReverseTagCsvLayout(
            int? modelIndexColumn,
            int nameColumn,
            int categoryColumn,
            int? intellectualPropertiesColumn)
        {
            _modelIndexColumn = modelIndexColumn;
            _nameColumn = nameColumn;
            _categoryColumn = categoryColumn;
            _intellectualPropertiesColumn = intellectualPropertiesColumn;
        }

        public bool UsesExplicitModelIndex => _modelIndexColumn.HasValue;

        public static ReverseTagCsvLayout FromHeader(IReadOnlyList<string> header)
        {
            bool isClassicWd14 =
                ColumnEquals(header, 0, "tag_id") &&
                ColumnEquals(header, 1, "name") &&
                ColumnEquals(header, 2, "category");

            if (isClassicWd14)
                return new ReverseTagCsvLayout(null, 1, 2, null);

            int? modelIndexColumn = FindFirstColumn(header, "id", "index", "tag_index", "model_index", "output_index");
            int nameColumn = FindFirstColumn(header, "name", "tag", "tag_name") ?? (header.Count >= 3 ? 2 : 1);
            int categoryColumn = FindFirstColumn(header, "category", "tag_category") ?? (header.Count >= 4 ? 3 : 2);
            int? ipColumn = FindFirstColumn(
                header,
                "intellectual_properties",
                "intellectual_property",
                "copyrights",
                "copyright_tags",
                "ips") ?? (header.Count >= 6 ? 5 : null);

            return new ReverseTagCsvLayout(modelIndexColumn ?? 0, nameColumn, categoryColumn, ipColumn);
        }

        public bool TryRead(IReadOnlyList<string> fields, int ordinalIndex, out ReverseTagDefinition tag)
        {
            tag = default!;

            int largestRequiredColumn = Math.Max(_nameColumn, _categoryColumn);
            if (fields.Count <= largestRequiredColumn)
                return false;

            int index = _modelIndexColumn.HasValue
                ? ParseRequiredInt(fields[_modelIndexColumn.Value], L("reverse.csv.tag_index"))
                : ordinalIndex;
            string name = fields[_nameColumn];
            int category = ParseRequiredInt(fields[_categoryColumn], L("reverse.csv.tag_category"));
            var ips = _intellectualPropertiesColumn.HasValue && fields.Count > _intellectualPropertiesColumn.Value
                ? ParseIps(fields[_intellectualPropertiesColumn.Value])
                : Array.Empty<string>();

            tag = new ReverseTagDefinition(index, name, category, ips);
            return true;
        }

        private static int? FindFirstColumn(IReadOnlyList<string> header, params string[] names)
        {
            for (int i = 0; i < header.Count; i++)
            {
                if (names.Contains(header[i], StringComparer.OrdinalIgnoreCase))
                    return i;
            }

            return null;
        }

        private static bool ColumnEquals(IReadOnlyList<string> header, int index, string value)
            => header.Count > index && string.Equals(header[index], value, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record ReverseTagDefinition(
    int Index,
    string Name,
    int Category,
    IReadOnlyList<string> IntellectualProperties);
