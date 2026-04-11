using System.Runtime.InteropServices;
using Mo.Models;

namespace Mo.Services;

public sealed class HotkeyService : IHotkeyService
{
    private readonly Dictionary<int, string> _registeredHotkeys = new();
    private int _nextId = 0x0001;
    private nint _hwnd;

    public event EventHandler<string>? HotkeyTriggered;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    public void SetWindowHandle(nint hwnd)
    {
        _hwnd = hwnd;
    }

    public void RegisterProfileHotkey(string profileId, HotkeyBinding binding)
    {
        if (_hwnd == 0) return;

        // Unregister existing hotkey for this profile
        UnregisterProfileHotkey(profileId);

        uint modifiers = MOD_NOREPEAT;
        if (binding.Ctrl) modifiers |= MOD_CONTROL;
        if (binding.Alt) modifiers |= MOD_ALT;
        if (binding.Shift) modifiers |= MOD_SHIFT;
        if (binding.Win) modifiers |= MOD_WIN;

        int id = _nextId++;
        if (RegisterHotKey(_hwnd, id, modifiers, (uint)binding.Key))
        {
            _registeredHotkeys[id] = profileId;
        }
    }

    public void UnregisterProfileHotkey(string profileId)
    {
        var entry = _registeredHotkeys.FirstOrDefault(kvp => kvp.Value == profileId);
        if (entry.Value != null)
        {
            UnregisterHotKey(_hwnd, entry.Key);
            _registeredHotkeys.Remove(entry.Key);
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _registeredHotkeys.Keys.ToList())
        {
            UnregisterHotKey(_hwnd, id);
        }
        _registeredHotkeys.Clear();
    }

    public void ProcessHotkeyMessage(int hotkeyId)
    {
        if (_registeredHotkeys.TryGetValue(hotkeyId, out var profileId))
        {
            HotkeyTriggered?.Invoke(this, profileId);
        }
    }

    public void Dispose()
    {
        UnregisterAll();
    }
}
