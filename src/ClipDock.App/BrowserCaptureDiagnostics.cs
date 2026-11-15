using System.Diagnostics;
using System.Globalization;
using System.IO;
using ClipDock.Core;

namespace ClipDock.App;

internal enum BrowserReadCode
{
    Success, Busy, WorkerStart, WaitTimeout, WalkTimeout, Cancelled, ElementUnavailable, UiaException,
    InvalidOperation, ReadException, InitialForeground, BrowserWindow, RootProcess, NodeLimit, ChildLimit,
    CandidateCount, Bounds, DepthLimit, ToolbarMissing, AddressEditMissing, AddressName, StartFocus,
    BeforeValueForeground, ValuePatternMissing, EndForeground, EndFocus, UrlCandidate
}

internal sealed class BrowserCaptureDiagnostics
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("CLIPDOCK_BROWSER_DIAGNOSTICS") == "1";
    private static readonly object FileGate = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private int reported;
    internal int Visited;
    internal int Depth;
    internal int Candidates;
    internal long ElapsedMilliseconds => elapsed.ElapsedMilliseconds;

    internal void Report(BrowserReadCode code)
    {
        if (!Enabled || Interlocked.Exchange(ref reported, 1) != 0) return;
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(AppPaths.Data);
                var file = Path.Combine(AppPaths.Data, "browser-diagnostic.log");
                if (File.Exists(file) && new FileInfo(file).Length >= 65536) return;
                File.AppendAllText(file, string.Create(CultureInfo.InvariantCulture,
                    $"code={code} visited={Volatile.Read(ref Visited)} depth={Volatile.Read(ref Depth)} elapsed_ms={ElapsedMilliseconds} candidates={Volatile.Read(ref Candidates)}\n"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
