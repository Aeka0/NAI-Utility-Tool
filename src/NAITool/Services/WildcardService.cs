using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NAITool.Services;

public enum WildcardWeightFormat
{
    StableDiffusion,
    NaiClassic,
    NaiNumeric,
}

public sealed record WildcardIndexEntry(
    string Name,
    string RelativePath,
    string FilePath,
    int OptionCount,
    DateTime LastWriteTime);

public sealed record WildcardSearchResult(WildcardIndexEntry Entry);

public sealed record WildcardExpandResult(
    string Text,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ExpansionLogs);

internal sealed record WildcardOption(string Text, double EmphasisWeight);

public sealed class WildcardExpandContext
{
    public WildcardExpandContext(int seed, WildcardWeightFormat weightFormat)
    {
        Seed = seed;
        WeightFormat = weightFormat;
        Random = new Random(seed);
    }

    public int Seed { get; }
    public WildcardWeightFormat WeightFormat { get; }
    public Random Random { get; }
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = new();
    public List<string> ExpansionLogs { get; } = new();
}

public sealed class WildcardService
{
    private static readonly Regex WildcardTokenRegex = new(@"__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex PlainTokenRegex = new(@"(?<=^|[,\r\n])\s*([^\r\n,]+?)\s*(?=$|[,\r\n])", RegexOptions.Compiled);

    private readonly Dictionary<string, WildcardIndexEntry> _entriesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<WildcardOption>> _optionsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _scanWarnings = new();

    public string RootDirectory { get; private set; } = "";
    public bool IsLoaded => _entriesByName.Count > 0;
    public IReadOnlyList<WildcardIndexEntry> Entries => _entriesByName.Values
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    public int FileCount => _entriesByName.Count;
    public int OptionCount => _optionsByName.Values.Sum(x => x.Count);
    public IReadOnlyList<string> ScanWarnings => _scanWarnings;

    public void Reload(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        Directory.CreateDirectory(rootDirectory);

        _entriesByName.Clear();
        _optionsByName.Clear();
        _scanWarnings.Clear();

        foreach (var path in Directory.GetFiles(rootDirectory, "*.txt", SearchOption.AllDirectories))
        {
            try
            {
                string relative = Path.GetRelativePath(rootDirectory, path);
                string normalized = NormalizeName(Path.ChangeExtension(relative, null) ?? "");
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (_entriesByName.ContainsKey(normalized))
                {
                    _scanWarnings.Add($"检测到重复抽卡器名称，已忽略后续文件：{normalized}");
                    continue;
                }

                var options = ParseOptions(path).ToList();
                _entriesByName[normalized] = new WildcardIndexEntry(
                    normalized,
                    relative.Replace('\\', '/'),
                    path,
                    options.Count,
                    File.GetLastWriteTime(path));
                _optionsByName[normalized] = options;
            }
            catch (Exception ex)
            {
                _scanWarnings.Add($"扫描抽卡器文件失败：{Path.GetFileName(path)} - {ex.Message}");
            }
        }
    }

    public bool HasEntry(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _entriesByName.ContainsKey(NormalizeName(name));
    }

    /// <summary>
    /// Lists immediate subdirectory names under the given relative path (empty = root).
    /// </summary>
    public List<string> ListSubDirectories(string relativePath = "")
    {
        string dir = string.IsNullOrWhiteSpace(relativePath)
            ? RootDirectory
            : Path.Combine(RootDirectory, relativePath);
        if (!Directory.Exists(dir)) return new();
        return Directory.GetDirectories(dir)
            .Select(d => Path.GetFileName(d))
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Lists wildcard entries (.txt files) that live directly in the given relative path (not recursive).
    /// </summary>
    public List<WildcardIndexEntry> ListEntriesInDirectory(string relativePath = "")
    {
        string prefix = string.IsNullOrWhiteSpace(relativePath) ? "" : NormalizeName(relativePath) + "/";
        return _entriesByName.Values
            .Where(e =>
            {
                string n = e.Name;
                if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                string remainder = n[prefix.Length..];
                return !remainder.Contains('/');
            })
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<WildcardSearchResult> Search(string prefix, int maxResults = 15)
    {
        if (string.IsNullOrWhiteSpace(prefix) || _entriesByName.Count == 0)
            return new List<WildcardSearchResult>();

        string normalizedPrefix = NormalizeName(prefix.Trim());
        return _entriesByName.Values
            .Where(x => x.Name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(x => new WildcardSearchResult(x))
            .ToList();
    }

    public WildcardExpandResult ExpandText(
        string text,
        WildcardExpandContext context,
        bool requireExplicitSyntax,
        bool enable)
    {
        if (!enable || string.IsNullOrWhiteSpace(text) || _entriesByName.Count == 0)
            return new WildcardExpandResult(text, context.Warnings.ToList(), context.ExpansionLogs.ToList());

        string expanded = requireExplicitSyntax
            ? ExpandExplicitSyntax(text, context)
            : ExpandLooseTokens(text, context);

        return new WildcardExpandResult(expanded, context.Warnings.ToList(), context.ExpansionLogs.ToList());
    }

    private string ExpandExplicitSyntax(string text, WildcardExpandContext context)
    {
        return WildcardTokenRegex.Replace(text, match =>
        {
            string body = match.Groups[1].Value.Trim();
            string? resolved = ResolveTokenBody(body, context);
            return resolved ?? match.Value;
        });
    }

    private string ExpandLooseTokens(string text, WildcardExpandContext context)
    {
        return PlainTokenRegex.Replace(text, match =>
        {
            string token = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(token))
                return match.Value;

            string? resolved = ResolveLooseToken(token, context);
            return resolved ?? match.Value;
        });
    }

    private string? ResolveLooseToken(string token, WildcardExpandContext context)
    {
        if (token.StartsWith("__", StringComparison.Ordinal) && token.EndsWith("__", StringComparison.Ordinal) && token.Length > 4)
            return ResolveTokenBody(token[2..^2], context);

        if (_entriesByName.ContainsKey(NormalizeName(token)))
            return ResolveTokenBody(token, context);

        return null;
    }

    private string? ResolveTokenBody(string body, WildcardExpandContext context)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        if (body.StartsWith("@", StringComparison.Ordinal))
        {
            string varName = body[1..].Trim();
            if (context.Variables.TryGetValue(varName, out string? stored))
                return stored;

            context.Warnings.Add($"抽卡器变量未定义：{varName}");
            return $"__{body}__";
        }

        string name = body;
        string? suffix = null;
        int atIndex = body.LastIndexOf('@');
        if (atIndex > 0 && atIndex < body.Length - 1)
        {
            name = body[..atIndex].Trim();
            suffix = body[(atIndex + 1)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
            return null;

        string normalized = NormalizeName(name);
        if (!_optionsByName.TryGetValue(normalized, out var options) || options.Count == 0)
        {
            context.Warnings.Add($"抽卡器不存在或为空：{normalized}");
            return $"__{body}__";
        }

        if (!string.IsNullOrWhiteSpace(suffix) && int.TryParse(suffix, out int count) && count > 0)
        {
            var picks = PickMultiple(options, count, context);
            context.ExpansionLogs.Add($"{normalized} x{count}");
            return string.Join(", ", picks);
        }

        string selected = PickOne(options, context);
        if (!string.IsNullOrWhiteSpace(suffix))
            context.Variables[suffix] = selected;

        context.ExpansionLogs.Add(!string.IsNullOrWhiteSpace(suffix)
            ? $"{normalized} -> @{suffix}"
            : normalized);
        return selected;
    }

    private static IEnumerable<WildcardOption> ParseOptions(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            string text = line;
            double emphasis = 1.0;
            int sepIndex = line.LastIndexOf('|');
            if (sepIndex > 0 && sepIndex < line.Length - 1)
            {
                string tail = line[(sepIndex + 1)..].Trim();
                if (double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    text = line[..sepIndex].Trim();
                    emphasis = parsed;
                }
            }

            if (string.IsNullOrWhiteSpace(text))
                continue;

            yield return new WildcardOption(text, emphasis);
        }
    }

    private static string NormalizeName(string value)
    {
        return value
            .Replace('\\', '/')
            .Trim()
            .Trim('/')
            .Replace("//", "/", StringComparison.Ordinal);
    }

    private static string PickOne(List<WildcardOption> options, WildcardExpandContext context)
    {
        int index = context.Random.Next(options.Count);
        return RenderOption(options[index], context.WeightFormat);
    }

    private static IReadOnlyList<string> PickMultiple(List<WildcardOption> options, int count, WildcardExpandContext context)
    {
        var pool = Enumerable.Range(0, options.Count).ToList();
        var result = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            if (pool.Count == 0)
                pool = Enumerable.Range(0, options.Count).ToList();

            int selectedIndex = context.Random.Next(pool.Count);
            int optionIndex = pool[selectedIndex];
            pool.RemoveAt(selectedIndex);
            result.Add(RenderOption(options[optionIndex], context.WeightFormat));
        }
        return result;
    }

    private static string RenderOption(WildcardOption option, WildcardWeightFormat format)
    {
        string text = option.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return text;

        if (Math.Abs(option.EmphasisWeight - 1.0) < 0.001)
            return text;

        return format switch
        {
            WildcardWeightFormat.NaiNumeric => $"{option.EmphasisWeight.ToString("0.##", CultureInfo.InvariantCulture)}::{text}::",
            WildcardWeightFormat.NaiClassic => RenderClassicWeight(text, option.EmphasisWeight),
            _ => $"({text}:{option.EmphasisWeight.ToString("0.##", CultureInfo.InvariantCulture)})",
        };
    }

    private static string RenderClassicWeight(string text, double weight)
    {
        if (Math.Abs(weight - 1.0) < 0.001)
            return text;

        const double stepFactor = 1.05;
        int repeats = Math.Max(1, (int)Math.Round(Math.Abs(Math.Log(weight) / Math.Log(stepFactor))));
        string open = weight >= 1.0 ? "{" : "[";
        string close = weight >= 1.0 ? "}" : "]";
        for (int i = 0; i < repeats; i++)
            text = open + text + close;
        return text;
    }
}
