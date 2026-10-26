# Contributing to CleanYourPC

[简体中文](CONTRIBUTING.md) | [English](CONTRIBUTING_en.md)

---

Thank you for your interest in contributing to `CleanYourPC`!

Our goal is to help regular PC users eliminate aggressive lock-screen ads, system notifications, and persistent adware residue. To protect the stability and security of tens of thousands of users' operating systems, all submitted code and rule updates must strictly adhere to the following guidelines.

---

## 1. Three Non-Negotiable Safety Red Lines

1. **Never Break Core Functionality**:
   - Any rule targeting WPS Office, input methods, browsers, or system components must NOT cause crashes, prevent typing, block document editing, or hinder system logon.
2. **Never Touch System Security Baselines or Hardware Drivers**:
   - Never classify Windows Defender, Windows Update, firewall components, core system services (DCOM/RPC/PlugPlay), or essential hardware drivers (Realtek, Synaptics, Intel, AMD, NVIDIA, Dolby, etc.) as cleanup targets.
3. **Reversibility & Backup First**:
   - Any action involving registry values, service states, or folder permissions must be fully reversible and logged via `BackupRestoreService`, allowing users to roll back at any time.

---

## 2. Setting Up the Local Development Environment

### Prerequisites
- **Operating Systems**: Windows 10/11, macOS, or Linux
- **.NET SDK**: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Recommended IDEs**: Visual Studio 2022 (with .NET desktop development workload), JetBrains Rider, or VS Code (with C# Dev Kit)

### Clone & Test
```bash
# Clone the repository
git clone https://github.com/zhihuzeye/CleanYourPC.git
cd CleanYourPC

# Restore dependencies and execute test suite
dotnet test

# Run the desktop client (Windows environment)
dotnet run --project src/CleanYourPC.App/CleanYourPC.App.csproj
```

---

## 3. Contributing New Mitigation Rules (`rules/default-rules.json`)

CleanYourPC’s core detection and cleanup capabilities are driven by pure JSON rules. If you discover a new persistent popup or adware residue, we welcome your contribution!

### Rule Structure Specification
When adding a new rule entry, ensure all required metadata fields are properly populated:

```json
{
  "RuleId": "AD-VENDOR-00X",
  "Category": "SoftwareAd",
  "DisplayName": "Disable Vendor News Popup",
  "TargetSoftware": "Target Software Name",
  "ImpactLevel": "Safe",
  "RequiresElevation": false,
  "ActionType": "RegistryValue",
  "TargetPath": "HKCU\\Software\\Vendor\\App",
  "ValueName": "EnablePopups",
  "ExpectedCleanValue": 0,
  "RollbackValue": 1,
  "SourceAndVerification": "Verified on Windows 11 23H2 + Vendor App v1.2.3. Popups disabled without affecting core functionality."
}
```

### Verification Requirements
- **Real-Device Testing**: Must be tested in a physical machine or test VM to confirm the popup is suppressed and normal functions remain unaffected.
- **Run Unit Tests**: Execute `dotnet test` locally to ensure schema parsing and sandbox isolation tests pass 100%.

---

## 4. Pull Request Workflow

1. **Fork this repository** to your GitHub account.
2. Create a new branch from `main`: `git checkout -b feat/add-new-rule`.
3. Follow the code formatting rules specified in `.editorconfig`.
4. Ensure all unit tests pass: `dotnet test`.
5. Commit your changes (recommended: follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/), e.g., `feat: add rule for XYZ popup`).
6. Push the branch and open a Pull Request against `main`.
7. Fill out the PR template checklist. The maintainers will review and test promptly!
