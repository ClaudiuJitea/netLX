using PortStrider.Infrastructure.Sniffing;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public class CaptureSelectionTests
{
    private sealed record Device(string Id, string Alias);
    [Fact]
    public void Missing_or_ambiguous_adapter_never_selects_a_different_device()
    {
        Device[] devices = [new("eth10", "Ethernet"), new("eth11", "Ethernet")];
        Device? Find(string name) => CaptureDeviceResolver.Select(devices, name, d => d.Id, d => d.Alias);
        Assert.Null(Find("eth1")); Assert.Null(Find("missing")); Assert.Null(Find("")); Assert.Null(Find("Ethernet"));
        Assert.Equal("eth10", Find("eth10")?.Id);
    }

    [Fact]
    public void Vlan_totals_include_others_untagged_and_priority_tags()
    {
        var counter = new VlanTrafficCounter();
        for (var vlan = 0; vlan < 12; vlan++)
        {
            var frame = new byte[64 + vlan]; frame[12] = 0x81; frame[15] = (byte)vlan;
            counter.Add(frame);
        }
        counter.Add(new byte[100]);
        var rows = counter.Snapshot();
        Assert.Equal(11, rows.Count);
        Assert.Contains(rows, r => r.Label == "Untagged" && r.Bytes == 100);
        Assert.Contains(rows, r => r.Label == "Other VLANs" && r.Frames == 3);
        Assert.Equal(13, rows.Sum(r => r.Frames));
        Assert.Equal(100d, rows.Sum(r => r.SharePercent), precision: 6);
        Assert.Equal(11, rows[0].VlanId);
    }

    [Fact]
    public void Priority_tag_is_separate_from_untagged_and_short_frames_are_ignored()
    {
        var counter = new VlanTrafficCounter();
        counter.Add(new byte[13]); counter.Add(new byte[64]);
        var priority = new byte[64]; priority[12] = 0x81; priority[14] = 0xA0;
        counter.Add(priority);
        Assert.Contains(counter.Snapshot(), r => r.Label == "0 (priority tag)");
        Assert.Contains(counter.Snapshot(), r => r.Label == "Untagged");
        Assert.Equal(2, counter.Snapshot().Sum(r => r.Frames));
    }
}
