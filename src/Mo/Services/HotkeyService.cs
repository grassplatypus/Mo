using System.Runtime.InteropServices;
using Mo.Models;
using Windows.System;

namespace Mo.Services;

// Registers process-wide hotkeys via RegisterHotKey + intercepts WM_HOTKEY by
// subclassing the main window. Without the subclass the system fires hotkeys
// into the message queue but no one ever reads them — earlier versions of this
// service registered keys but the events were silently dropped.
public sealed class HotkeyService : IHotkeyService
{
    // Action kinds the registered hotkeys can dispatch.
    public enum HotkeyAction { Profile, NextProfile, PrevProfile, ProfileSlot }

    private readonly Dictionary<int, HotkeyEntry> _registered = new();
    private int _nextId = 0xA001;
    private nint _hwnd;
    private SUBCLASSPROC? _subclassProc;
    private bool _subclassInstalled;

    public event EventHandler<HotkeyTriggeredArgs>? HotkeyTriggered;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

    private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    public void SetWindowHandle(nint hwnd)
    {
        if (_hwnd == hwnd) return;

        // Re-register everything on the new window if we already had bindings; the
        // OS associates each hotkey with a specific HWND so they would otherwise
        // become orphans.
        var snapshot = _registered.Values.ToList();
        UnregisterAll();
        UninstallSubclass();

        _hwnd = hwnd;
        if (_hwnd != 0) InstallSubclass();

        foreach (var entry in snapshot)
            Register(entry.Action, entry.Binding, entry.Payload);
    }

    private void InstallSubclass()
    {
        if (_subclassInstalled || _hwnd == 0) return;
        _subclassProc ??= SubclassWndProc;
        _subclassInstalled = SetWindowSubclass(_hwnd, _subclassProc, 0xA0FE, 0);
    }

    private void UninstallSubclass()
    {
        if (!_subclassInstalled || _subclassProc == null || _hwnd == 0) return;
        try { RemoveWindowSubclass(_hwnd, _subclassProc, 0xA0FE); } catch { }
        _subclassInstalled = false;
    }

    private nint SubclassWndProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_HOTKEY)
        {
            try { Dispatch((int)wParam); } catch { /* never let a handler crash the message loop */ }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void Dispatch(int id)
    {
        if (!_registered.TryGetValue(id, out var entry)) return;
        HotkeyTriggered?.Invoke(this, new HotkeyTriggeredArgs(entry.Action, entry.Payload));
    }

    public void RegisterProfileHotkey(string profileId, HotkeyBinding binding)
        => Register(HotkeyAction.Profile, binding, profileId);

    public void UnregisterProfileHotkey(string profileId)
    {
        var match = _registered.FirstOrDefault(kvp =>
            kvp.Value.Action == HotkeyAction.Profile && kvp.Value.Payload == profileId);
        if (match.Value != null)
        {
            try { UnregisterHotKey(_hwnd, match.Key); } catch { }
            _registered.Remove(match.Key);
        }
    }

    public void RegisterNextProfile(HotkeyBinding binding) => Register(HotkeyAction.NextProfile, binding, null);
    public void RegisterPrevProfile(HotkeyBinding binding) => Register(HotkeyAction.PrevProfile, binding, null);

    public void RegisterProfileSlot(int slot, HotkeyBinding binding)
    {
        if (slot is < 0 or > 9) return;
        Register(HotkeyAction.ProfileSlot, binding, slot.ToString());
    }

    private void Register(HotkeyAction action, HotkeyBinding binding, string? payload)
    {
        if (_hwnd == 0 || binding.Key == VirtualKey.None) return;

        // Replace any existing binding with the same (action, payload) tuple.
        var dupes = _registered.Where(kvp => kvp.Value.Action == action && kvp.Value.Payload == payload)
            .Select(kvp => kvp.Key).ToList();
        foreach (var k in dupes)
        {
            try { UnregisterHotKey(_hwnd, k); } catch { }
            _registered.Remove(k);
        }

        uint mods = MOD_NOREPEAT;
        if (binding.Ctrl) mods |= MOD_CONTROL;
        if (binding.Alt) mods |= MOD_ALT;
        if (binding.Shift) mods |= MOD_SHIFT;
        if (binding.Win) mods |= MOD_WIN;

        int id = _nextId++;
        if (RegisterHotKey(_hwnd, id, mods, (uint)binding.Key))
            _registered[id] = new HotkeyEntry(action, binding, payload);
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList())
        {
            try { UnregisterHotKey(_hwnd, id); } catch { }
        }
        _registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        UninstallSubclass();
    }

    private sealed record HotkeyEntry(HotkeyAction Action, HotkeyBinding Binding, string? Payload);
}

public sealed record HotkeyTriggeredArgs(HotkeyService.HotkeyAction Action, string? Payload);
