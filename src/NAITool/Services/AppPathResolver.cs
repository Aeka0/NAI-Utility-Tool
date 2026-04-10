using System;
using System.IO;

namespace NAITool.Services;

/// <summary>
/// 统一解析应用根目录。
/// Release 发布版中，外层入口壳将主程序放置在 bin/ 子目录下，
/// 而用户数据 (user/)、输出 (output/) 等位于壳同级根目录。
/// 此类检测到 BaseDirectory 末级目录为 bin 时自动上跳一层。
/// </summary>
public static class AppPathResolver
{
    private static readonly Lazy<string> CachedAppRootDir = new(ResolveAppRootDir);

    public static string AppRootDir => CachedAppRootDir.Value;

    private static string ResolveAppRootDir()
    {
        var baseDir = AppContext.BaseDirectory;
        string trimmed = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string lastSegment = Path.GetFileName(trimmed);

        if (lastSegment.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(baseDir, ".."));
        }

        return Path.GetFullPath(baseDir);
    }
}
