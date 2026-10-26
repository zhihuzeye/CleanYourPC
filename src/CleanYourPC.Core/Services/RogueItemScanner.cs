using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CleanYourPC.Core.Models;
using Microsoft.Win32;

namespace CleanYourPC.Core.Services;

public class RogueItemScanner
{
    // 已知常见推广软件或扩展插件关键词特征库（参考 RogueCleaner 厂商特征库）
    private static readonly (string Vendor, string[] Patterns, string RiskReason)[] RogueSignatures = new[]
    {
        ("360 系列", new[] {
            "Qihoo", "Qihu", "奇虎", "360Safe", "360sd", "360se", "360chrome", "360zip",
            "360Desktop", "360Wallpaper", "360Pic", "360KanPic", "Safe360Ext", "SoftMgrExt",
            "360看图", "360压缩", "360极速浏览器", "360安全浏览器", "360驱动大师"
        }, "右键或自启常驻推广组件"),
        ("WPS / 金山云盘", new[] {
            "WPS", "金山", "Kingsoft", "kdesktop", "kclouddrive", "wpscloud",
            "WpsDrive", "qingshell", "qingnse", "kwps", "kdocs", "wpscloudsvr",
            "QingNseContextMenu", "kwpsshellext", "qingshellext", "kdesktopshellext", "qkdesktopshellext"
        }, "WPS / 金山注入的云盘与右键组件"),
        ("2345 系列", new[] { "2345", "2345Pic", "2345Zip", "2345Explorer", "2345Kantuwang", "好压", "2345好压", "2345看图王" }, "侵入性右键关联及推广弹窗"),
        ("百度网盘", new[] { "BaiduNetdisk", "YunShell", "YunShellExt", "BaiduNetdiskImageViewer", "cloudpic.dll" }, "右键菜单注入与看图关联"),
        ("夸克网盘", new[] { "QuarkCloudDrive", "QuarkNetdisk", "pan.quark.cn", "QuarkPDF", "external_rclick" }, "右键菜单注入与多功能推广"),
        ("搜狗系列", new[] { "Sogou", "SogouInput", "SogouExplorer", "SogouNews", "SogouFlash", "SogouTips", "SGImeGuard" }, "输入法附带新闻弹窗与守护常驻"),
        ("快压/好压等看图压缩", new[] { "KuaiZip", "Kuaizip", "HaoZip", "KanPic", "看图王", "极速看图" }, "静默强占右键与文件关联"),
        ("Flash 特供版", new[] { "FlashHelperService", "FlashCenter", "Flash大厅", "flash.cn" }, "退役组件后台常驻与广告推送"),
        ("钉钉上传", new[] { "DingTalkShellExt", "DingTalkUpload", "上传到钉钉" }, "文件右键菜单注入")
    };

    // 常见 WPS 云盘虚拟盘符专属 CLSID
    private static readonly HashSet<string> KnownWpsDriveClsids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{5FCD4425-CA3A-48F4-A57C-B8A7572BE570}",
        "{8B58348B-21BE-4848-BD2D-F2E36C561EE8}",
        "{F7E04153-6D65-4F3B-819A-B357F3A9D1C7}",
        "{D0B48003-E565-4D0E-8A67-28E73072AC4F}",
        "{C5FB3CD2-2B3E-4B07-AE90-B8B708914F44}"
    };

    /// <summary>
    /// 判断是否命中 WPS 云盘相关特征
    /// </summary>
    public static bool IsWpsDriveMatch(string clsid, string display, string localized, string itemName, string targetFolder, string dllPath)
    {
        if (KnownWpsDriveClsids.Contains(clsid)) return true;

        string combined = $"{clsid} {display} {localized} {itemName} {targetFolder} {dllPath}";
        if (combined.Contains("WPS云盘", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("WPS网盘", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("kclouddrive", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("wpscloud", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("WpsDrive", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool hasWpsVendor = combined.Contains("WPS", StringComparison.OrdinalIgnoreCase) ||
                            combined.Contains("Kingsoft", StringComparison.OrdinalIgnoreCase) ||
                            combined.Contains("金山", StringComparison.OrdinalIgnoreCase);

        bool hasCloudConcept = combined.Contains("云盘", StringComparison.OrdinalIgnoreCase) ||
                               combined.Contains("网盘", StringComparison.OrdinalIgnoreCase) ||
                               combined.Contains("Drive", StringComparison.OrdinalIgnoreCase) ||
                               combined.Contains("kdesktop", StringComparison.OrdinalIgnoreCase) ||
                               combined.Contains("cloud", StringComparison.OrdinalIgnoreCase);

        return hasWpsVendor && hasCloudConcept;
    }

    // 常见浏览器可执行文件名称
    private static readonly HashSet<string> BrowserExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "360se.exe", "360chrome.exe", "firefox.exe",
        "2345explorer.exe", "sogouexplorer.exe", "iexplore.exe", "brave.exe", "opera.exe"
    };

    // 常见推广主页与网址特征
    private static readonly string[] RogueUrlPatterns = new[]
    {
        "hao123.com", "2345.com", "hao.360.cn", "123.sogou.com", "duba.com",
        "dh.2345.com", "?tn=", "?src=", "sp=navigation", "site=home"
    };

    /// <summary>
    /// 检查指定的 CLSID 是否已在 Windows 系统的 Shell Extensions\Blocked 屏蔽列表中（HKCU 或 HKLM，含 64位与 32位）
    /// </summary>
    public static bool IsShellExtensionBlocked(string clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid)) return false;
        if (!OperatingSystem.IsWindows()) return false;

        const string blockedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

        // 检查当前用户配置
        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(blockedPath, writable: false);
            if (hkcu?.GetValue(clsid) != null) return true;
        }
        catch { }

        // 检查整机 64 位注册表
        try
        {
            using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                          .OpenSubKey(blockedPath, writable: false);
            if (hklm64?.GetValue(clsid) != null) return true;
        }
        catch { }

        // 检查整机 32 位注册表 (WoW6432Node)
        try
        {
            using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                                          .OpenSubKey(blockedPath, writable: false);
            if (hklm32?.GetValue(clsid) != null) return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 扫描 Windows 第三方推广及失效右键菜单扩展 (ShellEx ContextMenuHandlers)
    /// </summary>
    public List<ScanItem> ScanContextMenus()
    {
        var results = new List<ScanItem>();
        if (!OperatingSystem.IsWindows()) return results;

        var contextRoots = new (string Path, string Scene)[]
        {
            (@"Software\Classes\*\shellex\ContextMenuHandlers", "所有文件右键"),
            (@"Software\Classes\Directory\shellex\ContextMenuHandlers", "文件夹右键"),
            (@"Software\Classes\Directory\Background\shellex\ContextMenuHandlers", "桌面与文件夹空白处右键"),
            (@"Software\Classes\Drive\shellex\ContextMenuHandlers", "磁盘右键"),
            (@"Software\Classes\Folder\shellex\ContextMenuHandlers", "文件夹对象右键")
        };

        var hives = new (RegistryKey Root, string HiveName, bool NeedsAdmin)[]
        {
            (Registry.CurrentUser, "HKCU", false),
            (Registry.LocalMachine, "HKLM", true)
        };

        foreach (var (rootKey, hiveName, needsAdmin) in hives)
        {
            foreach (var (basePath, scene) in contextRoots)
            {
                using var key = rootKey.OpenSubKey(basePath, writable: false);
                if (key == null) continue;

                foreach (var handlerName in SafeGetSubKeyNames(key))
                {
                    // 忽略系统标准自带组件
                    if (IsSystemStandardHandler(handlerName)) continue;

                    using var subKey = key.OpenSubKey(handlerName, writable: false);
                    if (subKey == null) continue;

                    string defaultVal = subKey.GetValue(null)?.ToString() ?? string.Empty;
                    string clsid = ExtractClsid(handlerName, defaultVal);

                    // 如果该 Shell 扩展已经在 Blocked 屏蔽列表中（HKCU 或 HKLM），说明已被安全禁用，不再重复上报
                    if (!string.IsNullOrEmpty(clsid) && IsShellExtensionBlocked(clsid))
                    {
                        continue;
                    }

                    // 检索 CLSID 的 DLL/组件注册信息
                    string dllPath = string.Empty;
                    string friendlyName = string.Empty;
                    if (!string.IsNullOrEmpty(clsid))
                    {
                        (dllPath, friendlyName) = QueryClsidInfo(clsid);
                    }

                    string checkTarget = $"{handlerName} {defaultVal} {friendlyName} {dllPath}";
                    var matchedVendor = RogueSignatures.FirstOrDefault(sig =>
                        sig.Patterns.Any(p => checkTarget.Contains(p, StringComparison.OrdinalIgnoreCase)));

                    bool isOrphaned = !string.IsNullOrEmpty(dllPath) && Path.IsPathRooted(dllPath) && !File.Exists(dllPath);

                    if (matchedVendor.Vendor != null || isOrphaned)
                    {
                        string title = isOrphaned
                            ? $"清理失效右键菜单残留: {handlerName} ({scene})"
                            : $"治理第三方推广右键菜单: {handlerName} ({matchedVendor.Vendor} - {scene})";

                        string desc = isOrphaned
                            ? $"该右键组件引用的 DLL 文件已不存在 ({dllPath})，属于软件卸载残留，常导致右键卡顿。"
                            : $"来自 {matchedVendor.Vendor} 的右键扩展注入。组件: {dllPath}。建议禁用或清理。";

                        // 优先采用禁用 Shell 扩展（非破坏性安全禁用），若无 CLSID 则直接删除注册表子项
                        bool canDisableByClsid = !string.IsNullOrEmpty(clsid);
                        string ruleUniqueId = $"CONTEXT-MENU-{hiveName}-{$"{basePath}\\{handlerName}".GetHashCode():X}";

                        results.Add(new ScanItem
                        {
                            RuleId = ruleUniqueId,
                            Title = title,
                            Category = "右键菜单治理",
                            Description = desc,
                            CurrentState = isOrphaned ? "已失效残留" : "已启用注入",
                            TargetState = canDisableByClsid ? "禁用Shell扩展" : "移除无效右键项",
                            Risk = isOrphaned ? RiskLevel.AutoSafe : RiskLevel.UserConfirm,
                            RequiresAdmin = needsAdmin && !canDisableByClsid,
                            RequiresUserConfirm = !isOrphaned,
                            SupportsRestore = true,
                            IsSelected = isOrphaned,
                            AssociatedRule = new CleanRule
                            {
                                Id = ruleUniqueId,
                                Name = title,
                                Category = "右键菜单治理",
                                Risk = isOrphaned ? RiskLevel.AutoSafe : RiskLevel.UserConfirm,
                                RequiresAdmin = needsAdmin && !canDisableByClsid,
                                RequiresUserConfirm = !isOrphaned,
                                SupportsRestore = true,
                                ActionType = canDisableByClsid ? RuleActionType.DisableShellExtension : RuleActionType.DeleteRegistryKey,
                                TargetPath = $@"{hiveName}\{basePath}\{handlerName}",
                                ValueName = canDisableByClsid ? clsid : string.Empty,
                                ExpectedCleanValue = "Disabled by CleanYourPC"
                            }
                        });
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 扫描“此电脑”、桌面导航栏中的多余网盘/同步入口图标残留与固定项
    /// </summary>
    public List<ScanItem> ScanExplorerNamespaces()
    {
        var results = new List<ScanItem>();
        if (!OperatingSystem.IsWindows()) return results;

        var nsRoots = new (string Path, string HiveName, RegistryKey Root, bool NeedsAdmin)[]
        {
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace", "HKLM", Registry.LocalMachine, true),
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace", "HKCU", Registry.CurrentUser, false),
            (@"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace", "HKLM", Registry.LocalMachine, true),
            (@"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace", "HKCU", Registry.CurrentUser, false),
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace", "HKCU", Registry.CurrentUser, false),
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace", "HKLM", Registry.LocalMachine, true),
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\NetworkNeighborhood\NameSpace", "HKCU", Registry.CurrentUser, false),
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\NetworkNeighborhood\NameSpace", "HKLM", Registry.LocalMachine, true)
        };

        foreach (var (path, hiveName, rootKey, needsAdmin) in nsRoots)
        {
            using var key = rootKey.OpenSubKey(path, writable: false);
            if (key == null) continue;

            foreach (var childClsid in SafeGetSubKeyNames(key))
            {
                if (IsSystemStandardClsid(childClsid)) continue;

                string display = string.Empty;
                string localized = string.Empty;
                string itemName = string.Empty;
                string targetFolder = string.Empty;

                try
                {
                    using var childKey = key.OpenSubKey(childClsid, writable: false);
                    if (childKey != null)
                    {
                        display = childKey.GetValue(null)?.ToString() ?? string.Empty;
                        localized = childKey.GetValue("LocalizedString")?.ToString() ?? string.Empty;
                        itemName = childKey.GetValue("System.ItemNameDisplay")?.ToString() ?? string.Empty;
                        targetFolder = childKey.GetValue("TargetFolderPath")?.ToString() ?? string.Empty;
                    }
                }
                catch { }

                var (dllPath, friendlyName) = QueryClsidInfo(childClsid);
                string text = $"{childClsid} {display} {localized} {itemName} {targetFolder} {friendlyName} {dllPath}";

                bool isWps = IsWpsDriveMatch(childClsid, display, localized, itemName, targetFolder, dllPath);

                var matched = RogueSignatures.FirstOrDefault(sig =>
                    sig.Patterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)));

                bool isOrphaned = !string.IsNullOrEmpty(dllPath) && Path.IsPathRooted(dllPath) && !File.Exists(dllPath);

                if (isWps || matched.Vendor != null || isOrphaned)
                {
                    string vendorName = isWps ? "WPS / 金山云盘" : (matched.Vendor ?? "未知来源");
                    string displayName = !string.IsNullOrEmpty(display) ? display :
                                         (!string.IsNullOrEmpty(friendlyName) ? friendlyName :
                                         (!string.IsNullOrEmpty(itemName) ? itemName : childClsid));

                    if (isWps && (displayName == childClsid || string.IsNullOrEmpty(displayName)))
                        displayName = "WPS云盘";

                    string title = isOrphaned
                        ? $"清理此电脑失效入口残留: {displayName}"
                        : $"移除此电脑多余云盘入口: {displayName} ({vendorName})";

                    string desc = isOrphaned
                        ? $"该资源管理器入口对应的组件文件不存在 ({dllPath})，属于残留失效图标。"
                        : isWps
                            ? $"WPS 注入到“此电脑”设备与驱动器中的虚拟云盘快捷入口 ({childClsid})。清理将安全移除此快捷入口，不影响 WPS 文档编辑功能。"
                            : $"来自 {vendorName} 的资源管理器/此电脑固定入口 ({childClsid})。";

                    string fullTargetPath = $@"{hiveName}\{path}\{childClsid}";

                    if (!results.Any(r => r.AssociatedRule?.TargetPath == fullTargetPath))
                    {
                        results.Add(new ScanItem
                        {
                            RuleId = $"EXPLORER-NS-{childClsid.GetHashCode():X}",
                            Title = title,
                            Category = "资源管理器入口治理",
                            Description = desc,
                            CurrentState = "常驻此电脑",
                            TargetState = "移除此电脑入口",
                            Risk = isOrphaned ? RiskLevel.AutoSafe : RiskLevel.UserConfirm,
                            RequiresAdmin = needsAdmin,
                            RequiresUserConfirm = !isOrphaned,
                            SupportsRestore = true,
                            IsSelected = isOrphaned,
                            AssociatedRule = new CleanRule
                            {
                                Id = $"EXPLORER-NS-{childClsid.GetHashCode():X}",
                                Name = title,
                                Category = "资源管理器入口治理",
                                Risk = isOrphaned ? RiskLevel.AutoSafe : RiskLevel.UserConfirm,
                                RequiresAdmin = needsAdmin,
                                RequiresUserConfirm = !isOrphaned,
                                SupportsRestore = true,
                                ActionType = RuleActionType.DeleteRegistryKey,
                                TargetPath = fullTargetPath,
                                ExpectedCleanValue = "__DELETE__"
                            }
                        });
                    }
                }
            }
        }

        // 进一步扫描左侧导航树中强行固定的云盘项 (System.IsPinnedToNameSpaceTree = 1)
        ScanNavigationPanePinnedItems(results);

        return results;
    }

    /// <summary>
    /// 扫描被强行固定到资源管理器导航栏树中的多余云盘入口 (System.IsPinnedToNameSpaceTree = 1)
    /// </summary>
    private void ScanNavigationPanePinnedItems(List<ScanItem> results)
    {
        // 1. 用户级 HKCU 的 CLSID 数量较小，可以直接枚举检查
        var userClsidRoots = new (string HiveName, RegistryKey Root, string SubKey, bool NeedsAdmin)[]
        {
            ("HKCU", Registry.CurrentUser, @"Software\Classes\CLSID", false),
            ("HKCU", Registry.CurrentUser, @"Software\Classes\Wow6432Node\CLSID", false)
        };

        foreach (var (hiveName, rootKey, subKey, needsAdmin) in userClsidRoots)
        {
            using var baseKey = rootKey.OpenSubKey(subKey, writable: false);
            if (baseKey == null) continue;

            foreach (var clsid in SafeGetSubKeyNames(baseKey))
            {
                if (IsSystemStandardClsid(clsid)) continue;
                InspectAndAddPinnedClsid(clsid, hiveName, rootKey, subKey, needsAdmin, results);
            }
        }

        // 2. 针对系统级 HKLM，绝不能暴力枚举几万个系统 CLSID！定向检测已知云盘/推广特征 CLSID 及已发现的命名空间 CLSID
        var targetedClsids = new HashSet<string>(KnownWpsDriveClsids, StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            if (!string.IsNullOrEmpty(r.AssociatedRule?.ValueName) && Guid.TryParse(r.AssociatedRule.ValueName.Trim('{', '}'), out _))
            {
                targetedClsids.Add(r.AssociatedRule.ValueName);
            }
            int lastSlash = r.AssociatedRule?.TargetPath.LastIndexOf('\\') ?? -1;
            if (lastSlash >= 0)
            {
                string tail = r.AssociatedRule!.TargetPath.Substring(lastSlash + 1);
                if (Guid.TryParse(tail.Trim('{', '}'), out _))
                {
                    targetedClsids.Add(tail);
                }
            }
        }

        var hklmRoots = new (string HiveName, RegistryKey Root, string SubKey, bool NeedsAdmin)[]
        {
            ("HKLM", Registry.LocalMachine, @"Software\Classes\CLSID", true),
            ("HKLM", Registry.LocalMachine, @"Software\Classes\Wow6432Node\CLSID", true)
        };

        foreach (var (hiveName, rootKey, subKey, needsAdmin) in hklmRoots)
        {
            foreach (var clsid in targetedClsids)
            {
                InspectAndAddPinnedClsid(clsid, hiveName, rootKey, subKey, needsAdmin, results);
            }
        }
    }

    private void InspectAndAddPinnedClsid(string clsid, string hiveName, RegistryKey rootKey, string subKey, bool needsAdmin, List<ScanItem> results)
    {
        try
        {
            using var baseKey = rootKey.OpenSubKey(subKey, writable: false);
            if (baseKey == null) return;

            using var clsidKey = baseKey.OpenSubKey(clsid, writable: false);
            if (clsidKey == null) return;

            var pinnedObj = clsidKey.GetValue("System.IsPinnedToNameSpaceTree");
            bool isPinned = pinnedObj != null && (pinnedObj.ToString() == "1" || (pinnedObj is int i && i == 1));
            if (!isPinned) return;

                    string display = clsidKey.GetValue(null)?.ToString() ?? string.Empty;
                    string localized = clsidKey.GetValue("LocalizedString")?.ToString() ?? string.Empty;
                    string itemName = clsidKey.GetValue("System.ItemNameDisplay")?.ToString() ?? string.Empty;
                    string infoTip = clsidKey.GetValue("InfoTip")?.ToString() ?? string.Empty;

                    string dllPath = string.Empty;
                    using (var inproc = clsidKey.OpenSubKey("InprocServer32", false))
                    {
                        dllPath = inproc?.GetValue(null)?.ToString() ?? string.Empty;
                    }
                    if (string.IsNullOrEmpty(dllPath))
                    {
                        using var localSvr = clsidKey.OpenSubKey("LocalServer32", false);
                        dllPath = localSvr?.GetValue(null)?.ToString() ?? string.Empty;
                    }

                    string text = $"{clsid} {display} {localized} {itemName} {infoTip} {dllPath}";

                    bool isWps = IsWpsDriveMatch(clsid, display, localized, itemName, string.Empty, dllPath);

                    var matched = RogueSignatures.FirstOrDefault(sig =>
                        sig.Patterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)));

                    if (isWps || matched.Vendor != null)
                    {
                        string vendorName = isWps ? "WPS / 金山云盘" : (matched.Vendor ?? "第三方软件");
                        string displayName = !string.IsNullOrEmpty(display) ? display :
                                             (!string.IsNullOrEmpty(itemName) ? itemName :
                                             (!string.IsNullOrEmpty(localized) ? localized : clsid));

                        if (isWps && (displayName == clsid || string.IsNullOrEmpty(displayName)))
                            displayName = "WPS云盘";

                        string title = $"取消导航栏固定的多余云盘入口: {displayName} ({vendorName})";
                        string desc = isWps
                            ? $"WPS 注入并强行固定在 Windows 资源管理器左侧导航树中的快捷入口 ({clsid})。治理将取消导航树固定显示。"
                            : $"来自 {vendorName} 的资源管理器导航树固定入口 ({clsid})。治理将取消其固定显示。";

                        string fullTargetPath = $@"{hiveName}\{subKey}\{clsid}";

                        if (!results.Any(r => r.AssociatedRule?.TargetPath == fullTargetPath && r.AssociatedRule?.ValueName == "System.IsPinnedToNameSpaceTree"))
                        {
                            results.Add(new ScanItem
                            {
                                RuleId = $"EXPLORER-PIN-{clsid.GetHashCode():X}",
                                Title = title,
                                Category = "资源管理器入口治理",
                                Description = desc,
                                CurrentState = "已固定在导航栏",
                                TargetState = "取消导航栏固定",
                                Risk = RiskLevel.UserConfirm,
                                RequiresAdmin = needsAdmin,
                                RequiresUserConfirm = true,
                                SupportsRestore = true,
                                IsSelected = false,
                                AssociatedRule = new CleanRule
                                {
                                    Id = $"EXPLORER-PIN-{clsid.GetHashCode():X}",
                                    Name = title,
                                    Category = "资源管理器入口治理",
                                    Risk = RiskLevel.UserConfirm,
                                    RequiresAdmin = needsAdmin,
                                    RequiresUserConfirm = true,
                                    SupportsRestore = true,
                                    ActionType = RuleActionType.RegistryValue,
                                    TargetPath = fullTargetPath,
                                    ValueName = "System.IsPinnedToNameSpaceTree",
                                    ExpectedCleanValue = "__DELETE__"
                                }
                            });
                        }
                    }
        }
        catch { }
    }

    /// <summary>
    /// 扫描桌面快捷方式 (.lnk) 中的参数篡改与推广网址劫持
    /// </summary>
    public List<ScanItem> ScanDesktopShortcutHijacks()
    {
        var results = new List<ScanItem>();
        if (!OperatingSystem.IsWindows()) return results;

        var desktopPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        foreach (var dir in desktopPaths)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

            foreach (var lnk in Directory.GetFiles(dir, "*.lnk", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var (targetPath, args) = ReadShortcut(lnk);
                    if (string.IsNullOrWhiteSpace(args)) continue;

                    string exeName = Path.GetFileName(targetPath);
                    bool isBrowser = BrowserExeNames.Contains(exeName);

                    // 检查快捷方式启动参数中是否包含推广劫持网址
                    bool isRogueParam = RogueUrlPatterns.Any(p => args.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
                                        (isBrowser && (args.Contains("http://", StringComparison.OrdinalIgnoreCase) || args.Contains("https://", StringComparison.OrdinalIgnoreCase)));

                    if (isRogueParam)
                    {
                        string fileName = Path.GetFileName(lnk);
                        results.Add(new ScanItem
                        {
                            RuleId = $"SHORTCUT-HIJACK-{fileName.GetHashCode():X}",
                            Title = $"修复桌面快捷方式参数劫持: {fileName}",
                            Category = "桌面快捷方式治理",
                            Description = $"快捷方式被追加了推广网址参数: {args}。修复将移除恶意参数并保留正常程序启动路径。",
                            CurrentState = $"参数: {args}",
                            TargetState = "清空恶意追加参数",
                            Risk = RiskLevel.AutoSafe,
                            RequiresAdmin = dir.Contains("Public", StringComparison.OrdinalIgnoreCase) || dir.Contains("All Users", StringComparison.OrdinalIgnoreCase),
                            RequiresUserConfirm = false,
                            SupportsRestore = true,
                            IsSelected = true,
                            AssociatedRule = new CleanRule
                            {
                                Id = $"SHORTCUT-HIJACK-{fileName.GetHashCode():X}",
                                Name = $"修复快捷方式 {fileName}",
                                Category = "桌面快捷方式治理",
                                Risk = RiskLevel.AutoSafe,
                                RequiresAdmin = dir.Contains("Public", StringComparison.OrdinalIgnoreCase),
                                SupportsRestore = true,
                                ActionType = RuleActionType.CleanShortcutArguments,
                                TargetPath = lnk,
                                ExpectedCleanValue = ""
                            }
                        });
                    }
                }
                catch { }
            }
        }

        return results;
    }

    /// <summary>
    /// 扫描浏览器策略强制安装的扩展 (ExtensionInstallForcelist)
    /// </summary>
    public List<ScanItem> ScanBrowserPolicyExtensions()
    {
        var results = new List<ScanItem>();
        if (!OperatingSystem.IsWindows()) return results;

        var policyPaths = new (string Hive, RegistryKey Root, string Path)[]
        {
            ("HKLM", Registry.LocalMachine, @"Software\Policies\Google\Chrome\ExtensionInstallForcelist"),
            ("HKCU", Registry.CurrentUser, @"Software\Policies\Google\Chrome\ExtensionInstallForcelist"),
            ("HKLM", Registry.LocalMachine, @"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist"),
            ("HKCU", Registry.CurrentUser, @"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist")
        };

        foreach (var (hive, rootKey, path) in policyPaths)
        {
            using var key = rootKey.OpenSubKey(path, writable: false);
            if (key == null) continue;

            foreach (var valName in key.GetValueNames())
            {
                var val = key.GetValue(valName)?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(val)) continue;

                results.Add(new ScanItem
                {
                    RuleId = $"BROWSER-FORCE-EXT-{valName.GetHashCode():X}",
                    Title = $"清理浏览器组策略强制注入的扩展: {valName}",
                    Category = "浏览器插件治理",
                    Description = $"组策略配置了企业强制安装扩展: {val}，部分推广软件常借此强行劫持浏览器搜索和主页。",
                    CurrentState = val,
                    TargetState = "移除强制扩展策略",
                    Risk = RiskLevel.UserConfirm,
                    RequiresAdmin = hive == "HKLM",
                    RequiresUserConfirm = true,
                    SupportsRestore = true,
                    IsSelected = false,
                    AssociatedRule = new CleanRule
                    {
                        Id = $"BROWSER-FORCE-EXT-{valName.GetHashCode():X}",
                        Name = $"清理强制扩展 {valName}",
                        Category = "浏览器插件治理",
                        Risk = RiskLevel.UserConfirm,
                        RequiresAdmin = hive == "HKLM",
                        RequiresUserConfirm = true,
                        SupportsRestore = true,
                        ActionType = RuleActionType.RegistryValue,
                        TargetPath = $@"{hive}\{path}",
                        ValueName = valName,
                        ExpectedCleanValue = "__DELETE__"
                    }
                });
            }
        }

        return results;
    }

    /// <summary>
    /// 扫描开始菜单与桌面中指向已不存在路径的失效快捷方式（.lnk）及已卸载软件的残留应用文件夹
    /// 解决卸载软件后 Windows 开始菜单依然显示旧图标（如 360 极速浏览器X、360安全浏览器、360压缩 等）
    /// </summary>
    public List<ScanItem> ScanStartMenuAndDesktopResiduals()
    {
        var results = new List<ScanItem>();
        if (!OperatingSystem.IsWindows()) return results;

        var candidateDirs = SoftwareUninstallService.GetCandidateShortcutDirectories();

        // 1. 扫描所有失效的孤立快捷方式（目标 exe/目录已被删除，快捷方式属于死链）
        foreach (var dir in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;

            var lnkFiles = SoftwareUninstallService.SafeEnumerateFiles(dir, "*.lnk").ToList();
            foreach (var lnk in lnkFiles)
            {
                try
                {
                    var (targetPath, _) = ReadShortcut(lnk);
                    if (string.IsNullOrWhiteSpace(targetPath)) continue;

                    // 检查目标是否存在（如果目标文件和目标目录均不存在，说明属于失效死链）
                    bool targetExists = File.Exists(targetPath) || Directory.Exists(targetPath);
                    if (!targetExists)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(lnk);

                        // 忽略某些特殊系统虚拟入口（如控制面板、上帝模式等无实体路径项）
                        if (targetPath.StartsWith("::", StringComparison.OrdinalIgnoreCase)) continue;

                        string itemType = dir.Contains("Desktop", StringComparison.OrdinalIgnoreCase) ? "桌面" : "开始菜单";
                        string title = $"清理{itemType}失效快捷方式: {fileName}";

                        results.Add(new ScanItem
                        {
                            RuleId = $"ORPHANED-LNK-{lnk.GetHashCode():X}",
                            Title = title,
                            Category = "桌面与菜单治理",
                            Description = $"该快捷方式指向的目标已被删除或不存在 ({targetPath})，属于已卸载软件在{itemType}留下的失效图标。",
                            CurrentState = $"死链: {targetPath}",
                            TargetState = "清理失效快捷方式",
                            Risk = RiskLevel.AutoSafe,
                            RequiresAdmin = dir.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) || dir.Contains("All Users", StringComparison.OrdinalIgnoreCase),
                            RequiresUserConfirm = false,
                            SupportsRestore = false,
                            IsSelected = true,
                            AssociatedRule = new CleanRule
                            {
                                Id = $"ORPHANED-LNK-{lnk.GetHashCode():X}",
                                Name = title,
                                Category = "桌面与菜单治理",
                                Risk = RiskLevel.AutoSafe,
                                RequiresAdmin = dir.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) || dir.Contains("All Users", StringComparison.OrdinalIgnoreCase),
                                RequiresUserConfirm = false,
                                SupportsRestore = false,
                                ActionType = RuleActionType.RemoveResidualDirectory,
                                TargetPath = lnk
                            }
                        });
                    }
                }
                catch { }
            }
        }

        // 2. 扫描开始菜单中的残留空文件夹或已卸载软件应用文件夹（如 360安全浏览器、360压缩 等）
        var startProgramsDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };

        // 已知的常见容易残留菜单文件夹特征
        var residualFolderKeywords = new[] { "360", "2345", "Kingsoft", "WPS", "ZipGo", "ProZip", "KuGou", "Baidu", "Sogou", "快压", "好压", "看图" };

        foreach (var pDir in startProgramsDirs)
        {
            if (!Directory.Exists(pDir)) continue;

            var subDirs = SoftwareUninstallService.SafeEnumerateDirectories(pDir, recursive: false).ToList();
            foreach (var sub in subDirs)
            {
                try
                {
                    var folderName = Path.GetFileName(sub);

                    // 检查文件夹内是否没有任何文件（纯空文件夹）
                    bool isEmpty = !Directory.EnumerateFileSystemEntries(sub).Any();

                    // 检查文件夹内是否只有失效死链快捷方式
                    bool allLinksBroken = false;
                    var lnksInside = Directory.GetFiles(sub, "*.lnk", SearchOption.AllDirectories);
                    if (lnksInside.Length > 0)
                    {
                        allLinksBroken = lnksInside.All(l =>
                        {
                            var (t, _) = ReadShortcut(l);
                            return string.IsNullOrWhiteSpace(t) || (!File.Exists(t) && !Directory.Exists(t));
                        });
                    }

                    bool matchResidualKeyword = residualFolderKeywords.Any(k => folderName.Contains(k, StringComparison.OrdinalIgnoreCase));

                    if (isEmpty || allLinksBroken || (matchResidualKeyword && lnksInside.Length == 0))
                    {
                        string reason = isEmpty ? "空文件夹残留" : "内部快捷方式目标已全部失效";
                        string title = $"清理开始菜单残留文件夹: {folderName}";

                        results.Add(new ScanItem
                        {
                            RuleId = $"STARTMENU-FOLDER-{sub.GetHashCode():X}",
                            Title = title,
                            Category = "桌面与菜单治理",
                            Description = $"该开始菜单文件夹属于软件卸载后的遗留项（{reason}）。清理将整组彻底移除，恢复开始菜单清爽。",
                            CurrentState = $"残留路径: {sub}",
                            TargetState = "彻底移除残留文件夹",
                            Risk = RiskLevel.AutoSafe,
                            RequiresAdmin = sub.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) || sub.Contains("All Users", StringComparison.OrdinalIgnoreCase),
                            RequiresUserConfirm = false,
                            SupportsRestore = false,
                            IsSelected = true,
                            AssociatedRule = new CleanRule
                            {
                                Id = $"STARTMENU-FOLDER-{sub.GetHashCode():X}",
                                Name = title,
                                Category = "桌面与菜单治理",
                                Risk = RiskLevel.AutoSafe,
                                RequiresAdmin = sub.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) || sub.Contains("All Users", StringComparison.OrdinalIgnoreCase),
                                RequiresUserConfirm = false,
                                SupportsRestore = false,
                                ActionType = RuleActionType.RemoveResidualDirectory,
                                TargetPath = sub
                            }
                        });
                    }
                }
                catch { }
            }
        }

        return results;
    }

    #region 辅助解析方法

    private static (string TargetPath, string Arguments) ReadShortcut(string lnkPath)
    {
        try
        {
            // 利用 Windows IShellLink COM 接口解析
            Type? shellType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (shellType == null) return (string.Empty, string.Empty);

            var shellLink = Activator.CreateInstance(shellType);
            if (shellLink is IPersistFile persistFile && shellLink is IShellLinkW nativeLink)
            {
                persistFile.Load(lnkPath, 0);
                var target = new StringBuilder(1024);
                var args = new StringBuilder(1024);
                nativeLink.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                nativeLink.GetArguments(args, args.Capacity);
                return (target.ToString(), args.ToString());
            }
        }
        catch { }
        return (string.Empty, string.Empty);
    }

    public static bool UpdateShortcutArguments(string lnkPath, string newArgs)
    {
        try
        {
            Type? shellType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (shellType == null) return false;

            var shellLink = Activator.CreateInstance(shellType);
            if (shellLink is IPersistFile persistFile && shellLink is IShellLinkW nativeLink)
            {
                persistFile.Load(lnkPath, 0);
                nativeLink.SetArguments(newArgs);
                persistFile.Save(lnkPath, true);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static (string DllPath, string FriendlyName) QueryClsidInfo(string clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid)) return (string.Empty, string.Empty);

        string clsidPath = $@"CLSID\{clsid}";
        string friendlyName = string.Empty;
        string dllPath = string.Empty;

        // 优先在 HKCU\Software\Classes\CLSID 中查询（覆盖当前用户独有安装的 WPS/云盘）
        try
        {
            using var userClsidKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{clsidPath}", writable: false);
            if (userClsidKey != null)
            {
                friendlyName = userClsidKey.GetValue(null)?.ToString() ??
                               userClsidKey.GetValue("System.ItemNameDisplay")?.ToString() ??
                               userClsidKey.GetValue("LocalizedString")?.ToString() ?? string.Empty;

                using var inprocKey = userClsidKey.OpenSubKey("InprocServer32", writable: false);
                dllPath = inprocKey?.GetValue(null)?.ToString() ?? string.Empty;

                if (string.IsNullOrEmpty(dllPath))
                {
                    using var localServerKey = userClsidKey.OpenSubKey("LocalServer32", writable: false);
                    dllPath = localServerKey?.GetValue(null)?.ToString() ?? string.Empty;
                }
            }
        }
        catch { }

        // 若用户级未命中，回退到系统级 ClassesRoot
        if (string.IsNullOrEmpty(friendlyName) || string.IsNullOrEmpty(dllPath))
        {
            try
            {
                using var clsidKey = Registry.ClassesRoot.OpenSubKey(clsidPath, writable: false);
                if (clsidKey != null)
                {
                    if (string.IsNullOrEmpty(friendlyName))
                    {
                        friendlyName = clsidKey.GetValue(null)?.ToString() ??
                                       clsidKey.GetValue("System.ItemNameDisplay")?.ToString() ??
                                       clsidKey.GetValue("LocalizedString")?.ToString() ?? string.Empty;
                    }

                    if (string.IsNullOrEmpty(dllPath))
                    {
                        using var inprocKey = clsidKey.OpenSubKey("InprocServer32", writable: false);
                        dllPath = inprocKey?.GetValue(null)?.ToString() ?? string.Empty;

                        if (string.IsNullOrEmpty(dllPath))
                        {
                            using var localServerKey = clsidKey.OpenSubKey("LocalServer32", writable: false);
                            dllPath = localServerKey?.GetValue(null)?.ToString() ?? string.Empty;
                        }
                    }
                }
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(dllPath))
        {
            dllPath = Environment.ExpandEnvironmentVariables(dllPath.Trim().Trim('"'));
            int commaIdx = dllPath.IndexOf(',');
            if (commaIdx > 0 && dllPath.Length > commaIdx + 1 && char.IsDigit(dllPath[commaIdx + 1]))
            {
                dllPath = dllPath.Substring(0, commaIdx).Trim().Trim('"');
            }
        }

        return (dllPath, friendlyName);
    }

    private static string ExtractClsid(string handlerName, string defaultVal)
    {
        if (Guid.TryParse(handlerName.Trim('{', '}'), out _)) return handlerName;
        if (Guid.TryParse(defaultVal.Trim('{', '}'), out _)) return defaultVal;
        var match = Regex.Match(defaultVal, @"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}");
        if (match.Success) return match.Value;
        return string.Empty;
    }

    private static bool IsSystemStandardHandler(string name)
    {
        var standard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OpenWith", "Sharing", "ModernSharing", "EPP", "WorkFolders",
            "SendTo", "Offline Files", "PinToStartScreen", "ShellImagePreview", "Print", "New"
        };
        return standard.Contains(name);
    }

    private static bool IsSystemStandardClsid(string clsid)
    {
        var systemClsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", // 此电脑
            "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", // 网络
            "{645FF040-5081-101B-9F08-00AA002F954E}", // 回收站
            "{031E4825-7B94-4dc3-B131-E946B44C8DD5}", // 库
            "{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}"  // 桌面
        };
        return systemClsids.Contains(clsid);
    }

    private static string[] SafeGetSubKeyNames(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); } catch { return Array.Empty<string>(); }
    }

    #endregion

    #region COM Interop

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    #endregion
}
