using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Mo.Services;

public sealed class AudioService : IAudioService
{
    public (string? id, string? name) GetDefaultAudioDevice()
    {
        try
        {
            var id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            return (id, null); // name resolved when listing
        }
        catch
        {
            return (null, null);
        }
    }

    public void SetDefaultAudioDevice(string deviceId)
    {
        try
        {
            // Use IPolicyConfig COM interface
            var policyConfig = (IPolicyConfig)new PolicyConfigClient();
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Enumerates render endpoints. Blocking, and therefore must be called from a
    /// background thread — <c>ProfileEditorPage.LoadProfileDeferredAsync</c> wraps it
    /// in <c>Task.Run</c>.
    /// </summary>
    public List<(string id, string name)> GetAudioDevices()
    {
        var result = new List<(string, string)>();
        try
        {
            var task = DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector()).AsTask();

            // Bounded wait: a stuck audio driver must degrade to an empty device list,
            // never hang whichever thread asked for it.
            if (!task.Wait(TimeSpan.FromSeconds(10))) return result;

            // RS0030 (no blocking on Task.Result) exists to stop UI-thread deadlocks.
            // This runs on a thread-pool thread, the task is already known to have
            // completed, and WinRT async completions do not marshal back to the
            // dispatcher — so there is no continuation to deadlock against.
#pragma warning disable RS0030
            var devices = task.Result;
#pragma warning restore RS0030

            foreach (var device in devices)
            {
                result.Add((device.Id, device.Name));
            }
        }
        catch
        {
        }
        return result;
    }

    // COM interop for setting default audio device
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // First 10 methods are OnPropertyValueChanged through GetPropertyValue - need placeholders
        void Reserved1();
        void Reserved2();
        void Reserved3();
        void Reserved4();
        void Reserved5();
        void Reserved6();
        void Reserved7();
        void Reserved8();
        void Reserved9();
        void Reserved10();

        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId,
            ERole eRole);
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }
}
