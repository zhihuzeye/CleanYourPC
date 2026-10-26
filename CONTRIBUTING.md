# 贡献指南 (Contributing to CleanYourPC)

[简体中文](CONTRIBUTING.md) | [English](CONTRIBUTING_en.md)

---

感谢你关注并参与 `CleanYourPC` 开源项目！

本项目致力于帮助普通用户摆脱系统原生广告、弹窗推广与流氓残留困扰。为了确保数以万计用户的系统稳定性与数据安全，所有提交的代码和规则都必须严格遵守以下规范。

---

## 一、三项绝对安全红线（严禁触碰）

1. **严禁破坏核心功能**：
   - 任何针对 WPS、输入法、浏览器或系统组件的广告关闭规则，不得导致软件崩溃、无法打字、无法编辑文档或无法登录系统。
2. **严禁触碰系统安全基线与硬件驱动**：
   - 绝不能将 Windows Defender、Windows Update、防火墙组件、核心系统服务（如 DCOM/RPC/PlugPlay）或任何硬件驱动（Realtek、Synaptics、Intel、AMD、NVIDIA、Dolby 等）列入清理目标。
3. **坚持可逆与备份优先**：
   - 凡涉及注册表修改、服务停用或目录权限锁定，必须在 `BackupRestoreService` 中支持记录原值，确保用户可以随时一键还原回滚。

---

## 二、本地开发环境搭建

### 1. 前置依赖
- **操作系统**：Windows 10/11、macOS (开发测试) 或 Linux (开发测试)
- **.NET SDK**：[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或 [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- **推荐 IDE**：Visual Studio 2022 (带 .NET 桌面开发工作负载)、JetBrains Rider 或 VS Code (配合 C# Dev Kit 插件)

### 2. 获取代码与运行测试
```bash
# 克隆代码
git clone https://github.com/zhihuzeye/CleanYourPC.git
cd CleanYourPC

# 还原依赖与执行单元测试
dotnet test

# 启动桌面客户端 (Windows 环境)
dotnet run --project src/CleanYourPC.App/CleanYourPC.App.csproj
```

---

## 三、如何贡献新的治理规则 (`rules/default-rules.json`)

CleanYourPC 的核心治理能力基于纯文本 JSON 规则。如果你发现了新的流氓弹窗、顽固残留或锁屏广告，欢迎提交规则！

### 1. 规则编写规范
在 `rules/default-rules.json` 中新增规则时，必须遵循以下结构并填写真实元数据：

```json
{
  "RuleId": "AD-VENDOR-00X",
  "Category": "SoftwareAd",
  "DisplayName": "关闭某某软件资讯弹窗",
  "TargetSoftware": "目标软件全称",
  "ImpactLevel": "Safe",
  "RequiresElevation": false,
  "ActionType": "RegistryValue",
  "TargetPath": "HKCU\\Software\\Vendor\\App",
  "ValueName": "EnablePopups",
  "ExpectedCleanValue": 0,
  "RollbackValue": 1,
  "SourceAndVerification": "在 Windows 11 23H2 + 某软件 v1.2.3 上实测验证通过，关闭后不影响正常使用"
}
```

### 2. 规则验证要求
- **本地实测**：必须在真实物理机或测试虚拟机中验证过，关闭后弹窗不再出现且软件核心功能完好。
- **运行单元测试**：修改规则后，在根目录执行 `dotnet test`，确保规则结构解析、字段有效性与沙箱测试 100% 通过。

---

## 四、Pull Request 流程

1. **Fork 本仓库** 到你的 GitHub 账号下。
2. 从 `main` 分支切出新分支：`git checkout -b feat/add-new-rule`。
3. 遵循代码风格（项目已配置 `.editorconfig`）。
4. 确保所有单元测试通过：`dotnet test`。
5. 提交 commit（推荐遵循 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/) 规范，例如 `feat: 新增某某输入法弹窗治理规则` 或 `fix: 修复某注册表路径容错`）。
6. 推送分支并向本仓库提交 Pull Request。
7. 填写完整的 PR 检查清单并勾选安全红线确认项。维护团队将尽快进行 Review 与实测。
