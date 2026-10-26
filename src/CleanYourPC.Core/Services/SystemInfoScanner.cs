using System.Management;
using System.Security.Principal;
using CleanYourPC.Core.Models;
using Microsoft.Win32;

namespace CleanYourPC.Core.Services;

public class SystemEnvironmentSummary
{
    public string OSCaption { get; set; } = "Unknown OS";
    public string OSBuild { get; set; } = string.Empty;
    public string OSType { get; set; } = "Windows"; // "Windows", "银河麒麟", "统信 UOS", "深度 Deepin", "华为鸿蒙", "Linux"
    public bool IsDomesticOS { get; set; }
    public bool IsWindows10Or11 { get; set; }
    public bool IsAdmin { get; set; }
    public List<string> ActiveAntivirus { get; set; } = new();
    public List<string> InstalledIMEs { get; set; } = new();
    public string? WpsVersion { get; set; }
}

public class SystemInfoScanner
{
    // 软件发布者保护白名单（受严格保护的系统组件、微软官方组件、国产系统关键基础库与知名硬件驱动发布者白名单）
    private static readonly string[] ProtectedPublishers = new[]
    {
        "Microsoft Corporation", "Realtek", "Synaptics", "Dolby Laboratories",
        "Intel Corporation", "NVIDIA Corporation", "Advanced Micro Devices", "Lenovo",
        "KylinSoft", "UnionTech", "Deepin", "Huawei"
    };

    /// <summary>
    /// 评估是否属于需要治理的目标软件（如 360 全家桶、2345 全家桶、鲁大师、推广捆绑压缩包等），并赋予明确的风险原因
    /// </summary>
    public static bool EvaluateTargetSoftwareRisk(InstalledSoftwareInfo info)
    {
        if (info.IsSystemProtected) return false;

        string name = info.DisplayName;
        string pub = info.Publisher;

        // 1. 360 旗下全系列软件全面识别（覆盖 360极速浏览器X、360安全浏览器、360压缩、360驱动大师、360安全卫士、360杀毒、360看图、360壁纸等）
        bool is360 = name.Contains("360", StringComparison.OrdinalIgnoreCase) ||
                     pub.Contains("360", StringComparison.OrdinalIgnoreCase) ||
                     pub.Contains("Qihoo", StringComparison.OrdinalIgnoreCase) ||
                     pub.Contains("奇虎", StringComparison.OrdinalIgnoreCase);

        if (is360)
        {
            info.IsTargetSecurityOrBloatware = true;
            if (name.Contains("极速浏览器") || name.Contains("安全浏览器") || name.Contains("浏览器"))
            {
                info.RiskReason = "360 浏览器产品（包含常驻推送服务、推广导航及主页绑定模块）";
            }
            else if (name.Contains("压缩") || name.Contains("zip", StringComparison.OrdinalIgnoreCase))
            {
                info.RiskReason = "360 压缩工具（包含广告弹窗推送服务与右键关联扩展）";
            }
            else if (name.Contains("卫士") || name.Contains("杀毒"))
            {
                info.RiskReason = "360 安全软件（包含常驻驱动与推广弹窗，卸载后由系统原生安全接管）";
            }
            else if (name.Contains("驱动"))
            {
                info.RiskReason = "360 驱动工具（常伴随开机自启、硬件资讯弹窗与软件推荐）";
            }
            else if (name.Contains("壁纸") || name.Contains("看图") || name.Contains("桌面"))
            {
                info.RiskReason = "360 桌面/视觉工具（常驻后台推送屏保画报与资讯广告）";
            }
            else if (name.Contains("软件管家"))
            {
                info.RiskReason = "360 软件管家（常驻下载服务与弹窗推荐）";
            }
            else
            {
                info.RiskReason = "360 旗下软件产品（包含后台常驻进程或推广弹窗模块）";
            }
            return true;
        }

        // 2. 2345 旗下软件识别
        if (name.Contains("2345") || pub.Contains("2345"))
        {
            info.IsTargetSecurityOrBloatware = true;
            info.RiskReason = "2345 旗下软件（常驻开机自启、侵入性右键及广告弹窗）";
            return true;
        }

        // 3. 鲁大师硬件工具
        if (name.Contains("鲁大师") || pub.Contains("Ludashi", StringComparison.OrdinalIgnoreCase))
        {
            info.IsTargetSecurityOrBloatware = true;
            info.RiskReason = "硬件检测工具，常伴随资讯弹窗与游戏推广中心";
            return true;
        }

        // 4. 腾讯电脑管家
        if (name.Contains("腾讯电脑管家") || (name.Contains("电脑管家") && pub.Contains("Tencent", StringComparison.OrdinalIgnoreCase)))
        {
            info.IsTargetSecurityOrBloatware = true;
            info.RiskReason = "第三方电脑管家类软件，常驻后台驱动与推广弹窗";
            return true;
        }

        // 5. 附带弹窗/推广压缩工具（ZipGo, ProZip, 快压, 好压等）
        if (name.Contains("ZipGo", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ProZip", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("快压", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("好压", StringComparison.OrdinalIgnoreCase))
        {
            info.IsTargetSecurityOrBloatware = true;
            info.RiskReason = "附带弹窗推广的压缩软件，含后台看门狗或常驻挂钩模块";
            return true;
        }

        return false;
    }

    public SystemEnvironmentSummary GetEnvironmentSummary()
    {
        var summary = new SystemEnvironmentSummary();

        // 1. 检查管理员 / Root 权限
        try
        {
            if (OperatingSystem.IsWindows())
            {
                summary.IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            }
            else
            {
                summary.IsAdmin = Environment.UserName == "root";
            }
        }
        catch
        {
            summary.IsAdmin = false;
        }

        // 2. 操作系统类型与版本检测（覆盖 Windows 10/11 与国产操作系统）
        DetectOperatingSystem(summary);

        // 3. Windows 环境专有信息检测（防病毒产品与注册表软件）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var avSearcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct");
                foreach (ManagementObject av in avSearcher.Get())
                {
                    var name = av["displayName"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        summary.ActiveAntivirus.Add(name);
                }
            }
            catch { /* 忽略非桌面版或WMI异常 */ }
        }

        // 4. 检测已安装输入法与 WPS
        var installed = ScanInstalledSoftware();
        foreach (var sw in installed)
        {
            if (sw.DisplayName.Contains("搜狗输入法") || sw.DisplayName.Contains("Sogou"))
                summary.InstalledIMEs.Add($"搜狗输入法 ({sw.DisplayVersion})");
            else if (sw.DisplayName.Contains("百度输入法"))
                summary.InstalledIMEs.Add($"百度输入法 ({sw.DisplayVersion})");
            else if (sw.DisplayName.Contains("讯飞输入法"))
                summary.InstalledIMEs.Add($"讯飞输入法 ({sw.DisplayVersion})");
            else if (sw.DisplayName.Contains("QQ输入法"))
                summary.InstalledIMEs.Add($"QQ输入法 ({sw.DisplayVersion})");

            if (sw.DisplayName.Contains("WPS Office", StringComparison.OrdinalIgnoreCase) ||
                sw.DisplayName.Contains("wps-office", StringComparison.OrdinalIgnoreCase))
                summary.WpsVersion = sw.DisplayVersion;
        }

        if (summary.InstalledIMEs.Count == 0)
        {
            summary.InstalledIMEs.Add(summary.IsDomesticOS ? "系统默认输入法 (Fcitx/IBus)" : "微软拼音 (系统自带)");
        }

        return summary;
    }

    private void DetectOperatingSystem(SystemEnvironmentSummary summary)
    {
        // 先检查 Linux / 国产操作系统标识
        if (File.Exists("/etc/os-release"))
        {
            try
            {
                var content = File.ReadAllText("/etc/os-release");
                if (content.Contains("Kylin", StringComparison.OrdinalIgnoreCase))
                {
                    summary.OSCaption = "银河麒麟桌面操作系统 (Kylin Linux)";
                    summary.OSType = "银河麒麟";
                    summary.IsDomesticOS = true;
                    return;
                }
                if (content.Contains("UOS", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("UnionTech", StringComparison.OrdinalIgnoreCase))
                {
                    summary.OSCaption = "统信桌面操作系统 (UnionTech UOS)";
                    summary.OSType = "统信 UOS";
                    summary.IsDomesticOS = true;
                    return;
                }
                if (content.Contains("Deepin", StringComparison.OrdinalIgnoreCase))
                {
                    summary.OSCaption = "深度桌面操作系统 (Deepin)";
                    summary.OSType = "深度 Deepin";
                    summary.IsDomesticOS = true;
                    return;
                }
                if (content.Contains("HarmonyOS", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("openEuler", StringComparison.OrdinalIgnoreCase))
                {
                    summary.OSCaption = "华为鸿蒙操作系统 (HarmonyOS PC / openEuler)";
                    summary.OSType = "华为鸿蒙";
                    summary.IsDomesticOS = true;
                    return;
                }
            }
            catch { }
        }

        if (File.Exists("/etc/kylin-release"))
        {
            summary.OSCaption = "银河麒麟操作系统 (Kylin)";
            summary.OSType = "银河麒麟";
            summary.IsDomesticOS = true;
            return;
        }

        if (File.Exists("/etc/uos-release"))
        {
            summary.OSCaption = "统信 UOS 操作系统";
            summary.OSType = "统信 UOS";
            summary.IsDomesticOS = true;
            return;
        }

        // Windows 系统检测
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
                foreach (ManagementObject os in searcher.Get())
                {
                    summary.OSCaption = os["Caption"]?.ToString()?.Trim() ?? "Windows";
                    summary.OSBuild = os["BuildNumber"]?.ToString() ?? "";
                    summary.IsWindows10Or11 = summary.OSCaption.Contains("Windows 10") || summary.OSCaption.Contains("Windows 11");
                    summary.OSType = summary.OSCaption.Contains("Windows 11") ? "Windows 11" : "Windows 10";
                }
            }
            catch
            {
                summary.OSCaption = Environment.OSVersion.VersionString;
                summary.IsWindows10Or11 = Environment.OSVersion.Version.Major >= 10;
                summary.OSType = "Windows";
            }
        }
        else
        {
            summary.OSCaption = $"{Environment.OSVersion.Platform} {Environment.OSVersion.VersionString}";
            summary.OSType = "Linux/Unix";
        }
    }

    public List<InstalledSoftwareInfo> ScanInstalledSoftware()
    {
        var results = new List<InstalledSoftwareInfo>();

        if (OperatingSystem.IsWindows())
        {
            var regPaths = new (RegistryKey Root, string SubKey)[]
            {
                (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
            };

            foreach (var (root, subKeyPath) in regPaths)
            {
                using var baseKey = root.OpenSubKey(subKeyPath);
                if (baseKey == null) continue;

                foreach (var subName in baseKey.GetSubKeyNames())
                {
                    try
                    {
                        using var appKey = baseKey.OpenSubKey(subName);
                        if (appKey == null) continue;

                        var displayName = appKey.GetValue("DisplayName")?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        var publisher = appKey.GetValue("Publisher")?.ToString()?.Trim() ?? "";
                        var version = appKey.GetValue("DisplayVersion")?.ToString()?.Trim() ?? "";
                        var installLocation = appKey.GetValue("InstallLocation")?.ToString()?.Trim() ?? "";
                        var uninstallString = appKey.GetValue("UninstallString")?.ToString()?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(uninstallString))
                        {
                            uninstallString = appKey.GetValue("QuietUninstallString")?.ToString()?.Trim() ?? "";
                        }

                        var systemComponent = appKey.GetValue("SystemComponent")?.ToString();

                        var hive = root == Registry.LocalMachine ? "HKLM" : "HKCU";
                        var info = new InstalledSoftwareInfo
                        {
                            DisplayName = displayName,
                            Publisher = publisher,
                            DisplayVersion = version,
                            InstallLocation = installLocation,
                            UninstallString = uninstallString,
                            RegistryKeyPath = $@"{hive}\{subKeyPath}\{subName}",
                            IsSystemProtected = ProtectedPublishers.Any(p => publisher.Contains(p, StringComparison.OrdinalIgnoreCase))
                        };

                        bool isTarget = EvaluateTargetSoftwareRisk(info);

                        // 正常系统组件过滤，但若命中了重点治理软件规则则不得隐匿跳过
                        if (systemComponent == "1" && !isTarget)
                            continue;

                        if (!results.Any(r => r.DisplayName.Equals(info.DisplayName, StringComparison.OrdinalIgnoreCase)))
                            results.Add(info);
                    }
                    catch { /* 忽略单个损坏的注册表项 */ }
                }
            }
        }

        return results.OrderBy(r => r.DisplayName).ToList();
    }
}