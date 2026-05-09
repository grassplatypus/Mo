using System;
using Mo.Models;

namespace Mo.Services;

public interface IHotkeyService : IDisposable
{
    void SetWindowHandle(nint hwnd);
    void RegisterProfileHotkey(string profileId, HotkeyBinding binding);
    void UnregisterProfileHotkey(string profileId);
    void RegisterNextProfile(HotkeyBinding binding);
    void RegisterPrevProfile(HotkeyBinding binding);
    void RegisterProfileSlot(int slot, HotkeyBinding binding);
    void UnregisterAll();
    event EventHandler<HotkeyTriggeredArgs>? HotkeyTriggered;
}
