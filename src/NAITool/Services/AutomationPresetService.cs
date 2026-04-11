using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using NAITool.Models;

namespace NAITool.Services;

public sealed class AutomationPresetService
{
    private readonly string _presetsDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AutomationPresetService(string appRootDir)
    {
        _presetsDir = Path.Combine(appRootDir, "user", "automation", "presets");
    }

    public string PresetsDirectory => _presetsDir;

    public IReadOnlyList<AutomationPresetListItem> ListPresets()
    {
        if (!Directory.Exists(_presetsDir))
            return [];

        var results = new List<AutomationPresetListItem>();
        foreach (string file in Directory.EnumerateFiles(_presetsDir, "*.json")
                     .OrderByDescending(File.GetLastWriteTime))
        {
            string label = Path.GetFileNameWithoutExtension(file);
            DateTime savedAt = File.GetLastWriteTime(file);

            try
            {
                var parsed = JsonSerializer.Deserialize<AutomationPresetFile>(File.ReadAllText(file), JsonOptions);
                if (parsed != null)
                {
                    if (!string.IsNullOrWhiteSpace(parsed.Name))
                        label = parsed.Name.Trim();
                    if (parsed.SavedAt != default)
                        savedAt = parsed.SavedAt;
                }
            }
            catch
            {
                // 忽略异常文件，列表页仍展示文件名，便于用户覆盖或修复。
            }

            results.Add(new AutomationPresetListItem
            {
                Name = label,
                SavedAt = savedAt,
                FilePath = file,
            });
        }

        return results;
    }

    public async Task<AutomationPresetFile?> LoadPresetAsync(string name)
    {
        string? path = TryResolvePresetPath(name);
        if (path == null || !File.Exists(path))
            return null;

        var parsed = JsonSerializer.Deserialize<AutomationPresetFile>(
            await File.ReadAllTextAsync(path), JsonOptions);
        if (parsed == null)
            return null;

        parsed.Name = string.IsNullOrWhiteSpace(parsed.Name) ? name.Trim() : parsed.Name.Trim();
        parsed.Settings ??= new AutomationSettings();
        parsed.Settings.Normalize();
        return parsed;
    }

    public async Task SavePresetAsync(string name, AutomationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(LocalizationService.Instance.GetString("automation.status.name_required"));

        string safeName = SanitizePresetFileName(name);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidOperationException(LocalizationService.Instance.GetString("automation.status.name_invalid"));

        settings.Normalize();
        Directory.CreateDirectory(_presetsDir);

        var payload = new AutomationPresetFile
        {
            Name = name.Trim(),
            SavedAt = DateTime.Now,
            Settings = settings.Clone(),
        };

        string path = Path.Combine(_presetsDir, $"{safeName}.json");
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    public string? TryResolvePresetPath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Directory.Exists(_presetsDir))
            return null;

        foreach (string file in Directory.EnumerateFiles(_presetsDir, "*.json"))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase))
                return file;

            try
            {
                var parsed = JsonSerializer.Deserialize<AutomationPresetFile>(File.ReadAllText(file), JsonOptions);
                if (string.Equals(parsed?.Name, name, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            catch
            {
                // 忽略损坏文件，继续尝试其它预设。
            }
        }

        return null;
    }

    public static string SanitizePresetFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }
}
