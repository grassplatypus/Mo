using System;
using System.Threading.Tasks;
using Mo.Models;

namespace Mo.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }

    /// <summary>Synchronous load for the startup path — the UI thread must not block on
    /// <see cref="LoadAsync"/> (.claude/rules/10-code-style.md). Idempotent with it;
    /// whichever runs first wins.</summary>
    void Load();

    Task LoadAsync();
    Task SaveAsync();
}
