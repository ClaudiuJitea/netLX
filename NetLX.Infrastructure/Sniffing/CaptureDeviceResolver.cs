using SharpPcap;
using SharpPcap.LibPcap;

namespace NetLX.Infrastructure.Sniffing;

internal static class CaptureDeviceResolver
{
    public static ILiveDevice? Find(string name) => Select(CaptureDeviceList.Instance, name,
        device => device.Name, device => (device as LibPcapLiveDevice)?.Interface.FriendlyName);

    internal static T? Select<T>(IEnumerable<T> devices, string name, Func<T, string> id, Func<T, string?> friendly) where T : class
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var items = devices.ToArray();
        var exact = items.Where(d => string.Equals(id(d), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1) return exact[0];
        var aliases = items.Where(d => string.Equals(friendly(d), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return aliases.Length == 1 ? aliases[0] : null;
    }
}
