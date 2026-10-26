# Security Policy

[简体中文](SECURITY.md) | [English](SECURITY_en.md)

---

`CleanYourPC` is dedicated to delivering transparent, secure, and controllable system ad mitigation and purification. We place the highest priority on the security of our own code and the cleanup operations executed on users' machines.

## Core Security Principles

1. **Zero Backdoors, Zero Telemetry, 100% Transparent**:
   - CleanYourPC does NOT collect or upload user privacy data, hardware telemetry, browsing logs, or system history.
   - All rule definitions (`rules/default-rules.json`, etc.) are plain-text JSON, completely open and verifiable.
2. **Built-in System Whitelisting**:
   - Strict protection mechanisms prohibit any rule from modifying native system drivers, Windows Defender / Security Center, or critical OS boot components.
3. **Automated Safety Audit & Sandbox Isolation**:
   - Rules are parsed and tested with virtual sandboxes. Automated unit tests ensure no unsafe actions can be merged.

## Supported Versions

| Version | Supported |
| :--- | :--- |
| 1.0.x | :white_check_mark: |
| < 1.0 | :x: |

## Reporting a Vulnerability

If you identify a security vulnerability or potential accidental deletion risk in CleanYourPC or its built-in rules, **please do not disclose it via public issues**.

- **Preferred Method**: Report privately via GitHub [Security Advisories](https://github.com/zhihuzeye/CleanYourPC/security/advisories) or contact the maintainers directly.
- **Please include**:
  - Affected CleanYourPC version and OS build.
  - Reproduction steps or PoC.
  - Impact assessment (e.g., privilege escalation risk, accidental file deletion).

We will acknowledge receipt within 48 hours and work on a security fix promptly.
