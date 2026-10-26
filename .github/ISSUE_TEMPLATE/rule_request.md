---
name: 广告治理规则贡献 / Rule Request & Contribution
about: 提交发现的新弹窗广告、锁屏推广、流氓残留或规则建议 / Submit a new adware, lock screen ad, or cleanup rule
title: '[RULE] '
labels: ['rule-contribution']
assignees: ''
---

**目标软件/推广类型 / Target Software & Ad Type**
- 目标软件名称 / Software Name: [e.g. 某某压缩 / 某某输入法 / LockScreen Widget]
- 目标版本 / Version: [e.g. v5.2.1]
- 弹窗表现 / Behavior: [开机弹窗 / 锁屏卡片 / 托盘气泡 / 计划任务静默下发 / Startup popup, Lock screen widget, etc.]

**广告特征定位（若已知） / Technical Identifiers (If known)**
- 进程名 / Process Name: [e.g. xxxpush.exe]
- 物理路径 / File Path: [e.g. %ProgramFiles%\xxx\]
- 注册表路径 / Registry Key: [e.g. HKCU\Software\...\ShowNews]
- 计划任务名 / Task Name: [e.g. XxxUpdateTask]
- 系统服务名 / Service Name: [e.g. XxxGuardSvc]

**实测验证 / Verification**
- 关闭该项后，软件的核心功能（如文字录入、文件解压、文档编辑）是否依然正常？
- Does core functionality (typing, editing, extracting) remain intact?
  - [ ] 是，已实测验证，功能完全不受影响 / Yes, verified on real device
  - [ ] 未实测，需维护团队进一步分析 / Not yet verified
