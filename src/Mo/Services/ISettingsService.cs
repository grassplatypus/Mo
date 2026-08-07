using System;
using System.Threading.Tasks;
using Mo.Models;

namespace Mo.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }

    /// <summary>
    /// Synchronous load for the startup path. settings.json is a few hundred bytes,
    /// so a blocking read costs nothing — and the UI thread MUST NOT block on
    /// <see cref="LoadAsync"/>, whose continuation is posted back to that same
    /// (blocked) thread by the DispatcherQueueSynchronizationContext.
    /// Idempotent with <see cref="LoadAsync"/>; whichever runs first wins.
    /// </summary>
    void Load();

    Task LoadAsync();
    Task SaveAsync();
}
