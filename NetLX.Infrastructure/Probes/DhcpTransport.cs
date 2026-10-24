using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using PacketDotNet;
using NetLX.Core.Models;
using SharpPcap;
using SharpPcap.LibPcap;

namespace NetLX.Infrastructure.Probes;

internal interface IDhcpTransport : IDisposable
{
    Task SendAsync(byte[] payload, IPAddress source, CancellationToken cancellationToken);
    Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken);
}

internal static class DhcpTransport
{
    public static IDhcpTransport Open(AdapterInfo adapter)
    {
        try { return new PcapDhcpTransport(adapter); }
        catch (Exception captureError)
        {
            try { return new UdpDhcpTransport(adapter); }
            catch (Exception udpError)
            {
                throw new InvalidOperationException(
                    $"DHCP transport unavailable on {adapter.Name}. Check libpcap/Npcap and capture privileges; " +
                    $"UDP/68 may be owned by the OS DHCP client. Capture: {captureError.Message}; UDP: {udpError.Message}");
            }
        }
    }
}

/// <summary>A separate pcap handle coexists with OS DHCP and AutoTest switch discovery.</summary>
internal sealed class PcapDhcpTransport : IDhcpTransport
{
    private readonly LibPcapLiveDevice _device;
    private readonly byte[] _mac;
    private readonly Channel<UdpReceiveResult> _received = Channel.CreateBounded<UdpReceiveResult>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });

    public PcapDhcpTransport(AdapterInfo adapter)
    {
        _mac = DhcpPacket.ParseMac(adapter.MacAddress);
        // Never use the shared singleton handle: changing its filter would break switch discovery.
        _device = LibPcapLiveDeviceList.New().FirstOrDefault(d =>
            d.Name.Equals(adapter.CaptureDevice, StringComparison.OrdinalIgnoreCase)
            || d.Interface.FriendlyName?.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new InvalidOperationException("Selected capture interface not found.");
        try
        {
            _device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = 100, Snaplen = 65535 });
            if (_device.LinkType != LinkLayers.Ethernet)
                throw new NotSupportedException("DHCP injection requires an Ethernet capture interface.");
            _device.Filter = "udp src port 67 and udp dst port 68";
            _device.OnPacketArrival += OnPacket;
            _device.StartCapture();
        }
        catch { Dispose(); throw; }
    }

    private void OnPacket(object sender, PacketCapture capture)
    {
        try
        {
            var raw = capture.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var ip = packet.Extract<IPv4Packet>();
            var udp = packet.Extract<UdpPacket>();
            if (ip is not null && udp is { SourcePort: 67, DestinationPort: 68 })
                _received.Writer.TryWrite(new UdpReceiveResult(udp.PayloadData, new IPEndPoint(ip.SourceAddress, 67)));
        }
        catch (Exception) { /* Ignore malformed frames; protocol matching happens in the service. */ }
    }

    public Task SendAsync(byte[] payload, IPAddress source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _device.SendPacket(BuildFrame(payload, _mac, source));
        return Task.CompletedTask;
    }

    internal static byte[] BuildFrame(byte[] payload, byte[] mac, IPAddress source)
    {
        var udp = new UdpPacket(68, 67) { PayloadData = payload };
        var ip = new IPv4Packet(source, IPAddress.Broadcast) { PayloadPacket = udp, TimeToLive = 64 };
        var ethernet = new EthernetPacket(new System.Net.NetworkInformation.PhysicalAddress(mac),
            new System.Net.NetworkInformation.PhysicalAddress([255, 255, 255, 255, 255, 255]), EthernetType.IPv4)
            { PayloadPacket = ip };
        udp.UpdateCalculatedValues();
        ip.UpdateCalculatedValues();
        udp.UpdateUdpChecksum();
        ip.UpdateIPChecksum();
        return ethernet.Bytes;
    }

    public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken) =>
        await _received.Reader.ReadAsync(cancellationToken);

    public void Dispose()
    {
        _device.OnPacketArrival -= OnPacket;
        try { _device.StopCapture(); }
        finally { _device.Close(); _received.Writer.TryComplete(); }
    }
}

internal sealed class UdpDhcpTransport : IDhcpTransport
{
    private readonly UdpClient _client;

    public UdpDhcpTransport(AdapterInfo adapter)
    {
        _client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        try
        {
            // Never share UDP/68: unicast ACK delivery would race the OS client.
            _client.Client.ExclusiveAddressUse = true;
            if (OperatingSystem.IsLinux())
            {
                _client.Client.SetRawSocketOption(1, 25, Encoding.ASCII.GetBytes(adapter.Name + "\0"));
                _client.Client.Bind(new IPEndPoint(IPAddress.Any, 68));
            }
            else
            {
                // Without an interface-bound raw transport, Windows broadcast routing cannot be guaranteed.
                throw new NotSupportedException("Install Npcap to test DHCP on the selected adapter.");
            }
        }
        catch { _client.Dispose(); throw; }
    }

    public async Task SendAsync(byte[] payload, IPAddress source, CancellationToken cancellationToken) =>
        await _client.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, 67), cancellationToken);

    public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken) =>
        await _client.ReceiveAsync(cancellationToken);

    public void Dispose() => _client.Dispose();
}
