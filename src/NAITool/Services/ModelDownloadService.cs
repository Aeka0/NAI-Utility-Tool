using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NAITool.Services;

public class ModelDownloadProgress
{
    public string StatusMessage { get; set; } = "";
    public string FileName { get; set; } = "";
    public int FileIndex { get; set; }
    public int TotalFiles { get; set; }
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public double ProgressPercent { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsError { get; set; }
}

public static class ModelDownloadService
{
    private const string HuggingFaceRepo = "https://huggingface.co/deepghs/pixai-tagger-v0.9-onnx/resolve/main";
    private const int ProgressReportIntervalMilliseconds = 1000;

    private static readonly string[] RequiredFiles = ["model.onnx", "selected_tags.csv"];

    public static string DefaultDownloadPath => Path.Combine(AppPathResolver.AppRootDir, "models", "tagger", "pixai-tagger-v0.9-onnx");

    public static bool IsModelDownloaded(string directory)
    {
        if (!Directory.Exists(directory))
            return false;
        bool hasOnnx = Directory.GetFiles(directory, "*.onnx", SearchOption.TopDirectoryOnly).Length > 0;
        bool hasCsv = File.Exists(Path.Combine(directory, "selected_tags.csv"));
        return hasOnnx && hasCsv;
    }

    public static async Task<string> DownloadModelAsync(
        string? targetDir = null,
        Action<ModelDownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        targetDir ??= DefaultDownloadPath;
        Directory.CreateDirectory(targetDir);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        long?[] remoteFileSizes = await TryGetRemoteFileSizesAsync(client, cancellationToken);
        long? totalRemoteBytes = remoteFileSizes.All(size => size.HasValue)
            ? remoteFileSizes.Sum(size => size!.Value)
            : null;
        long completedBytes = 0;

        for (int i = 0; i < RequiredFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = RequiredFiles[i];
            var targetPath = Path.Combine(targetDir, file);

            ReportProgress(
                onProgress,
                file,
                i,
                bytesReceived: 0,
                fileTotalBytes: remoteFileSizes[i],
                completedBytes,
                totalRemoteBytes);

            var url = $"{HuggingFaceRepo}/{file}";

            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                long? fileTotalBytes = response.Content.Headers.ContentLength ?? remoteFileSizes[i];
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = File.Create(targetPath);
                long fileBytesReceived = await CopyToFileWithProgressAsync(
                    contentStream,
                    fileStream,
                    onProgress,
                    file,
                    i,
                    fileTotalBytes,
                    completedBytes,
                    totalRemoteBytes,
                    cancellationToken);

                completedBytes += fileBytesReceived;
                ReportProgress(
                    onProgress,
                    file,
                    i,
                    fileBytesReceived,
                    fileTotalBytes,
                    completedBytes - fileBytesReceived,
                    totalRemoteBytes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                onProgress?.Invoke(new ModelDownloadProgress
                {
                    StatusMessage = $"Failed to download {file}",
                    FileName = file,
                    FileIndex = i + 1,
                    TotalFiles = RequiredFiles.Length,
                    ProgressPercent = CalculateProgressPercent(i, 0, null, completedBytes, totalRemoteBytes),
                    IsError = true,
                });
                throw new IOException($"Failed to download {file}: {ex.Message}", ex);
            }
        }

        onProgress?.Invoke(new ModelDownloadProgress
        {
            StatusMessage = "Download complete.",
            FileIndex = RequiredFiles.Length,
            TotalFiles = RequiredFiles.Length,
            BytesReceived = completedBytes,
            TotalBytes = totalRemoteBytes,
            ProgressPercent = 100,
            IsCompleted = true,
        });

        return targetDir;
    }

    private static async Task<long?[]> TryGetRemoteFileSizesAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var sizes = new long?[RequiredFiles.Length];
        for (int i = 0; i < RequiredFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, $"{HuggingFaceRepo}/{RequiredFiles[i]}");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                    sizes[i] = response.Content.Headers.ContentLength;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"[ModelDownload] Unable to read remote size for {RequiredFiles[i]}: {ex.Message}");
            }
        }

        return sizes;
    }

    private static async Task<long> CopyToFileWithProgressAsync(
        Stream source,
        Stream destination,
        Action<ModelDownloadProgress>? onProgress,
        string file,
        int fileIndex,
        long? fileTotalBytes,
        long completedBytes,
        long? totalRemoteBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long bytesReceived = 0;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesReceived += read;

            if (stopwatch.ElapsedMilliseconds >= ProgressReportIntervalMilliseconds)
            {
                ReportProgress(
                    onProgress,
                    file,
                    fileIndex,
                    bytesReceived,
                    fileTotalBytes,
                    completedBytes,
                    totalRemoteBytes);
                stopwatch.Restart();
            }
        }

        return bytesReceived;
    }

    private static void ReportProgress(
        Action<ModelDownloadProgress>? onProgress,
        string file,
        int fileIndex,
        long bytesReceived,
        long? fileTotalBytes,
        long completedBytes,
        long? totalRemoteBytes)
    {
        double percent = CalculateProgressPercent(
            fileIndex,
            bytesReceived,
            fileTotalBytes,
            completedBytes,
            totalRemoteBytes);

        onProgress?.Invoke(new ModelDownloadProgress
        {
            FileName = file,
            FileIndex = fileIndex + 1,
            TotalFiles = RequiredFiles.Length,
            BytesReceived = completedBytes + bytesReceived,
            TotalBytes = totalRemoteBytes,
            ProgressPercent = percent,
            StatusMessage = $"Downloading {file} ({fileIndex + 1}/{RequiredFiles.Length}) {percent:0.0}%",
        });
    }

    private static double CalculateProgressPercent(
        int fileIndex,
        long bytesReceived,
        long? fileTotalBytes,
        long completedBytes,
        long? totalRemoteBytes)
    {
        if (totalRemoteBytes is > 0)
            return Math.Clamp((completedBytes + bytesReceived) * 100d / totalRemoteBytes.Value, 0, 100);

        double currentFileProgress = fileTotalBytes is > 0
            ? Math.Clamp(bytesReceived / (double)fileTotalBytes.Value, 0, 1)
            : 0;
        return Math.Clamp((fileIndex + currentFileProgress) * 100d / RequiredFiles.Length, 0, 100);
    }
}
