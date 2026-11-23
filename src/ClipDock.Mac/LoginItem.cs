using System.Xml;
using System.Xml.Linq;

namespace ClipDock.Mac;

// Opens ClipDock in the menu bar when the user logs in: a per-user LaunchAgent that starts the bundle's executable with
// --background. Only this one file is written and turning the option off deletes it. launchd reads it at the next
// login; it is never loaded now, which would start a second copy. A switch turned off in System Settings >
// General > Login Items is kept by macOS and never touched here.
internal sealed class LoginItem(string? agentsDirectory = null)
{
    internal const string Label = "app.clipdock.ClipDock";
    internal const string BackgroundArgument = "--background";

    internal string AgentFile => Path.Combine(agentsDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"), Label + ".plist");

    // The executable of an app bundle (…/ClipDock.app/Contents/MacOS/ClipDock), or null for a development build.
    internal static string? BundleExecutable(string? processPath)
    {
        if (processPath is not { Length: > 0 } || !processPath.StartsWith('/')) return null;
        var macOS = Path.GetDirectoryName(processPath);
        var contents = Path.GetDirectoryName(macOS);
        var bundle = Path.GetDirectoryName(contents);
        return Path.GetFileName(macOS) == "MacOS" && Path.GetFileName(contents) == "Contents" &&
            bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? processPath : null;
    }

    // A copy opened from the disk image, or one macOS runs from a translocated (quarantined, not yet moved) folder:
    // that path is gone after a restart, so the login item waits until ClipDock is opened from where it stays.
    internal static bool IsTemporary(string executable) =>
        executable.StartsWith("/Volumes/", StringComparison.Ordinal) || executable.Contains("/AppTranslocation/", StringComparison.Ordinal);

    internal static string Plist(string executable)
    {
        static XElement Key(string name) => new("key", name);
        static XElement Text(string value) => new("string", value);
        var document = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                Key("Label"), Text(Label),
                Key("ProgramArguments"), new XElement("array", Text(executable), Text(BackgroundArgument)),
                Key("RunAtLoad"), new XElement("true"),
                // Only in the logged-in desktop session, with the resources of an ordinary app rather than a throttled daemon.
                Key("LimitLoadToSessionType"), Text("Aqua"),
                Key("ProcessType"), Text("Interactive"),
                // Lets System Settings > Login Items show the item under ClipDock's name.
                Key("AssociatedBundleIdentifiers"), new XElement("array", Text(Label)))));
        using var writer = new Utf8StringWriter();
        using (var xml = XmlWriter.Create(writer, new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\n" })) document.Save(xml);
        return writer + "\n";
    }

    // The executable an agent file starts, or null when it is not the form ClipDock writes.
    internal static string? Target(string? plist)
    {
        if (plist is not { Length: > 0 }) return null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(plist), settings);
            var entries = XDocument.Load(reader).Root?.Element("dict")?.Elements().ToList() ?? [];
            XElement? Value(string key) => entries.SkipWhile(e => e.Name != "key" || e.Value != key).Skip(1).FirstOrDefault();
            if (Value("Label")?.Value != Label) return null;
            var arguments = Value("ProgramArguments")?.Elements("string").Select(e => e.Value).ToList();
            return arguments is [{ Length: > 0 } executable, BackgroundArgument] && executable.StartsWith('/') ? executable : null;
        }
        catch (XmlException) { return null; }
    }

    internal bool Targets(string executable)
    {
        if (!File.Exists(AgentFile)) return false;
        return Target(File.ReadAllText(AgentFile)) == executable;
    }

    // At launch this also keeps the agent pointing at this copy, for example after ClipDock was moved.
    // An unchanged file is not rewritten: each new agent file makes macOS announce a new background item.
    internal void Enable(string executable)
    {
        var contents = Plist(executable);
        if (File.Exists(AgentFile) && File.ReadAllText(AgentFile) == contents) return;
        Directory.CreateDirectory(Path.GetDirectoryName(AgentFile)!);
        var temporary = AgentFile + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        // launchd ignores an agent that others can write to.
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        using (var stream = new FileStream(temporary, options)) stream.Write(System.Text.Encoding.UTF8.GetBytes(contents));
        File.Move(temporary, AgentFile, true);
    }

    internal void Disable()
    {
        if (File.Exists(AgentFile)) File.Delete(AgentFile);
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false);
    }
}
