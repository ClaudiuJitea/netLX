using System.Net;
using NetLX.Core.Enums;

namespace NetLX.Core.Models;

public static class TargetValidation
{
    public static string? Error(TestTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Label)) return "Enter a target label.";
        if (!Enum.IsDefined(target.Kind)) return "Unsupported target type.";
        if (!target.Enabled) return null;
        if (target.Kind == TargetKind.Http)
            return Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) ? null : "Enter an HTTP(S) URL without embedded credentials.";
        if (target.Kind == TargetKind.TcpPort && target.Port is < 1 or > 65535) return "TCP port must be between 1 and 65535.";
        if (target.Id == "gw" && string.IsNullOrWhiteSpace(target.Host)) return null; // dynamic gateway default
        return IPAddress.TryParse(target.Host, out _) || Uri.CheckHostName(target.Host) != UriHostNameType.Unknown
            ? null : "Enter a hostname or IP address.";
    }
}
