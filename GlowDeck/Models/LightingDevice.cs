using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GlowDeck.Models;

public sealed class LightingDevice : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isSessionValid = true;
    private string _status = string.Empty;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Details { get; init; }
    public required string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    public DeviceGroup Group { get; init; }
    public uint? ControllerId { get; init; }
    public int? ZoneIndex { get; init; }
    public int LedCount { get; init; }
    public bool SupportsControl { get; init; } = true;

    public string GroupName => Group.ToDisplayName();
    public bool IsControllable => SupportsControl && IsSessionValid && ControllerId.HasValue && LedCount > 0;
    public string LedCountLabel => IsControllable ? $"{LedCount} LEDs" : "Read only";
    public string ConnectionStateLabel => !SupportsControl
        ? "Read only"
        : ControllerId.HasValue && !IsSessionValid
            ? "Refresh first"
            : IsControllable ? "Ready" : "Read only";

    public bool IsSessionValid
    {
        get => _isSessionValid;
        set
        {
            if (_isSessionValid == value)
            {
                return;
            }

            _isSessionValid = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsControllable));
            OnPropertyChanged(nameof(LedCountLabel));
            OnPropertyChanged(nameof(ConnectionStateLabel));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
