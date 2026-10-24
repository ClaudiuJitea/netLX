using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using NetLX.Infrastructure.Adapters;
using NetLX.Infrastructure.Cable;
using NetLX.Infrastructure.Capabilities;
using NetLX.Infrastructure.PreFlight;
using NetLX.Infrastructure.Probes;
using NetLX.Infrastructure.Profiles;
using NetLX.Infrastructure.Reports;
using NetLX.Infrastructure.Sniffing;
using NetLX.Infrastructure.Wifi;
using NetLX.UI.ViewModels;
using NetLX.UI.Views;

namespace NetLX.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                // Let ip/ethtool/wpa_supplicant children inherit the binary's setcap grants (no-op without them).
                _ = NetLX.Infrastructure.Platform.PrivilegedProcess.HasAmbientNetCapabilities;
                var adapters = new NetworkAdapterService();
                var session = new AppSession(adapters);
                var preflight = new DependencyManager();
                var discovery = new SwitchDiscoveryService();
                var probes = new ProbeService();
                var dhcp = new DhcpTestService();
                var telemetry = new LinkTelemetryService();
                var ifconfig = new InterfaceConfigService();
                var dot1x = new WpaSupplicantDot1xService();
                var autoTest = new AutoTestRunner(probes, dhcp, telemetry, ifconfig, dot1x, adapters);
                var capture = new PacketCaptureService();
                var vlanMonitor = new VlanMonitorService();
                var reflector = new ReflectorService();
                var cable = new CableTestService();
                var sfp = new SfpDiagnosticsService();
                var iperf = new IperfService();
                var neighbors = new NeighborService();
                var reports = new JsonReportStore();
                var profiles = new JsonProfileStore();
                var capabilities = new CapabilityService();

                var vm = new MainViewModel(
                    session,
                    new AutoTestViewModel(autoTest, discovery, reports, profiles, telemetry, probes, adapters, ifconfig, session),
                    new SwitchViewModel(discovery, ifconfig, session),
                    new CaptureViewModel(capture, session),
                    new ToolsViewModel(probes, iperf, session),
                    new ReflectorViewModel(reflector, session),
                    new CableViewModel(cable, sfp, session),
                    new NeighborsViewModel(neighbors, session),
                    new ReportsViewModel(reports),
                    new ProfilesViewModel(profiles, session),
                    new VlanMonitorViewModel(vlanMonitor, session),
                    new CapabilitiesViewModel(capabilities, session),
                    new PreFlightViewModel(preflight),
                    new WifiViewModel(new WifiService(), session));

                desktop.MainWindow = new MainWindow { DataContext = vm };
                desktop.Exit += (_, _) =>
                {
                    vm.Wifi.Stop();
                    capture.Dispose();
                    vlanMonitor.Dispose();
                    reflector.Dispose();
                    _ = iperf.StopServerAsync();
                };
                desktop.MainWindow.Opened += async (_, _) => await vm.InitializeAsync();
            }
            catch (Exception ex)
            {
                desktop.MainWindow = new Window
                {
                    Title = "netLX startup error",
                    Width = 720,
                    Height = 420,
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = ex.ToString(),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(16)
                        }
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
