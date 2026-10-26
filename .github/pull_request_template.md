## 变更概述 / Summary of Changes
简要说明本次 Pull Request 修改或新增的内容。
A brief description of what this PR introduces or fixes.

---

## 变更类型 / Type of Change
- [ ] 规则更新与新增 / Rule Update or New Rule (`rules/*.json`)
- [ ] 核心引擎优化 / Core Engine Improvement (`CleanYourPC.Core`)
- [ ] 桌面客户端 UI / Desktop App UI & UX (`CleanYourPC.App`)
- [ ] 单元测试补充 / Test Suite (`CleanYourPC.Tests`)
- [ ] 文档与 CI/CD 配置 / Documentation & CI/CD

---

## 安全红线自查清单 / Security & Safety Checklist
- [ ] **不误伤核心功能 / Core Functionality Intact**: 已确认修改/新增的规则绝不会影响输入法打字、Office 文档编辑等核心功能。 / Confirmed changes do not break core typing, office editing, or system logins.
- [ ] **不误删系统与驱动 / No System/Driver Disruption**: 绝不包含 Windows Defender、系统核心服务或硬件驱动。 / No modifications targeting Windows Defender, OS core components, or hardware drivers.
- [ ] **可逆与备份支持 / Fully Reversible**: 所有涉及的注册表/服务/权限变更均支持原路还原。 / All registry/service actions are logged and reversible via snapshots.
- [ ] **自动化测试全部通过 / Tests Passing**: 本地运行 `dotnet test`，所有单元测试 100% 通过。 / Executed `dotnet test` and all unit tests pass locally.
- [ ] **无无关临时文件 / Clean Workspace**: 未提交 `.DS_Store`、`bin/`、`obj/` 或日志文件。 / No unwanted binaries, logs, or OS artifacts committed.
