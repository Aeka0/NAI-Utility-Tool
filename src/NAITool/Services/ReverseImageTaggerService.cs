using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace NAITool.Services;

public sealed class ReverseImageTaggerService : IDisposable
{
    private readonly object _sync = new();
    private readonly object _runSync = new();

    private InferenceSession? _session;
    private string? _loadedModelDirectory;
    private string _inputName = "";
    private string[] _outputNames = [];
    private string _executionProvider = "CPU";
    private IReadOnlyList<ReverseTagDefinition> _tagDefinitions = Array.Empty<ReverseTagDefinition>();

    public Task<ReverseTaggerResult> InferAsync(
        byte[] imageBytes,
        ReverseTaggerSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            throw new InvalidOperationException("没有可用于反推的图片数据。");

        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var modelDirectory = ResolveModelDirectory(settings.ModelPath);
            EnsureModelLoaded(modelDirectory);

            var (tensorData, tensorShape, width, height) = PreprocessImage(imageBytes, cancellationToken);
            var scores = RunInference(tensorData, tensorShape, cancellationToken);
            return BuildResult(scores, settings, width, height);
        }, cancellationToken);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _session?.Dispose();
            _session = null;
            _loadedModelDirectory = null;
            _inputName = "";
            _outputNames = [];
            _executionProvider = "CPU";
            _tagDefinitions = Array.Empty<ReverseTagDefinition>();
        }
    }

    private void EnsureModelLoaded(string modelDirectory)
    {
        lock (_sync)
        {
            if (_session != null &&
                string.Equals(_loadedModelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _session?.Dispose();

            var modelPath = ResolveModelFile(modelDirectory);
            _tagDefinitions = LoadTagDefinitions(modelDirectory);

            var (session, provider) = CreateSession(modelPath);
            _session = session;
            _loadedModelDirectory = modelDirectory;
            _executionProvider = provider;
            _inputName = session.InputMetadata.Keys.FirstOrDefault()
                ?? throw new InvalidOperationException("模型缺少输入节点。");
            _outputNames = SelectOutputNames(session, _tagDefinitions.Count);
        }
    }

    private float[] RunInference(float[] tensorData, long[] tensorShape, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_runSync)
        {
            if (_session == null)
                throw new InvalidOperationException("反推模型尚未初始化。");

            using var inputValue = OrtValue.CreateTensorValueFromMemory(tensorData, tensorShape);
            using var runOptions = new RunOptions();
            var inputs = new Dictionary<string, OrtValue>
            {
                [_inputName] = inputValue,
            };
            using var results = _session.Run(runOptions, inputs, _outputNames);

            if (results.Count == 0)
                throw new InvalidOperationException("模型未返回有效输出。");

            float[]? raw = null;
            int bestLength = -1;
            for (int i = 0; i < results.Count; i++)
            {
                var result = results[i];
                if (!result.IsTensor)
                    continue;

                var candidate = result.GetTensorDataAsSpan<float>().ToArray();
                if (candidate.Length == _tagDefinitions.Count)
                {
                    raw = candidate;
                    break;
                }

                if (candidate.Length > bestLength)
                {
                    bestLength = candidate.Length;
                    raw = candidate;
                }
            }

            if (raw == null)
                throw new InvalidOperationException("模型未返回可用的 tensor 输出。");

            if (raw.Length == 0)
                throw new InvalidOperationException("模型输出为空。");

            bool looksLikeProbability = raw.All(value => value >= 0f && value <= 1f);
            if (looksLikeProbability)
                return raw;

            for (int i = 0; i < raw.Length; i++)
                raw[i] = 1f / (1f + MathF.Exp(-raw[i]));
            return raw;
        }
    }

    private ReverseTaggerResult BuildResult(
        float[] scores,
        ReverseTaggerSettings settings,
        int width,
        int height)
    {
        if (scores.Length < _tagDefinitions.Count)
            throw new InvalidOperationException(
                $"模型输出数量与标签定义不一致（输出 {scores.Length}，标签 {_tagDefinitions.Count}）。");

        double generalThreshold = Math.Clamp(settings.GeneralThreshold, 0, 1);
        double characterThreshold = Math.Clamp(settings.CharacterThreshold, 0, 1);

        var generalTags = new List<ReverseTagPrediction>();
        var characterTags = new List<ReverseTagPrediction>();
        var copyrightScores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in _tagDefinitions)
        {
            float score = scores[tag.Index];
            if (tag.Category == 0)
            {
                if (score >= generalThreshold)
                    generalTags.Add(new ReverseTagPrediction(FormatTag(tag.Name, settings), score));
                continue;
            }

            if (tag.Category != 4 || score < characterThreshold)
                continue;

            var formattedCharacter = FormatTag(tag.Name, settings);
            characterTags.Add(new ReverseTagPrediction(formattedCharacter, score));

            foreach (var ip in tag.IntellectualProperties)
            {
                var formattedIp = FormatTag(ip, settings);
                if (copyrightScores.TryGetValue(formattedIp, out float currentScore))
                    copyrightScores[formattedIp] = Math.Max(currentScore, score);
                else
                    copyrightScores[formattedIp] = score;
            }
        }

        generalTags.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        characterTags.Sort(static (left, right) => right.Score.CompareTo(left.Score));

        var copyrightTags = copyrightScores
            .Select(pair => new ReverseTagPrediction(pair.Key, pair.Value))
            .OrderByDescending(tag => tag.Score)
            .ToList();

        var promptParts = new List<string>();
        if (settings.AddCharacterTags)
            promptParts.AddRange(characterTags.Select(tag => tag.Name));
        if (settings.AddCopyrightTags)
            promptParts.AddRange(copyrightTags.Select(tag => tag.Name));
        promptParts.AddRange(generalTags.Select(tag => tag.Name));

        var uniquePromptParts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in promptParts)
        {
            if (seen.Add(part))
                uniquePromptParts.Add(part);
        }

        if (uniquePromptParts.Count == 0)
            throw new InvalidOperationException("没有标签达到当前阈值，请尝试降低阈值后重试。");

        return new ReverseTaggerResult
        {
            PositivePrompt = string.Join(", ", uniquePromptParts),
            GeneralTags = generalTags,
            CharacterTags = characterTags,
            CopyrightTags = copyrightTags,
            ExecutionProvider = _executionProvider,
            ImageWidth = width,
            ImageHeight = height,
        };
    }

    private static (InferenceSession Session, string Provider) CreateSession(string modelPath)
    {
        try
        {
            var directMlOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            directMlOptions.AppendExecutionProvider_DML(0);
            return (new InferenceSession(modelPath, directMlOptions), "GPU (DirectML)");
        }
        catch
        {
            var cpuOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            return (new InferenceSession(modelPath, cpuOptions), "CPU");
        }
    }

    private static string ResolveModelDirectory(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new InvalidOperationException("请先在设置中配置反推模型路径。");

        var fullPath = Path.GetFullPath(modelPath.Trim());
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("反推模型路径不存在。");

        return fullPath;
    }

    private static string ResolveModelFile(string modelDirectory)
    {
        var candidates = Directory.GetFiles(modelDirectory, "*.onnx", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
        {
            candidates = Directory.GetFiles(modelDirectory, "*.onnx", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (candidates.Length == 0)
            throw new FileNotFoundException("所选目录中未找到 .onnx 模型文件。");

        return candidates[0];
    }

    private static string[] SelectOutputNames(InferenceSession session, int expectedTagCount)
    {
        if (session.OutputMetadata.Count == 0)
            throw new InvalidOperationException("模型缺少输出节点。");

        var prioritized = new List<(string Name, int Priority, long KnownSize)>();

        foreach (var pair in session.OutputMetadata)
        {
            var dimensions = pair.Value.Dimensions;
            bool matchesTagCount = dimensions.Any(dim => dim == expectedTagCount);
            long knownSize = 1;
            bool hasPositiveDimension = false;
            foreach (var dim in dimensions)
            {
                if (dim <= 0) continue;
                hasPositiveDimension = true;
                knownSize *= dim;
            }

            if (!hasPositiveDimension)
                knownSize = -1;

            prioritized.Add((pair.Key, matchesTagCount ? 0 : 1, knownSize));
        }

        return prioritized
            .OrderBy(item => item.Priority)
            .ThenByDescending(item => item.KnownSize)
            .Select(item => item.Name)
            .ToArray();
    }

    private static IReadOnlyList<ReverseTagDefinition> LoadTagDefinitions(string modelDirectory)
    {
        var csvPath = Path.Combine(modelDirectory, "selected_tags.csv");
        if (!File.Exists(csvPath))
            throw new FileNotFoundException("所选目录缺少 selected_tags.csv。");

        var tags = new List<ReverseTagDefinition>();
        bool isFirstLine = true;
        foreach (var line in File.ReadLines(csvPath, Encoding.UTF8))
        {
            if (isFirstLine)
            {
                isFirstLine = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseCsvLine(line);
            if (fields.Count < 6)
                continue;

            int index = ParseRequiredInt(fields[0], "标签索引");
            string name = fields[2];
            int category = ParseRequiredInt(fields[3], "标签类别");
            var ips = ParseIps(fields[5]);
            tags.Add(new ReverseTagDefinition(index, name, category, ips));
        }

        if (tags.Count == 0)
            throw new InvalidOperationException("selected_tags.csv 中未读取到任何标签定义。");

        int expectedIndex = 0;
        foreach (var tag in tags.OrderBy(tag => tag.Index))
        {
            if (tag.Index != expectedIndex)
                throw new InvalidOperationException("selected_tags.csv 的标签索引不连续。");
            expectedIndex++;
        }

        return tags.OrderBy(tag => tag.Index).ToArray();
    }

    private static int ParseRequiredInt(string text, string fieldName)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return value;
        throw new InvalidOperationException($"{fieldName}解析失败。");
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

    private static (float[] TensorData, long[] TensorShape, int Width, int Height) PreprocessImage(
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var source = SKBitmap.Decode(imageBytes);
        if (source == null)
            throw new InvalidOperationException("无法读取图片内容。");

        const int inputSize = 448;
        using var resized = new SKBitmap(new SKImageInfo(inputSize, inputSize, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(source, new SKRect(0, 0, inputSize, inputSize), paint);
        }

        var data = new float[1 * 3 * inputSize * inputSize];
        for (int y = 0; y < inputSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < inputSize; x++)
            {
                var pixel = resized.GetPixel(x, y);
                int pixelIndex = y * inputSize + x;
                data[pixelIndex] = NormalizePixel(pixel.Red);
                data[(inputSize * inputSize) + pixelIndex] = NormalizePixel(pixel.Green);
                data[(2 * inputSize * inputSize) + pixelIndex] = NormalizePixel(pixel.Blue);
            }
        }

        return (data, [1, 3, inputSize, inputSize], source.Width, source.Height);
    }

    private static float NormalizePixel(byte channel)
        => (channel / 255f - 0.5f) / 0.5f;

    private static string FormatTag(string raw, ReverseTaggerSettings settings)
    {
        if (!settings.ReplaceUnderscoresWithSpaces)
            return raw;
        return raw.Replace('_', ' ');
    }

    private sealed record ReverseTagDefinition(
        int Index,
        string Name,
        int Category,
        IReadOnlyList<string> IntellectualProperties);
}

public sealed class ReverseTaggerResult
{
    public string PositivePrompt { get; init; } = "";
    public IReadOnlyList<ReverseTagPrediction> GeneralTags { get; init; } = Array.Empty<ReverseTagPrediction>();
    public IReadOnlyList<ReverseTagPrediction> CharacterTags { get; init; } = Array.Empty<ReverseTagPrediction>();
    public IReadOnlyList<ReverseTagPrediction> CopyrightTags { get; init; } = Array.Empty<ReverseTagPrediction>();
    public string ExecutionProvider { get; init; } = "CPU";
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
}

public sealed record ReverseTagPrediction(string Name, float Score);
