namespace ClipDock.Core;

public static class Integration
{
    public const string HostName = "com.clipdock.downloader";
    public const string ExtensionId = "oeolbdjffnnmaanghklbkhfhgihepfhd";
    public const string Origin = "chrome-extension://" + ExtensionId + "/";

    // The GUI must not inherit the native messaging process's standard streams.
    public static System.Diagnostics.ProcessStartInfo AppStartInfo(string executable) => new(executable) { UseShellExecute = true };
}
