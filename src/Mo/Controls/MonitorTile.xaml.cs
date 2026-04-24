using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mo.Models;

namespace Mo.Controls;

public sealed partial class MonitorTile : UserControl
{
    private MonitorInfo? _monitor;
    private int _monitorIndex;
    private bool _isSelected;

    public MonitorTile()
    {
        InitializeComponent();
    }

    public MonitorInfo? Monitor
    {
        get => _monitor;
        set
        {
            _monitor = value;
            UpdateDisplay();
        }
    }

    public int MonitorIndex
    {
        get => _monitorIndex;
        set
        {
            _monitorIndex = value;
            NumberText.Text = (value + 1).ToString();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            RootBorder.BorderBrush = value
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            RootBorder.BorderThickness = value ? new Thickness(2) : new Thickness(1);
        }
    }

    private void UpdateDisplay()
    {
        if (_monitor == null) return;

        var fullName = string.IsNullOrEmpty(_monitor.FriendlyName)
            ? $"Display {_monitorIndex + 1}"
            : _monitor.FriendlyName;
        NameText.Text = fullName;
        ToolTipService.SetToolTip(NameText, fullName);

        ResolutionText.Text = _monitor.ResolutionText;

        if (_monitor.Rotation != DisplayRotation.None)
        {
            RotationBadge.Visibility = Visibility.Visible;
            RotationIcon.RenderTransform = new RotateTransform
            {
                Angle = (int)_monitor.Rotation,
                CenterX = 6,
                CenterY = 6,
            };
        }
        else
        {
            RotationBadge.Visibility = Visibility.Collapsed;
        }

        PrimaryBadge.Visibility = _monitor.IsPrimary ? Visibility.Visible : Visibility.Collapsed;

        RootBorder.Opacity = _monitor.IsEnabled ? 1.0 : 0.4;
    }
}
