using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CleanYourPC.Core.Models;
using Microsoft.Win32;

namespace CleanYourPC.Core.Services;

public class StartupAndBrowserScanner
{
    /// <summary>
    /// Windows 任务管理器与资源管理器 (StartupApproved) 核心位置矩阵
    /// 覆盖当前用户 HKCU、本地计算机 64位视图原生项与 32位 WOW64 重定向项
    /// </summary>
    private static readonly (RegistryHive Hive, RegistryView View, string SubPath)[] ApprovedTargetLocations = new[]
    {
        // 1. 当前登录用户配置 (HKCU，任务管理器与 Explorer 优先识别)
        (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
        (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
        (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"),

        // 2. 本地计算机 64 位注册表视图 (HKLM 64-bit)
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),

        // 3. 本地计算机 32 位注册表视图 (HKLM 32-bit WOW64)
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder")
    };

    /// <summary>
    /// 生成符合 Windows 任务管理器与 Explorer 规范的 12 字节“已禁用”二进制标记
    /// Byte 0: 0x03 (已禁用) | Bytes 1-3: 00 00 00 | Bytes 4-11: 当前有效 UTC FILETIME (小端序)
    /// 注意：时间戳不可全为 0，否则任务管理器会判定为无效脏数据并自动回退显示为“已启用”
    /// </summary>
    public static byte[] CreateDisabledBinary()
    {
        byte[] data = new byte[12];
        data[0] = 0x03; // 微软官方状态：03 = 已禁用 (Disabled)
        long fileTime = DateTime.UtcNow.ToFileTimeUtc();
        byte[] timeBytes = BitConverter.GetBytes(fileTime);
        Buffer.BlockCopy(timeBytes, 0, data, 4, 8);
        return data;
    }

    /// <summary>
    /// 生成符合 Windows 任务管理器规范的 12 字节“已启用”二进制标记
    /// </summary>
    public static byte[] CreateEnabledBinary()
    {
        byte[] data = new byte[12];
        data[0] = 0x02; // 微软官方状态：02 = 已启用 (Enabled)
        long fileTime = DateTime.UtcNow.ToFileTimeUtc();
        byte[] timeBytes = BitConverter.GetBytes(fileTime);
        Buffer.BlockCopy(timeBytes, 0, data, 4, 8);
        return data;
    }

    /// <summary>
    /// 精准检查指定的开机启动项是否已经被系统级彻底有效禁用
    /// 必须严格对齐 Windows 任务管理器的实际读取源：
    /// - HKLM 32位程序 (Wow6432Node)：任务管理器仅读取 HKLM\\...\\StartupApproved\\Run32
    /// - HKLM 64位原生程序：任务管理器仅读取 HKLM\\...\\StartupApproved\\Run
    /// - HKCU 程序：任务管理器仅读取 HKCU\\...\\StartupApproved\\Run
    /// 绝对不能跨 Hive 误判（不能因为 HKCU 有标记就误判 HKLM 程序已被禁用）！
    /// </summary>
    /// <summary>
    /// 精准检查指定的开机启动项是否已经被系统级彻底有效禁用
    /// 严格对齐 Windows 任务管理器的实际读取源与优先级：
    /// 1. 优先读取当前用户 HKCU（用户级配置优先级最高）：若 HKCU 存在该项且首字节为奇数则判定为【已禁用】；若首字节为偶数（如 0x02），任务管理器必然显示【已启用】；
    /// 2. 若 HKCU 未覆盖，再回退查阅 HKLM（包含 64 位与 Wow6432Node Run32/Run 权威键）；
    /// </summary>
    public static bool IsStartupItemDisabled(string hivePrefix, string subPath, string valName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(valName)) return false;

        bool isHklm = hivePrefix.Equals("HKLM", StringComparison.OrdinalIgnoreCase) ||
                      subPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);
        bool is32Bit = subPath.Contains("Wow6432Node", StringComparison.OrdinalIgnoreCase);

        // 整理所有关联别名
        var checkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { valName };
        if (valName.Contains("Sysdiag", StringComparison.OrdinalIgnoreCase) || valName.Contains("Huorong", StringComparison.OrdinalIgnoreCase) || valName.Contains("HipsTray", StringComparison.OrdinalIgnoreCase))
        {
            checkNames.Add("Sysdiag");
            checkNames.Add("sysdiag");
            checkNames.Add("HipsTray");
            checkNames.Add("hipstray");
            checkNames.Add("火绒安全软件托盘程序");
            checkNames.Add("火绒安全软件");
        }
        if (valName.Contains("360", StringComparison.OrdinalIgnoreCase) || valName.Contains("Safetray", StringComparison.OrdinalIgnoreCase) || valName.Contains("Tianqing", StringComparison.OrdinalIgnoreCase))
        {
            checkNames.Add("360Safetray");
            checkNames.Add("360safetray");
            checkNames.Add("360SafeTray");
            checkNames.Add("360Safe");
            checkNames.Add("360Tray");
            checkNames.Add("360tray");
            checkNames.Add("360安全卫士");
            checkNames.Add("360安全卫士 安全防护中心模块");
            checkNames.Add("TQTray");
        }

        // 步骤 1：优先检查 HKCU（任务管理器首要查阅点）
        string[] hkcuSubPaths = is32Bit
            ? new[] { @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run" }
            : new[] { @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run" };

        foreach (var hkcuPath in hkcuSubPaths)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(hkcuPath, writable: false);
                if (key != null)
                {
                    foreach (var name in checkNames)
                    {
                        var val = key.GetValue(name);
                        if (val is byte[] bytes && bytes.Length >= 12)
                        {
                            if (bytes[0] % 2 != 0)
                            {
                                long fileTime = BitConverter.ToInt64(bytes, 4);
                                if (fileTime > 0) return true; // HKCU 中确凿已被禁用
                            }
                            else
                            {
                                return false; // HKCU 中明确标记为 0x02 启用，任务管理器直接显示【已启用】
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // 步骤 2：若 HKCU 未定义且属于 HKLM 项，回退检查 HKLM
        if (isHklm)
        {
            var hklmChecks = is32Bit
                ? new[]
                {
                    (RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
                    (RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
                    (RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
                    (RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
                    (RegistryView.Registry64, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
                    (RegistryView.Registry64, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")
                }
                : new[]
                {
                    (RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
                    (RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")
                };

            foreach (var (view, path) in hklmChecks)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(path, writable: false);
                    if (key != null)
                    {
                        foreach (var name in checkNames)
                        {
                            var val = key.GetValue(name);
                            if (val is byte[] bytes && bytes.Length >= 12)
                            {
                                if (bytes[0] % 2 != 0)
                                {
                                    long fileTime = BitConverter.ToInt64(bytes, 4);
                                    if (fileTime > 0) return true;
                                }
                                else
                                {
                                    return false; // HKLM 明确为启用
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        return false;
    }

    public static bool IsStartupItemDisabled(string valName)
    {
        return IsStartupItemDisabled("HKLM", "Software\\Wow6432Node\\Run", valName) ||
               IsStartupItemDisabled("HKLM", "Software\\Run", valName) ||
               IsStartupItemDisabled("HKCU", "Software\\Run", valName);
    }

    private static void ExecuteRegCommand(string arguments)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch { }
    }

    /// <summary>
    /// 将指定的启动项通过 Windows 官方 StartupApproved 全矩阵机制与受信任工具彻底禁用，并清理所有 Run/RunOnce 键值
    /// 确保任务管理器“启动”列表中状态确凿变更为【已禁用】且开机绝对不拉起
    /// </summary>
    public static bool DisableStartupItem(string hivePrefix, string subPath, string valName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(valName)) return false;

        byte[] disabledBytes = CreateDisabledBinary();
        string hexStr = BitConverter.ToString(disabledBytes).Replace("-", "");

        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { valName };

        // 针对火绒安全软件，全量补齐常见托盘启动项别名
        if (valName.Contains("Sysdiag", StringComparison.OrdinalIgnoreCase) ||
            valName.Contains("Huorong", StringComparison.OrdinalIgnoreCase) ||
            valName.Contains("HipsTray", StringComparison.OrdinalIgnoreCase))
        {
            candidateNames.Add("Sysdiag");
            candidateNames.Add("sysdiag");
            candidateNames.Add("HipsTray");
            candidateNames.Add("hipstray");
            candidateNames.Add("sysdiag.exe");
            candidateNames.Add("HipsTray.exe");
            candidateNames.Add("火绒安全软件托盘程序");
            candidateNames.Add("火绒安全软件");
        }

        // 针对 360 / 天擎，全量补齐常见托盘启动项别名
        if (valName.Contains("360", StringComparison.OrdinalIgnoreCase) ||
            valName.Contains("Safetray", StringComparison.OrdinalIgnoreCase) ||
            valName.Contains("Tianqing", StringComparison.OrdinalIgnoreCase) ||
            valName.Contains("TQTray", StringComparison.OrdinalIgnoreCase))
        {
            candidateNames.Add("360Safetray");
            candidateNames.Add("360safetray");
            candidateNames.Add("360SafeTray");
            candidateNames.Add("360Safe");
            candidateNames.Add("360safe");
            candidateNames.Add("360Tray");
            candidateNames.Add("360tray");
            candidateNames.Add("360tray.exe");
            candidateNames.Add("360safetray.exe");
            candidateNames.Add("360安全卫士");
            candidateNames.Add("360安全卫士 安全防护中心模块");
            candidateNames.Add("TQTray");
            candidateNames.Add("tqtray");
        }

        // 1. 系统级受信任原生工具 reg.exe 执行强制写入与删除（穿透杀软对第三方未签名程序的写拦截）
        ExecuteRegCommand("add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run32\" /f /reg:64");
        ExecuteRegCommand("add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run\" /f /reg:64");
        ExecuteRegCommand("add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run32\" /f");
        ExecuteRegCommand("add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run\" /f");

        foreach (var name in candidateNames)
        {
            // 写入任务管理器对应的核心键
            ExecuteRegCommand($"add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run32\" /v \"{name}\" /t REG_BINARY /d {hexStr} /f /reg:64");
            ExecuteRegCommand($"add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run\" /v \"{name}\" /t REG_BINARY /d {hexStr} /f /reg:64");
            ExecuteRegCommand($"add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run32\" /v \"{name}\" /t REG_BINARY /d {hexStr} /f");
            ExecuteRegCommand($"add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run\" /v \"{name}\" /t REG_BINARY /d {hexStr} /f");

            // 物理删除 Run 启动项
            ExecuteRegCommand($"delete \"HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run\" /v \"{name}\" /f /reg:64");
            ExecuteRegCommand($"delete \"HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run\" /v \"{name}\" /f /reg:32");
            ExecuteRegCommand($"delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run\" /v \"{name}\" /f /reg:64");
            ExecuteRegCommand($"delete \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run\" /v \"{name}\" /f");
        }

        // 穿透覆盖 HKEY_USERS 所有活跃登录用户配置（防止任务管理器在特定提权用户会话下读漏）
        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                {
                    string userRun = $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
                    string userRun32 = $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
                    ExecuteRegCommand($"add \"HKU\\{userRun}\" /f");
                    ExecuteRegCommand($"add \"HKU\\{userRun32}\" /f");
                    foreach (var name in candidateNames)
                    {
                        ExecuteRegCommand($"add \"HKU\\{userRun}\" /v \"{name}\" /t REG_BINARY /d {hexStr} /f");
                        ExecuteRegCommand($"add \"HKU\\{userRun32}\" /v \"{name}\" /t REG_BINARY /d {hexStr} /f");
                    }
                    try
                    {
                        using var uKey = Registry.Users.OpenSubKey(userRun, writable: true);
                        if (uKey != null) foreach (var name in candidateNames) uKey.SetValue(name, disabledBytes, RegistryValueKind.Binary);
                    }
                    catch { }
                    try
                    {
                        using var uKey32 = Registry.Users.OpenSubKey(userRun32, writable: true);
                        if (uKey32 != null) foreach (var name in candidateNames) uKey32.SetValue(name, disabledBytes, RegistryValueKind.Binary);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 2. C# 原生 API 全矩阵双向写入 StartupApproved
        bool anySuccess = false;
        foreach (var (hive, view, approvedTarget) in ApprovedTargetLocations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appKey = baseKey.CreateSubKey(approvedTarget, writable: true);
                if (appKey != null)
                {
                    foreach (var name in candidateNames)
                    {
                        appKey.SetValue(name, disabledBytes, RegistryValueKind.Binary);
                        anySuccess = true;
                    }
                }
            }
            catch { }
        }

        // 3. 从全系统 Run 键物理删除并空值化
        string[] runSubPaths = new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\RunOnce"
        };
        var runRoots = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32)
        };
        foreach (var (hive, view) in runRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (var runPath in runSubPaths)
                {
                    try
                    {
                        using var runKey = baseKey.OpenSubKey(runPath, writable: true);
                        if (runKey != null)
                        {
                            foreach (var name in candidateNames)
                            {
                                try { runKey.DeleteValue(name, throwOnMissingValue: false); } catch { }
                                try
                                {
                                    if (runKey.GetValue(name) != null)
                                    {
                                        runKey.SetValue(name, "", RegistryValueKind.String);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        return anySuccess;
    }

    public static bool DisableStartupItem(string valName)
    {
        return DisableStartupItem("HKLM", @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", valName);
    }

    /// <summary>
    /// 将指定的启动项在 StartupApproved 中恢复为启用状态 (02 00 00 00 ...)
    /// </summary>
    public static void EnableStartupItem(string valName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(valName)) return;

        byte[] enabledBytes = CreateEnabledBinary();

        foreach (var (hive, view, subPath) in ApprovedTargetLocations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appKey = baseKey.OpenSubKey(subPath, writable: true);
                if (appKey != null)
                {
                    var matchNames = appKey.GetValueNames()
                        .Where(n => n.Equals(valName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var mn in matchNames)
                    {
                        appKey.SetValue(mn, enabledBytes, RegistryValueKind.Binary);
                    }
                }
            }
            catch { }
        }
    }

    public static void EnableStartupItem(string hivePrefix, string subPath, string valName)
    {
        EnableStartupItem(valName);
    }

    /// <summary>
    /// 判断是否属于 Windows 原生核心系统自启组件（如 Windows Defender 安全中心、CTF 语言输入指示器等）
    /// </summary>
    public static bool IsNativeWindowsSystemComponent(string cmd, string valName)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;

        // 仅保留 Windows 操作系统底层自带的系统核心守护组件，其余任何第三方软件一律不作为系统组件放行
        if (cmd.Contains("SecurityHealthSystray.exe", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("ctfmon.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 扫描当前用户与系统 Run/RunOnce 注册表启动项，识别所有非系统的第三方自启项与失效残留
    /// </summary>
    public List<ScanItem> ScanStartupItems()
    {
        var results = new List<ScanItem>();
        var runPaths = new (RegistryKey Root, string Path, string Prefix, bool NeedsAdmin, bool Is32Bit)[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU", false, false),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM", true, false),
            (Registry.LocalMachine, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM", true, true),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU", false, false),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM", true, false),
            (Registry.LocalMachine, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM", true, true)
        };

        foreach (var (root, subPath, prefix, needsAdmin, is32Bit) in runPaths)
        {
            using var key = root.OpenSubKey(subPath, writable: false);
            if (key == null) continue;

            foreach (var valName in key.GetValueNames())
            {
                var cmd = key.GetValue(valName)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(cmd)) continue;

                // 仅放行 Windows 原生系统核心应用，其余所有第三方软件自启全部纳入治理
                if (IsNativeWindowsSystemComponent(cmd, valName))
                    continue;

                bool isDisabled = IsStartupItemDisabled(prefix, subPath, valName);

                var (exePath, _) = SoftwareUninstallService.ParseCommandLine(Environment.ExpandEnvironmentVariables(cmd));
                bool isOrphaned = Path.IsPathRooted(exePath) && !File.Exists(exePath);

                string title = isOrphaned
                    ? $"清理无效的开机启动项残留: {valName}"
                    : (isDisabled ? $"彻底清除已禁用自启残留: {valName}" : $"禁用非系统开机自启动: {valName}");

                string desc = isOrphaned
                    ? $"该启动项指向的文件已不存在 ({exePath})，属于软件卸载残留。"
                    : (isDisabled
                        ? $"非系统自启程序: {cmd}。当前在系统启动策略中已标记为禁用，但该启动项在注册表 Run 键中仍有物理残留。建议彻底清理注册表项以保持系统纯净。"
                        : $"非系统自启程序: {cmd}。开机自动运行会占用系统内存与CPU资源，拖慢开机速度。建议彻底禁用以确保仅系统应用运行。");

                string uniqueRuleId = $"STARTUP-{prefix}-{$"{subPath}\\{valName}".GetHashCode():X}";

                results.Add(new ScanItem
                {
                    RuleId = uniqueRuleId,
                    Title = title,
                    Category = "开机启动项治理",
                    Description = desc,
                    CurrentState = isOrphaned ? "已失效残留" : (isDisabled ? "已在策略中禁用(Run残留)" : "已启用自启"),
                    TargetState = isOrphaned ? "移除无效启动项" : "彻底禁用并清理",
                    Risk = RiskLevel.UserConfirm,
                    RequiresAdmin = needsAdmin,
                    RequiresUserConfirm = true,
                    SupportsRestore = true,
                    IsSelected = isOrphaned || !isDisabled,
                    AssociatedRule = new CleanRule
                    {
                        Id = uniqueRuleId,
                        Name = title,
                        Category = "开机启动项治理",
                        Risk = RiskLevel.UserConfirm,
                        RequiresAdmin = needsAdmin,
                        RequiresUserConfirm = true,
                        SupportsRestore = true,
                        ActionType = RuleActionType.RegistryValue,
                        TargetPath = $@"{prefix}\{subPath}",
                        ValueName = valName,
                        ExpectedCleanValue = "__DELETE__"
                    }
                });
            }
        }
        return results;
    }

    /// <summary>
    /// 扫描 Windows 开始菜单“启动”物理文件夹中的第三方自启动快捷方式
    /// </summary>
    public List<ScanItem> ScanStartupFolders()
    {
        var results = new List<ScanItem>();
        if (!OperatingSystem.IsWindows()) return results;

        var startupDirs = new (string Path, string ScopeName, bool NeedsAdmin)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "当前用户启动文件夹", false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "系统公共启动文件夹", true)
        };

        foreach (var (dir, scopeName, needsAdmin) in startupDirs)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                var files = Directory.GetFiles(dir);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    string ruleId = $"STARTUP-FOLDER-{scopeName.GetHashCode():X}-{fileName.GetHashCode():X}";

                    results.Add(new ScanItem
                    {
                        RuleId = ruleId,
                        Title = $"清理启动文件夹第三方自启: {fileName}",
                        Category = "开机启动项治理",
                        Description = $"位于【{scopeName}】的第三方自启快捷方式: {file}。开机登录后会自动拉起该程序。建议清理以实现仅系统应用自启。",
                        CurrentState = "常驻启动文件夹",
                        TargetState = "移出启动文件夹",
                        Risk = RiskLevel.UserConfirm,
                        RequiresAdmin = needsAdmin,
                        RequiresUserConfirm = true,
                        SupportsRestore = true,
                        IsSelected = true,
                        AssociatedRule = new CleanRule
                        {
                            Id = ruleId,
                            Name = $"清理启动文件夹第三方自启: {fileName}",
                            Category = "开机启动项治理",
                            Risk = RiskLevel.UserConfirm,
                            RequiresAdmin = needsAdmin,
                            RequiresUserConfirm = true,
                            SupportsRestore = true,
                            ActionType = RuleActionType.RemoveResidualDirectory,
                            TargetPath = file
                        }
                    });
                }
            }
            catch { }
        }

        return results;
    }

    /// <summary>
    /// 清除 Chrome / Edge 等基于 Chromium 的浏览器中已被恶意授权的所有网页弹窗通知权限
    /// </summary>
    public static (bool Success, string Message) ClearBrowserNotifications(string prefPath)
    {
        try
        {
            if (!File.Exists(prefPath))
                return (false, "浏览器配置文件不存在。");

            string originalJson = File.ReadAllText(prefPath);
            var node = System.Text.Json.Nodes.JsonNode.Parse(originalJson);
            if (node == null)
                return (false, "解析浏览器配置文件失败。");

            var notificationsNode = node["profile"]?["content_settings"]?["exceptions"]?["notifications"];
            if (notificationsNode is System.Text.Json.Nodes.JsonObject notifObj)
            {
                int count = notifObj.Count;
                notifObj.Clear();
                File.WriteAllText(prefPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return (true, $"已成功撤销 {count} 个网站的弹窗通知授权。");
            }

            return (true, "无需清理或通知权限列表已为空。");
        }
        catch (Exception ex)
        {
            return (false, $"清理弹窗通知权限异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查 Edge 与 Chrome 浏览器配置文件中是否开启了第三方网站的桌面通知权限（网页右下角弹窗广告源头）
    /// </summary>
    public List<ScanItem> ScanBrowserNotificationPermissions()
    {
        var results = new List<ScanItem>();
        var browserPrefs = new (string BrowserName, string PrefPath)[]
        {
            ("Microsoft Edge", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\User Data\Default\Preferences")),
            ("Google Chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data\Default\Preferences"))
        };

        foreach (var (browser, prefPath) in browserPrefs)
        {
            if (!File.Exists(prefPath)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(prefPath));
                if (doc.RootElement.TryGetProperty("profile", out var profile) &&
                    profile.TryGetProperty("content_settings", out var settings) &&
                    settings.TryGetProperty("exceptions", out var exceptions) &&
                    exceptions.TryGetProperty("notifications", out var notifications))
                {
                    int allowedSites = 0;
                    foreach (var prop in notifications.EnumerateObject())
                    {
                        if (prop.Value.TryGetProperty("setting", out var settingVal) && settingVal.GetInt32() == 1)
                        {
                            allowedSites++;
                        }
                    }

                    if (allowedSites > 0)
                    {
                        results.Add(new ScanItem
                        {
                            RuleId = $"BROWSER-NOTIFY-{browser.Replace(" ", "")}",
                            Title = $"{browser} 存在 {allowedSites} 个允许弹窗通知的网站",
                            Category = "浏览器弹窗治理",
                            Description = "恶意网站常在访问时诱导点击“允许”以获取桌面通知权限，进而在右下角持续推送广告弹窗。治理将一键撤销这些网站的弹窗推送授权。",
                            CurrentState = $"已授权 {allowedSites} 个网站通知",
                            TargetState = "撤销所有网站弹窗通知授权",
                            Risk = RiskLevel.UserConfirm,
                            RequiresAdmin = false,
                            RequiresUserConfirm = true,
                            SupportsRestore = true,
                            IsSelected = false,
                            AssociatedRule = new CleanRule
                            {
                                Id = $"BROWSER-NOTIFY-{browser.Replace(" ", "")}",
                                Name = $"清除 {browser} 网站弹窗通知授权",
                                Category = "浏览器弹窗治理",
                                Risk = RiskLevel.UserConfirm,
                                RequiresAdmin = false,
                                RequiresUserConfirm = true,
                                SupportsRestore = true,
                                ActionType = RuleActionType.CleanBrowserNotifications,
                                TargetPath = prefPath,
                                ExpectedCleanValue = "Cleared"
                            }
                        });
                    }
                }
            }
            catch { /* 忽略正在锁定的文件或格式差异 */ }
        }
        return results;
    }
}