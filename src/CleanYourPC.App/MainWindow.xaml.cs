using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Animation;
using CleanYourPC.Core.Models;
using CleanYourPC.Core.Services;
using Microsoft.Win32;

namespace CleanYourPC.App;

public partial class MainWindow : Window
{
    private readonly SystemInfoScanner _systemScanner = new();
    private readonly BackupRestoreService _backupService = new();
    private readonly SoftwareUninstallService _uninstallService = new();
    private readonly RuleEngine _ruleEngine;

    private SystemEnvironmentSummary _envSummary = new();
    private ObservableCollection<ScanItem> _scanItems = new();
    private CleanReport? _lastReport;

    public MainWindow()
    {
        InitializeComponent();
        _ruleEngine = new RuleEngine(_backupService, _uninstallService);
        LoadSystemBaseline();
    }

    private void LoadSystemBaseline()
    {
        _envSummary = _systemScanner.GetEnvironmentSummary();
        string adminTag = _envSummary.IsAdmin ? "管理员模式 (已授权)" : "标准用户模式 (推荐提权)";
        string avTag = _envSummary.ActiveAntivirus.Count > 0 ? string.Join(", ", _envSummary.ActiveAntivirus) : "系统原生防护";
        string wpsTag = string.IsNullOrEmpty(_envSummary.WpsVersion) ? "未安装" : _envSummary.WpsVersion;
        string imeTag = string.Join(", ", _envSummary.InstalledIMEs);

        TxtEnvSummary.Text = $"系统: {_envSummary.OSCaption} | 权限: {adminTag} | 原生防护: {avTag} | WPS: {wpsTag} | 输入法: {imeTag}";
        AppendLog($"[系统基线] 已完成环境识别: {TxtEnvSummary.Text}");
        CrashLogger.SetEnvironmentSummary(_envSummary);
    }

    private void RestartAsAdmin()
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
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception w32Ex) when (w32Ex.NativeErrorCode == 1223)
        {
            AppendLog("[提权取消] 用户在 UAC 对话框中取消了提权。");
        }
        catch (Exception ex)
        {
            var logPath = CrashLogger.LogError("以管理员身份重启异常", ex);
            MessageBox.Show($"以管理员身份重启失败: {ex.Message}\n\n详细排查日志已实时记录至运行项目目录：\n{logPath}", "提权错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        await PerformScanAsync();
    }

    private async Task PerformScanAsync()
    {
        try
        {
            // 启用扫描动态动画效果
            PillScanning.Visibility = Visibility.Visible;
            ScanProgressBar.Visibility = Visibility.Visible;
            BtnScan.IsEnabled = false;
            BtnClean.IsEnabled = false;

            if (Resources["PulseAnimation"] is Storyboard sb)
            {
                Storyboard.SetTarget(sb, DotPulse);
                sb.Begin();
            }

            AppendLog("[阶段一：系统扫描] 正在只读扫描系统配置、软件列表、锁屏小组件、计划任务与残留目录...");
            var rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules");

            // 将所有注册表检索、软件与规则匹配全部转移至后台线程异步执行，确保 UI 线程丝滑无阻
            var foundItems = await Task.Run(() =>
            {
                var rules = _ruleEngine.LoadRules(rulesDir);
                var installedApps = _systemScanner.ScanInstalledSoftware();
                return _ruleEngine.EvaluateRules(rules, _envSummary, installedApps);
            });

            _scanItems = new ObservableCollection<ScanItem>(foundItems);
            GridScanItems.ItemsSource = _scanItems;
            SyncHeaderCheckState();

            int autoSafeCount = foundItems.Count(i => i.Risk == RiskLevel.AutoSafe);
            int adminCount = foundItems.Count(i => i.Risk == RiskLevel.AdminRequired);
            int confirmCount = foundItems.Count(i => i.Risk == RiskLevel.UserConfirm);

            TxtScanStats.Text = $"扫描完成：共发现 {foundItems.Count} 个可治理项 —— [A类自动安全项: {autoSafeCount}] [B类需管理员权限: {adminCount}] [C类需用户确认: {confirmCount}]";
            BtnClean.IsEnabled = foundItems.Count > 0;

            // 结果列表渐入动画效果
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(300));
            GridScanItems.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            AppendLog($"[阶段二：风险分析] {TxtScanStats.Text}");
            foreach (var item in foundItems)
            {
                AppendLog($"  -> [{item.Risk}] {item.Title} | 当前: {item.CurrentState} => 目标: {item.TargetState}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[扫描异常] 扫描过程发生错误: {ex.Message}");
            MessageBox.Show($"扫描过程发生异常: {ex.Message}", "扫描错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PillScanning.Visibility = Visibility.Collapsed;
            ScanProgressBar.Visibility = Visibility.Collapsed;
            BtnScan.IsEnabled = true;
            SyncHeaderCheckState();
        }
    }

    private async void BtnClean_Click(object sender, RoutedEventArgs e)
    {
        var selected = new List<ScanItem>();
        var results = new List<ExecutionItemResult>();
        try
        {
            selected = _scanItems.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请至少勾选一项需要处理的项目。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 针对 C 类需确认项目（卸载软件、删除残留等）进行明确确认
            var confirmItems = selected.Where(i => i.RequiresUserConfirm).ToList();
            if (confirmItems.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("您勾选了以下需要人工确认的操作（包含卸载软件或清理残留）：");
                sb.AppendLine();
                foreach (var c in confirmItems)
                {
                    sb.AppendLine($"* {c.Title}");
                    if (!c.SupportsRestore)
                        sb.AppendLine("  (注意：此操作不可通过“恢复设置”自动回滚)");
                }
                sb.AppendLine();
                sb.AppendLine("是否确认继续执行？");

                if (MessageBox.Show(sb.ToString(), "操作安全确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    AppendLog("[用户取消] 已中止清理操作。");
                    return;
                }
            }

            // 锁定按钮并显示进度
            BtnClean.IsEnabled = false;
            BtnScan.IsEnabled = false;
            ScanProgressBar.Visibility = Visibility.Visible;

            // 记录本次清理版本快照
            var snapshotVersion = _backupService.GetNextVersion();
            var initialBackupCount = _backupService.LoadBackups().Count;

            AppendLog($"[阶段三：自动执行] 开始处理 {selected.Count} 个选中项目（目标快照版本: {snapshotVersion}）...");
            results.Clear();

            // 拆分普通权限可执行项与需管理员提权项
            var normalItems = selected.Where(i => !i.RequiresAdmin || _envSummary.IsAdmin).ToList();
            var elevateItems = selected.Where(i => i.RequiresAdmin && !_envSummary.IsAdmin).ToList();

            // 如果勾选了系统级项目且当前不是管理员，优先推荐直接以管理员权限重启运行（体验最好、性能最快、绝对不报错）
            if (elevateItems.Count > 0 && !_envSummary.IsAdmin)
            {
                var askResult = MessageBox.Show(
                    $"您选中的项目中包含 {elevateItems.Count} 个系统级治理项（锁屏策略、系统服务、系统目录免疫等），需要管理员权限执行。\n\n" +
                    "【推荐方案】点击【是】：立即以管理员身份重新启动本软件进行清理（最稳定，绝不报错）\n" +
                    "【尝试执行】点击【否】：在当前窗口尝试在后台提权执行\n" +
                    "【取消清理】点击【取消】：返回主界面",
                    "需要管理员权限",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (askResult == MessageBoxResult.Yes)
                {
                    RestartAsAdmin();
                    return;
                }
                else if (askResult == MessageBoxResult.Cancel)
                {
                    AppendLog("[用户取消] 已中止清理操作。");
                    return;
                }
            }

            // 异步后台执行普通项，避免卡死界面
            if (normalItems.Count > 0)
            {
                var normalResults = await Task.Run(() =>
                {
                    var resList = new List<ExecutionItemResult>();
                    foreach (var item in normalItems)
                    {
                        resList.Add(_ruleEngine.ExecuteScanItem(item));
                    }
                    return resList;
                });

                results.AddRange(normalResults);
                foreach (var res in normalResults)
                {
                    AppendLog($"  [{(res.Success ? "成功" : "失败")}] {res.Title}: {res.Message}");
                }
            }

            // 按需请求一次 UAC 管理员提权执行系统级项
            if (elevateItems.Count > 0)
            {
                AppendLog($"[按需提权] 正在为 {elevateItems.Count} 个系统级项目请求管理员权限，请在弹出的系统对话框中点击“是”...");
                var elevatedResults = await Task.Run(() => RunElevatedBatch(elevateItems));
                results.AddRange(elevatedResults);
                foreach (var res in elevatedResults)
                {
                    AppendLog($"  [提权-{(res.Success ? "成功" : "失败")}] {res.Title}: {res.Message}");
                }
            }

            // 归档本次清理生成的版本快照
            try
            {
                var updatedBackups = _backupService.LoadBackups();
                var newEntries = updatedBackups.Skip(initialBackupCount).ToList();
                if (newEntries.Count > 0)
                {
                    _backupService.CreateSnapshot(snapshotVersion, newEntries, $"系统净化快照 [{snapshotVersion}]", _envSummary.OSCaption);
                    AppendLog($"[版本快照] 本次修改已归档为历史版本号: {snapshotVersion} (包含 {newEntries.Count} 条配置快照)");
                }
            }
            catch (Exception ex)
            {
                CrashLogger.LogError("归档版本快照异常", ex);
            }

            // 生成阶段四清理报告
            _lastReport = new CleanReport
            {
                OSVersion = $"{_envSummary.OSCaption} ({_envSummary.OSBuild})",
                RanAsAdmin = _envSummary.IsAdmin || elevateItems.Count > 0,
                TotalScannedItems = _scanItems.Count,
                IssuesFound = _scanItems.Count,
                SucceededCount = results.Count(r => r.Success),
                FailedCount = results.Count(r => !r.Success),
                SkippedOrUnconfirmedCount = _scanItems.Count - selected.Count,
                Results = results
            };

            BtnExport.IsEnabled = true;
            AppendLog($"[阶段四：清理结果] 处理完成！成功: {_lastReport.SucceededCount} 项，失败: {_lastReport.FailedCount} 项，跳过未勾选: {_lastReport.SkippedOrUnconfirmedCount} 项。");

            // 若有清理失败项，自动将完整的“系统环境信息”和“清除什么东西报错明细”写入运行项目目录下的 error.txt
            if (_lastReport.FailedCount > 0)
            {
                var failLogPath = CrashLogger.LogCleanExecutionDetails(_envSummary, selected, results, null);
                AppendLog($"[排查提醒] 本次有 {_lastReport.FailedCount} 个项目清理报错，系统环境、目标路径与详细报错原因已记录至：{failLogPath}");
            }

            // 提示用户重启电脑以使锁屏小组件、服务及系统策略彻底生效
            PromptRebootComputer(_lastReport.SucceededCount);

            // 异步刷新扫描视图
            await PerformScanAsync();
        }
        catch (Exception ex)
        {
            var logPath = CrashLogger.LogCleanExecutionDetails(_envSummary, selected, results, ex);
            AppendLog($"[执行异常] 清理过程中断: {ex.Message} (详见 error.txt)");
            MessageBox.Show($"清理过程发生异常: {ex.Message}\n\n已将详细系统环境、清除目标与报错日志保存至：\n{logPath}", "清理错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ScanProgressBar.Visibility = Visibility.Collapsed;
            BtnClean.IsEnabled = true;
            BtnScan.IsEnabled = true;
        }
    }

    private void PromptRebootComputer(int successCount)
    {
        string taskmgrHint = "";
        try
        {
            if (Process.GetProcessesByName("Taskmgr").Length > 0)
            {
                taskmgrHint = "\n\n【任务管理器显示提示】\n检测到 Windows 任务管理器当前正在运行。任务管理器具有启动项界面缓存，若要查看火绒与 360 等启动项的最新【已禁用】状态，请将任务管理器关闭后重新打开。";
            }
        }
        catch { }

        var rebootPrompt = $"清理已全部完成！共成功处理 {successCount} 项。\n\n" +
                           "【重要提示】\n" +
                           "锁屏天气组件、资讯卡片关闭、服务停用与系统防静默加固策略，需要在重启计算机后方能彻底生效。" +
                           taskmgrHint + "\n\n" +
                           "是否立即重启电脑？\n" +
                           "* 点击【是】：立即重启计算机以完成最终净化\n" +
                           "* 点击【否】：稍后手动重启电脑";

        if (MessageBox.Show(rebootPrompt, "清理完成 - 建议重启电脑", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            AppendLog("[用户授权] 正在执行系统重启指令...");
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                }
                catch (Exception ex)
                {
                    CrashLogger.LogError("系统重启指令异常", ex);
                    AppendLog($"[执行失败] 启动系统重启命令失败: {ex.Message}");
                }
            }
        }
        else
        {
            AppendLog("[系统提示] 您选择了稍后重启。请在工作保存后尽快重启计算机，以便所有清理配置完全生效。");
        }
    }

    private List<ExecutionItemResult> RunElevatedBatch(List<ScanItem> elevateItems)
    {
        var tempIn = Path.GetTempFileName();
        var tempOut = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempIn, JsonSerializer.Serialize(elevateItems));

            string exePath;
            string arguments;
            var processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "CleanYourPC.App.exe";

            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                exePath = processPath;
                var dllPath = Path.Combine(AppContext.BaseDirectory, "CleanYourPC.App.dll");
                arguments = $"\"{dllPath}\" --elevated-worker \"{tempIn}\" \"{tempOut}\"";
            }
            else
            {
                exePath = processPath;
                arguments = $"--elevated-worker \"{tempIn}\" \"{tempOut}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                CrashLogger.LogError("提权启动失败", null, "Process.Start 返回空进程对象");
                return new List<ExecutionItemResult>
                {
                    new ExecutionItemResult { Title = "提权启动失败", Success = false, Message = "未能启动提权子进程。" }
                };
            }

            // 安全等待超时 60 秒，若超过则强行 Kill 避免界面永久卡死
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(); } catch { }
                var itemsSummary = string.Join("\n", elevateItems.Select(i => $"  * [{i.RuleId}] {i.Title} (目标路径: {i.AssociatedRule?.TargetPath ?? i.TargetState})"));
                CrashLogger.LogError("提权超时中断 (60秒)", null, $"提权子进程超过 60 秒未返回。请求清理的系统级项目清单如下:\n{itemsSummary}");
                return new List<ExecutionItemResult>
                {
                    new ExecutionItemResult { Title = "提权超时中断", Success = false, Message = "提权子进程执行超过 60 秒，已安全终止。" }
                };
            }

            if (File.Exists(tempOut))
            {
                var json = File.ReadAllText(tempOut);
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonSerializer.Deserialize<List<ExecutionItemResult>>(json) ?? new List<ExecutionItemResult>();
            }
            var missingSummary = string.Join("\n", elevateItems.Select(i => $"  * [{i.RuleId}] {i.Title} (目标路径: {i.AssociatedRule?.TargetPath ?? i.TargetState})"));
            CrashLogger.LogError("提权执行未返回结果", null, $"tempOut 文件不存在或为空: {tempOut}\n请求清理的系统级项目清单:\n{missingSummary}");
            return new List<ExecutionItemResult>
            {
                new ExecutionItemResult { Title = "提权执行未返回结果", Success = false, Message = "子进程未能成功生成处理明细。" }
            };
        }
        catch (System.ComponentModel.Win32Exception w32Ex) when (w32Ex.NativeErrorCode == 1223)
        {
            return new List<ExecutionItemResult>
            {
                new ExecutionItemResult { Title = "用户取消提权", Success = false, Message = "您在 Windows UAC 提示中取消了提权授权，系统级项目已跳过。" }
            };
        }
        catch (Exception ex)
        {
            CrashLogger.LogError("提权批处理异常", ex);
            return new List<ExecutionItemResult>
            {
                new ExecutionItemResult { Title = "提权执行异常", Success = false, Message = ex.Message }
            };
        }
        finally
        {
            try { if (File.Exists(tempIn)) File.Delete(tempIn); } catch { }
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
        }
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        var snapshots = _backupService.LoadSnapshots();
        if (snapshots.Count == 0)
        {
            MessageBox.Show("当前没有可恢复的历史版本快照记录。", "恢复历史设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ListSnapshots.ItemsSource = snapshots.AsEnumerable().Reverse().ToList();
        if (ListSnapshots.Items.Count > 0)
            ListSnapshots.SelectedIndex = 0;

        ModalRestoreHistory.Visibility = Visibility.Visible;
    }

    private void BtnCloseRestoreModal_Click(object sender, RoutedEventArgs e)
    {
        ModalRestoreHistory.Visibility = Visibility.Collapsed;
    }

    private void BtnConfirmRestoreVersion_Click(object sender, RoutedEventArgs e)
    {
        if (ListSnapshots.SelectedItem is not BackupSnapshot selected)
        {
            MessageBox.Show("请在列表中选择一个需要回滚的版本号。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show($"确定要将系统配置还原至版本 [{selected.Version}] 吗？\n该版本创建于: {selected.CreatedAt:yyyy-MM-dd HH:mm:ss}\n包含项: {selected.ItemCount} 项",
            "确认版本回滚", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        ModalRestoreHistory.Visibility = Visibility.Collapsed;
        AppendLog($"[恢复历史] 开始按版本号 [{selected.Version}] 执行配置回滚...");

        var (ok, msg, count) = _backupService.RestoreSnapshotByVersion(selected.Version);
        AppendLog($"  [{(ok ? "已还原" : "失败")}] {selected.Version}: {msg}");

        MessageBox.Show($"版本 [{selected.Version}] 恢复完成！共回滚 {count} 项配置。\n建议重启计算机以生效。", "恢复完成", MessageBoxButton.OK, MessageBoxImage.Information);
        _ = PerformScanAsync();
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport == null) return;

        var dlg = new SaveFileDialog
        {
            Title = "导出清理报告",
            Filter = "文本报告 (*.txt)|*.txt|JSON 报告 (*.json)|*.json",
            FilterIndex = 1,
            DefaultExt = ".txt",
            FileName = $"CleanYourPC-清理报告-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dlg.ShowDialog() == true)
        {
            if (dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(_lastReport, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dlg.FileName, json, Encoding.UTF8);
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine("                       CleanYourPC 系统净化与广告治理报告");
                sb.AppendLine("================================================================================");
                sb.AppendLine($"生成时间: {_lastReport.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"操作系统: {_lastReport.OSVersion}");
                sb.AppendLine($"运行权限: {(_lastReport.RanAsAdmin ? "管理员模式 (已授权系统级修复)" : "标准用户模式")}");
                sb.AppendLine();
                sb.AppendLine("【治理成果概览】");
                sb.AppendLine($"  * 扫描问题总数 : {_lastReport.TotalScannedItems} 项");
                sb.AppendLine($"  * 成功处理项   : {_lastReport.SucceededCount} 项");
                sb.AppendLine($"  * 处理失败项   : {_lastReport.FailedCount} 项");
                sb.AppendLine($"  * 未勾选跳过项 : {_lastReport.SkippedOrUnconfirmedCount} 项");
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine();
                sb.AppendLine("【详细处理明细】");
                int index = 1;
                foreach (var r in _lastReport.Results)
                {
                    string statusTag = r.Success ? "【成功】" : "【失败】";
                    sb.AppendLine($"{index++,2}. {statusTag} [{r.Category}] {r.Title}");
                    sb.AppendLine($"    * 治理前状态: {r.BeforeValue}");
                    sb.AppendLine($"    * 治理后状态: {r.AfterValue}");
                    sb.AppendLine($"    * 执行反馈  : {r.Message}");
                    sb.AppendLine();
                }

                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine("【重启生效建议】");
                sb.AppendLine("部分系统级策略（如锁屏天气动态组件、服务停用、系统策略加固）需在重启计算机后彻底生效。");
                sb.AppendLine();
                sb.AppendLine("【恢复与安全提示】");
                sb.AppendLine("1. 本工具所有修改过的注册表、服务与权限设置均已自动生成版本快照。");
                sb.AppendLine("2. 如需撤销修改，可在 CleanYourPC 界面中点击【恢复历史设置 (按版本)】选择指定版本号一键回滚。");
                sb.AppendLine("3. 开源仓库: https://github.com/CleanYourPC");
                sb.AppendLine("================================================================================");

                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            }
            AppendLog($"[导出成功] 文本清理报告已保存至: {dlg.FileName}");
        }
    }

    private void AppendLog(string line)
    {
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        TxtLog.ScrollToEnd();
        CrashLogger.LogRunning(line);
    }

    private void ChkSelectAllHeader_Click(object sender, RoutedEventArgs e)
    {
        bool isChecked = ChkSelectAllHeader.IsChecked == true;
        foreach (var item in _scanItems)
        {
            if (item.CanExecute)
            {
                item.IsSelected = isChecked;
            }
        }
        SyncHeaderCheckState();
        AppendLog(isChecked ? "[一键全选] 已勾选列表中全部可治理项目。" : "[取消全选] 已取消列表中所有勾选。");
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_scanItems.Count == 0) return;
        foreach (var item in _scanItems)
        {
            if (item.CanExecute)
            {
                item.IsSelected = true;
            }
        }
        SyncHeaderCheckState();
        AppendLog("[一键全选] 已将列表中全部可治理项标记为勾选。");
    }

    private void BtnSelectRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (_scanItems.Count == 0) return;
        foreach (var item in _scanItems)
        {
            // 推荐勾选：A类自动安全项与无确认要求的B类管理员安全项，默认跳过C类需确认项和D类
            item.IsSelected = (item.Risk == RiskLevel.AutoSafe || item.Risk == RiskLevel.AdminRequired) && !item.RequiresUserConfirm;
        }
        SyncHeaderCheckState();
        AppendLog("[一键推荐] 已重置为推荐选中模式（勾选安全项目，保留高风险/卸载确认项需手动勾选）。");
    }

    private void BtnUnselectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_scanItems.Count == 0) return;
        foreach (var item in _scanItems)
        {
            item.IsSelected = false;
        }
        SyncHeaderCheckState();
        AppendLog("[取消全选] 已取消列表中所有勾选。");
    }

    private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            e.Handled = true; // 阻止事件冒泡干扰 DataGrid 行选择与单元格交互
            SyncHeaderCheckState();
        }
        catch (Exception ex)
        {
            AppendLog($"[选择异常] 状态更新错误: {ex.Message}");
        }
    }

    private void SyncHeaderCheckState()
    {
        try
        {
            if (_scanItems.Count == 0)
            {
                if (ChkSelectAllHeader != null) ChkSelectAllHeader.IsChecked = false;
                BtnClean.IsEnabled = false;
                return;
            }

            var actionableItems = _scanItems.Where(i => i.CanExecute).ToList();
            int selectedCount = actionableItems.Count(i => i.IsSelected);

            if (ChkSelectAllHeader != null)
            {
                if (actionableItems.Count > 0 && selectedCount == actionableItems.Count)
                {
                    ChkSelectAllHeader.IsChecked = true;
                }
                else if (selectedCount == 0)
                {
                    ChkSelectAllHeader.IsChecked = false;
                }
                else
                {
                    ChkSelectAllHeader.IsChecked = null; // 三态：部分选中
                }
            }

            BtnClean.IsEnabled = selectedCount > 0;
        }
        catch { }
    }
}