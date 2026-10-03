using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? directory);

    public static async Task Main(string[] args)
    {
        string nativeDirectory;
        if (args.Length > 0)
            nativeDirectory = Path.GetFullPath(args[0]);
        else
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "NAITool.sln")))
                root = root.Parent;
            if (root == null) throw new InvalidOperationException("Cannot locate NAITool.sln.");
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            nativeDirectory = Path.Combine(root.FullName, "build", configuration, "bin");
        }
        if (!File.Exists(Path.Combine(nativeDirectory, "Microsoft.WindowsAppRuntime.dll")))
            throw new InvalidOperationException("Build NAITool (x64) first, then supply its bin directory.");
        if (!SetDllDirectory(nativeDirectory)) throw new System.ComponentModel.Win32Exception();
        try { await RunChecksAsync(args.Length > 1 ? args[1] : null); }
        finally { SetDllDirectory(null); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task RunChecksAsync(string? model) => LocalUpscaleChecks.RunAsync(model);
}
