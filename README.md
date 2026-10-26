<div align="center">

# CleanYourPC 🧹

**让你的电脑系统回归纯净 —— 面向普通用户的开源广告治理与系统净化工具**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Build & Test](https://github.com/zhihuzeye/CleanYourPC/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/zhihuzeye/CleanYourPC/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20国产OS-blue)](#二支持的操作系统)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)

<p align="center">
  <b>简体中文</b> | <a href="README_en.md">English</a>
</p>

[功能特性](#一解决什么问题) • [快速下载](#三快速下载与运行) • [源码编译](#四源码编译与构建) • [常见问题](#六常见问题解答-faq) • [参与贡献](#七参与贡献)

</div>

---

> [!IMPORTANT]
> **免责声明 (Disclaimer)**  
> 1. 本项目为开源公益软件，代码和规则库（JSON）完全透明开放。  
> 2. CleanYourPC 在执行任何注册表与系统服务变更前均会自动创建快照备份。但系统环境因人而异，**在执行关键清理操作（尤其是强力卸载软件模式）前，请务必保存好正在编辑的文档并了解相关变更**。  
> 3. 作者与贡献者不对使用本软件造成的任何意外系统异常或数据损失承担法律责任。

---

## 📸 界面预览

<!-- 如果你有桌面客户端运行截图，可以将图片放置在 docs/screenshots/ 并在下方展示 -->
<div align="center">
  <img src="docs/screenshots/preview.png" alt="CleanYourPC 主界面预览" width="750" onerror="this.style.display='none'"/>
  <p><em>简洁纯粹、一键体检与治理，拒绝任何复杂的配置陷阱</em></p>
</div>

---

## 一、解决什么问题？

日常使用电脑时，各种隐蔽的系统原生推荐、锁屏广告、输入法新闻资讯、软件守护进程弹窗让人不胜其烦。CleanYourPC 旨在用最安全、透明的方式解决这些顽疾：

1. **锁屏与系统原生广告治理**：
   - 一键关闭锁屏底部的天气预报、体育赛事、历史上的今天等动态卡片（Weather and more）与小组件资讯。
   - 彻底关闭锁屏聚焦推广、开始菜单推荐、任务栏“资讯和兴趣”、通知中心营销建议及资源管理器同步广告横幅。
2. **WPS Office 广告与静默下发治理**：
   - 禁用非核心后台云唤醒服务 (`wpscloudsvr`)。
   - 对 download 与 update 缓存目录施加只读免疫锁，彻底阻断后台静默下发捆绑安装包，**100% 不影响日常文档、表格、幻灯片与 PDF 核心编辑功能**。
3. **主流输入法推广弹窗治理**：
   - 深度识别搜狗输入法、百度输入法、讯飞输入法、QQ 输入法等。
   - 精准阻断热点新闻弹窗、皮肤营销推荐与广告常驻进程，**绝不影响正常的文字录入**。
4. **流氓软件残留与多余计划任务清理**：
   - 自动排查 360 卸载遗留的划词任务 (`QihooGetWordSearchFatch`)、推广压缩软件看门狗任务 (`beijing-zipgo`、`ProZip`) 及残留挂钩 DLL 目录。
5. **360 / 2345 / 电脑管家 / 捆绑压缩包等强力直接卸载**：
   - 支持类似 Geek Uninstaller 的强力移除模式。
   - 用户勾选确认后，程序自动强力终结其常驻守护进程、粉碎安装目录、注销注册表项并清除开机自启，**无需忍受繁琐挽留弹窗**。
   - 内置系统白名单保护，绝对保护系统原生安全防护（Windows Defender）与硬件驱动。
6. **防静默安装安全加固**：
   - 支持配置管理员提权强制密码验证 (`ConsentPromptBehaviorAdmin=1`)，预防流氓软件偷偷提权静默安装。
7. **历史快照版本化管理（一键原路回滚）**：
   - 每次清理前自动归档结构化快照（v1.0、v1.1...），误操作随时一键原路还原。
8. **导出结构化报告**：
   - 支持将扫描与治理结果一键导出为格式规范的纯文本 (`.txt`) 报告。

---

## 二、支持的操作系统

- **Windows 系列**：
  - Windows 11（64 位，全版本）
  - Windows 10（64 位，1809 及以上版本）
- **国产桌面操作系统**：
  - 统信桌面操作系统 (UnionTech UOS)
  - 银河麒麟桌面操作系统 (Kylin Linux)
  - 深度桌面操作系统 (Deepin)
  - 华为鸿蒙桌面操作系统 (HarmonyOS PC / openEuler)

---

## 三、快速下载与运行

### 方式 1：下载官方编译好的免安装绿色包（推荐普通用户）

1. 前往 GitHub **[Releases 页面](https://github.com/zhihuzeye/CleanYourPC/releases)** 下载最新版 `CleanYourPC-win-x64.zip`。
2. 解压到任意目录，右键以**管理员身份运行** `CleanYourPC.App.exe`。
3. 点击“一键扫描”即可查看系统现存广告隐患，勾选确认后点击“立即治理”。

---

## 四、源码编译与构建

如果你是开发者，可以自行从源码编译与打包。

### 前置要求
- 安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或 [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### 编译与运行
```powershell
# 1. 运行自动化单元测试
dotnet test

# 2. 启动桌面端 (Windows)
dotnet run --project src/CleanYourPC.App/CleanYourPC.App.csproj

# 3. 打包为 Windows 绿色独立单文件发布包
dotnet publish src/CleanYourPC.App/CleanYourPC.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

### 跨架构打包国产操作系统产物 (Linux ARM64 / LoongArch64 / x64)
```bash
# 针对飞腾 / 鲲鹏 / 华为海思 ARM64 打包
dotnet publish src/CleanYourPC.App/CleanYourPC.App.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -o ./publish-linux-arm64

# 针对龙芯 LoongArch64 打包
dotnet publish src/CleanYourPC.App/CleanYourPC.App.csproj -c Release -r linux-loongarch64 --self-contained true -p:PublishSingleFile=true -o ./publish-linux-loongarch64
```

---

## 五、项目架构设计

CleanYourPC 采用“引擎与规则解耦、界面与逻辑分离”的模块化架构：

```
CleanYourPC/
├── .github/              # GitHub Actions CI/CD 流水线与 Issue/PR 模板
├── rules/                # 跨平台纯文本治理规则库 (JSON)
│   ├── default-rules.json # 核心默认规则集
│   └── ...               # 分操作系统专用规则
├── src/
│   ├── CleanYourPC.Core/ # 核心治理引擎 (扫描、备份还原、注册表/服务/任务调度器)
│   └── CleanYourPC.App/  # 现代 WPF 桌面交互界面与命令行 Elevated Worker
├── tests/
│   └── CleanYourPC.Tests/# 自动化测试套件 (沙箱验证、白名单保护、规则完整性)
├── docs/                 # 技术设计文档与资源
└── CleanYourPC.sln       # 统一解决方案
```

---

## 六、常见问题解答 (FAQ)

### Q1: 软件会被杀毒软件报毒或拦截吗？
**答**：由于本工具包含“强力卸载软件”、“终结流氓进程”与“修改系统策略注册表”等敏感底层操作，且未购买昂贵的企业级 EV 数字签名证书，部分杀毒软件可能会提示风险拦截。CleanYourPC **100% 开源透明**，不含任何恶意代码、后门或隐蔽数据采集，你可以随时查阅源码并自行编译。建议在杀软中加入信任名单。

### Q2: 为什么建议以“管理员身份运行”？
**答**：关闭锁屏动态画报、禁止特定后台服务自启、清除卸载顽固残留需要写入系统级策略注册表（`HKLM`）以及管理系统服务。普通用户权限下只能处理当前用户（`HKCU`）的配置，无法彻底根除系统级守护进程。

### Q3: 清理后如果遇到问题，如何还原？
**答**：CleanYourPC 在每次点击治理前，都会在本地自动生成带有版本号的时间戳快照存档。如果你发现某项变更需要回滚，只需在界面中进入“历史快照管理”，选中对应的快照版本点击“一键还原”即可原路恢复。

### Q4: 会不会误伤我的正常文件或电脑硬件驱动？
**答**：绝不会。CleanYourPC 内置了严格的安全防护白名单，核心治理规则均经过严格实测并由单元测试（`CleanYourPC.Tests`）进行安全红线把关，禁止触碰任何音频、显卡、芯片组等硬件驱动和系统基础服务。

---

## 七、参与贡献

我们非常欢迎社区参与贡献！无论是提交代码、完善规则，还是报告 Bug，都能让这个开源工具变得更好。

- 发现新广告？欢迎提交 [规则贡献 Issue](https://github.com/zhihuzeye/CleanYourPC/issues/new?template=rule_request.md)
- 提交代码或规则更新前，请详细阅读 [贡献指南 (CONTRIBUTING.md)](CONTRIBUTING.md)
- 请遵守 [社区行为准则 (CODE_OF_CONDUCT.md)](CODE_OF_CONDUCT.md)

---

## 八、开源许可证

本项目采用 [MIT 许可证](LICENSE)。你可以自由使用、修改、分发与集成，但请保留原版权声明与免责声明。

---

<div align="center">
  如果 CleanYourPC 帮助你的电脑重获清爽，请给项目点一个 ⭐️ <strong>Star</strong>，支持开源项目的持续迭代！
</div>
