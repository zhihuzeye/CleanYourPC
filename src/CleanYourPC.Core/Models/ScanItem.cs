using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CleanYourPC.Core.Models;

public class ScanItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public string RuleId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CurrentState { get; set; } = string.Empty;
    public string TargetState { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; }
    public bool RequiresAdmin { get; set; }
    public bool RequiresUserConfirm { get; set; }
    public bool SupportsRestore { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanExecute => Risk != RiskLevel.HighRiskOrUnknown;
    public CleanRule? AssociatedRule { get; set; }
    public InstalledSoftwareInfo? AssociatedSoftware { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class InstalledSoftwareInfo
{
    public string DisplayName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;
    public string RegistryKeyPath { get; set; } = string.Empty;
    public bool IsTargetSecurityOrBloatware { get; set; }
    public bool IsSystemProtected { get; set; }
    public string RiskReason { get; set; } = string.Empty;
}