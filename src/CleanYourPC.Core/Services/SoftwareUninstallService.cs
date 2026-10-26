using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CleanYourPC.Core.Models;

namespace CleanYourPC.Core.Services;

public class SoftwareUninstallService
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_FLUSH = 0x1000;

    // 经过实战验证的已知广告推广软件残留目录（仅在主程序已卸载或无卸载器时清理）
    private static readonly (string Path, string Name, string Description)[] KnownResidualDirs = new[]
    {
        (@"C:\Program Files\ZipGo", "ZipGo 压缩残留目录", "包含 ZGHookLoader.dll、ZGScreenCore.dll 等弹窗挂钩残留"),
        (@"C:\Program Files (x86)\ProZip", "ProZip 压缩残留目录", "ProZip 卸载后遗留的空目录或托盘配置"),
        (@"C:\Program Files (x86)\LdsMultiWechatA", "LdsMultiWechatA 残留目录", "多开工具遗留目录")
    };

    public List<ScanItem> ScanResiduals()
    {
        var list = new List<ScanItem>();
        foreach (var (path, name, desc) in KnownResidualDirs)
        {
            if (Directory.Exists(path))
            {
                // 检查目录下是否还有官方 uninstall.exe
                var hasUninstaller = Directory.GetFiles(path, "*uninst*.exe", SearchOption.TopDirectoryOnly).Any();
                if (!hasUninstaller)
                {
                    int fileCount = Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length;
                    list.Add(new ScanItem
                    {
                        RuleId = $"RESIDUAL-{Path.GetFileName(path).ToUpperInvariant()}",
                        Title = $"清理残留目录: {name}",
                        Category = "软件残留清理",
                        Description = $"{desc} (检测到 {fileCount} 个残留文件，无官方卸载程序)",
                        CurrentState = $"存在 ({path})",
                        TargetState = "安全移除残留目录",
                        Risk = RiskLevel.UserConfirm,
                        RequiresAdmin = true,
                        RequiresUserConfirm = true,
                        SupportsRestore = false,
                        IsSelected = false,
                        AssociatedRule = new CleanRule
                        {
                            Id = $"RESIDUAL-{Path.GetFileName(path).ToUpperInvariant()}",
                            Name = $"清理 {name}",
                            Category = "软件残留清理",
                            Risk = RiskLevel.UserConfirm,
                            RequiresAdmin = true,
                            RequiresUserConfirm = true,
                            SupportsRestore = false,
                            ActionType = RuleActionType.RemoveResidualDirectory,
                            TargetPath = path
                        }
                    });
                }
            }
        }
        return list;
    }

    /// <summary>
    /// 强力直接卸载目标软件：不调用任何软件自带的冗长挽留引导程序，直接强力终结常驻进程、递归删除安装目录、清理注册表卸载项与桌面快捷方式
    /// </summary>
    public (bool Success, string Message) ForceUninstall(InstalledSoftwareInfo software)
    {
        if (software.IsSystemProtected)
            return (false, $"安全拦截：[{software.DisplayName}] 属于受保护的系统或关键硬件驱动组件，严禁卸载。");

        int killedProcs = 0;
        int deletedFiles = 0;
        bool regDeleted = false;

        // 1. 解析目标安装物理主目录
        string targetDir = ResolveSoftwareDirectory(software);
        bool hasValidDir = !string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir);

        if (hasValidDir && IsDangerousSystemDirectory(targetDir))
        {
            return (false, $"安全防护拦截：目标路径 [{targetDir}] 属于 Windows 系统核心公共目录，严禁整目录删除。");
        }

        try
        {
            // 2. 强力终结正在该目录下运行的所有常驻后台进程
            if (hasValidDir)
            {
                killedProcs = TerminateProcessesInDirectory(targetDir);
            }

            // 3. 强力注销与删除 Windows Uninstall 注册表项（使其从系统“应用与功能”列表中彻底消失）
            if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(software.RegistryKeyPath))
            {
                regDeleted = RemoveUninstallRegistryEntry(software.RegistryKeyPath);
            }

            // 4. 强力递归删除安装目录及其文件（自动解除只读属性，被系统占用的文件标记重启删除）
            if (hasValidDir)
            {
                deletedFiles = ForceDeleteDirectory(targetDir);
            }

            // 5. 清理桌面与开始菜单快捷方式（不管是否有安装物理主目录，均全覆盖清理关联桌面与开始菜单项）
            int cleanedShortcuts = 0;
            if (OperatingSystem.IsWindows())
            {
                cleanedShortcuts = CleanShortcutsAndStartup(targetDir, software.DisplayName, software.Publisher);
            }

            string dirMsg = hasValidDir ? $"已清除安装目录 ({deletedFiles} 个文件)" : "未发现安装物理目录";
            string procMsg = killedProcs > 0 ? $"已终结 {killedProcs} 个常驻进程" : "无运行中进程";
            string regMsg = regDeleted ? "已清除注册表卸载登记" : "未发现注册表卸载项";
            string shortcutMsg = cleanedShortcuts > 0 ? $"已清除 {cleanedShortcuts} 个桌面/开始菜单快捷方式与应用组文件夹" : "桌面与开始菜单无残留快捷方式";

            return (true, $"已彻底直接卸载：{procMsg}，{dirMsg}，{regMsg}，{shortcutMsg}。");
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"直接强制卸载异常 [{software.DisplayName}]", ex, $"Dir: {targetDir}, RegKey: {software.RegistryKeyPath}");
            return (false, $"直接强制卸载遇到异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析软件的物理安装目录（优先从 InstallLocation 取，若无则从 UninstallString 所在目录解析）
    /// </summary>
    public static string ResolveSoftwareDirectory(InstalledSoftwareInfo software)
    {
        if (!string.IsNullOrWhiteSpace(software.InstallLocation) && Directory.Exists(software.InstallLocation))
            return Path.GetFullPath(software.InstallLocation);

        if (!string.IsNullOrWhiteSpace(software.UninstallString))
        {
            var (exe, _) = ParseCommandLine(software.UninstallString);
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    return Path.GetFullPath(dir);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 系统关键目录安全红线检测，严禁误删系统根目录或顶级公共文件夹
    /// </summary>
    public static bool IsDangerousSystemDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return true;
        try
        {
            string rawTrimmed = dir.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\', '/');
            // 硬编码保护已知的通用系统公共顶级目录
            var forbiddenTops = new[]
            {
                @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\Users", @"C:\ProgramData",
                "C:", "D:", "E:", "F:",
                "/", "/bin", "/sbin", "/usr", "/etc", "/var", "/System", "/Library", "/Applications"
            };
            foreach (var f in forbiddenTops)
            {
                var fNorm = f.TrimEnd('\\', '/');
                if (rawTrimmed.Equals(fNorm, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var normalized = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPath = Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";

            // 严禁是分区盘符根路径（如 C: 或 C:\ 或 /）
            if (string.IsNullOrEmpty(normalized) || normalized.Equals(rootPath, StringComparison.OrdinalIgnoreCase)) return true;

            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
            var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd(Path.DirectorySeparatorChar);
            var pfDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd(Path.DirectorySeparatorChar);
            var pf86Dir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd(Path.DirectorySeparatorChar);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop).TrimEnd(Path.DirectorySeparatorChar);

            if (!string.IsNullOrEmpty(winDir) && normalized.Equals(winDir, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(sysDir) && normalized.Equals(sysDir, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(pfDir) && normalized.Equals(pfDir, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(pf86Dir) && normalized.Equals(pf86Dir, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(userProfile) && normalized.Equals(userProfile, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(desktop) && normalized.Equals(desktop, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 强力终结位于指定安装目录下的所有运行中进程
    /// </summary>
    private static int TerminateProcessesInDirectory(string dirPath)
    {
        int killed = 0;
        try
        {
            var fullDirPath = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    string? mainModule = null;
                    try { mainModule = proc.MainModule?.FileName; } catch { }
                    if (!string.IsNullOrEmpty(mainModule) && mainModule.StartsWith(fullDirPath, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(1000);
                        killed++;
                    }
                }
                catch { }
            }
        }
        catch { }
        return killed;
    }

    /// <summary>
    /// 强力递归删除安装目录及其所有文件（自动解除只读，占用文件标记下次重启自动删除）
    /// </summary>
    private static int ForceDeleteDirectory(string dirPath)
    {
        int fileCount = 0;
        try
        {
            var dir = new DirectoryInfo(dirPath);
            if (!dir.Exists) return 0;

            // 1. 去除只读并尝试直接删除所有文件
            foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    file.Attributes = FileAttributes.Normal;
                    file.Delete();
                    fileCount++;
                }
                catch
                {
                    // 若文件被占用无法立即删除，标记 Windows 下次系统重启自动删除
                    if (OperatingSystem.IsWindows())
                    {
                        try { MoveFileEx(file.FullName, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                    }
                }
            }

            // 2. 深度遍历删除所有子文件夹
            foreach (var sub in dir.GetDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length))
            {
                try { sub.Delete(false); } catch { }
            }

            // 3. 尝试直接删除主目录
            try { dir.Delete(true); } catch { }
        }
        catch { }
        return fileCount;
    }

    /// <summary>
    /// 从 Windows 注册表中彻底注销并删除该软件的 Uninstall 登记项
    /// </summary>
    private static bool RemoveUninstallRegistryEntry(string regKeyPath)
    {
        try
        {
            var (root, subKey) = RuleEngine.ParseRegistryPath(regKeyPath);
            root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"清理软件注册表卸载项失败 [{regKeyPath}]", ex);
            return false;
        }
    }

    /// <summary>
    /// 通知 Windows Shell 刷新桌面与开始菜单图标与关联缓存，立即清除失效快捷方式图标
    /// 并重启 StartMenuExperienceHost 强制 Windows 10/11 即刻重建开始菜单所有应用列表
    /// </summary>
    public static void RefreshShellIcons()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // 1. 发送 Windows Shell 图标与文件关联全局变更通知
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);

                // 2. 结束 Windows 10 (1903+) / Windows 11 开始菜单独立渲染宿主 StartMenuExperienceHost
                // 该进程被终结后系统会在几十毫秒内静默自愈重启，从而强制刷新内存中的所有应用列表与磁贴
                try
                {
                    foreach (var proc in Process.GetProcessesByName("StartMenuExperienceHost"))
                    {
                        try { proc.Kill(); } catch { }
                    }
                }
                catch { }

                // 3. 结束早期 Win10 开始菜单与通知中心宿主 ShellExperienceHost
                try
                {
                    foreach (var proc in Process.GetProcessesByName("ShellExperienceHost"))
                    {
                        try { proc.Kill(); } catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 将文本归一化处理（去除所有空格、破折号、标点符号并转小写），用于无损鲁棒匹配
    /// </summary>
    public static string CleanToken(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 智能提取软件名称核心关键字，剥离版本号、架构标识、括号说明、修饰词及各种空格变体
    /// </summary>
    public static List<string> ExtractKeywords(string displayName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(displayName)) return new List<string>();

        var clean = displayName.Trim();
        result.Add(clean);

        // 去除空格形态
        result.Add(clean.Replace(" ", ""));

        // 1. 去除括号说明，如 (x64), (32-bit), (11.8.2.12198), (官方版)
        var noParens = System.Text.RegularExpressions.Regex.Replace(clean, @"\([^)]*\)|\[[^\]]*\]", "").Trim();
        if (!string.IsNullOrWhiteSpace(noParens) && noParens.Length >= 2)
        {
            result.Add(noParens);
            result.Add(noParens.Replace(" ", ""));
        }

        // 2. 去除尾部版本号，如 "WPS Office 2019" -> "WPS Office", "酷狗音乐 10.2.3" -> "酷狗音乐"
        var noVersion = System.Text.RegularExpressions.Regex.Replace(noParens, @"\s+\d+(\.\d+)*.*$", "").Trim();
        if (!string.IsNullOrWhiteSpace(noVersion) && noVersion.Length >= 2)
        {
            result.Add(noVersion);
            result.Add(noVersion.Replace(" ", ""));
        }

        // 3. 去除常见修饰词：专业版、个人版、企业版、家庭版、社区版、极速版、精简版、增强版
        var suffixRegex = new System.Text.RegularExpressions.Regex(@"(专业版|个人版|企业版|家庭版|社区版|极速版|精简版|增强版|正式版|测试版|Beta|Preview)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var noSuffix = suffixRegex.Replace(noVersion, "").Trim();
        if (!string.IsNullOrWhiteSpace(noSuffix) && noSuffix.Length >= 2)
        {
            result.Add(noSuffix);
            result.Add(noSuffix.Replace(" ", ""));
        }

        return result.Where(k => k.Length >= 2).ToList();
    }

    /// <summary>
    /// 检查 LNK 快捷方式二进制字节流中是否包含目标物理安装主目录
    /// </summary>
    public static bool DoesLnkContainTarget(string lnkPath, string? targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir) || targetDir.Length < 4) return false;
        try
        {
            if (!File.Exists(lnkPath)) return false;
            var fi = new FileInfo(lnkPath);
            if (fi.Length > 1024 * 1024) return false; // 忽略超过 1MB 的异常文件

            var bytes = File.ReadAllBytes(lnkPath);
            var ascii = Encoding.ASCII.GetString(bytes);
            if (ascii.Contains(targetDir, StringComparison.OrdinalIgnoreCase)) return true;

            var unicode = Encoding.Unicode.GetString(bytes);
            if (unicode.Contains(targetDir, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 安全列举指定目录及其子目录下的文件，自动忽略无权限访问的受保护系统子目录，杜绝扫描中断
    /// </summary>
    public static IEnumerable<string> SafeEnumerateFiles(string dir, string searchPattern = "*.*")
    {
        var opt = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        try
        {
            if (Directory.Exists(dir))
            {
                return Directory.EnumerateFiles(dir, searchPattern, opt);
            }
        }
        catch { }
        return Enumerable.Empty<string>();
    }

    /// <summary>
    /// 安全列举指定目录下的子目录，自动忽略无权限子目录
    /// </summary>
    public static IEnumerable<string> SafeEnumerateDirectories(string dir, bool recursive = false)
    {
        var opt = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = recursive,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        try
        {
            if (Directory.Exists(dir))
            {
                return Directory.EnumerateDirectories(dir, "*", opt);
            }
        }
        catch { }
        return Enumerable.Empty<string>();
    }

    /// <summary>
    /// 获取系统所有的桌面、开始菜单程序组、公共开始菜单与启动项目录全集
    /// </summary>
    public static List<string> GetCandidateShortcutDirectories()
    {
        var dirs = new List<string>();
        void AddSpecial(Environment.SpecialFolder sf)
        {
            try
            {
                var p = Environment.GetFolderPath(sf);
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) dirs.Add(p);
            }
            catch { }
        }

        AddSpecial(Environment.SpecialFolder.Desktop);
        AddSpecial(Environment.SpecialFolder.CommonDesktopDirectory);
        AddSpecial(Environment.SpecialFolder.StartMenu);
        AddSpecial(Environment.SpecialFolder.CommonStartMenu);
        AddSpecial(Environment.SpecialFolder.Programs);
        AddSpecial(Environment.SpecialFolder.CommonPrograms);
        AddSpecial(Environment.SpecialFolder.Startup);
        AddSpecial(Environment.SpecialFolder.CommonStartup);

        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                var oneDriveDesktop = Path.Combine(userProfile, "OneDrive", "Desktop");
                if (Directory.Exists(oneDriveDesktop)) dirs.Add(oneDriveDesktop);

                var userDesktop = Path.Combine(userProfile, "Desktop");
                if (Directory.Exists(userDesktop)) dirs.Add(userDesktop);
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                var quickLaunch = Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch");
                if (Directory.Exists(quickLaunch)) dirs.Add(quickLaunch);

                var taskBar = Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (Directory.Exists(taskBar)) dirs.Add(taskBar);
            }
        }
        catch { }

        // 去重规范化
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            try
            {
                var full = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (seen.Add(full)) result.Add(full);
            }
            catch { }
        }
        return result;
    }

    /// <summary>
    /// 深度清理桌面、开始菜单以及启动项中所有与目标软件关联的快捷方式（.lnk/.url）及专属应用文件夹
    /// 支持忽略空格的全量模糊匹配，并在清理完成后强制刷新 Windows 10/11 开始菜单所有应用列表
    /// </summary>
    public static int CleanShortcutsAndStartup(string? targetDir, string appName, string? publisher = null)
    {
        int deletedCount = 0;
        try
        {
            var rawKeywords = ExtractKeywords(appName);
            var cleanKeywords = rawKeywords.Select(CleanToken).Where(k => k.Length >= 2).Distinct().ToList();
            var shortcutDirs = GetCandidateShortcutDirectories();

            // 1. 遍历清理所有快捷方式文件（.lnk 和 .url 网页快捷方式）
            foreach (var sDir in shortcutDirs)
            {
                if (!Directory.Exists(sDir)) continue;

                var files = SafeEnumerateFiles(sDir, "*.*")
                    .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var file in files)
                {
                    try
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        var parentDirName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";

                        var cleanFileName = CleanToken(fileName);
                        var cleanParentDir = CleanToken(parentDirName);

                        bool matched = false;

                        // 规则 A: 快捷方式二进制字节流指向目标安装主目录
                        if (!string.IsNullOrWhiteSpace(targetDir) && DoesLnkContainTarget(file, targetDir))
                        {
                            matched = true;
                        }

                        // 规则 B: 快捷方式文件名命中软件核心关键字（包含常规匹配与无空格归一化匹配）
                        if (!matched && (rawKeywords.Any(k => fileName.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
                                         cleanKeywords.Any(k => cleanFileName.Contains(k, StringComparison.OrdinalIgnoreCase))))
                        {
                            matched = true;
                        }

                        // 规则 C: 快捷方式所在的上级文件夹名称命中软件核心关键字（如开始菜单中的应用文件夹）
                        if (!matched && (rawKeywords.Any(k => parentDirName.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
                                         cleanKeywords.Any(k => cleanParentDir.Contains(k, StringComparison.OrdinalIgnoreCase))))
                        {
                            matched = true;
                        }

                        if (matched)
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                            deletedCount++;
                        }
                    }
                    catch { }
                }
            }

            // 2. 清理开始菜单中的应用专属文件夹（整组文件夹删除：若文件夹名命中核心关键字，或文件夹内部已被清空）
            var startProgramsDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            };

            foreach (var pDir in startProgramsDirs)
            {
                if (!Directory.Exists(pDir)) continue;
                try
                {
                    var subDirs = SafeEnumerateDirectories(pDir, recursive: false).ToList();
                    foreach (var sub in subDirs)
                    {
                        try
                        {
                            var folderName = Path.GetFileName(sub);
                            var cleanFolder = CleanToken(folderName);

                            bool matchFolder = rawKeywords.Any(k => folderName.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
                                               cleanKeywords.Any(k => cleanFolder.Contains(k, StringComparison.OrdinalIgnoreCase));

                            bool isEmpty = !Directory.EnumerateFileSystemEntries(sub).Any();

                            if (matchFolder || isEmpty)
                            {
                                // 解除内部文件只读后强力删除整组文件夹
                                var di = new DirectoryInfo(sub);
                                foreach (var f in di.GetFiles("*", SearchOption.AllDirectories))
                                {
                                    try { f.Attributes = FileAttributes.Normal; } catch { }
                                }
                                Directory.Delete(sub, recursive: true);
                                deletedCount++;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // 3. 强制刷新 Windows 图标与开始菜单应用列表缓存
            RefreshShellIcons();
        }
        catch { }

        return deletedCount;
    }

    /// <summary>
    /// 触发官方自带卸载器（备用兼容方法）
    /// </summary>
    public (bool Success, string Message) TriggerOfficialUninstall(InstalledSoftwareInfo software)
    {
        if (software.IsSystemProtected)
            return (false, $"安全拦截：[{software.DisplayName}] 属于受保护的系统或硬件驱动组件，严禁卸载。");

        if (string.IsNullOrWhiteSpace(software.UninstallString))
            return (false, $"未找到 [{software.DisplayName}] 的官方卸载入口。");

        try
        {
            var (fileName, args) = ParseCommandLine(software.UninstallString);
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return (false, "无法启动官方卸载程序。");

            bool exited = proc.WaitForExit(3_000);
            if (exited)
            {
                string exitInfo = "已退出";
                try { exitInfo = $"退出码: {proc.ExitCode}"; } catch { }
                return (true, $"已完成 [{software.DisplayName}] 官方卸载调用（{exitInfo}）。");
            }

            return (true, $"已成功拉起 [{software.DisplayName}] 官方卸载向导，请在弹出的窗口中确认卸载。");
        }
        catch (Exception ex)
        {
            return (false, $"调用官方卸载程序失败: {ex.Message}");
        }
    }

    public static (string FileName, string Arguments) ParseCommandLine(string cmdLine)
    {
        cmdLine = cmdLine.Trim();
        if (cmdLine.StartsWith('"'))
        {
            int endQuote = cmdLine.IndexOf('"', 1);
            if (endQuote > 0)
            {
                var exe = cmdLine.Substring(1, endQuote - 1);
                var args = cmdLine.Substring(endQuote + 1).Trim();
                return (exe, args);
            }
        }

        int exeIndex = cmdLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            var exe = cmdLine.Substring(0, exeIndex + 4).Trim();
            var args = cmdLine.Substring(exeIndex + 4).Trim();
            return (exe, args);
        }

        var parts = cmdLine.Split(' ', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }
}