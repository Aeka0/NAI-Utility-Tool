using System;
using System.Diagnostics;
using System.IO;

namespace NAIToolLauncher
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                // 获取当前启动器所在目录
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                
                // 目标可执行文件路径
                string targetExe = Path.Combine(baseDir, "bin", "NAIUtilityTool.exe");
                
                if (File.Exists(targetExe))
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = targetExe,
                        WorkingDirectory = Path.Combine(baseDir, "bin"),
                        UseShellExecute = false
                    };
                    
                    // 传递命令行参数
                    if (args.Length > 0)
                    {
                        startInfo.Arguments = string.Join(" ", args);
                    }
                    
                    Process.Start(startInfo);
                }
                else
                {
                    // 如果找不到，尝试显示错误信息
                    System.Windows.Forms.MessageBox.Show($"找不到主程序文件：\n{targetExe}\n\n请确保程序文件完整。", "启动错误", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show($"启动失败：\n{ex.Message}", "启动错误", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
        }
    }
}
