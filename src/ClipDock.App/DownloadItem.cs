using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClipDock.Core;

namespace ClipDock.App;

public sealed class DownloadItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DownloadRequest Request { get; set; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string OutputDirectory { get; init; } = "";
    private string title = "";
    private string state = "queued";
    private string detail = "順番を待っています";
    private string filePath = "";
    private double percent;
    public string Title { get => title; set => Set(ref title, value); }
    public string State
    {
        get => state;
        set
        {
            // Progress lines re-assign the same state many times a second; the window rebuilds its list on every State change.
            if (!Set(ref state, value)) return;
            Notify(nameof(StateLabel)); Notify(nameof(CanCancel)); Notify(nameof(CanRetry)); Notify(nameof(IsCompleted)); Notify(nameof(IsBusy));
        }
    }
    public string Detail { get => detail; set => Set(ref detail, value); }
    public string FilePath { get => filePath; set => Set(ref filePath, value); }
    public double Percent { get => percent; set => Set(ref percent, value); }
    public bool CanCancel => State is "queued" or "downloading" or "converting";
    public bool CanRetry => State is "failed" or "cancelled" or "interrupted";
    public bool IsCompleted => State == "completed";
    public bool IsBusy => State is "downloading" or "converting";
    public string FormatLabel => Request.Format == "mp3" ? "MP3" : "MP4";
    public string Meta => $"{(Uri.TryCreate(Request.PageUrl, UriKind.Absolute, out var page) ? page.Host : "")}   ·   {(Request.Format == "mp3" ? "音声のみ" : Request.Quality == "best" ? "最高画質" : Request.Quality + "p 以下")}   ·   {CreatedAt:MM/dd HH:mm}";
    public string StateLabel => State switch
    {
        "queued" => "待機中", "downloading" => "ダウンロード中", "converting" => $"{FormatLabel}を仕上げています",
        "completed" => "保存完了", "failed" => "保存できませんでした", "cancelled" => "キャンセル済み", _ => "中断されました"
    };
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(property);
        return true;
    }
    public SavedDownload Save() => new(Id, Request, CreatedAt, OutputDirectory, Title, State, FilePath, Detail);
}

public sealed record SavedDownload(string Id, DownloadRequest Request, DateTimeOffset CreatedAt, string OutputDirectory, string Title, string State, string FilePath, string Detail);

public static class DownloadHistory
{
    public static List<SavedDownload> Snapshot(IEnumerable<DownloadItem> source, IEnumerable<DownloadItem>? preserve = null)
    {
        var jobs = source.ToList();
        var required = preserve?.ToHashSet() ?? [];
        var selected = jobs.Where(j => j.CanCancel || required.Contains(j))
            .Concat(jobs.Where(j => !j.CanCancel && !required.Contains(j))).Take(300).ToHashSet();
        return jobs.Where(selected.Contains).Select(j => j.Save()).ToList();
    }
}
