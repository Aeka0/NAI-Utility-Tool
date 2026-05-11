using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NAITool.Services;

public class ModelDownloadProgress
{
    public string StatusMessage { get; set; } = "";
    public bool IsCompleted { get; set; }
    public bool IsError { get; set; }
}

public static class ModelDownloadService
{
    private const string HuggingFaceRepo = "https://huggingface.co/deepghs/pixai-tagger-v0.9-onnx/resolve/main";

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

        for (int i = 0; i < RequiredFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = RequiredFiles[i];
            var targetPath = Path.Combine(targetDir, file);

            onProgress?.Invoke(new ModelDownloadProgress
            {
                StatusMessage = $"Downloading {file} ({i + 1}/{RequiredFiles.Length})...",
            });

            var url = $"{HuggingFaceRepo}/{file}";

            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = File.Create(targetPath);
                await contentStream.CopyToAsync(fileStream, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                onProgress?.Invoke(new ModelDownloadProgress
                {
                    StatusMessage = $"Failed to download {file}",
                    IsError = true,
                });
                throw new IOException($"Failed to download {file}: {ex.Message}", ex);
            }
        }

        onProgress?.Invoke(new ModelDownloadProgress
        {
            StatusMessage = "Download complete.",
            IsCompleted = true,
        });

        return targetDir;
    }
}
