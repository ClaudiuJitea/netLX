using System.Collections.Concurrent;
using NetLX.Core.Models;

namespace NetLX.Infrastructure.Sniffing;

internal sealed class VlanTrafficCounter
{
    private readonly ConcurrentDictionary<int, (long Frames, long Bytes)> _counts = new();
    public void Clear() => _counts.Clear();
    public void Add(byte[] frame)
    {
        if (frame.Length < 14) return;
        var type = (frame[12] << 8) | frame[13];
        var vlan = -1;
        if (type is 0x8100 or 0x88A8)
        {
            if (frame.Length < 18) return;
            vlan = ((frame[14] & 15) << 8) | frame[15]; // outer tag for stacked VLANs
        }
        _counts.AddOrUpdate(vlan, (1, frame.Length), (_, old) => (old.Frames + 1, old.Bytes + frame.Length));
    }
    public IReadOnlyList<VlanObservation> Snapshot()
    {
        var snapshot = _counts.ToArray();
        var total = snapshot.Sum(x => x.Value.Bytes);
        var tagged = snapshot.Where(x => x.Key >= 0).OrderByDescending(x => x.Value.Bytes).ThenBy(x => x.Key).ToArray();
        VlanObservation Row(int id, long frames, long bytes) => new() { VlanId = id, Frames = frames, Bytes = bytes, SharePercent = total == 0 ? 0 : 100d * bytes / total };
        var rows = tagged.Take(9).Select(x => Row(x.Key, x.Value.Frames, x.Value.Bytes)).ToList();
        if (tagged.Length > 9) rows.Add(Row(-2, tagged.Skip(9).Sum(x => x.Value.Frames), tagged.Skip(9).Sum(x => x.Value.Bytes)));
        foreach (var untagged in snapshot.Where(x => x.Key == -1)) rows.Add(Row(-1, untagged.Value.Frames, untagged.Value.Bytes));
        return rows;
    }
}
