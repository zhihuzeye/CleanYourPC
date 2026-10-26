using CleanYourPC.Core.Models;
using CleanYourPC.Core.Services;
using Microsoft.Win32;
using Xunit;

namespace CleanYourPC.Tests;

public class EngineTests : IDisposable
{
    private const string SandboxRegKey = @"Software\CleanYourPC_UnitTest_Sandbox";
    private readonly string _tempBackupFile;
    private readonly string _tempLockDir;
    private readonly BackupRestoreService _backupService;
    private readonly SoftwareUninstallService _uninstallService;
    private readonly RuleEngine _engine;

    public EngineTests()
    {
        _tempBackupFile = Path.Combine(Path.GetTempPath(), $"cypc_test_backup_{Guid.NewGuid():N}.json");
        _tempLockDir = Path.Combine(Path.GetTempPath(), $"cypc_lock_test_{Guid.NewGuid():N}");
        _backupService = new BackupRestoreService(_tempBackupFile);
        _uninstallService = new SoftwareUninstallService();
        _engine = new RuleEngine(_backupService, _uninstallService);
    }

    [Fact(DisplayName = "测试1：注册表规则检测、执行、幂等性（重复清理无副作用）与备份恢复")]
    public void TestRegistryRule_Execute_Idempotency_And_Restore()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 此测试依赖 Windows 注册表环境，仅在 Windows 系统下执行
            return;
        }

        // Arrange: 在沙箱注册表中模拟开启了广告 (值为 1)
        using (var k = Registry.CurrentUser.CreateSubKey(SandboxRegKey, writable: true))
        {
            k.SetValue("TestAdSwitch", 1, RegistryValueKind.DWord);
        }

        var rule = new CleanRule
        {
            Id = "TEST-REG-001",
            Name = "沙箱广告开关测试",
            Category = "单元测试",
            Risk = RiskLevel.AutoSafe,
            RequiresAdmin = false,
            SupportsRestore = true,
            ActionType = RuleActionType.RegistryValue,
            TargetPath = $@"HKCU\{SandboxRegKey}",
            ValueName = "TestAdSwitch",
            ExpectedCleanValue = "0",
            ValueKind = "DWord"
        };

        // Act 1: 检测问题
        var (isIssue, currentVal) = _engine.CheckRuleState(rule);
        Assert.True(isIssue);
        Assert.Equal("1", currentVal);

        // Act 2: 执行清理
        var scanItem = new ScanItem { RuleId = rule.Id, Title = rule.Name, CurrentState = currentVal, Risk = rule.Risk, AssociatedRule = rule };
        var execRes = _engine.ExecuteScanItem(scanItem);
        Assert.True(execRes.Success);

        // Act 3: 重复检测验证幂等性（不再报告为问题）
        var (isIssueAfter, valAfter) = _engine.CheckRuleState(rule);
        Assert.False(isIssueAfter);
        Assert.Equal("0", valAfter);

        // Act 4: 执行一键恢复并验证是否回到初始值 1
        var backups = _backupService.LoadBackups();
        Assert.Single(backups);
        var (restoreOk, _) = _backupService.RestoreEntry(backups[0]);
        Assert.True(restoreOk);

        var (isIssueRestored, valRestored) = _engine.CheckRuleState(rule);
        Assert.True(isIssueRestored);
        Assert.Equal("1", valRestored);
    }

    [Fact(DisplayName = "测试2：WPS 静默下载目录权限免疫锁定与解锁恢复测试")]
    public void TestDirectoryWriteLock_And_Restore()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 此测试依赖 Windows NTFS 访问控制列表(ACL)，仅在 Windows 系统下执行
            return;
        }

        var rule = new CleanRule
        {
            Id = "TEST-DIR-LOCK",
            Name = "测试目录写入免疫",
            Risk = RiskLevel.AutoSafe,
            SupportsRestore = true,
            ActionType = RuleActionType.LockDirectoryWrite,
            TargetPath = _tempLockDir
        };

        var item = new ScanItem { RuleId = rule.Id, Title = rule.Name, Risk = rule.Risk, AssociatedRule = rule };
        var res = _engine.ExecuteScanItem(item);
        Assert.True(res.Success);

        // 验证锁定后无法在该目录写入新文件
        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            File.WriteAllText(Path.Combine(_tempLockDir, "bundle_adware.exe"), "test");
        });

        // 验证恢复后可以正常写入并清理
        var backups = _backupService.LoadBackups();
        var (restoreOk, _) = _backupService.RestoreEntry(backups.Last());
        Assert.True(restoreOk);

        var testFile = Path.Combine(_tempLockDir, "normal_file.txt");
        File.WriteAllText(testFile, "ok");
        Assert.True(File.Exists(testFile));
    }

    [Fact(DisplayName = "测试3：D类未知/高风险项目与系统保护组件严禁自动执行或卸载")]
    public void TestHighRiskAndProtectedSoftware_Blocked()
    {
        // D 类规则拦截验证
        var unknownItem = new ScanItem
        {
            RuleId = "UNKNOWN-001",
            Title = "未知第三方后台服务",
            Risk = RiskLevel.HighRiskOrUnknown
        };
        var execRes = _engine.ExecuteScanItem(unknownItem);
        Assert.False(execRes.Success);
        Assert.Contains("安全策略拦截", execRes.Message);

        // 微软与硬件驱动白名单防误删验证
        var protectedDriver = new InstalledSoftwareInfo
        {
            DisplayName = "Realtek High Definition Audio Driver",
            Publisher = "Realtek Semiconductor Corp.",
            IsSystemProtected = true,
            UninstallString = "C:\\Program Files\\Realtek\\Setup.exe -uninstall"
        };
        var (uninstOk, msg) = _uninstallService.TriggerOfficialUninstall(protectedDriver);
        Assert.False(uninstOk);
        Assert.Contains("安全拦截", msg);

        var (forceOk, forceMsg) = _uninstallService.ForceUninstall(protectedDriver);
        Assert.False(forceOk);
        Assert.Contains("安全拦截", forceMsg);
    }

    [Fact(DisplayName = "测试4：备份历史版本号生成与快照归档管理")]
    public void TestSnapshotVersioning_Create_And_Query()
    {
        var nextVer = _backupService.GetNextVersion();
        Assert.Equal("v1.0", nextVer);

        var entries = new List<BackupEntry>
        {
            new BackupEntry
            {
                RuleId = "TEST-SNAP-001",
                RuleName = "测试快照项1",
                ActionType = RuleActionType.RegistryValue,
                SupportsRestore = true
            }
        };

        _backupService.CreateSnapshot(nextVer, entries, "单元测试快照 v1.0", "测试系统");

        var snapshots = _backupService.LoadSnapshots();
        Assert.NotEmpty(snapshots);
        Assert.Equal("v1.0", snapshots.Last().Version);
        Assert.Single(snapshots.Last().Entries);

        var nextVer2 = _backupService.GetNextVersion();
        Assert.Equal("v1.1", nextVer2);
    }

    [Fact(DisplayName = "测试5：模块化规则目录递归扫描与平台作用域过滤")]
    public void TestModularRuleLoading_ByDirectory_And_PlatformScope()
    {
        var rulesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "rules");
        if (!Directory.Exists(rulesRoot))
        {
            rulesRoot = Path.Combine(Directory.GetCurrentDirectory(), "rules");
        }

        if (Directory.Exists(rulesRoot))
        {
            var allRules = _engine.LoadRules(rulesRoot);
            Assert.NotEmpty(allRules);

            // 验证已涵盖多平台分类
            Assert.Contains(allRules, r => r.Scope == PlatformScope.Common);
            Assert.Contains(allRules, r => r.Scope == PlatformScope.Windows);
            Assert.Contains(allRules, r => r.Scope == PlatformScope.UOS);

            // 验证 Windows 环境下不会匹配统信 UOS 专属规则
            var winEnv = new SystemEnvironmentSummary { OSType = "Windows 10", OSCaption = "Windows 10 Pro" };
            var winItems = _engine.EvaluateRules(allRules, winEnv, new List<InstalledSoftwareInfo>());
            Assert.DoesNotContain(winItems, i => i.AssociatedRule?.Scope == PlatformScope.UOS);
        }
    }

    [Fact(DisplayName = "测试6：右键菜单Shell扩展禁用 (DisableShellExtension) 与备份恢复")]
    public void TestDisableShellExtension_Execute_And_Restore()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string testClsid = "{99999999-8888-7777-6666-555555555555}";
        var rule = new CleanRule
        {
            Id = "TEST-SHELL-EXT",
            Name = "测试禁用多余推广右键扩展",
            Category = "右键菜单治理",
            Risk = RiskLevel.UserConfirm,
            SupportsRestore = true,
            ActionType = RuleActionType.DisableShellExtension,
            TargetPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked",
            ValueName = testClsid,
            ExpectedCleanValue = "Disabled by CleanYourPC"
        };

        var item = new ScanItem { RuleId = rule.Id, Title = rule.Name, Risk = rule.Risk, AssociatedRule = rule };
        var execRes = _engine.ExecuteScanItem(item);
        Assert.True(execRes.Success);

        // 验证 Blocked 列表中已登记该 CLSID
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", false))
        {
            Assert.NotNull(key);
            var val = key.GetValue(testClsid)?.ToString();
            Assert.Equal("Disabled by CleanYourPC", val);
        }

        // 验证一键还原功能
        var backups = _backupService.LoadBackups();
        Assert.NotEmpty(backups);
        var (ok, _) = _backupService.RestoreEntry(backups.Last());
        Assert.True(ok);

        using (var keyAfter = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", false))
        {
            Assert.True(keyAfter == null || keyAfter.GetValue(testClsid) == null);
        }
    }

    [Fact(DisplayName = "测试7：全量注册表项清理 (DeleteRegistryKey) 及其快照重建还原")]
    public void TestDeleteRegistryKey_Execute_And_Restore()
    {
        if (!OperatingSystem.IsWindows()) return;

        string subPath = $@"{SandboxRegKey}\SubTestKey";
        using (var k = Registry.CurrentUser.CreateSubKey(subPath, true))
        {
            k.SetValue("Param1", "Value1");
            k.SetValue("Param2", "Value2");
        }

        var rule = new CleanRule
        {
            Id = "TEST-DEL-REG-KEY",
            Name = "测试删除注册表子项",
            Category = "资源管理器入口治理",
            Risk = RiskLevel.AutoSafe,
            SupportsRestore = true,
            ActionType = RuleActionType.DeleteRegistryKey,
            TargetPath = $@"HKCU\{subPath}",
            ExpectedCleanValue = "__DELETE__"
        };

        var item = new ScanItem { RuleId = rule.Id, Title = rule.Name, Risk = rule.Risk, AssociatedRule = rule };
        var execRes = _engine.ExecuteScanItem(item);
        Assert.True(execRes.Success);

        // 验证注册表项已被删除
        using (var kCheck = Registry.CurrentUser.OpenSubKey(subPath, false))
        {
            Assert.Null(kCheck);
        }

        // 验证从快照备份还原该子项及参数
        var backups = _backupService.LoadBackups();
        var (restoreOk, _) = _backupService.RestoreEntry(backups.Last());
        Assert.True(restoreOk);

        using (var kRestored = Registry.CurrentUser.OpenSubKey(subPath, false))
        {
            Assert.NotNull(kRestored);
            Assert.Equal("Value1", kRestored.GetValue("Param1"));
            Assert.Equal("Value2", kRestored.GetValue("Param2"));
        }
    }

    [Fact(DisplayName = "测试8：治理项属性通知 (INotifyPropertyChanged) 与一键全选/推荐勾选逻辑")]
    public void TestScanItem_IsSelected_PropertyChangeNotification_And_BatchSelection()
    {
        var item1 = new ScanItem { RuleId = "R1", Title = "安全项", Risk = RiskLevel.AutoSafe, RequiresUserConfirm = false };
        var item2 = new ScanItem { RuleId = "R2", Title = "提权项", Risk = RiskLevel.AdminRequired, RequiresUserConfirm = false };
        var item3 = new ScanItem { RuleId = "R3", Title = "确认项", Risk = RiskLevel.UserConfirm, RequiresUserConfirm = true };
        var item4 = new ScanItem { RuleId = "R4", Title = "未知项", Risk = RiskLevel.HighRiskOrUnknown };

        var list = new List<ScanItem> { item1, item2, item3, item4 };

        // 验证 PropertyChanged 触发
        string? changedProp = null;
        item1.PropertyChanged += (s, e) => changedProp = e.PropertyName;
        item1.IsSelected = true;
        Assert.Equal("IsSelected", changedProp);
        Assert.True(item1.IsSelected);

        // 验证一键全选逻辑（排除 D 类不可执行项）
        foreach (var item in list)
        {
            if (item.CanExecute) item.IsSelected = true;
        }
        Assert.True(item1.IsSelected);
        Assert.True(item2.IsSelected);
        Assert.True(item3.IsSelected);
        Assert.False(item4.IsSelected); // D 类不能执行，不勾选

        // 验证一键推荐逻辑（只勾选 A/B 类非确认项）
        foreach (var item in list)
        {
            item.IsSelected = (item.Risk == RiskLevel.AutoSafe || item.Risk == RiskLevel.AdminRequired) && !item.RequiresUserConfirm;
        }
        Assert.True(item1.IsSelected);
        Assert.True(item2.IsSelected);
        Assert.False(item3.IsSelected);
        Assert.False(item4.IsSelected);

        // 验证一键取消全选
        foreach (var item in list) item.IsSelected = false;
        Assert.All(list, i => Assert.False(i.IsSelected));
    }

    [Fact(DisplayName = "测试9：360全系列软件（360极速浏览器X、360安全浏览器、360压缩等）全面识别与卸载项生成")]
    public void Test360Software_And_Bloatware_Recognition()
    {
        var testApps = new[]
        {
            new InstalledSoftwareInfo { DisplayName = "360 极速浏览器X", Publisher = "北京奇虎科技有限公司", DisplayVersion = "22.0.1012.0", UninstallString = "C:\\Program Files\\360\\360ChromeX\\uninst.exe" },
            new InstalledSoftwareInfo { DisplayName = "360安全浏览器", Publisher = "Qihoo 360 Software Co., Ltd.", DisplayVersion = "15.3.6156.64", UninstallString = "C:\\Users\\User\\AppData\\Roaming\\360se\\uninst.exe" },
            new InstalledSoftwareInfo { DisplayName = "360压缩", Publisher = "北京奇虎科技有限公司", DisplayVersion = "4.0.0.1430", UninstallString = "C:\\Program Files (x86)\\360\\360zip\\uninst.exe" },
            new InstalledSoftwareInfo { DisplayName = "360驱动大师", Publisher = "360.cn", DisplayVersion = "2.0.0.1980", UninstallString = "C:\\Program Files\\360\\360Driver\\uninst.exe" },
            new InstalledSoftwareInfo { DisplayName = "360安全卫士", Publisher = "奇虎360", DisplayVersion = "13.0.0.2001", UninstallString = "C:\\Program Files (x86)\\360\\360Safe\\uninst.exe" }
        };

        foreach (var app in testApps)
        {
            bool isTarget = SystemInfoScanner.EvaluateTargetSoftwareRisk(app);
            Assert.True(isTarget, $"{app.DisplayName} 应该被识别为目标软件");
            Assert.True(app.IsTargetSecurityOrBloatware);
            Assert.False(string.IsNullOrWhiteSpace(app.RiskReason));
        }

        // 验证系统保护白名单不会被误判
        var protectedApp = new InstalledSoftwareInfo
        {
            DisplayName = "Intel Graphics Command Center",
            Publisher = "Intel Corporation",
            IsSystemProtected = true
        };
        bool isProtectedTarget = SystemInfoScanner.EvaluateTargetSoftwareRisk(protectedApp);
        Assert.False(isProtectedTarget);
        Assert.False(protectedApp.IsTargetSecurityOrBloatware);

        // 验证在 RuleEngine 中评估生成目标软件卸载扫描项
        var env = new SystemEnvironmentSummary { OSType = "Windows 10" };
        var scanItems = _engine.EvaluateRules(new List<CleanRule>(), env, testApps.ToList());
        var uninstallItems = scanItems.Where(i => i.Category == "目标软件卸载").ToList();

        Assert.Equal(5, uninstallItems.Count);
        Assert.Contains(uninstallItems, i => i.Title.Contains("360 极速浏览器X"));
        Assert.Contains(uninstallItems, i => i.Title.Contains("360安全浏览器"));
        Assert.Contains(uninstallItems, i => i.Title.Contains("360压缩"));
        Assert.Contains(uninstallItems, i => i.Title.Contains("360驱动大师"));
        Assert.Contains(uninstallItems, i => i.Title.Contains("360安全卫士"));
    }

    [Fact(DisplayName = "测试10：WPS云盘此电脑入口与导航栏固定项特征精准匹配与治理规则")]
    public void TestWpsCloudDrive_Namespace_And_PinnedItem_Detection()
    {
        // 1. 验证各种形态下的 WPS 云盘入口特征匹配
        bool matchClassic = RogueItemScanner.IsWpsDriveMatch("{5FCD4425-CA3A-48F4-A57C-B8A7572BE570}", "", "", "", "", "");
        Assert.True(matchClassic, "已知 WPS 经典 CLSID 应该直接命中");

        bool matchByName = RogueItemScanner.IsWpsDriveMatch("{11111111-2222-3333-4444-555555555555}", "WPS云盘", "", "WPS云盘", "", "");
        Assert.True(matchByName, "显示名称为 WPS云盘 应直接命中");

        bool matchByKdesktop = RogueItemScanner.IsWpsDriveMatch("{12345678-0000-0000-0000-000000000000}", "", "@kdesktop.exe,-101", "", @"C:\Users\apple\AppData\Local\Kingsoft\WPS Office", "kclouddrive.dll");
        Assert.True(matchByKdesktop, "组件含 kdesktop/kclouddrive 应命中 WPS 云盘");

        bool matchNegative = RogueItemScanner.IsWpsDriveMatch("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "此电脑", "", "此电脑", "", "shell32.dll");
        Assert.False(matchNegative, "系统此电脑不应该命中 WPS 云盘");
    }

    [Fact(DisplayName = "测试11：CrashLogger 运行日志 log.txt 与错误日志 error.txt 智能定位与持久保留测试")]
    public void TestCrashLogger_WritesLogFile_InCandidateDirectories()
    {
        // Act 1: 验证错误日志 error.txt
        var ex = new InvalidOperationException("单元测试模拟抛出的崩溃排查异常", new ArgumentNullException("InnerParam"));
        var errorLogPath = CrashLogger.LogError("单元测试验证崩溃日志落盘", ex, "测试附加诊断上下文");

        // Assert 1: 验证返回 error.txt 且包含异常内容
        Assert.False(string.IsNullOrEmpty(errorLogPath), "应返回有效的日志文件路径");
        Assert.True(File.Exists(errorLogPath), $"错误日志文件应存在于: {errorLogPath}");
        Assert.EndsWith("error.txt", errorLogPath);

        var errContent = File.ReadAllText(errorLogPath);
        Assert.Contains("崩溃排查日志", errContent);
        Assert.Contains("单元测试模拟抛出的崩溃排查异常", errContent);
        Assert.Contains("InnerParam", errContent);

        // Act 2: 验证运行日志 log.txt
        var runningLogPath = CrashLogger.LogRunning("单元测试运行日志条目1：开始扫描系统");
        CrashLogger.LogRunning("单元测试运行日志条目2：完成扫描分析");

        // Assert 2: 验证返回 log.txt 且追加保留
        Assert.False(string.IsNullOrEmpty(runningLogPath), "应返回有效的运行日志路径");
        Assert.True(File.Exists(runningLogPath), $"运行日志文件应存在于: {runningLogPath}");
        Assert.EndsWith("log.txt", runningLogPath);

        var runContent = File.ReadAllText(runningLogPath);
        Assert.Contains("单元测试运行日志条目1：开始扫描系统", runContent);
        Assert.Contains("单元测试运行日志条目2：完成扫描分析", runContent);

        // 验证候选目录探测列表中包含当前目录
        var candidates = CrashLogger.GetCandidateLogDirectories();
        Assert.NotEmpty(candidates);
        Assert.Contains(Environment.CurrentDirectory, candidates);
    }

    [Fact(DisplayName = "测试12：BackupRestoreService 多线程并发与批量快照安全保存测试")]
    public void TestBackupRestoreService_ThreadSafe_BatchSave()
    {
        var entries = new List<BackupEntry>();
        for (int i = 0; i < 50; i++)
        {
            entries.Add(new BackupEntry
            {
                RuleId = $"BATCH-RULE-{i}",
                RuleName = $"批量测试项 {i}",
                ActionType = RuleActionType.RegistryValue,
                TargetPath = @"HKCU\Software\TestBatch",
                ValueName = $"Val{i}",
                OriginalValue = "0",
                NewValue = "1",
                SupportsRestore = true
            });
        }

        // Act: 批量安全保存
        _backupService.SaveBackupEntries(entries);

        // Assert: 验证全部写入成功，无丢失
        var loaded = _backupService.LoadBackups();
        Assert.Equal(50, loaded.Count);

        // Act 2: 创建快照
        _backupService.CreateSnapshot("v1.1", entries, "批量保存测试快照");
        var snapshots = _backupService.LoadSnapshots();
        Assert.Contains(snapshots, s => s.Version == "v1.1" && s.Entries.Count == 50);
    }

    [Fact(DisplayName = "测试13：验证错误日志包含详尽系统环境信息以及清除失败项目标路径与报错明细")]
    public void TestCrashLogger_LogsEnvironmentSummary_And_CleanExecutionDetails()
    {
        // Arrange: 构造系统环境信息
        var env = new SystemEnvironmentSummary
        {
            OSCaption = "Windows 11 专业版 23H2",
            OSBuild = "22631.3007",
            OSType = "Windows 11",
            IsAdmin = false,
            ActiveAntivirus = new List<string> { "Windows Defender", "火绒安全软件" },
            InstalledIMEs = new List<string> { "搜狗输入法 (13.8)", "微软拼音" },
            WpsVersion = "12.1.0.16412"
        };
        CrashLogger.SetEnvironmentSummary(env);

        var attemptedItems = new List<ScanItem>
        {
            new ScanItem
            {
                RuleId = "AD-LOCKSCREEN-001",
                Title = "关闭锁屏广告资讯画报",
                Category = "锁屏推广治理",
                CurrentState = "1 (开启)",
                TargetState = "0 (关闭)",
                AssociatedRule = new CleanRule
                {
                    Id = "AD-LOCKSCREEN-001",
                    Name = "关闭锁屏广告资讯画报",
                    Category = "锁屏推广治理",
                    ActionType = RuleActionType.RegistryValue,
                    TargetPath = @"HKLM\Software\Policies\Microsoft\Windows\CloudContent",
                    ValueName = "DisableWindowsConsumerFeatures",
                    ExpectedCleanValue = "1",
                    RequiresAdmin = true
                }
            },
            new ScanItem
            {
                RuleId = "SERVICE-360-TRAY",
                Title = "禁用 360 托盘推广服务",
                Category = "系统服务治理",
                CurrentState = "Running",
                TargetState = "Stopped & Disabled",
                AssociatedRule = new CleanRule
                {
                    Id = "SERVICE-360-TRAY",
                    Name = "禁用 360 托盘推广服务",
                    Category = "系统服务治理",
                    ActionType = RuleActionType.DisableService,
                    TargetPath = "QutrayService",
                    RequiresAdmin = true
                }
            }
        };

        var results = new List<ExecutionItemResult>
        {
            new ExecutionItemResult
            {
                RuleId = "AD-LOCKSCREEN-001",
                Title = "关闭锁屏广告资讯画报",
                Category = "锁屏推广治理",
                Success = false,
                BeforeValue = "1 (开启)",
                AfterValue = "1 (开启)",
                Message = "权限不足：该系统项需要以管理员身份运行程序才能修改。"
            },
            new ExecutionItemResult
            {
                RuleId = "SERVICE-360-TRAY",
                Title = "禁用 360 托盘推广服务",
                Category = "系统服务治理",
                Success = true,
                BeforeValue = "Running",
                AfterValue = "Stopped & Disabled",
                Message = "服务已成功禁用并停止。"
            }
        };

        // Act: 记录清理报错详情
        var logPath = CrashLogger.LogCleanExecutionDetails(env, attemptedItems, results, null);

        // Assert: 验证日志包含详尽的系统环境信息和清除失败项的目标路径与报错
        Assert.True(File.Exists(logPath));
        var content = File.ReadAllText(logPath);

        // 验证系统环境信息
        Assert.Contains("Windows 11 专业版 23H2", content);
        Assert.Contains("22631.3007", content);
        Assert.Contains("火绒安全软件", content);
        Assert.Contains("搜狗输入法 (13.8)", content);
        Assert.Contains("12.1.0.16412", content);

        // 验证清除什么东西报错（目标路径与错误原因）
        Assert.Contains("清除失败项明细清单", content);
        Assert.Contains("AD-LOCKSCREEN-001", content);
        Assert.Contains("关闭锁屏广告资讯画报", content);
        Assert.Contains(@"HKLM\Software\Policies\Microsoft\Windows\CloudContent", content);
        Assert.Contains("DisableWindowsConsumerFeatures", content);
        Assert.Contains("权限不足：该系统项需要以管理员身份运行程序才能修改。", content);

        // 验证完整勾选清单概览
        Assert.Contains("本次勾选清理的完整项目清单", content);
    }

    [Fact(DisplayName = "测试14：验证强力直接卸载功能（不调用官方卸载向导，直接清除进程、安装目录与注册表）")]
    public void TestForceUninstall_DirectRemoval_WithoutCallingUninstaller()
    {
        // Arrange: 创建一个模拟的推广软件安装目录，包含多个 dll 和伪装文件
        var fakeAppDir = Path.Combine(Path.GetTempPath(), $"cypc_fake_app_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fakeAppDir);
        var subDir = Path.Combine(fakeAppDir, "bin");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(fakeAppDir, "rogue_tool.exe"), "fake binary");
        File.WriteAllText(Path.Combine(subDir, "hook.dll"), "fake hook dll");

        // 设置一个只读文件以检验解除只读强力删除
        var readOnlyFile = Path.Combine(fakeAppDir, "readonly.dat");
        File.WriteAllText(readOnlyFile, "read only config");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        Assert.True(Directory.Exists(fakeAppDir));
        Assert.True(File.Exists(readOnlyFile));

        var fakeSoftware = new InstalledSoftwareInfo
        {
            DisplayName = "推广看图王",
            Publisher = "恶意推广公司",
            DisplayVersion = "1.0.0",
            InstallLocation = fakeAppDir,
            UninstallString = Path.Combine(fakeAppDir, "uninstall.exe"), // 故意指向不存在的或假的卸载器，证明绝不调用它
            IsTargetSecurityOrBloatware = true,
            IsSystemProtected = false
        };

        // Act: 执行强力直接卸载
        var (ok, msg) = _uninstallService.ForceUninstall(fakeSoftware);

        // Assert: 验证直接卸载成功，且物理文件与安装目录已被彻底清除，无需调用任何第三方卸载程序
        Assert.True(ok);
        Assert.Contains("已彻底直接卸载", msg);
        Assert.False(Directory.Exists(fakeAppDir), "安装目录应已被彻底强力删除");

        // 验证关键系统公共根目录安全防线生效
        bool dangerousWin = SoftwareUninstallService.IsDangerousSystemDirectory(@"C:\Windows");
        bool dangerousPf = SoftwareUninstallService.IsDangerousSystemDirectory(@"C:\Program Files");
        bool dangerousRoot = SoftwareUninstallService.IsDangerousSystemDirectory(@"C:\");
        Assert.True(dangerousWin, "系统目录应被严格防御拒绝直接删除");
        Assert.True(dangerousPf, "Program Files 顶级目录应被严格防御拒绝直接删除");
        Assert.True(dangerousRoot, "分区根路径应被严格防御拒绝直接删除");
    }

    [Fact(DisplayName = "测试15：SoftwareUninstallService 桌面与开始菜单快捷方式深度清理及智能分词测试")]
    public void TestSoftwareUninstallService_CleansDesktopAndStartMenuShortcuts()
    {
        // 1. 验证关键字智能提取
        var keywords1 = SoftwareUninstallService.ExtractKeywords("WPS Office 2019 专业版 (11.8.2.12198)");
        Assert.Contains("WPS Office", keywords1);

        var keywords2 = SoftwareUninstallService.ExtractKeywords("360安全卫士极速版");
        Assert.Contains("360安全卫士", keywords2);

        var keywords3 = SoftwareUninstallService.ExtractKeywords("酷狗音乐 10.2.5 [正式版]");
        Assert.Contains("酷狗音乐", keywords3);

        // 2. 验证 LNK 目标物理路径嵌入识别
        var tempLnk = Path.Combine(Path.GetTempPath(), $"test_shortcut_{Guid.NewGuid():N}.lnk");
        var fakeTarget = @"C:\Program Files\FakeBloatware";
        var lnkContent = System.Text.Encoding.ASCII.GetBytes($"Header...{fakeTarget}\\app.exe...Footer");
        File.WriteAllBytes(tempLnk, lnkContent);

        try
        {
            bool detected = SoftwareUninstallService.DoesLnkContainTarget(tempLnk, fakeTarget);
            Assert.True(detected, "应能在 LNK 字节流中精准检测出软件安装主目录");

            bool notDetected = SoftwareUninstallService.DoesLnkContainTarget(tempLnk, @"C:\Program Files\AnotherApp");
            Assert.False(notDetected, "不相关的安装主目录不应被误判");
        }
        finally
        {
            try { File.Delete(tempLnk); } catch { }
        }

        // 3. 验证去空格归一化与 360 极速浏览器X、360安全浏览器、360压缩模糊匹配
        string token1 = SoftwareUninstallService.CleanToken("360 极速浏览器X");
        string token2 = SoftwareUninstallService.CleanToken("360极速浏览器X");
        Assert.Equal(token1, token2);

        var keywords360 = SoftwareUninstallService.ExtractKeywords("360 极速浏览器X");
        Assert.Contains("360极速浏览器X", keywords360);

        // 4. 验证 ApplyRemoveResidualDirRule 支持直接清除单文件（如 .lnk 死链快捷方式）
        var tempFileLnk = Path.Combine(Path.GetTempPath(), $"cypc_dead_link_{Guid.NewGuid():N}.lnk");
        File.WriteAllText(tempFileLnk, "fake dead link");
        Assert.True(File.Exists(tempFileLnk));

        var cleanResult = _engine.ExecuteScanItem(new ScanItem
        {
            RuleId = "TEST-REMOVE-LNK",
            Title = "清理失效快捷方式",
            Category = "桌面与菜单治理",
            AssociatedRule = new CleanRule
            {
                Id = "TEST-REMOVE-LNK",
                Name = "清理失效快捷方式",
                Category = "桌面与菜单治理",
                ActionType = RuleActionType.RemoveResidualDirectory,
                TargetPath = tempFileLnk
            }
        });

        Assert.True(cleanResult.Success);
        Assert.False(File.Exists(tempFileLnk), "单文件快捷方式应已被彻底删除");
    }

    [Fact(DisplayName = "测试16：ShellExtension Blocked 屏蔽状态检测与跨注册表视图免疫校验")]
    public void TestShellExtensionBlockedDetectionAndDualHiveSupport()
    {
        // 1. 空或非有效 CLSID 判定安全
        Assert.False(RogueItemScanner.IsShellExtensionBlocked(""));
        Assert.False(RogueItemScanner.IsShellExtensionBlocked("   "));
        Assert.False(RogueItemScanner.IsShellExtensionBlocked(null!));

        if (!OperatingSystem.IsWindows()) return;

        string fakeClsid = $"{{{Guid.NewGuid():D}}}";
        const string blockedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

        try
        {
            // 写入当前用户 Blocked 注册表沙箱
            using (var key = Registry.CurrentUser.CreateSubKey(blockedPath, writable: true))
            {
                key.SetValue(fakeClsid, "Disabled by CleanYourPC Unit Test");
            }

            // 验证 IsShellExtensionBlocked 能够精准识别已屏蔽状态
            Assert.True(RogueItemScanner.IsShellExtensionBlocked(fakeClsid));

            // 验证 RuleEngine 对该 CLSID 的 Evaluate 判断为非风险项（已安全屏蔽）
            var rule = new CleanRule
            {
                Id = "TEST-CTX-BLOCK",
                Name = "测试屏蔽右键扩展",
                Category = "右键菜单治理",
                ActionType = RuleActionType.DisableShellExtension,
                TargetPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked",
                ValueName = fakeClsid
            };

            var (isIssue, state) = _engine.CheckRuleState(rule);
            Assert.False(isIssue, "已被 Blocked 的 Shell 扩展不应再作为待治理项上报");
            Assert.Equal("已安全屏蔽", state);
        }
        finally
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(blockedPath, writable: true);
                key?.DeleteValue(fakeClsid, throwOnMissingValue: false);
            }
            catch { }
        }
    }

    [Fact(DisplayName = "测试17：仅系统核心组件可开机自启，其余一切第三方软件自启通通纳入治理")]
    public void TestAllThirdPartyStartupItemsAreTargeted_AndStartupApprovedLogic()
    {
        // 1. 验证仅 Windows 原生核心应用通过，第三方软件（无论是 360、天擎、微信、搜狗等）通通不通过
        Assert.True(StartupAndBrowserScanner.IsNativeWindowsSystemComponent(@"C:\Windows\System32\SecurityHealthSystray.exe", "SecurityHealth"));
        Assert.True(StartupAndBrowserScanner.IsNativeWindowsSystemComponent(@"C:\Windows\System32\ctfmon.exe", "ctfmon"));

        string[] thirdPartyCmds = new[]
        {
            @"C:\Program Files (x86)\360\360Safe\360safetray.exe /autorun",
            @"C:\Program Files (x86)\QAX\Tianqing\360safetray.exe",
            @"C:\Program Files\Tencent\WeChat\WeChat.exe",
            @"C:\Program Files\SogouInput\Components\SogouServices.exe",
            @"C:\Users\Admin\AppData\Local\Programs\BaiduNetdisk\BaiduNetdisk.exe"
        };

        foreach (var cmd in thirdPartyCmds)
        {
            Assert.False(StartupAndBrowserScanner.IsNativeWindowsSystemComponent(cmd, "test"),
                $"第三方自启项 [{cmd}] 必须被判定为非系统自启，纳入治理");
        }

        // 2. 验证 StartupApproved 禁用状态判定 (IsStartupItemDisabled)
        Assert.False(StartupAndBrowserScanner.IsStartupItemDisabled("HKCU", "Software\\Run", "NonExistentKey"));
    }

    [Fact(DisplayName = "测试18：浏览器弹窗通知权限自动清除与快照还原校验")]
    public void TestClearBrowserNotifications_And_Restore()
    {
        // 模拟 Chromium Preferences 文件内容
        string tempPrefFile = Path.Combine(Path.GetTempPath(), $"cypc_pref_{Guid.NewGuid():N}.json");
        string fakeJson = """
        {
          "profile": {
            "content_settings": {
              "exceptions": {
                "notifications": {
                  "https://ad-site.com:443,*": {
                    "setting": 1
                  },
                  "https://malicious-news.cn:443,*": {
                    "setting": 1
                  }
                }
              }
            }
          }
        }
        """;

        File.WriteAllText(tempPrefFile, fakeJson);

        try
        {
            // 1. 执行自动清除
            var (ok, msg) = StartupAndBrowserScanner.ClearBrowserNotifications(tempPrefFile);
            Assert.True(ok);

            // 验证 notifications 节点被完全清空
            string updatedJson = File.ReadAllText(tempPrefFile);
            using var doc = System.Text.Json.JsonDocument.Parse(updatedJson);
            var notifs = doc.RootElement.GetProperty("profile")
                                        .GetProperty("content_settings")
                                        .GetProperty("exceptions")
                                        .GetProperty("notifications");
            Assert.Empty(notifs.EnumerateObject());

            // 2. 验证 RuleEngine 执行该规则并记录快照
            File.WriteAllText(tempPrefFile, fakeJson); // 重置为有恶意通知状态
            var rule = new CleanRule
            {
                Id = "TEST-NOTIF-CLEAN",
                Name = "清除测试通知",
                Category = "浏览器弹窗治理",
                ActionType = RuleActionType.CleanBrowserNotifications,
                TargetPath = tempPrefFile,
                SupportsRestore = true
            };

            var execResult = _engine.ExecuteScanItem(new ScanItem
            {
                RuleId = rule.Id,
                Title = rule.Name,
                Category = rule.Category,
                AssociatedRule = rule,
                Risk = RiskLevel.UserConfirm
            });

            Assert.True(execResult.Success);

            // 3. 验证通过 BackupRestoreService 一键还原
            var backups = _backupService.LoadBackups();
            var targetBackup = backups.Find(b => b.RuleId == rule.Id);
            Assert.NotNull(targetBackup);

            var (restoreOk, restoreMsg) = _backupService.RestoreEntry(targetBackup);
            Assert.True(restoreOk);

            // 验证还原后的文件与原始带通知的内容完全一致
            string restoredJson = File.ReadAllText(tempPrefFile);
            Assert.Contains("ad-site.com", restoredJson);
        }
        finally
        {
            try { if (File.Exists(tempPrefFile)) File.Delete(tempPrefFile); } catch { }
        }
    }

    [Fact(DisplayName = "测试19：验证开机自启全矩阵禁用 (StartupApproved) 与第三方自启识别判定")]
    public void TestStartupItem_DisableAndCheck()
    {
        // 验证非 Windows 环境下安全降级与空参数防御
        Assert.False(StartupAndBrowserScanner.IsStartupItemDisabled(""));
        Assert.False(StartupAndBrowserScanner.IsStartupItemDisabled("   "));
        Assert.False(StartupAndBrowserScanner.IsStartupItemDisabled("Sysdiag_Not_Found"));
        Assert.False(StartupAndBrowserScanner.IsStartupItemDisabled("360Safetray_Not_Found"));

        // 验证 Sysdiag 与 360Safetray 等第三方自启项绝不被误判为系统组件
        Assert.False(StartupAndBrowserScanner.IsNativeWindowsSystemComponent(@"C:\Program Files\Huorong\Sysdiag\bin\sysdiag.exe", "Sysdiag"));
        Assert.False(StartupAndBrowserScanner.IsNativeWindowsSystemComponent(@"C:\Program Files (x86)\QAX\Tianqing\360safetray.exe", "360Safetray"));

        // 验证符合 Windows 任务管理器规范的 12 字节二进制与有效 FILETIME 时间戳
        byte[] disabledBytes = StartupAndBrowserScanner.CreateDisabledBinary();
        Assert.Equal(12, disabledBytes.Length);
        Assert.Equal(0x03, disabledBytes[0]);
        Assert.Equal(0x00, disabledBytes[1]);
        Assert.Equal(0x00, disabledBytes[2]);
        Assert.Equal(0x00, disabledBytes[3]);
        long fileTime = BitConverter.ToInt64(disabledBytes, 4);
        Assert.True(fileTime > 0, "FILETIME 时间戳必须大于0，避免任务管理器校验失败自动退回启用");

        byte[] enabledBytes = StartupAndBrowserScanner.CreateEnabledBinary();
        Assert.Equal(12, enabledBytes.Length);
        Assert.Equal(0x02, enabledBytes[0]);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser?.DeleteSubKeyTree(SandboxRegKey, throwOnMissingSubKey: false);
        }
        try { if (File.Exists(_tempBackupFile)) File.Delete(_tempBackupFile); } catch { }
        try { var snapFile = Path.ChangeExtension(_tempBackupFile, ".snapshots.json"); if (File.Exists(snapFile)) File.Delete(snapFile); } catch { }
        try
        {
            BackupRestoreService.UnlockDirectoryWrite(_tempLockDir);
            if (Directory.Exists(_tempLockDir)) Directory.Delete(_tempLockDir, true);
        }
        catch { }
    }
}