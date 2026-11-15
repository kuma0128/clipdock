namespace ClipDock.Core;

public static class Integration
{
    public const string HostName = "com.clipdock.downloader";
    public const string ExtensionId = "oeolbdjffnnmaanghklbkhfhgihepfhd";
    public const string Origin = "chrome-extension://" + ExtensionId + "/";
    public const string StoreExtensionId = "okehkphoehepefjddoiinohnhpjghefe";
    public const string StoreOrigin = "chrome-extension://" + StoreExtensionId + "/";
    public static IReadOnlyList<string> AllowedOrigins { get; } = Array.AsReadOnly(new[] { Origin, StoreOrigin });

    public static bool IsAllowedOrigin(string? origin) => origin is Origin or StoreOrigin;

    public static DownloadRequest ValidateRequest(string origin, DownloadRequest request)
    {
        if (!IsAllowedOrigin(origin)) throw new ArgumentException("許可されていない拡張機能です。");
        if (origin == StoreOrigin && request.Action == "download") request = request with { RestrictYouTube = true };
        return request.Validated();
    }

    // The GUI must not inherit the native messaging process's standard streams.
    public static System.Diagnostics.ProcessStartInfo AppStartInfo(string executable) => new(executable) { UseShellExecute = true };
}
