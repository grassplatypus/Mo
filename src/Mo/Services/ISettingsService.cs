using System;
using System.Threading.Tasks;
using Mo.Models;

namespace Mo.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    Task LoadAsync();
    Task SaveAsync();
}
