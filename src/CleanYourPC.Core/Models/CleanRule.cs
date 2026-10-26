using System.Text.Json.Serialization;

namespace CleanYourPC.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlatformScope
{
    Common,       // 跨平台公共规则
    Windows,      // Windows 电脑专属
    UOS,          // 统信 UOS 专属
    Kylin,        // 银河麒麟专属
    Deepin,       // 深度 Deepin 专属
    HarmonyOS     // 华为鸿蒙专属
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RiskLevel
{
    AutoSafe,           // A类：自动处理的安全项目
    AdminRequired,      // B类：需要管理员权限的安全项目
    UserConfirm,        // C类：需要用户明确确认的项目（如卸载360/管家、删残留）
    HighRiskOrUnknown   // D类：高风险或未知归属项目（默认只读保护，禁止自动处理）
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleActionType
{
    RegistryValue,
    DisableService,
    RemoveScheduledTask,
    LockDirectoryWrite,
    UninstallSoftware,
    RemoveResidualDirectory,
    DeleteRegistryKey,
    DisableShellExtension,
    CleanShortcutArguments,
    CleanBrowserNotifications
}

public class CleanRule
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public PlatformScope Scope { get; set; } = PlatformScope.Common;
    public List<string> SupportedOS { get; set; } = new();
    public string TargetSoftware { get; set; } = string.Empty;
    public string VersionRange { get; set; } = "*";
    public RiskLevel Risk { get; set; } = RiskLevel.HighRiskOrUnknown;
    public bool RequiresAdmin { get; set; }
    public bool RequiresUserConfirm { get; set; }
    public bool SupportsRestore { get; set; }
    public string SourceAndVerification { get; set; } = string.Empty;
    public RuleActionType ActionType { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public string ExpectedCleanValue { get; set; } = string.Empty;
    public string ValueKind { get; set; } = "DWord";
    public string SuccessCriteria { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}