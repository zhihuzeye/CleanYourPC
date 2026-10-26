using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CleanYourPC.Core.Models;

namespace CleanYourPC.Core.Services;

public static class CrashLogger
{
    private static readonly object _lock = new();
    private static SystemEnvironmentSummary? _cachedEnvSummary;

    public const string ErrorLogFileName = "error.txt";
    public const string RunningLogFileName = "log.txt";

    /// <summary>
    /// 记录运行执行日志，持久保留追加至运行程序项目目录下的 log.txt
    /// </summary>
    public static string LogRunning(string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var formattedLine = $"[{timestamp}] {message}";
        return AppendToLogFile(RunningLogFileName, formattedLine + Environment.NewLine);
    }

    /// <summary>
    /// 设置全局系统环境基线快照，后续所有错误和崩溃日志都将自动带上该详尽环境诊断信息
    /// </summary>
    public static void SetEnvironmentSummary(SystemEnvironmentSummary? summary)
    {
        lock (_lock)
        {
            _cachedEnvSummary = summary;
        }
    }

    /// <summary>
    /// 获取当前程序实际运行所在的物理目录（智能识别 SingleFile 单文件环境与 dotnet.exe 调试环境）
    /// </summary>
    public static string GetAppDirectory()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                var fileName = Path.GetFileNameWithoutExtension(processPath);
                if (!fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = Path.GetDirectoryName(processPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        return dir;
                }
            }
        }
        catch { }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 获取所有可能的程序运行目录与项目根目录候选列表，确保能百分百命中用户眼前的项目目录
    /// </summary>
    public static List<string> GetCandidateLogDirectories()
    {
        var dirs = new List<string>();

        // 1. 当前工作目录（用户在终端启动、快捷方式启动或项目根目录工作空间）
        try
        {
            var curDir = Environment.CurrentDirectory;
            if (!string.IsNullOrWhiteSpace(curDir) && Directory.Exists(curDir))
            {
                dirs.Add(curDir);
            }
        }
        catch { }

        // 2. 程序执行基础目录
        try
        {
            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir))
            {
                dirs.Add(baseDir);
            }
        }
        catch { }

        // 3. 物理可执行文件所在目录
        try
        {
            var appDir = GetAppDirectory();
            if (!string.IsNullOrWhiteSpace(appDir) && Directory.Exists(appDir))
            {
                dirs.Add(appDir);
            }
        }
        catch { }

        // 4. 向上查找项目根目录（特征：包含 CleanYourPC.sln 或 rules 文件夹）
        try
        {
            var searchOrigins = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory, GetAppDirectory() };
            foreach (var origin in searchOrigins)
            {
                if (string.IsNullOrWhiteSpace(origin) || !Directory.Exists(origin)) continue;

                var dirInfo = new DirectoryInfo(origin);
                int depth = 0;
                while (dirInfo != null && depth < 6)
                {
                    if (File.Exists(Path.Combine(dirInfo.FullName, "CleanYourPC.sln")) ||
                        Directory.Exists(Path.Combine(dirInfo.FullName, "rules")))
                    {
                        dirs.Add(dirInfo.FullName);
                        break;
                    }
                    dirInfo = dirInfo.Parent;
                    depth++;
                }
            }
        }
        catch { }

        // 5. 兜底回退目录：系统 LocalAppData
        try
        {
            var localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CleanYourPC", "Logs");
            dirs.Add(localAppData);
        }
        catch { }

        // 规范化并去重
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            try
            {
                var full = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (seen.Add(full))
                {
                    result.Add(full);
                }
            }
            catch { }
        }

        return result;
    }

    /// <summary>
    /// 将结构化的系统环境信息格式化追加到日志缓冲区中
    /// </summary>
    private static void AppendEnvironmentSection(StringBuilder sb, SystemEnvironmentSummary? env)
    {
        env ??= _cachedEnvSummary;

        sb.AppendLine("【系统环境诊断信息】");
        if (env != null)
        {
            sb.AppendLine($"  * 操作系统: {env.OSCaption} (版本号: {env.OSBuild}, 内核类型: {env.OSType})");
            sb.AppendLine($"  * 运行权限: {(env.IsAdmin ? "管理员权限 (Administrator - 完全控制)" : "标准普通用户权限 (未提权 - 部分系统项可能受限)")}");
            sb.AppendLine($"  * 安全防护/杀毒软件: {(env.ActiveAntivirus.Count > 0 ? string.Join(", ", env.ActiveAntivirus) : "系统原生防护 (无第三方杀毒软件)")}");
            sb.AppendLine($"  * 已安装输入法: {(env.InstalledIMEs.Count > 0 ? string.Join(", ", env.InstalledIMEs) : "未知/默认输入法")}");
            sb.AppendLine($"  * WPS Office 状态: {(string.IsNullOrEmpty(env.WpsVersion) ? "未安装" : $"已安装 ({env.WpsVersion})")}");
        }
        else
        {
            sb.AppendLine($"  * 操作系统基线: {Environment.OSVersion.VersionString}");
            sb.AppendLine($"  * 运行权限: {(Environment.UserName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) || Environment.UserName.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ? "高权限模式" : "当前用户会话")}");
        }

        sb.AppendLine($"  * 宿主架构: {RuntimeInformation.OSArchitecture} (进程架构: {RuntimeInformation.ProcessArchitecture}, 64位进程: {Environment.Is64BitProcess})");
        sb.AppendLine($"  * .NET 运行时: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"  * 计算机名称: {Environment.MachineName} | 当前用户名: {Environment.UserName}");
        sb.AppendLine($"  * 当前工作目录: {Environment.CurrentDirectory}");
        sb.AppendLine($"  * 物理可执行程序路径: {Environment.ProcessPath}");
        sb.AppendLine($"  * AppContext 基础目录: {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// 将崩溃或异常错误详细日志可靠地写入运行程序项目目录下的 crash_error.log（多重有效目录同时落盘）
    /// </summary>
    public static string LogError(string context, Exception? ex, string additionalInfo = "", SystemEnvironmentSummary? env = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"[崩溃排查日志] 触发时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"异常上下文: {context}");
        sb.AppendLine();

        // 1. 注入详尽系统环境信息
        AppendEnvironmentSection(sb, env);
        sb.AppendLine();

        // 2. 附加信息
        if (!string.IsNullOrWhiteSpace(additionalInfo))
        {
            sb.AppendLine("【附加诊断上下文】");
            sb.AppendLine(additionalInfo);
            sb.AppendLine();
        }

        // 3. 详细异常信息与全层级 InnerException 堆栈展开
        if (ex != null)
        {
            sb.AppendLine("【异常详细堆栈】");
            sb.AppendLine($"异常类型: {ex.GetType().FullName}");
            sb.AppendLine($"错误消息: {ex.Message}");
            sb.AppendLine($"调用堆栈:\n{ex.StackTrace}");

            var inner = ex.InnerException;
            int depth = 1;
            while (inner != null && depth <= 8)
            {
                sb.AppendLine();
                sb.AppendLine($"--> 内部异常 [{depth++}]: {inner.GetType().FullName}");
                sb.AppendLine($"    错误消息: {inner.Message}");
                sb.AppendLine($"    调用堆栈:\n{inner.StackTrace}");
                inner = inner.InnerException;
            }
        }
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        return FlushToLogFiles(sb.ToString());
    }

    /// <summary>
    /// 专门记录“清理执行详细报错日志”：完整记录系统环境、勾选清除的项目清单、所有失败项目的目标路径与具体报错原因
    /// </summary>
    public static string LogCleanExecutionDetails(
        SystemEnvironmentSummary? env,
        List<ScanItem> attemptedItems,
        List<ExecutionItemResult> results,
        Exception? fatalException = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"[清理执行报错排查日志] 触发时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");

        int totalCount = attemptedItems.Count;
        int failedCount = results.Count(r => !r.Success);
        int successCount = results.Count(r => r.Success);
        bool hasInterruption = fatalException != null;

        sb.AppendLine($"清理摘要: 勾选总数 {totalCount} 项 | 成功: {successCount} 项 | 失败: {failedCount} 项 | 运行状态: {(hasInterruption ? "主流程发生异常中断" : (failedCount > 0 ? "部分项目清理报错" : "全部成功"))}");
        sb.AppendLine();

        // 1. 完整输出系统环境信息
        AppendEnvironmentSection(sb, env);
        sb.AppendLine();

        // 2. 致命中断异常（若有）
        if (fatalException != null)
        {
            sb.AppendLine("【主流程中断致命异常】");
            sb.AppendLine($"异常类型: {fatalException.GetType().FullName}");
            sb.AppendLine($"错误消息: {fatalException.Message}");
            sb.AppendLine($"调用堆栈:\n{fatalException.StackTrace}");

            var inner = fatalException.InnerException;
            int depth = 1;
            while (inner != null && depth <= 8)
            {
                sb.AppendLine($"--> 内部异常 [{depth++}]: {inner.GetType().FullName} - {inner.Message}");
                sb.AppendLine($"    堆栈:\n{inner.StackTrace}");
                inner = inner.InnerException;
            }
            sb.AppendLine();
        }

        // 3. 详细列出：清除什么东西报错（所有失败项目的详细档案与报错原因）
        var failedResults = results.Where(r => !r.Success).ToList();
        if (failedResults.Count > 0)
        {
            sb.AppendLine($"【清除失败项明细清单 (共 {failedResults.Count} 项报错)】");
            int idx = 1;
            foreach (var fail in failedResults)
            {
                var matchedItem = attemptedItems.FirstOrDefault(i => i.RuleId == fail.RuleId);
                var rule = matchedItem?.AssociatedRule;
                var soft = matchedItem?.AssociatedSoftware;

                sb.AppendLine($"--------------------------------------------------------------------------------");
                sb.AppendLine($"[失败项 #{idx++}] {fail.Title}");
                sb.AppendLine($"  * 规则编号: {fail.RuleId}");
                sb.AppendLine($"  * 治理分类: {fail.Category}");
                sb.AppendLine($"  * 失败报错消息: {fail.Message}");
                sb.AppendLine($"  * 清理前状态: {fail.BeforeValue}");

                if (rule != null)
                {
                    sb.AppendLine($"  * 操作类型 (ActionType): {rule.ActionType}");
                    sb.AppendLine($"  * 清理目标物理路径 (TargetPath): {rule.TargetPath}");
                    if (!string.IsNullOrEmpty(rule.ValueName))
                        sb.AppendLine($"  * 目标键名/参数 (ValueName): {rule.ValueName}");
                    sb.AppendLine($"  * 目标预期值 (ExpectedCleanValue): {rule.ExpectedCleanValue}");
                    sb.AppendLine($"  * 是否需要管理员权限: {rule.RequiresAdmin}");
                }
                else if (soft != null)
                {
                    sb.AppendLine($"  * 目标软件名称: {soft.DisplayName} ({soft.DisplayVersion})");
                    sb.AppendLine($"  * 软件发布者: {soft.Publisher}");
                    sb.AppendLine($"  * 安装位置: {soft.InstallLocation}");
                    sb.AppendLine($"  * 卸载命令行: {soft.UninstallString}");
                }
                else if (matchedItem != null)
                {
                    sb.AppendLine($"  * 目标状态: {matchedItem.TargetState}");
                    sb.AppendLine($"  * 描述: {matchedItem.Description}");
                    sb.AppendLine($"  * 是否需管理员权限: {matchedItem.RequiresAdmin}");
                }
            }
            sb.AppendLine($"--------------------------------------------------------------------------------");
            sb.AppendLine();
        }

        // 4. 列出本次触发清理的完整勾选项目清单（方便排查当时是在对哪些东西做清理）
        sb.AppendLine($"【本次勾选清理的完整项目清单 (共 {attemptedItems.Count} 项)】");
        int itemIndex = 1;
        foreach (var item in attemptedItems)
        {
            var res = results.FirstOrDefault(r => r.RuleId == item.RuleId);
            string statusTag = res != null ? (res.Success ? "[成功]" : "[失败]") : "[未完成/被中断]";
            string targetPath = item.AssociatedRule?.TargetPath ?? item.AssociatedSoftware?.InstallLocation ?? item.TargetState;
            sb.AppendLine($"  {itemIndex++}. {statusTag} [{item.Category}] {item.Title} (目标路径: {targetPath})");
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine();

        return FlushToLogFiles(sb.ToString());
    }

    /// <summary>
    /// 将错误日志字符串安全持久刷写到项目目录及所有候选有效目录下的 error.txt 中（支持历史追加保留）
    /// </summary>
    private static string FlushToLogFiles(string logText)
    {
        return AppendToLogFile(ErrorLogFileName, logText);
    }

    /// <summary>
    /// 将文本以追加模式写入各候选目录下的指定文件名中，确保多重冗余落盘与历史数据持久保留
    /// </summary>
    public static string AppendToLogFile(string fileName, string text)
    {
        string primaryLogPath = string.Empty;

        lock (_lock)
        {
            var candidates = GetCandidateLogDirectories();
            foreach (var dir in candidates)
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var logPath = Path.Combine(dir, fileName);
                    File.AppendAllText(logPath, text, Encoding.UTF8);

                    if (string.IsNullOrEmpty(primaryLogPath))
                    {
                        primaryLogPath = logPath;
                    }
                }
                catch
                {
                    // 若此目录只读或受限，继续尝试下一候选目录
                }
            }
        }

        return !string.IsNullOrEmpty(primaryLogPath) ? primaryLogPath : "无法写入磁盘";
    }
}
