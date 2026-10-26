# Changelog

[简体中文](CHANGELOG.md) | [English](CHANGELOG_en.md)

---

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-10-26

### 🚀 Initial Public Release
- **Lock Screen & Native Windows Promotions**:
  - One-click disabling of dynamic lock-screen widgets (Weather and more, sports cards, etc.).
  - Complete suppression of Windows Spotlight tips, Start Menu recommendations, Taskbar "News & Interests", Action Center marketing prompts, and File Explorer sync banners.
- **WPS Office Ad & Silent Downloader Neutralization**:
  - Automatically disables non-core background cloud wakeup service (`wpscloudsvr`).
  - Enforces read-only immunity locks on download and update cache directories, stopping silent background downloads without affecting document/sheet/slide/PDF operations.
- **Input Method Ad & Popup Suppression**:
  - Inspects Sogou, Baidu, iFlytek, and QQ input methods.
  - Blocks news popups, skin marketing, and ad daemons while preserving core typing features.
- **Force Uninstallation & Adware Residue Cleanup**:
  - Direct forced removal mode (Geek Uninstaller style) with process termination and file shredding.
  - Neutralizes 360 search hook remnants (`QihooGetWordSearchFatch`) and rogue compression tool watchdogs.
- **Snapshot Management & Security Hardening**:
  - Automatically archives structured rollback snapshots (`v1.0`, `v1.1`...) before each mitigation run.
  - Supports enforcing UAC credential prompt verification (`ConsentPromptBehaviorAdmin=1`) to prevent silent elevation.
- **Cross-Platform & Domestic OS Support**:
  - Supports Windows 10 (1809+) and Windows 11.
  - Ready for cross-compilation and wine-compatibility on Kylin, UnionTech UOS, Deepin, and HarmonyOS PC (openEuler).
- **Engineering & Open Source Infrastructure**:
  - Full GitHub Actions CI/CD workflows for automated testing and single-file release packaging.
  - Bilingual Issue/PR templates, Security Policy (`SECURITY.md`), and Contributor Guidelines.
