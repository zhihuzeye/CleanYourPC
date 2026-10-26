using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using CleanYourPC.Core.Models;
using CleanYourPC.Core.Services;

namespace CleanYourPC.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            CrashLogger.LogRunning($"CleanYourPC 进程启动 (参数: [{(args.Length > 0 ? string.Join(" ", args) : "无")}], PID: {Environment.ProcessId})");

            // 全局挂接非托管未处理异常
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                CrashLogger.LogError("AppDomain 未处理致命异常", ex, $"IsTerminating: {e.IsTerminating}");
            };

            // 1. 若以提权 Worker 模式启动，直接在纯命令行模式下后台执行，绝对不加载 WPF 图形引擎与 Direct3D，彻底避免非托管退出崩溃
            int workerIdx = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--elevated-worker", StringComparison.OrdinalIgnoreCase))
                {
                    workerIdx = i;
                    break;
                }
            }

            if (workerIdx >= 0 && workerIdx + 2 < args.Length)
            {
                return RunElevatedWorkerHeadless(args[workerIdx + 1], args[workerIdx + 2]);
            }

            // 2. 检测管理员权限，若非管理员则主动提示用户提权（系统 UAC 输入密码或授权）
            if (TryPromptAndElevateAsAdmin(args))
            {
                return 0;
            }

            // 3. 正常桌面 GUI 模式启动 WPF 应用程序
            var app = new App();
            return app.Run();
        }
        catch (Exception ex)
        {
            var logPath = CrashLogger.LogError("程序顶层启动崩溃", ex);
            try
            {
                System.Windows.MessageBox.Show(
                    $"CleanYourPC 启动或运行遇到严重异常：\n{ex.Message}\n\n已将详细错误排查日志写入：\n{logPath}",
                    "程序异常终止",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            catch { }
            return 1;
        }
    }

    private static int RunElevatedWorkerHeadless(string inputPath, string outputPath)
    {
        try
        {
            var backupService = new BackupRestoreService();
            var uninstallService = new SoftwareUninstallService();
            var engine = new RuleEngine(backupService, uninstallService);

            if (!File.Exists(inputPath))
            {
                File.WriteAllText(outputPath, "[]");
                return 0;
            }

            var json = File.ReadAllText(inputPath);
            var items = JsonSerializer.Deserialize<List<ScanItem>>(json) ?? new List<ScanItem>();
            var results = new List<ExecutionItemResult>();

            foreach (var item in items)
            {
                results.Add(engine.ExecuteScanItem(item));
            }

            if (results.Any(r => !r.Success))
            {
                var sysScanner = new SystemInfoScanner();
                var env = sysScanner.GetEnvironmentSummary();
                CrashLogger.LogCleanExecutionDetails(env, items, results, null);
            }

            File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("提权 Worker 进程执行异常", ex, $"Input: {inputPath}, Output: {outputPath}");
            try
            {
                var errResult = new List<ExecutionItemResult>
                {
                    new ExecutionItemResult { RuleId = "ELEVATED-ERR", Title = "管理员提权执行异常", Success = false, Message = ex.Message }
                };
                File.WriteAllText(outputPath, JsonSerializer.Serialize(errResult));
            }
            catch { }
            return 1;
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            return Environment.UserName == "root";
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPromptAndElevateAsAdmin(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (IsAdministrator())
        {
            return false;
        }

        // 避免特定参数下重复弹窗（例如命令行测试或无弹窗启动）
        if (args.Any(a => a.Equals("--no-uac-prompt", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var result = MessageBox.Show(
            "检测到 CleanYourPC 当前未以【管理员身份】运行。\n\n" +
            "为了确保能够深度扫描并清理系统策略、残留注册表、捆绑推广软件及开机自启服务，建议以管理员权限运行。\n\n" +
            "是否立即提权为管理员？\n" +
            "• 点击【是】：弹出系统 UAC 提示，可输入密码或点击“是”授权\n" +
            "• 点击【否】：继续以受限普通用户权限运行",
            "CleanYourPC - 提权提示",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "CleanYourPC.App.exe";
                var psi = new ProcessStartInfo
                {
                    FileName = processPath,
                    Verb = "runas",
                    UseShellExecute = true
                };
                Process.Start(psi);
                // 成功唤起提权进程（UAC 授权或输入密码后），退出当前非提权进程
                return true;
            }
            catch (System.ComponentModel.Win32Exception w32Ex) when (w32Ex.NativeErrorCode == 1223)
            {
                // 用户在 UAC 对话框中点击了取消或拒绝，降级为普通权限启动
            }
            catch (Exception ex)
            {
                CrashLogger.LogError("启动主动提权请求异常", ex);
            }
        }

        return false;
    }
}
