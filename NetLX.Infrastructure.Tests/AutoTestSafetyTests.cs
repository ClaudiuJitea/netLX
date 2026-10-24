using NetLX.Core.Enums;
using NetLX.Core.Models;
using NetLX.Core.Services;
using NetLX.Infrastructure.Probes;
using Xunit;

namespace NetLX.Infrastructure.Tests;

public class AutoTestSafetyTests
{
    [Theory]
    [InlineData(TestStatus.Fail)]
    [InlineData(TestStatus.Warning)]
    public async Task Incomplete_interface_configuration_stops_probes_and_rolls_back(TestStatus status)
    {
        var config = new FailedConfiguration(status);
        // All probe dependencies intentionally absent: none may run after partial configuration.
        var runner = new AutoTestRunner(null!, null!, null!, config, null!, null!);
        var result = await runner.ExecuteAsync(new AdapterInfo { Id = "eth0", Name = "eth0" },
            TestProfile.Default() with { VlanId = 20 }, new Progress<TestStepUpdate>());
        Assert.Equal("FAIL", result.Overall);
        Assert.True(config.Restored);
        Assert.Equal(AutoTestRunner.StepConfig, Assert.Single(result.Steps).Id);
    }

    [Fact]
    public async Task Missing_configured_adapter_does_not_fall_back_to_the_original_adapter()
    {
        var config = new FailedConfiguration(TestStatus.Pass);
        var runner = new AutoTestRunner(null!, null!, null!, config, null!, new MissingAdapters());
        var result = await runner.ExecuteAsync(new AdapterInfo { Id = "test0", Name = "test0" },
            TestProfile.Default() with { OverrideMac = "02:11:22:33:44:55" }, new Progress<TestStepUpdate>());
        Assert.Equal("FAIL", result.Overall);
        Assert.Contains("Cannot read configured interface", Assert.Single(result.Steps).Details);
        Assert.True(config.Restored);
    }

    private sealed class MissingAdapters : IAdapterService
    {
        public IReadOnlyList<AdapterInfo> ListAdapters(bool includeDown = true) => [];
        public AdapterInfo? Find(string nameOrId) => null;
        public InterfaceSnapshot? Snapshot(string adapterId) => null;
    }

    private sealed class FailedConfiguration(TestStatus status) : IInterfaceConfigService
    {
        public bool Restored { get; private set; }
        public bool IsSupported => true;
        public bool RequiresChanges(TestProfile profile) => true;
        public Task<InterfaceConfigSession> ApplyAsync(AdapterInfo adapter, TestProfile profile, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InterfaceConfigSession(new InterfaceConfigResult { Status = status, Summary = "VLAN creation failed" }, adapter.Name, adapter.Name,
                _ => { Restored = true; return Task.CompletedTask; }));
        public Task<string?> ApplyAddressAsync(InterfaceConfigSession session, string address, string? subnetMask, string? gateway, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task FlashPortAsync(string interfaceName, TimeSpan period, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
