using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Mo.Models;
using Windows.System;

namespace Mo.Controls;

public sealed partial class HotkeyPicker : UserControl
{
    private bool _isCapturing;
    private bool _ctrlDown;
    private bool _altDown;
    private bool _shiftDown;
    private bool _winDown;

    public HotkeyPicker()
    {
        InitializeComponent();
    }

    public event EventHandler<HotkeyBinding?>? HotkeyChanged;

    public HotkeyBinding? CurrentBinding { get; private set; }

    public void SetBinding(HotkeyBinding? binding)
    {
        CurrentBinding = binding;
        UpdateDisplay();
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        StartCapture();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        StopCapture();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isCapturing) return;

        e.Handled = true;

        switch (e.Key)
        {
            case VirtualKey.Control:
                _ctrlDown = true;
                UpdateCaptureDisplay();
                return;
            case VirtualKey.Menu: // Alt
                _altDown = true;
                UpdateCaptureDisplay();
                return;
            case VirtualKey.Shift:
                _shiftDown = true;
                UpdateCaptureDisplay();
                return;
            case VirtualKey.LeftWindows:
            case VirtualKey.RightWindows:
                _winDown = true;
                UpdateCaptureDisplay();
                return;
            case VirtualKey.Escape:
                StopCapture();
                return;
            case VirtualKey.Back:
            case VirtualKey.Delete:
                // Clear the binding
                CurrentBinding = null;
                HotkeyChanged?.Invoke(this, null);
                StopCapture();
                return;
        }

        // Need at least one modifier
        if (!_ctrlDown && !_altDown && !_shiftDown && !_winDown)
            return;

        // Got a real key + modifier combination
        CurrentBinding = new HotkeyBinding
        {
            Key = e.Key,
            Ctrl = _ctrlDown,
            Alt = _altDown,
            Shift = _shiftDown,
            Win = _winDown,
        };

        HotkeyChanged?.Invoke(this, CurrentBinding);
        StopCapture();
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_isCapturing) return;

        switch (e.Key)
        {
            case VirtualKey.Control:
                _ctrlDown = false;
                break;
            case VirtualKey.Menu:
                _altDown = false;
                break;
            case VirtualKey.Shift:
                _shiftDown = false;
                break;
            case VirtualKey.LeftWindows:
            case VirtualKey.RightWindows:
                _winDown = false;
                break;
        }
        UpdateCaptureDisplay();
    }

    private void StartCapture()
    {
        _isCapturing = true;
        _ctrlDown = _altDown = _shiftDown = _winDown = false;
        RootGrid.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        RootGrid.BorderThickness = new Thickness(2);
        DisplayText.Text = "Press a key combination...";
        DisplayText.Opacity = 0.7;
    }

    private void StopCapture()
    {
        _isCapturing = false;
        _ctrlDown = _altDown = _shiftDown = _winDown = false;
        RootGrid.BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        RootGrid.BorderThickness = new Thickness(1);
        UpdateDisplay();
    }

    private void UpdateCaptureDisplay()
    {
        var parts = new List<string>();
        if (_ctrlDown) parts.Add("Ctrl");
        if (_altDown) parts.Add("Alt");
        if (_shiftDown) parts.Add("Shift");
        if (_winDown) parts.Add("Win");

        DisplayText.Text = parts.Count > 0
            ? string.Join(" + ", parts) + " + ..."
            : "Press a key combination...";
    }

    private void UpdateDisplay()
    {
        if (CurrentBinding != null)
        {
            DisplayText.Text = CurrentBinding.ToString();
            DisplayText.Opacity = 1.0;
            ClearButton.Visibility = Visibility.Visible;
        }
        else
        {
            DisplayText.Text = "Click to set hotkey";
            DisplayText.Opacity = 0.5;
            ClearButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        CurrentBinding = null;
        HotkeyChanged?.Invoke(this, null);
        UpdateDisplay();
    }
}
