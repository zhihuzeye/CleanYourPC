# 更新日志 (Changelog)

[简体中文](CHANGELOG.md) | [English](CHANGELOG_en.md)

---

本项目所有重要更新都将记录在此文件中。格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)，并遵循 [语义化版本 2.0.0](https://semver.org/lang/zh-CN/)。

## [1.0.0] - 2026-10-26

### 🚀 核心特性与发布
- **锁屏与系统原生广告治理**：
  - 支持一键关闭锁屏“天气与更多”(Weather and more) 动态卡片与资讯小组件。
  - 关闭锁屏聚焦推广、开始菜单推荐、任务栏“资讯和兴趣”、通知中心营销建议及资源管理器同步横幅。
- **WPS Office 弹窗与静默下发治理**：
  - 自动禁用后台非核心服务 `wpscloudsvr`。
  - 对 download 与 update 缓存目录施加只读免疫锁，彻底阻断静默推广下载，不影响日常文字、表格、PPT 与 PDF 编辑。
- **主流输入法弹窗治理**：
  - 深度识别搜狗输入法、百度输入法、讯飞输入法、QQ 输入法等。
  - 关闭热点新闻弹窗、皮肤营销推荐与广告进程，保障核心打字功能完好无损。
- **强力卸载与流氓残留清理**：
  - 支持类似 Geek Uninstaller 的强力直接移除模式，终结常驻进程并彻底粉碎残留。
  - 清理 360 划词搜索残留 (QihooGetWordSearchFatch) 及推广压缩包守护进程。
- **历史快照与安全加固**：
  - 每次清理前自动创建结构化快照存档（v1.0、v1.1...），支持一键还原回滚。
  - 支持配置 UAC 提权密码强制验证 (ConsentPromptBehaviorAdmin=1)，预防流氓软件偷偷提权。
- **跨平台与国产操作系统支持**：
  - 支持 Windows 10 (1809+)、Windows 11 (全版本)。
  - 支持银河麒麟、统信 UOS、深度 Deepin、华为鸿蒙桌面 (openEuler) 跨架构编译与兼容运行。
- **工程化与开源生态健全**：
  - 建立自动化 GitHub Actions CI/CD 流水线，支持 Windows 绿色免安装单文件自动化构建与发布。
  - 健全 Issue/PR 模板、安全策略 (SECURITY.md)、行为准则与贡献指南。
