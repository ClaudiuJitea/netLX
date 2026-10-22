using System.Net;
using System.Net.Sockets;

namespace PortStrider.Infrastructure.Probes;

internal static class BoundConnection
{
    public static IPAddress? ParseSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!IPAddress.TryParse(value, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            throw new ArgumentException("Choose a specific source IP address.");
        return address;
    }

    public static async Task<NetworkStream> ConnectAsync(string host, int port, string? sourceAddress, CancellationToken ct)
    {
        var source = ParseSource(sourceAddress);
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        Exception? last = null;
        foreach (var address in addresses.Where(a => source is null || a.AddressFamily == source.AddressFamily))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                if (source is not null) socket.Bind(new IPEndPoint(source, 0));
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) { socket.Dispose(); throw; }
            catch (Exception ex) { socket.Dispose(); last = ex; }
        }
        throw last ?? new InvalidOperationException("No destination address matches the selected source address family.");
    }
}
