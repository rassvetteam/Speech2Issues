using NAudio.CoreAudioApi;

namespace Speech2Issues.App.Audio;

public sealed record AudioDeviceInfo(string? Id, string Name, bool IsDefault, bool UsesSystemDefault = false)
{
    public string DisplayName => UsesSystemDefault
        ? "По умолчанию (Windows)"
        : IsDefault ? $"{Name} (сейчас выбрано в Windows)" : Name;

    public override string ToString() => DisplayName;
}

public static class AudioDeviceService
{
    public static IReadOnlyList<AudioDeviceInfo> GetMicrophones() => GetDevices(DataFlow.Capture, Role.Communications);

    public static IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices() => GetDevices(DataFlow.Render, Role.Multimedia);

    public static MMDevice ResolveMicrophone(string? id) => Resolve(DataFlow.Capture, Role.Communications, id);

    public static MMDevice ResolvePlayback(string? id) => Resolve(DataFlow.Render, Role.Multimedia, id);

    private static IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow, Role role)
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = enumerator.GetDefaultAudioEndpoint(flow, role).ID;
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(device => new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId))
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        devices.Insert(0, new AudioDeviceInfo(null, string.Empty, true, true));
        return devices;
    }

    private static MMDevice Resolve(DataFlow flow, Role role, string? id)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                var device = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault(x => x.ID == id);
                if (device is not null)
                {
                    return device;
                }
            }
            return enumerator.GetDefaultAudioEndpoint(flow, role);
        }
        finally
        {
            enumerator.Dispose();
        }
    }
}
