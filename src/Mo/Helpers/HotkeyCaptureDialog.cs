using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mo.Models;
using Windows.System;

namespace Mo.Helpers;

// Modal hotkey-capture dialog. Listens for the first non-modifier key press while
// any modifier(s) are held, then returns the resulting HotkeyBinding. Returning
// null clears the binding.
public static class HotkeyCaptureDialog
{
    public static async Task<HotkeyResult?> ShowAsync(XamlRoot root, HotkeyBinding? current)
    {
        var hint = new TextBlock
        {
            Text = ResourceHelper.GetString("HotkeyCaptureHint"),
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };
        var preview = new TextBlock
        {
            Text = current?.ToString() ?? ResourceHelper.GetString("HotkeyNone"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 16),
        };

        HotkeyBinding? captured = current;
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(hint);
        panel.Children.Add(preview);

        var dialog = new ContentDialog
        {
            Title = ResourceHelper.GetString("HotkeyCaptureTitle"),
            Content = panel,
            PrimaryButtonText = ResourceHelper.GetString("Save"),
            SecondaryButtonText = ResourceHelper.GetString("HotkeyClear"),
            CloseButtonText = ResourceHelper.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        // Capture at the dialog level so any focused element forwards events.
        void OnKeyDown(object? s, KeyRoutedEventArgs e)
        {
            // Ignore raw modifier presses — wait for a non-modifier finalizer.
            if (IsModifier(e.Key)) return;

            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            var altState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            var winState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows);
            var winRState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows);

            captured = new HotkeyBinding
            {
                Key = e.Key,
                Ctrl = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down),
                Alt = altState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down),
                Shift = shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down),
                Win = winState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
                       || winRState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down),
            };
            preview.Text = captured.ToString();
            e.Handled = true;
        }

        dialog.KeyDown += OnKeyDown;
        var result = await dialog.ShowAsync();
        dialog.KeyDown -= OnKeyDown;

        return result switch
        {
            ContentDialogResult.Primary => new HotkeyResult(captured),
            ContentDialogResult.Secondary => new HotkeyResult(null), // Clear
            _ => null, // Cancel — no change
        };
    }

    private static bool IsModifier(VirtualKey k) => k is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;
}

public sealed record HotkeyResult(HotkeyBinding? Binding);
