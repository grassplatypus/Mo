using System;

namespace Mo.Services;

public interface ITrayService : IDisposable
{
    void Initialize();
    void UpdateContextMenu();
}
