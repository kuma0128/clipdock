namespace ClipDock.Core;

// Owns a slot until the operation and its process have finished.
// Work runs on the thread pool; callers marshal UI updates onto their dispatcher.
public sealed class DownloadScheduler
{
    public const int DefaultConcurrency = 2;
    public const int MaximumConcurrency = 4;
    private readonly object gate = new();
    private readonly LinkedList<DownloadOperation> pending = [];
    private readonly HashSet<DownloadOperation> running = [];
    private int concurrency;
    private bool stopped;

    public DownloadScheduler(int maxConcurrency = DefaultConcurrency) => concurrency = Validate(maxConcurrency);

    public int MaxConcurrency
    {
        get { lock (gate) return concurrency; }
        set { lock (gate) { concurrency = Validate(value); StartPending(); } }
    }

    public DownloadOperation Enqueue(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (gate)
        {
            if (stopped) throw new InvalidOperationException("The download queue has stopped.");
            var operation = new DownloadOperation(this, work);
            operation.Node = pending.AddLast(operation);
            StartPending();
            return operation;
        }
    }

    // Reducing the limit leaves current operations alone. New work waits until enough
    // slots have been freed, and increasing the limit fills the new slots immediately.
    private void StartPending()
    {
        while (!stopped && running.Count < concurrency && pending.First is { } first)
        {
            var operation = first.Value;
            pending.RemoveFirst();
            operation.Node = null;
            running.Add(operation);
            _ = Task.Run(() => ExecuteAsync(operation));
        }
    }

    private async Task ExecuteAsync(DownloadOperation operation)
    {
        Exception? error = null;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            await operation.Work(operation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { error = ex; }
        lock (gate)
        {
            running.Remove(operation);
            operation.Finish(error);
            StartPending();
        }
    }

    internal void Cancel(DownloadOperation operation)
    {
        lock (gate)
        {
            if (operation.Completion.IsCompleted) return;
            if (operation.Node is { } node)
            {
                pending.Remove(node);
                operation.Node = null;
                operation.Finish(new OperationCanceledException());
                return;
            }
        }
        // Cancellation invokes process-kill callbacks; never hold the scheduler lock.
        operation.RequestCancellation();
    }

    public async Task StopAsync()
    {
        DownloadOperation[] active;
        lock (gate)
        {
            stopped = true;
            foreach (var operation in pending)
            {
                operation.Node = null;
                operation.Finish(new OperationCanceledException());
            }
            pending.Clear();
            active = running.ToArray();
        }
        foreach (var operation in active) operation.RequestCancellation();
        // Wait for every operation even if some failed or were cancelled.
        try { await Task.WhenAll(active.Select(o => o.Completion)).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static int Validate(int value) => value is >= 1 and <= MaximumConcurrency ? value
        : throw new ArgumentOutOfRangeException(nameof(value), $"Choose 1–{MaximumConcurrency} simultaneous downloads.");
}

public sealed class DownloadOperation
{
    private readonly DownloadScheduler owner;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<CancellationToken, Task> Work { get; }
    internal CancellationToken Token { get; }
    internal LinkedListNode<DownloadOperation>? Node { get; set; }
    public Task Completion => completion.Task;

    internal DownloadOperation(DownloadScheduler owner, Func<CancellationToken, Task> work)
    {
        this.owner = owner;
        Work = work;
        Token = cancellation.Token;
    }

    public void Cancel() => owner.Cancel(this);
    internal void RequestCancellation()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* Completion won the race with cancel. */ }
    }
    internal void Finish(Exception? error)
    {
        if (error is OperationCanceledException) completion.TrySetCanceled();
        else if (error is not null) completion.TrySetException(error);
        else completion.TrySetResult();
        cancellation.Dispose();
    }
}

public sealed record AppSettings(string OutputDirectory, string Quality, int MaxConcurrentDownloads = DownloadScheduler.DefaultConcurrency,
    string Format = "mp4", bool BrowserOverlayEnabled = false, bool UseLogin = true, string Language = "ja");
