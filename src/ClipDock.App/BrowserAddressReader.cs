using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ClipDock.App;

internal sealed record BrowserAddressResult(string? Url = null, bool AddedScheme = false, string Message = BrowserAddressReader.ManualMessage);

internal sealed class BrowserAddressReader
{
    internal const string ManualMessage = "ブラウザーからURLを取得できませんでした。アドレスバーのURLをコピーし、ここに貼り付けてください。";
    private const string EditingMessage = "アドレスバーが編集中です。ページを開いてから取り込むか、URLをここに貼り付けてください。";
    private static readonly HashSet<string> AddressNames = new(StringComparer.Ordinal)
    {
        "Address and search bar", "Address bar", "Search or enter web address",
        "アドレスと検索バー", "アドレス バーと検索バー", "アドレス バー", "検索または Web アドレスを入力"
    };
    private int reading;

    internal async Task<BrowserAddressResult> ReadAsync(nint window, CancellationToken cancellationToken)
    {
        var attempt = new BrowserCaptureDiagnostics();
        if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
        { attempt.Report(BrowserReadCode.Busy); return new(); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = budget.Token;
        var completion = new TaskCompletionSource<BrowserAddressResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.TrySetResult(Read(window, token, attempt)); }
            catch (Exception ex)
            {
                attempt.Report(ex switch
                {
                    OperationCanceledException => BrowserReadCode.Cancelled,
                    ElementNotAvailableException => BrowserReadCode.ElementUnavailable,
                    COMException => BrowserReadCode.UiaException,
                    InvalidOperationException => BrowserReadCode.InvalidOperation,
                    _ => BrowserReadCode.ReadException
                });
                completion.TrySetResult(new());
            }
            finally { Volatile.Write(ref reading, 0); }
        }) { IsBackground = true, Name = "ClipDock browser address" };
        try
        {
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }
        catch (Exception)
        { Volatile.Write(ref reading, 0); attempt.Report(BrowserReadCode.WorkerStart); return new(); }
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
        catch (TimeoutException) { attempt.Report(BrowserReadCode.WaitTimeout); return new(); }
        catch (OperationCanceledException) { attempt.Report(BrowserReadCode.Cancelled); return new(); }
        finally { budget.Cancel(); }
    }

    private static CacheRequest MetadataCache(TreeScope scope)
    {
        var cache = new CacheRequest { TreeScope = scope, TreeFilter = Automation.ControlViewCondition, AutomationElementMode = AutomationElementMode.Full };
        foreach (var property in new[] { AutomationElement.ControlTypeProperty, AutomationElement.ClassNameProperty,
            AutomationElement.ProcessIdProperty, AutomationElement.IsPasswordProperty, AutomationElement.IsOffscreenProperty,
            AutomationElement.IsEnabledProperty, AutomationElement.BoundingRectangleProperty }) cache.Add(property);
        return cache;
    }

    private static BrowserAddressResult Read(nint window, CancellationToken cancellationToken, BrowserCaptureDiagnostics attempt)
    {
        BrowserAddressResult Fail(BrowserReadCode code, bool editing = false)
        { attempt.Report(code); return new(Message: editing ? EditingMessage : ManualMessage); }
        if (BrowserWindow.GetForegroundWindow() != window) return Fail(BrowserReadCode.InitialForeground);
        if (!BrowserWindow.TryGet(window, out var browser)) return Fail(BrowserReadCode.BrowserWindow);
        var root = AutomationElement.FromHandle(window).GetUpdatedCache(MetadataCache(TreeScope.Element));
        if (root.Cached.ProcessId != browser.ProcessId) return Fail(BrowserReadCode.RootProcess);
        var childrenCache = MetadataCache(TreeScope.Children);
        var pending = new Stack<(AutomationElement Element, int Depth, bool Toolbar)>();
        pending.Push((root, 0, false));
        var candidates = new List<AutomationElement>();
        var depthStopped = false;
        var rejectedBounds = false;
        var sawToolbar = false;
        var sawToolbarEdit = false;
        while (pending.TryPop(out var entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt.Visited++;
            attempt.Depth = Math.Max(attempt.Depth, entry.Depth);
            if (attempt.Visited > 256) return Fail(BrowserReadCode.NodeLimit);
            if (attempt.ElapsedMilliseconds > 2600) return Fail(BrowserReadCode.WalkTimeout);
            var current = entry.Element.Cached;
            if (current.ControlType == ControlType.Document || current.ControlType == ControlType.Tab || current.ClassName == "Chrome_RenderWidgetHostHWND") continue;
            var toolbar = entry.Toolbar || current.ControlType == ControlType.ToolBar;
            sawToolbar |= toolbar;
            if (toolbar && current.ControlType == ControlType.Edit && current.ProcessId == browser.ProcessId &&
                !current.IsPassword && !current.IsOffscreen && current.IsEnabled)
            {
                sawToolbarEdit = true;
                if (AddressNames.Contains(entry.Element.Current.Name))
                {
                    var bounds = current.BoundingRectangle;
                    if (bounds.Width >= 100 * browser.Scale && bounds.Top >= browser.Top &&
                        bounds.Bottom <= browser.Top + 240 * browser.Scale && bounds.Left >= browser.Left && bounds.Right <= browser.Right)
                    { candidates.Add(entry.Element); attempt.Candidates = candidates.Count; }
                    else rejectedBounds = true;
                }
            }
            if (current.ControlType == ControlType.Edit) continue;
            if (entry.Depth >= 10) { depthStopped = true; continue; }
            // Cache one level at a time; never request the children of a document or render widget.
            var children = entry.Element.GetUpdatedCache(childrenCache).CachedChildren;
            if (children.Count > 64) return Fail(BrowserReadCode.ChildLimit);
            if (pending.Count + children.Count > 256) return Fail(BrowserReadCode.NodeLimit);
            for (var index = children.Count - 1; index >= 0; index--)
                pending.Push((children[index], entry.Depth + 1, toolbar));
        }
        if (candidates.Count != 1)
            return Fail(candidates.Count > 1 ? BrowserReadCode.CandidateCount : rejectedBounds ? BrowserReadCode.Bounds :
                depthStopped ? BrowserReadCode.DepthLimit : !sawToolbar ? BrowserReadCode.ToolbarMissing :
                !sawToolbarEdit ? BrowserReadCode.AddressEditMissing : BrowserReadCode.AddressName);
        cancellationToken.ThrowIfCancellationRequested();
        if (attempt.ElapsedMilliseconds > 2600) return Fail(BrowserReadCode.WalkTimeout);
        var address = candidates[0];
        if (address.Current.HasKeyboardFocus) return Fail(BrowserReadCode.StartFocus, true);
        if (BrowserWindow.GetForegroundWindow() != window) return Fail(BrowserReadCode.BeforeValueForeground);
        cancellationToken.ThrowIfCancellationRequested();
        if (!address.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) return Fail(BrowserReadCode.ValuePatternMissing);
        cancellationToken.ThrowIfCancellationRequested();
        var value = ((ValuePattern)pattern).Current.Value;
        cancellationToken.ThrowIfCancellationRequested();
        if (BrowserWindow.GetForegroundWindow() != window) return Fail(BrowserReadCode.EndForeground);
        if (address.Current.HasKeyboardFocus) return Fail(BrowserReadCode.EndFocus, true);
        if (!BrowserUrlCandidate.TryParse(value, out var url, out var addedScheme)) return Fail(BrowserReadCode.UrlCandidate);
        attempt.Report(BrowserReadCode.Success);
        return new(url, addedScheme);
    }
}