using CommunityToolkit.Mvvm.ComponentModel;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;

namespace PortStrider.UI.ViewModels;

public partial class TargetEditorViewModel : ViewModelBase
{
    public string Id { get; }
    [ObservableProperty] private string _label;
    [ObservableProperty] private TargetKind _kind;
    [ObservableProperty] private string _host;
    [ObservableProperty] private int _port;
    [ObservableProperty] private string _url;
    [ObservableProperty] private bool _enabled;
    public IReadOnlyList<TargetKind> Kinds { get; } = Enum.GetValues<TargetKind>();
    public bool IsHttp => Kind == TargetKind.Http;
    public bool IsTcp => Kind == TargetKind.TcpPort;
    partial void OnKindChanged(TargetKind value) { OnPropertyChanged(nameof(IsHttp)); OnPropertyChanged(nameof(IsTcp)); }

    public TargetEditorViewModel(TestTarget target)
    {
        Id = target.Id; _label = target.Label; _kind = target.Kind; _host = target.Host;
        _port = target.Port; _url = target.Url; _enabled = target.Enabled;
    }

    public TestTarget ToTarget() => new()
    {
        Id = Id, Label = Label.Trim(), Kind = Kind, Host = Host.Trim(), Port = Port, Url = Url.Trim(), Enabled = Enabled
    };
}
