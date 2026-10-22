using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Infrastructure.Adapters;
using PortStrider.Infrastructure.Platform;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public class InterfaceConfigurationSafetyTests
{
    private static readonly AdapterInfo Adapter = new() { Id = "test0", Name = "test0", MacAddress = "00:11:22:33:44:55" };
    private static PrivilegedProcess.Result Ok(string output = "") => new(0, output, "", "");
    private static PrivilegedProcess.Result Refused() => new(2, "", "File exists", "");

    [Fact]
    public async Task Existing_vlan_is_never_deleted_or_reconfigured()
    {
        var commands = new List<string>();
        var service = new InterfaceConfigService((file, args, ct) =>
        {
            commands.Add(file + " " + string.Join(' ', args));
            return Task.FromResult(Refused());
        });
        await using var session = await service.ApplyAsync(Adapter, TestProfile.Default() with { VlanId = 20 });
        Assert.Equal(TestStatus.Fail, session.Result.Status);
        Assert.Single(commands);
        Assert.StartsWith("ip link add ", commands[0]);
    }

    [Fact]
    public async Task Cancellation_after_vlan_creation_deletes_only_the_owned_interface()
    {
        var commands = new List<string>();
        var service = new InterfaceConfigService((file, args, ct) =>
        {
            commands.Add(string.Join(' ', args));
            if (args[1] == "set") throw new OperationCanceledException();
            Assert.False(ct.IsCancellationRequested);
            return Task.FromResult(Ok());
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyAsync(Adapter, TestProfile.Default() with { VlanId = 20 }));
        Assert.Equal("link del test0.20", commands[^1]);
        Assert.Equal(3, commands.Count);
    }

    [Fact]
    public async Task Failed_mac_change_restores_original_mac_and_link_state()
    {
        var commands = new List<string>();
        var service = new InterfaceConfigService((file, args, ct) =>
        {
            var command = string.Join(' ', args);
            commands.Add(command);
            return Task.FromResult(command.EndsWith("address 02:aa:bb:cc:dd:ee") ? Refused() : Ok());
        });
        await using var session = await service.ApplyAsync(Adapter, TestProfile.Default() with { OverrideMac = "02:aa:bb:cc:dd:ee" });
        Assert.Equal(TestStatus.Fail, session.Result.Status);
        Assert.Contains("link set dev test0 address 00:11:22:33:44:55", commands);
        Assert.Equal("link set dev test0 up", commands[^1]);
    }

    [Fact]
    public async Task Failed_forced_mode_restores_previously_forced_settings()
    {
        var commands = new List<string>();
        var service = new InterfaceConfigService((file, args, ct) =>
        {
            var command = string.Join(' ', args);
            commands.Add(command);
            return Task.FromResult(args.Count == 1 ? Ok("Speed: 100Mb/s\nDuplex: Half\nAuto-negotiation: off\n") : Refused());
        });
        await using var session = await service.ApplyAsync(Adapter, TestProfile.Default() with { LinkSpeed = LinkSpeedSetting.Mbps1000 });
        Assert.Equal(TestStatus.Fail, session.Result.Status);
        Assert.Equal("-s test0 autoneg off speed 100 duplex half", commands[^1]);
    }

    [Fact]
    public async Task Conflicting_static_address_is_not_replaced_or_removed()
    {
        var commands = new List<string>();
        var service = new InterfaceConfigService((file, args, ct) =>
        {
            commands.Add(string.Join(' ', args));
            return Task.FromResult(Refused());
        });
        await using var session = await service.ApplyAsync(Adapter, TestProfile.Default() with { AddressMode = AddressMode.Static, StaticIpv4 = "192.0.2.7/24" });
        Assert.Equal(TestStatus.Fail, session.Result.Status);
        Assert.Equal("addr add 192.0.2.7/24 dev test0", Assert.Single(commands));
    }

    [Theory]
    [InlineData("192.0.2.1", "255.0.255.0")]
    [InlineData("192.0.2.1", "ffff:ffff::")]
    [InlineData("2001:db8::1/24", null)]
    [InlineData("192.0.2.1/24/2", null)]
    [InlineData("192.0.2.1/33", null)]
    public void Rejects_invalid_ipv4_subnets(string address, string? mask)
        => Assert.Null(InterfaceConfigService.ToCidr(address, mask));
}
