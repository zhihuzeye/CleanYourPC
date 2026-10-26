using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using CleanYourPC.Core.Models;
using Microsoft.Win32;

namespace CleanYourPC.Core.Services;

public class RuleEngine
{
    private readonly BackupRestoreService _backupService;
    private readonly SoftwareUninstallService _uninstallService;
    private readonly RogueItemScanner _rogueScanner;
    private readonly StartupAndBrowserScanner _startupScanner;

    public RuleEngine(
        BackupRestoreService backupService,
        SoftwareUninstallService uninstallService,
        RogueItemScanner? rogueScanner = null,
        StartupAndBrowserScanner? startupScanner = null)
    {
        _backupService = backupService;
        _uninstallService = uninstallService;
        _rogueScanner = rogueScanner ?? new RogueItemScanner();
        _startupScanner = startupScanner ?? new StartupAndBrowserScanner();
    }

    public List<CleanRule> LoadRules(string pathOrDirectory)
    {
        var rules = new List<CleanRule>();
        if (Directory.Exists(pathOrDirectory))
        {
            var jsonFiles = Directory.GetFiles(pathOrDirectory, "*.json", SearchOption.AllDirectories);
            foreach (var f in jsonFiles)
            {
                if (f.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("report", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    rules.AddRange(LoadRulesFromFile(f));
                }
                catch { }
            }
        }
        else if (File.Exists(pathOrDirectory))
        {
            rules.AddRange(LoadRulesFromFile(pathOrDirectory));
        }

        return rules.GroupBy(r => r.Id).Select(g => g.First()).ToList();
    }

    public List<CleanRule> LoadRulesFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return new List<CleanRule>();
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<List<CleanRule>>(json) ?? new List<CleanRule>();
    }

    public List<ScanItem> EvaluateRules(IEnumerable<CleanRule> rules, SystemEnvironmentSummary env, IEnumerable<InstalledSoftwareInfo> installedApps)
    {
        var items = new List<ScanItem>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            // 1. 匹配平台作用域 (Scope)
            if (rule.Scope != PlatformScope.Common)
            {
                bool matchScope = rule.Scope switch
                {
                    PlatformScope.Windows => env.OSType.Contains("Windows", StringComparison.OrdinalIgnoreCase),
                    PlatformScope.UOS => env.OSType.Contains("UOS", StringComparison.OrdinalIgnoreCase) || env.OSCaption.Contains("UOS", StringComparison.OrdinalIgnoreCase),
                    PlatformScope.Kylin => env.OSType.Contains("Kylin", StringComparison.OrdinalIgnoreCase) || env.OSCaption.Contains("Kylin", StringComparison.OrdinalIgnoreCase),
                    PlatformScope.Deepin => env.OSType.Contains("Deepin", StringComparison.OrdinalIgnoreCase) || env.OSCaption.Contains("Deepin", StringComparison.OrdinalIgnoreCase),
                    PlatformScope.HarmonyOS => env.OSType.Contains("Harmony", StringComparison.OrdinalIgnoreCase) || env.OSCaption.Contains("Harmony", StringComparison.OrdinalIgnoreCase),
                    _ => true
                };
                if (!matchScope) continue;
            }

            // 2. 匹配操作系统具体版本支持范围（Windows 10/11，银河麒麟、统信 UOS、深度 Deepin、华为鸿蒙等）
            if (rule.SupportedOS.Count > 0 && !rule.SupportedOS.Contains("*"))
            {
                bool matchesOS = rule.SupportedOS.Any(os =>
                    env.OSCaption.Contains(os, StringComparison.OrdinalIgnoreCase) ||
                    env.OSType.Contains(os, StringComparison.OrdinalIgnoreCase));
                if (!matchesOS) continue;
            }

            // 按需匹配前置条件（如输入法规则仅在安装了对应输入法或存在对应注册表项时才报告）
            if (rule.Category == "输入法广告" && !DoesRegistryKeyExist(rule.TargetPath))
                continue;

            var (isIssue, currentVal) = CheckRuleState(rule);
            if (!isIssue) continue;

            items.Add(new ScanItem
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Description = $"来源与验证: {rule.SourceAndVerification}",
                CurrentState = currentVal,
                TargetState = rule.ExpectedCleanValue,
                Risk = rule.Risk,
                RequiresAdmin = rule.RequiresAdmin,
                RequiresUserConfirm = rule.RequiresUserConfirm,
                SupportsRestore = rule.SupportsRestore,
                // A类和免确认的B类默认勾选；C类（需确认）默认不勾选，等待用户授权
                IsSelected = (rule.Risk == RiskLevel.AutoSafe || rule.Risk == RiskLevel.AdminRequired) && !rule.RequiresUserConfirm,
                AssociatedRule = rule
            });
        }

        // 加入目标卸载软件（360、腾讯电脑管家等 C 类需用户确认项目）
        foreach (var app in installedApps.Where(a => a.IsTargetSecurityOrBloatware && !a.IsSystemProtected))
        {
            items.Add(new ScanItem
            {
                RuleId = $"UNINSTALL-{app.DisplayName.GetHashCode():X}",
                Title = $"卸载软件: {app.DisplayName} ({app.DisplayVersion})",
                Category = "目标软件卸载",
                Description = $"{app.RiskReason} | 发布者: {app.Publisher}",
                CurrentState = "已安装",
                TargetState = "强力直接卸载（终结进程/清除目录与注册表，不调用自带卸载器）",
                Risk = RiskLevel.UserConfirm,
                RequiresAdmin = true, // 彻底删除 Program Files 安装目录与系统注册表卸载项需要管理员权限
                RequiresUserConfirm = true,
                SupportsRestore = false,
                IsSelected = false,
                AssociatedSoftware = app
            });
        }

        // 加入残留文件夹扫描结果
        items.AddRange(_uninstallService.ScanResiduals());

        // 仅在 Windows 环境下启用深度多余项目专项治理（右键菜单、此电脑入口、桌面快捷方式篡改、开机自启残留、浏览器弹窗）
        if (OperatingSystem.IsWindows() || env.OSType.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            try { items.AddRange(_rogueScanner.ScanContextMenus()); } catch { }
            try { items.AddRange(_rogueScanner.ScanExplorerNamespaces()); } catch { }
            try { items.AddRange(_rogueScanner.ScanDesktopShortcutHijacks()); } catch { }
            try { items.AddRange(_rogueScanner.ScanBrowserPolicyExtensions()); } catch { }
            try { items.AddRange(_startupScanner.ScanStartupItems()); } catch { }
            try { items.AddRange(_startupScanner.ScanStartupFolders()); } catch { }
            try { items.AddRange(_startupScanner.ScanBrowserNotificationPermissions()); } catch { }
            try { items.AddRange(_rogueScanner.ScanStartMenuAndDesktopResiduals()); } catch { }
        }

        return items;
    }

    public (bool IsIssue, string CurrentValue) CheckRuleState(CleanRule rule)
    {
        try
        {
            switch (rule.ActionType)
            {
                case RuleActionType.RegistryValue:
                    if ((rule.TargetPath.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase) || rule.TargetPath.EndsWith(@"\RunOnce", StringComparison.OrdinalIgnoreCase))
                        && rule.ExpectedCleanValue == "__DELETE__")
                    {
                        var (rootK, subK) = ParseRegistryPath(rule.TargetPath);
                        using (var k = rootK.OpenSubKey(subK, writable: false))
                        {
                            var v = k?.GetValue(rule.ValueName);
                            if (v == null || string.IsNullOrWhiteSpace(v.ToString()))
                            {
                                return (false, "已彻底清理(无残留)");
                            }
                        }
                        if (StartupAndBrowserScanner.IsStartupItemDisabled(rule.TargetPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU", rule.TargetPath, rule.ValueName))
                        {
                            return (false, "已彻底禁用自启(StartupApproved)");
                        }
                    }
                    var (root, subKey) = ParseRegistryPath(rule.TargetPath);
                    using (var key = root.OpenSubKey(subKey, writable: false))
                    {
                        var val = key?.GetValue(rule.ValueName)?.ToString();
                        bool isClean = val == rule.ExpectedCleanValue;
                        return (!isClean, val ?? "未配置(默认开启推广)");
                    }

                case RuleActionType.DisableService:
                    using (var sc = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals(rule.TargetPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (sc == null) return (false, "未安装该服务");
                        bool isDisabled = sc.StartType == ServiceStartMode.Disabled && sc.Status == ServiceControllerStatus.Stopped;
                        return (!isDisabled, $"Status={sc.Status}, StartType={sc.StartType}");
                    }

                case RuleActionType.RemoveScheduledTask:
                    bool exists = ScheduledTaskExists(rule.TargetPath);
                    return (exists, exists ? "存在定时任务" : "已清除");

                case RuleActionType.LockDirectoryWrite:
                    var expanded = Environment.ExpandEnvironmentVariables(rule.TargetPath);
                    bool isLocked = IsDirectoryWriteLocked(expanded);
                    return (!isLocked, isLocked ? "已免疫锁定" : "允许静默写入");

                case RuleActionType.DeleteRegistryKey:
                    var (delRoot, delSubKey) = ParseRegistryPath(rule.TargetPath);
                    using (var delKey = delRoot.OpenSubKey(delSubKey, writable: false))
                    {
                        bool keyExists = delKey != null;
                        return (keyExists, keyExists ? "存在残留子项" : "不存在");
                    }

                case RuleActionType.DisableShellExtension:
                    bool isBlocked = RogueItemScanner.IsShellExtensionBlocked(rule.ValueName);
                    return (!isBlocked, isBlocked ? "已安全屏蔽" : "未屏蔽(启用中)");

                case RuleActionType.CleanShortcutArguments:
                    if (!File.Exists(rule.TargetPath)) return (false, "快捷方式不存在");
                    return (true, rule.TargetPath);

                case RuleActionType.CleanBrowserNotifications:
                    if (!File.Exists(rule.TargetPath)) return (false, "浏览器配置不存在");
                    return (true, rule.TargetPath);

                default:
                    return (false, "Unknown");
            }
        }
        catch (Exception ex)
        {
            return (false, $"检测跳过: {ex.Message}");
        }
    }

    public ExecutionItemResult ExecuteScanItem(ScanItem item)
    {
        // D 类高风险或未知规则严禁执行任何破坏性操作
        if (item.Risk == RiskLevel.HighRiskOrUnknown)
        {
            return new ExecutionItemResult
            {
                RuleId = item.RuleId,
                Title = item.Title,
                Category = item.Category,
                Success = false,
                BeforeValue = item.CurrentState,
                AfterValue = item.CurrentState,
                Message = "安全策略拦截：高风险或未知项目禁止自动执行。"
            };
        }

        // 处理软件强力直接卸载（不调用目标软件自带的冗长挽留向导，直接终结进程并清除物理目录与注册表）
        if (item.AssociatedSoftware != null)
        {
            var (ok, msg) = _uninstallService.ForceUninstall(item.AssociatedSoftware);
            return new ExecutionItemResult
            {
                RuleId = item.RuleId,
                Title = item.Title,
                Category = item.Category,
                Success = ok,
                BeforeValue = "已安装",
                AfterValue = ok ? "已直接彻底清除" : "直接卸载未完成",
                Message = msg
            };
        }

        var rule = item.AssociatedRule;
        if (rule == null)
        {
            return new ExecutionItemResult
            {
                RuleId = item.RuleId,
                Title = item.Title,
                Success = false,
                Message = "未关联有效规则。"
            };
        }

        try
        {
            switch (rule.ActionType)
            {
                case RuleActionType.RegistryValue:
                    return ApplyRegistryRule(rule, item.CurrentState);

                case RuleActionType.DisableService:
                    return ApplyDisableServiceRule(rule, item.CurrentState);

                case RuleActionType.RemoveScheduledTask:
                    return ApplyRemoveScheduledTaskRule(rule, item.CurrentState);

                case RuleActionType.LockDirectoryWrite:
                    return ApplyLockDirectoryRule(rule, item.CurrentState);

                case RuleActionType.RemoveResidualDirectory:
                    return ApplyRemoveResidualDirRule(rule);

                case RuleActionType.DeleteRegistryKey:
                    return ApplyDeleteRegistryKeyRule(rule, item.CurrentState);

                case RuleActionType.DisableShellExtension:
                    return ApplyDisableShellExtensionRule(rule, item.CurrentState);

                case RuleActionType.CleanShortcutArguments:
                    return ApplyCleanShortcutArgumentsRule(rule, item.CurrentState);

                case RuleActionType.CleanBrowserNotifications:
                    return ApplyCleanBrowserNotificationsRule(rule, item.CurrentState);

                default:
                    return new ExecutionItemResult { RuleId = rule.Id, Title = rule.Name, Success = false, Message = "未知操作类型" };
            }
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"规则执行异常 [{rule.Id} - {rule.Name}]", ex, $"TargetPath: {rule.TargetPath}");
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = item.CurrentState,
                AfterValue = item.CurrentState,
                Message = $"执行异常: {ex.Message}"
            };
        }
    }

    private ExecutionItemResult ApplyRegistryRule(CleanRule rule, string beforeState)
    {
        try
        {
            var (root, subKey) = ParseRegistryPath(rule.TargetPath);
            using var key = root.CreateSubKey(subKey, writable: true);
            if (key == null)
            {
                return new ExecutionItemResult
                {
                    RuleId = rule.Id,
                    Title = rule.Name,
                    Category = rule.Category,
                    Success = false,
                    BeforeValue = beforeState,
                    AfterValue = beforeState,
                    Message = $"无法打开或创建注册表路径 [{rule.TargetPath}]（可能缺少权限）。"
                };
            }
            var existingObj = key.GetValue(rule.ValueName);

            // 1. 先保存备份
            _backupService.SaveBackupEntry(new BackupEntry
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                ActionType = RuleActionType.RegistryValue,
                TargetPath = rule.TargetPath,
                ValueName = rule.ValueName,
                OriginalExisted = existingObj != null,
                OriginalValue = existingObj?.ToString(),
                NewValue = rule.ExpectedCleanValue,
                SupportsRestore = rule.SupportsRestore
            });

            // 2. 写入新值或删除项
            if (rule.ExpectedCleanValue == "__DELETE__")
            {
                if (string.IsNullOrEmpty(rule.ValueName))
                {
                    root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
                }
                else
                {
                    // 若目标为 Run / RunOnce 开机自启动项，通过 Windows 官方 StartupApproved 全矩阵机制与受信任工具彻底禁用并清理
                    if (subKey.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase) || subKey.EndsWith(@"\RunOnce", StringComparison.OrdinalIgnoreCase))
                    {
                        StartupAndBrowserScanner.DisableStartupItem(rule.TargetPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU", subKey, rule.ValueName);
                        try { key.DeleteValue(rule.ValueName, throwOnMissingValue: false); } catch { }
                        try
                        {
                            if (key.GetValue(rule.ValueName) != null)
                            {
                                key.SetValue(rule.ValueName, "", RegistryValueKind.String);
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        key.DeleteValue(rule.ValueName, throwOnMissingValue: false);
                    }
                }
                return new ExecutionItemResult
                {
                    RuleId = rule.Id,
                    Title = rule.Name,
                    Category = rule.Category,
                    Success = true,
                    BeforeValue = beforeState,
                    Message = (subKey.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase) || subKey.EndsWith(@"\RunOnce", StringComparison.OrdinalIgnoreCase))
                        ? $"已成功通过系统启动策略与注册表彻底禁用开机自启 [{rule.ValueName}]（任务管理器中将显示为已禁用；若任务管理器当前已打开，请重新打开以刷新状态显示）。"
                        : $"已成功清理注册表项 [{rule.ValueName}]。"
                };
            }

            if (rule.ValueKind.Equals("DWord", StringComparison.OrdinalIgnoreCase) && int.TryParse(rule.ExpectedCleanValue, out int dword))
                key.SetValue(rule.ValueName, dword, RegistryValueKind.DWord);
            else
                key.SetValue(rule.ValueName, rule.ExpectedCleanValue, RegistryValueKind.String);

            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = true,
                BeforeValue = beforeState,
                AfterValue = rule.ExpectedCleanValue,
                Message = "已安全更新注册表配置并生成还原快照。"
            };
        }
        catch (UnauthorizedAccessException uex)
        {
            CrashLogger.LogError($"注册表权限拒绝 [{rule.TargetPath}]", uex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = "权限不足：该系统项需要以管理员身份运行程序才能修改。"
            };
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"ApplyRegistryRule 失败 [{rule.TargetPath}]", ex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = $"注册表操作异常: {ex.Message}"
            };
        }
    }

    private ExecutionItemResult ApplyDeleteRegistryKeyRule(CleanRule rule, string beforeState)
    {
        try
        {
            var (root, subKey) = ParseRegistryPath(rule.TargetPath);
            var valuesDict = new Dictionary<string, string>();
            string? defaultValue = null;

            using (var key = root.OpenSubKey(subKey, writable: false))
            {
                if (key != null)
                {
                    defaultValue = key.GetValue(null)?.ToString();
                    foreach (var v in key.GetValueNames())
                    {
                        valuesDict[v] = key.GetValue(v)?.ToString() ?? "";
                    }
                }
            }

            _backupService.SaveBackupEntry(new BackupEntry
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                ActionType = RuleActionType.DeleteRegistryKey,
                TargetPath = rule.TargetPath,
                ValueName = "",
                OriginalExisted = true,
                OriginalValue = defaultValue,
                BackupData = JsonSerializer.Serialize(valuesDict),
                NewValue = "Deleted",
                SupportsRestore = rule.SupportsRestore
            });

            root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);

            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = true,
                BeforeValue = beforeState,
                AfterValue = "已移除",
                Message = $"已成功清理注册表项 [{rule.TargetPath}] 并记录还原快照。"
            };
        }
        catch (UnauthorizedAccessException uex)
        {
            CrashLogger.LogError($"清理注册表子项权限拒绝 [{rule.TargetPath}]", uex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = "权限不足：清理该注册表树需要以管理员身份运行。"
            };
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"ApplyDeleteRegistryKeyRule 异常 [{rule.TargetPath}]", ex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = $"清理注册表项失败: {ex.Message}"
            };
        }
    }

    private ExecutionItemResult ApplyDisableShellExtensionRule(CleanRule rule, string beforeState)
    {
        try
        {
            const string blockedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

            // 1. 在 HKCU 与 HKLM（64位及32位视图）中全面登记 CLSID 屏蔽，确保当前与全局用户均生效
            if (!string.IsNullOrWhiteSpace(rule.ValueName))
            {
                try
                {
                    using var hkcuKey = Registry.CurrentUser.CreateSubKey(blockedPath, writable: true);
                    hkcuKey?.SetValue(rule.ValueName, "Disabled by CleanYourPC", RegistryValueKind.String);
                }
                catch { }

                try
                {
                    using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                                  .CreateSubKey(blockedPath, writable: true);
                    hklm64?.SetValue(rule.ValueName, "Disabled by CleanYourPC", RegistryValueKind.String);
                }
                catch { }

                try
                {
                    using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                                                  .CreateSubKey(blockedPath, writable: true);
                    hklm32?.SetValue(rule.ValueName, "Disabled by CleanYourPC", RegistryValueKind.String);
                }
                catch { }
            }

            // 2. 如果 TargetPath 指向具体的 ContextMenuHandlers 注册表子项，物理删除该子项并完整快照备份
            bool targetKeyDeleted = false;
            if (!string.IsNullOrWhiteSpace(rule.TargetPath) && !rule.TargetPath.Contains("Shell Extensions\\Blocked", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var (root, subKey) = ParseRegistryPath(rule.TargetPath);
                    using var existingKey = root.OpenSubKey(subKey, writable: false);
                    if (existingKey != null)
                    {
                        var defaultValue = existingKey.GetValue(null)?.ToString();
                        var valuesDict = new Dictionary<string, string>();
                        foreach (var vn in existingKey.GetValueNames())
                        {
                            valuesDict[vn] = existingKey.GetValue(vn)?.ToString() ?? "";
                        }

                        _backupService.SaveBackupEntry(new BackupEntry
                        {
                            RuleId = rule.Id,
                            RuleName = rule.Name,
                            ActionType = RuleActionType.DisableShellExtension,
                            TargetPath = rule.TargetPath,
                            ValueName = rule.ValueName,
                            OriginalExisted = true,
                            OriginalValue = defaultValue,
                            BackupData = JsonSerializer.Serialize(valuesDict),
                            NewValue = "Disabled",
                            SupportsRestore = rule.SupportsRestore
                        });

                        root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
                        targetKeyDeleted = true;
                    }
                }
                catch { }
            }

            // 3. 若未删除特定注册表项，则仅记录 Blocked 条目快照以供恢复
            if (!targetKeyDeleted && !string.IsNullOrWhiteSpace(rule.ValueName))
            {
                _backupService.SaveBackupEntry(new BackupEntry
                {
                    RuleId = rule.Id,
                    RuleName = rule.Name,
                    ActionType = RuleActionType.DisableShellExtension,
                    TargetPath = rule.TargetPath,
                    ValueName = rule.ValueName,
                    OriginalExisted = false,
                    OriginalValue = null,
                    NewValue = "Disabled",
                    SupportsRestore = rule.SupportsRestore
                });
            }

            // 4. 刷新桌面与资源管理器缓存
            try
            {
                SoftwareUninstallService.RefreshShellIcons();
            }
            catch { }

            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = true,
                BeforeValue = beforeState,
                AfterValue = "已安全禁用",
                Message = $"已成功禁用右键扩展 [{rule.ValueName}] 并清理关联注册表项。"
            };
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"ApplyDisableShellExtensionRule 异常 [{rule.ValueName}]", ex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = $"禁用Shell扩展失败: {ex.Message}"
            };
        }
    }

    private ExecutionItemResult ApplyCleanShortcutArgumentsRule(CleanRule rule, string beforeState)
    {
        try
        {
            if (!File.Exists(rule.TargetPath))
            {
                return new ExecutionItemResult { RuleId = rule.Id, Title = rule.Name, Success = false, Message = "快捷方式文件不存在" };
            }

            _backupService.SaveBackupEntry(new BackupEntry
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                ActionType = RuleActionType.CleanShortcutArguments,
                TargetPath = rule.TargetPath,
                ValueName = "Arguments",
                OriginalExisted = true,
                OriginalValue = beforeState.Replace("参数: ", "").Trim(),
                NewValue = "",
                SupportsRestore = rule.SupportsRestore
            });

            bool ok = RogueItemScanner.UpdateShortcutArguments(rule.TargetPath, "");

            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = ok,
                BeforeValue = beforeState,
                AfterValue = "已净化为无参",
                Message = ok ? "已成功移除桌面快捷方式追加的恶意网址参数。" : "修改快捷方式参数失败。"
            };
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"ApplyCleanShortcutArgumentsRule 异常 [{rule.TargetPath}]", ex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = $"清理快捷方式参数失败: {ex.Message}"
            };
        }
    }

    private ExecutionItemResult ApplyCleanBrowserNotificationsRule(CleanRule rule, string beforeState)
    {
        try
        {
            if (!File.Exists(rule.TargetPath))
            {
                return new ExecutionItemResult
                {
                    RuleId = rule.Id,
                    Title = rule.Name,
                    Category = rule.Category,
                    Success = false,
                    BeforeValue = beforeState,
                    AfterValue = beforeState,
                    Message = $"未找到浏览器配置文件: {rule.TargetPath}"
                };
            }

            // 保存原配置文件文本快照，支持无损还原
            string originalContent = File.ReadAllText(rule.TargetPath);
            _backupService.SaveBackupEntry(new BackupEntry
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                ActionType = RuleActionType.CleanBrowserNotifications,
                TargetPath = rule.TargetPath,
                ValueName = "notifications",
                OriginalExisted = true,
                OriginalValue = beforeState,
                BackupData = originalContent,
                NewValue = "Cleared",
                SupportsRestore = rule.SupportsRestore
            });

            var (ok, msg) = StartupAndBrowserScanner.ClearBrowserNotifications(rule.TargetPath);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = ok,
                BeforeValue = beforeState,
                AfterValue = ok ? "已撤销授权" : beforeState,
                Message = msg
            };
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"ApplyCleanBrowserNotificationsRule 异常 [{rule.TargetPath}]", ex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = $"清理浏览器弹窗通知权限异常: {ex.Message}"
            };
        }
    }

    private ExecutionItemResult ApplyDisableServiceRule(CleanRule rule, string beforeState)
    {
        try
        {
            using var regKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{rule.TargetPath}", writable: true);
            var origStart = regKey?.GetValue("Start")?.ToString() ?? "3";

            _backupService.SaveBackupEntry(new BackupEntry
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                ActionType = RuleActionType.DisableService,
                TargetPath = rule.TargetPath,
                ValueName = "Start",
                OriginalExisted = true,
                OriginalValue = origStart,
                NewValue = "4",
                SupportsRestore = true
            });

            regKey?.SetValue("Start", 4, RegistryValueKind.DWord); // 4 = Disabled

            try
            {
                using var sc = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals(rule.TargetPath, StringComparison.OrdinalIgnoreCase));
                if (sc != null && sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                CrashLogger.LogError($"停止服务异常 [{rule.TargetPath}]", ex);
            }

            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = true,
                BeforeValue = beforeState,
                AfterValue = "Stopped & Disabled",
                Message = $"已停止并禁用服务 [{rule.TargetPath}]。"
            };
        }
        catch (UnauthorizedAccessException uex)
        {
            CrashLogger.LogError($"禁用服务权限拒绝 [{rule.TargetPath}]", uex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = "权限不足：停止或禁用系统服务需要以管理员身份运行。"
            };
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"ApplyDisableServiceRule 异常 [{rule.TargetPath}]", ex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = $"禁用服务失败: {ex.Message}"
            };
        }
    }

    private ExecutionItemResult ApplyRemoveScheduledTaskRule(CleanRule rule, string beforeState)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{rule.TargetPath}\" /F",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            string err = string.Empty;
            if (proc != null)
            {
                err = proc.StandardError.ReadToEnd();
                _ = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5_000);
            }
            bool ok = proc?.ExitCode == 0 || !ScheduledTaskExists(rule.TargetPath);

            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = ok,
                BeforeValue = beforeState,
                AfterValue = ok ? "已注销" : "仍存在",
                Message = ok ? $"已成功注销计划任务 [{rule.TargetPath}]。" : $"注销计划任务失败: {err}"
            };
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"ApplyRemoveScheduledTaskRule 异常 [{rule.TargetPath}]", ex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = $"注销计划任务异常: {ex.Message}"
            };
        }
    }

    private ExecutionItemResult ApplyLockDirectoryRule(CleanRule rule, string beforeState)
    {
        try
        {
            var expandedDir = Environment.ExpandEnvironmentVariables(rule.TargetPath);
            Directory.CreateDirectory(expandedDir);

            // 清空里面已有的静默安装包缓存
            foreach (var file in Directory.GetFiles(expandedDir))
            {
                try { File.Delete(file); } catch { }
            }

            var dirInfo = new DirectoryInfo(expandedDir);
            var security = dirInfo.GetAccessControl();
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var denyWrite = new FileSystemAccessRule(
                everyone,
                FileSystemRights.Write,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Deny);

            security.AddAccessRule(denyWrite);
            dirInfo.SetAccessControl(security);

            _backupService.SaveBackupEntry(new BackupEntry
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                ActionType = RuleActionType.LockDirectoryWrite,
                TargetPath = rule.TargetPath,
                ValueName = "ACL_DenyWrite",
                OriginalExisted = true,
                OriginalValue = "Unlocked",
                NewValue = "Locked",
                SupportsRestore = true
            });

            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = true,
                BeforeValue = beforeState,
                AfterValue = "已免疫锁定",
                Message = $"已清空并锁定目录 [{expandedDir}] 的写入权限。"
            };
        }
        catch (UnauthorizedAccessException uex)
        {
            CrashLogger.LogError($"目录免疫锁定权限拒绝 [{rule.TargetPath}]", uex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = "权限不足：免疫锁定系统目录需要以管理员身份运行。"
            };
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"ApplyLockDirectoryRule 异常 [{rule.TargetPath}]", ex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = beforeState,
                AfterValue = beforeState,
                Message = $"目录免疫锁定失败: {ex.Message}"
            };
        }
    }

    private ExecutionItemResult ApplyRemoveResidualDirRule(CleanRule rule)
    {
        try
        {
            if (File.Exists(rule.TargetPath))
            {
                File.SetAttributes(rule.TargetPath, FileAttributes.Normal);
                File.Delete(rule.TargetPath);
            }
            else if (Directory.Exists(rule.TargetPath))
            {
                var di = new DirectoryInfo(rule.TargetPath);
                foreach (var f in di.GetFiles("*", SearchOption.AllDirectories))
                {
                    try { f.Attributes = FileAttributes.Normal; } catch { }
                }
                Directory.Delete(rule.TargetPath, recursive: true);
            }

            // 清理快捷方式或开始菜单目录后，立即强制刷新 Windows 图标与开始菜单应用列表缓存
            if (rule.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                rule.TargetPath.Contains("Start Menu", StringComparison.OrdinalIgnoreCase) ||
                rule.TargetPath.Contains("Programs", StringComparison.OrdinalIgnoreCase) ||
                rule.TargetPath.Contains("Desktop", StringComparison.OrdinalIgnoreCase))
            {
                SoftwareUninstallService.RefreshShellIcons();
            }

            bool exists = File.Exists(rule.TargetPath) || Directory.Exists(rule.TargetPath);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = !exists,
                BeforeValue = "存在残留项",
                AfterValue = !exists ? "已彻底移除" : "移除未完成",
                Message = $"已清理残留项 [{rule.TargetPath}]。"
            };
        }
        catch (UnauthorizedAccessException uex)
        {
            CrashLogger.LogError($"删除残留目录权限拒绝 [{rule.TargetPath}]", uex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = "存在残留目录",
                AfterValue = "存在残留目录",
                Message = "权限不足：删除 Program Files 下的软件残留目录需要以管理员身份运行。"
            };
        }
        catch (Exception ex)
        {
            CrashLogger.LogError($"ApplyRemoveResidualDirRule 异常 [{rule.TargetPath}]", ex);
            return new ExecutionItemResult
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                Success = false,
                BeforeValue = "存在残留目录",
                AfterValue = "存在残留目录",
                Message = $"清理残留目录失败: {ex.Message}"
            };
        }
    }

    public static (RegistryKey Root, string SubKeyPath) ParseRegistryPath(string fullPath)
    {
        var parts = fullPath.Split('\\', 2);
        var rootName = parts[0].ToUpperInvariant();
        var sub = parts.Length > 1 ? parts[1] : "";
        return rootName switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => (Registry.CurrentUser, sub),
            "HKLM" or "HKEY_LOCAL_MACHINE" => (Registry.LocalMachine, sub),
            _ => throw new ArgumentException($"不支持的注册表根键: {rootName}")
        };
    }

    private static bool DoesRegistryKeyExist(string fullPath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var (root, sub) = ParseRegistryPath(fullPath);
        using var k = root.OpenSubKey(sub, writable: false);
        return k != null;
    }

    private static bool ScheduledTaskExists(string taskName)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Query /TN \"{taskName}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        if (proc != null)
        {
            _ = proc.StandardOutput.ReadToEnd();
            _ = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);
        }
        return proc?.ExitCode == 0;
    }

    private static bool IsDirectoryWriteLocked(string expandedDir)
    {
        if (!Directory.Exists(expandedDir)) return false;
        try
        {
            var dirInfo = new DirectoryInfo(expandedDir);
            var security = dirInfo.GetAccessControl();
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule r in rules)
            {
                if (r.IdentityReference == everyone &&
                    r.AccessControlType == AccessControlType.Deny &&
                    (r.FileSystemRights & FileSystemRights.Write) == FileSystemRights.Write)
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}