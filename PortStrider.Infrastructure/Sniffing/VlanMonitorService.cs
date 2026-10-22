using System.Diagnostics;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using SharpPcap;

namespace PortStrider.Infrastructure.Sniffing;

public sealed class VlanMonitorService : IVlanMonitorService, IDisposable
{
    private ILiveDevice? _device;
    private IDisposable? _lease;
    private PacketArrivalEventHandler? _handler;
    private readonly VlanTrafficCounter _counts = new();
    private long _lastUpdate;

    public bool IsRunning { get; private set; }
    public IReadOnlyList<VlanObservation> Current => BuildSnapshot();
    public event EventHandler<IReadOnlyList<VlanObservation>>? Updated;

    public Task StartAsync(string captureDevice, CancellationToken cancellationToken = default)
    {
        StopInternal();
        _counts.Clear();
        _lastUpdate = 0;
        var device = FindDevice(captureDevice)
            ?? throw new InvalidOperationException($"Capture device '{captureDevice}' not found.");
        _lease = CaptureDeviceLease.Acquire(device.Name);

        try
        {
            device.Open(DeviceModes.Promiscuous, 500);
            _handler = (_, e) =>
            {
                var raw = e.GetPacket().Data;
                _counts.Add(raw);
                var now = Stopwatch.GetTimestamp();
                if (now - Interlocked.Read(ref _lastUpdate) >= Stopwatch.Frequency / 4)
                {
                    Interlocked.Exchange(ref _lastUpdate, now);
                    Updated?.Invoke(this, BuildSnapshot());
                }
            };
            device.OnPacketArrival += _handler;
            device.StartCapture();
            _device = device;
            IsRunning = true;
            return Task.CompletedTask;
        }
        catch
        {
            try { device.Close(); } catch { /* ignore cleanup failure */ }
            _handler = null;
            _lease.Dispose();
            _lease = null;
            throw;
        }
    }

    public Task StopAsync()
    {
        StopInternal();
        Updated?.Invoke(this, BuildSnapshot());
        return Task.CompletedTask;
    }

    private IReadOnlyList<VlanObservation> BuildSnapshot() => _counts.Snapshot();

    private static ILiveDevice? FindDevice(string name) => CaptureDeviceResolver.Find(name);

    private void StopInternal()
    {
        IsRunning = false;
        if (_device is null)
        {
            _lease?.Dispose();
            _lease = null;
            return;
        }
        try
        {
            if (_handler is not null) _device.OnPacketArrival -= _handler;
            _device.StopCapture();
            _device.Close();
        }
        catch
        {
            // ignore
        }

        _device = null;
        _handler = null;
        _lease?.Dispose();
        _lease = null;
    }

    public void Dispose() => StopInternal();
}
