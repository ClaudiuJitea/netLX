namespace NetLX.Infrastructure.Platform;

/// <summary>
/// Per-user locations for netLX data (profiles, sessions), exports and downloaded helper tools.
/// Releases before 1.4 used a different folder name; its contents are moved here once on first access.
/// </summary>
public static class AppPaths
{
    public const string FolderName = "netLX";
    private const string LegacyFolderName = "PortStrider";

    private static readonly Lazy<string> DataRootLazy = new(() =>
        Prepare(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    private static readonly Lazy<string> ExportRootLazy = new(() =>
        Prepare(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    /// <summary>Application state: profiles, session index and attachments.</summary>
    public static string DataRoot => DataRootLazy.Value;

    /// <summary>User-visible exports: captures, reports and profile files.</summary>
    public static string ExportRoot => ExportRootLazy.Value;

    /// <summary>Helper binaries downloaded by Pre-flight (Windows iperf3).</summary>
    public static string ToolsDir => Path.Combine(ExportRoot, "bin");

    /// <summary>Returns <see cref="ExportRoot"/> after making sure it exists.</summary>
    public static string EnsureExportRoot()
    {
        Directory.CreateDirectory(ExportRoot);
        return ExportRoot;
    }

    internal static string Prepare(string parent)
    {
        var target = Path.Combine(parent, FolderName);
        var legacy = Path.Combine(parent, LegacyFolderName);
        try
        {
            if (!Directory.Exists(target) && Directory.Exists(legacy))
                Directory.Move(legacy, target);
        }
        catch
        {
            // Leave the old folder in place; the app starts with fresh data instead of failing.
        }
        return target;
    }
}
