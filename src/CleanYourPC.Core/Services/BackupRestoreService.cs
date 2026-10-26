using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using CleanYourPC.Core.Models;
using Microsoft.Win32;

namespace CleanYourPC.Core.Services;

public class BackupRestoreService
{
    private readonly string _backupFilePath;
    private readonly string _snapshotsFilePath;

    public BackupRestoreService(string? customBackupPath = null, string? customSnapshotsPath = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "CleanYourPC", "Backups");
        Directory.CreateDirectory(dir);
        _backupFilePath = customBackupPath ?? Path.Combine(dir, "backup-history.json");
        _snapshotsFilePath = customSnapshotsPath ?? 
            (customBackupPath != null ? Path.ChangeExtension(customBackupPath, ".snapshots.json") : Path.Combine(dir, "snapshots-history.json"));
    }

    public List<BackupSnapshot> LoadSnapshots()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_snapshotsFilePath))
            {
                // 如果历史快照文件不存在但存在旧的 backup-history.json，自动升级为 v1.0 快照
                var oldBackups = LoadBackups();
                if (oldBackups.Count > 0)
                {
                    var initialSnapshot = new BackupSnapshot
                    {
                        Version = "v1.0",
                        CreatedAt = oldBackups.First().Timestamp,
                        Description = "系统初始清理快照",
                        Entries = oldBackups
                    };
                    var list = new List<BackupSnapshot> { initialSnapshot };
                    SafeWriteAllText(_snapshotsFilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                    return list;
                }
                return new List<BackupSnapshot>();
            }

            try
            {
                var json = File.ReadAllText(_snapshotsFilePath);
                return JsonSerializer.Deserialize<List<BackupSnapshot>>(json) ?? new List<BackupSnapshot>();
            }
            catch
            {
                return new List<BackupSnapshot>();
            }
        }
    }

    public string GetNextVersion()
    {
        var snapshots = LoadSnapshots();
        if (snapshots.Count == 0) return "v1.0";

        int maxMinor = 0;
        foreach (var s in snapshots)
        {
            var v = s.Version.TrimStart('v', 'V');
            var parts = v.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[1], out int minor))
            {
                if (minor > maxMinor) maxMinor = minor;
            }
        }
        return $"v1.{maxMinor + 1}";
    }

    private readonly object _fileLock = new();

    private void SafeWriteAllText(string filePath, string content)
    {
        lock (_fileLock)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllText(filePath, content);
                    return;
                }
                catch (IOException) when (i < 4)
                {
                    Thread.Sleep(50 * (i + 1));
                }
                catch (Exception ex)
                {
                    CrashLogger.LogError($"写入备份文件失败 [{filePath}]", ex);
                    return;
                }
            }
        }
    }

    public void SaveSnapshot(BackupSnapshot snapshot)
    {
        lock (_fileLock)
        {
            var list = LoadSnapshots();
            list.RemoveAll(s => s.Version.Equals(snapshot.Version, StringComparison.OrdinalIgnoreCase));
            list.Add(snapshot);
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            SafeWriteAllText(_snapshotsFilePath, json);
        }
    }

    public void CreateSnapshot(string version, List<BackupEntry> entries, string description, string osVersion = "")
    {
        lock (_fileLock)
        {
            foreach (var e in entries)
            {
                e.SnapshotVersion = version;
            }

            var snapshot = new BackupSnapshot
            {
                Version = version,
                CreatedAt = DateTime.Now,
                Description = description,
                OSVersion = osVersion,
                Entries = entries
            };
            SaveSnapshot(snapshot);

            // 同步追加到扁平 backup-history.json 保持兼容
            var allEntries = LoadBackups();
            allEntries.AddRange(entries);
            SafeWriteAllText(_backupFilePath, JsonSerializer.Serialize(allEntries, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public (bool Success, string Message, int RestoredCount) RestoreSnapshotByVersion(string version)
    {
        var snapshots = LoadSnapshots();
        var target = snapshots.FirstOrDefault(s => s.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
        if (target == null)
            return (false, $"未找到版本号为 [{version}] 的历史备份快照。", 0);

        int restored = 0;
        foreach (var entry in target.Entries.AsEnumerable().Reverse())
        {
            var (ok, _) = RestoreEntry(entry);
            if (ok) restored++;
        }

        return (true, $"已按版本号 [{version}] 成功恢复 {restored} 项系统配置。", restored);
    }

    public List<BackupEntry> LoadBackups()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_backupFilePath)) return new List<BackupEntry>();
            try
            {
                var json = File.ReadAllText(_backupFilePath);
                return JsonSerializer.Deserialize<List<BackupEntry>>(json) ?? new List<BackupEntry>();
            }
            catch
            {
                return new List<BackupEntry>();
            }
        }
    }

    public void SaveBackupEntry(BackupEntry entry)
    {
        lock (_fileLock)
        {
            var list = LoadBackups();
            list.Add(entry);
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            SafeWriteAllText(_backupFilePath, json);
        }
    }

    public void SaveBackupEntries(IEnumerable<BackupEntry> entries)
    {
        lock (_fileLock)
        {
            var list = LoadBackups();
            list.AddRange(entries);
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            SafeWriteAllText(_backupFilePath, json);
        }
    }

    public (bool Success, string Message) RestoreEntry(BackupEntry entry)
    {
        if (!entry.SupportsRestore)
            return (false, $"项目 [{entry.RuleName}] 不支持自动恢复。");

        try
        {
            switch (entry.ActionType)
            {
                case RuleActionType.RegistryValue:
                    return RestoreRegistryValue(entry);

                case RuleActionType.DisableShellExtension:
                    return RestoreShellExtension(entry);

                case RuleActionType.CleanShortcutArguments:
                    return RestoreShortcutArguments(entry);

                case RuleActionType.DeleteRegistryKey:
                    return RestoreRegistryKey(entry);

                case RuleActionType.LockDirectoryWrite:
                    return UnlockDirectoryWrite(Environment.ExpandEnvironmentVariables(entry.TargetPath));

                case RuleActionType.DisableService:
                    if (OperatingSystem.IsWindows())
                    {
                        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{entry.TargetPath}", writable: true);
                        if (key != null && int.TryParse(entry.OriginalValue, out int origStart))
                        {
                            key.SetValue("Start", origStart, RegistryValueKind.DWord);
                            return (true, $"已恢复服务 [{entry.TargetPath}] 的原始启动类型 ({origStart})。");
                        }
                    }
                    return (false, "未找到服务注册表项或缺少管理员权限。");

                case RuleActionType.CleanBrowserNotifications:
                    return RestoreBrowserNotifications(entry);

                default:
                    return (false, "该操作类型暂不支持自动回滚。");
            }
        }
        catch (Exception ex)
        {
            return (false, $"恢复失败: {ex.Message}");
        }
    }

    private (bool Success, string Message) RestoreRegistryValue(BackupEntry entry)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "当前非 Windows 环境，跳过注册表还原。");

        var (root, subKeyPath) = RuleEngine.ParseRegistryPath(entry.TargetPath);
        using var key = root.OpenSubKey(subKeyPath, writable: true);
        if (key == null) return (true, "目标注册表路径已不存在，无需恢复。");

        if (!entry.OriginalExisted)
        {
            key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            return (true, $"已删除新增的注册表项 {entry.ValueName}（恢复至初始未配置状态）。");
        }

        if (int.TryParse(entry.OriginalValue, out int dwordVal))
            key.SetValue(entry.ValueName, dwordVal, RegistryValueKind.DWord);
        else
            key.SetValue(entry.ValueName, entry.OriginalValue ?? "", RegistryValueKind.String);

        if (subKeyPath.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase) || subKeyPath.EndsWith(@"\RunOnce", StringComparison.OrdinalIgnoreCase))
        {
            StartupAndBrowserScanner.EnableStartupItem(entry.ValueName);
        }

        return (true, $"已将 {entry.ValueName} 恢复为原始值 [{entry.OriginalValue}] 并重新启用自启。");
    }

    private (bool Success, string Message) RestoreShellExtension(BackupEntry entry)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "当前非 Windows 环境，跳过 Shell 扩展还原。");

        const string blockedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

        // 1. 如果备份了 ContextMenuHandlers 注册表子项，进行重建还原
        if (!string.IsNullOrWhiteSpace(entry.TargetPath) && !entry.TargetPath.Contains("Shell Extensions\\Blocked", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (root, subKeyPath) = RuleEngine.ParseRegistryPath(entry.TargetPath);
                using var key = root.CreateSubKey(subKeyPath, writable: true);
                if (!string.IsNullOrEmpty(entry.BackupData))
                {
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(entry.BackupData);
                    if (values != null)
                    {
                        foreach (var kv in values)
                        {
                            key.SetValue(string.IsNullOrEmpty(kv.Key) ? null : kv.Key, kv.Value ?? "", RegistryValueKind.String);
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(entry.OriginalValue))
                {
                    key.SetValue(null, entry.OriginalValue, RegistryValueKind.String);
                }
            }
            catch { }
        }

        // 2. 解除 HKCU 与 HKLM（64位及32位视图）中的 Blocked 屏蔽标记
        if (!string.IsNullOrWhiteSpace(entry.ValueName))
        {
            try
            {
                using var hkcu = Registry.CurrentUser.OpenSubKey(blockedPath, writable: true);
                if (entry.OriginalExisted && !string.IsNullOrEmpty(entry.OriginalValue))
                {
                    hkcu?.SetValue(entry.ValueName, entry.OriginalValue, RegistryValueKind.String);
                }
                else
                {
                    hkcu?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                }
            }
            catch { }

            try
            {
                using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                              .OpenSubKey(blockedPath, writable: true);
                hklm64?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            }
            catch { }

            try
            {
                using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                                              .OpenSubKey(blockedPath, writable: true);
                hklm32?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            }
            catch { }
        }

        return (true, $"已解除对 Shell 扩展 [{entry.ValueName}] 的屏蔽限制并恢复关联右键项。");
    }

    private (bool Success, string Message) RestoreShortcutArguments(BackupEntry entry)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "当前非 Windows 环境，跳过快捷方式还原。");

        if (!File.Exists(entry.TargetPath))
            return (false, $"快捷方式文件已不存在: {entry.TargetPath}");

        bool ok = RogueItemScanner.UpdateShortcutArguments(entry.TargetPath, entry.OriginalValue ?? "");
        return (ok, ok ? $"已将快捷方式 [{Path.GetFileName(entry.TargetPath)}] 参数还原为原始值。" : "还原快捷方式参数失败。");
    }

    private (bool Success, string Message) RestoreRegistryKey(BackupEntry entry)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "当前非 Windows 环境，跳过注册表还原。");

        var (root, subKeyPath) = RuleEngine.ParseRegistryPath(entry.TargetPath);
        if (entry.OriginalExisted)
        {
            using var key = root.CreateSubKey(subKeyPath, writable: true);
            if (!string.IsNullOrEmpty(entry.BackupData))
            {
                try
                {
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(entry.BackupData);
                    if (values != null)
                    {
                        foreach (var kv in values)
                        {
                            key.SetValue(string.IsNullOrEmpty(kv.Key) ? null : kv.Key, kv.Value ?? "", RegistryValueKind.String);
                        }
                    }
                }
                catch { }
            }
            else if (!string.IsNullOrEmpty(entry.OriginalValue))
            {
                key.SetValue(null, entry.OriginalValue, RegistryValueKind.String);
            }
            return (true, $"已重建注册表子项 [{entry.TargetPath}] 并还原默认值。");
        }
        else
        {
            root.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
            return (true, $"已移除该注册表子项 [{entry.TargetPath}]。");
        }
    }

    public static (bool Success, string Message) UnlockDirectoryWrite(string expandedDir)
    {
        if (!Directory.Exists(expandedDir))
            return (true, "目录不存在，无需解锁。");

        if (!OperatingSystem.IsWindows())
        {
            return (true, "非 Windows 系统无需解除 NTFS 写入限制。");
        }

        var dirInfo = new DirectoryInfo(expandedDir);
        var security = dirInfo.GetAccessControl();
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var denyRule = new FileSystemAccessRule(
            everyone,
            FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny);

        security.RemoveAccessRule(denyRule);
        dirInfo.SetAccessControl(security);
        return (true, $"已解除目录 [{expandedDir}] 的写入限制。");
    }

    private (bool Success, string Message) RestoreBrowserNotifications(BackupEntry entry)
    {
        try
        {
            if (string.IsNullOrEmpty(entry.TargetPath) || string.IsNullOrEmpty(entry.BackupData))
                return (false, "缺少还原所需的配置备份数据。");

            File.WriteAllText(entry.TargetPath, entry.BackupData);
            return (true, $"已还原浏览器配置文件 [{Path.GetFileName(entry.TargetPath)}]。");
        }
        catch (Exception ex)
        {
            return (false, $"还原浏览器配置失败: {ex.Message}");
        }
    }
}