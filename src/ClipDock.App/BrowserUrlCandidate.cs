namespace ClipDock.App;

internal static class BrowserUrlCandidate
{
    internal static bool TryParse(string? displayed, out string url, out bool addedScheme)
    {
        url = "";
        addedScheme = false;
        var value = displayed?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 8192 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || value.Contains('\\')) return false;
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            var authority = value.Split('/', '?', '#')[0];
            if (!authority.Contains('.') && !authority.StartsWith('[')) return false;
            value = "https://" + value;
            addedScheme = true;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || parsed.UserInfo.Length != 0 ||
            parsed.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)) return false;
        url = value;
        return true;
    }
}
