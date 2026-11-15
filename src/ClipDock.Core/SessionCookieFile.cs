using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ClipDock.Core;

public sealed record SessionCookie(string Domain, string Path, string Name, string Value, bool Secure, bool HttpOnly, long Expires);

public sealed class SessionCookieFile : IDisposable
{
    public string FilePath { get; }
    private readonly string[] values;
    private SessionCookieFile(string filePath, string[] secrets) { FilePath = filePath; values = secrets; }

    public static SessionCookieFile? Create(string url, IEnumerable<SessionCookie> source, string directory)
    {
        var cookies = source.Where(c => SiteSessionScope.AllowsCookie(url, c.Domain) &&
            c.Name.Length > 0 && c.Value.Length > 0 && c.Path.StartsWith('/') &&
            new[] { c.Domain, c.Path, c.Name, c.Value }.All(v => !v.Any(char.IsControl)) &&
            (c.Expires == 0 || c.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            .DistinctBy(c => (c.Domain, c.Path, c.Name)).Take(500).ToArray();
        if (cookies.Length == 0) return null;
        var dir = new DirectoryInfo(directory);
        if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException(ClipDock.Core.Localization.Text("ログイン情報の一時保存先を使用できません。"));
        // Only the current user may read the folder that briefly holds session cookies.
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (!dir.Exists) dir.Create(security); else dir.SetAccessControl(security);
        }
        else
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if (!dir.Exists) Directory.CreateDirectory(dir.FullName, ownerOnly); else File.SetUnixFileMode(dir.FullName, ownerOnly);
        }
        var path = Path.Combine(dir.FullName, Guid.NewGuid().ToString("N") + ".cookies");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine("# Netscape HTTP Cookie File");
            foreach (var c in cookies)
                writer.WriteLine(string.Join('\t', (c.HttpOnly ? "#HttpOnly_" : "") + c.Domain, c.Domain.StartsWith('.') ? "TRUE" : "FALSE",
                    c.Path, c.Secure ? "TRUE" : "FALSE", c.Expires.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Name, c.Value));
        }
        catch { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
        return new(path, cookies.Select(c => c.Value).Distinct().OrderByDescending(v => v.Length).ToArray());
    }
    public string Redact(string line)
    {
        foreach (var value in values) line = line.Replace(value, "[login]", StringComparison.Ordinal);
        return line.Replace(FilePath, "[session]", StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose() => File.Delete(FilePath);
}
