using System;
using Mo.Models;

namespace Mo.Services;

public interface IHotkeyService : IDisposable
{
    void RegisterProfileHotkey(string profileId, HotkeyBinding binding);
    void UnregisterProfileHotkey(string profileId);
    void UnregisterAll();
    event EventHandler<string>? HotkeyTriggered;
}
