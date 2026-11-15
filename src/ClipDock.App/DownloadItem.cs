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
    public CancellationTokenSource? Cancellation { get; set; }
    private string title = "";
    private string state = "queued";
    private string detail = "順番を待っています";
    private string filePath = "";
    private double percent;
    public string Title { get => title; set { title = value; Notify(); } }
    public string State { get => state; set { state = value; Notify(); Notify(nameof(StateLabel)); Notify(nameof(CanCancel)); Notify(nameof(CanRetry)); Notify(nameof(IsCompleted)); Notify(nameof(IsBusy)); } }
    public string Detail { get => detail; set { detail = value; Notify(); } }
    public string FilePath { get => filePath; set { filePath = value; Notify(); } }
    public double Percent { get => percent; set { percent = value; Notify(); } }
    public bool CanCancel => State is "queued" or "downloading" or "converting";
    public bool CanRetry => State is "failed" or "cancelled" or "interrupted";
    public bool IsCompleted => State == "completed";
    public bool IsBusy => State is "downloading" or "converting";
    public string Meta => $"{new Uri(Request.PageUrl).Host}   ·   {(Request.Quality == "best" ? "最高画質" : Request.Quality + "p 以下")}   ·   {CreatedAt:MM/dd HH:mm}";
    public string StateLabel => State switch
    {
        "queued" => "待機中", "downloading" => "ダウンロード中", "converting" => "MP4を仕上げています",
        "completed" => "保存完了", "failed" => "保存できませんでした", "cancelled" => "キャンセル済み", _ => "中断されました"
    };
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public SavedDownload Save() => new(Id, Request with { Cookies = [] }, CreatedAt, OutputDirectory, Title, State, FilePath, Detail);
}

public sealed record SavedDownload(string Id, DownloadRequest Request, DateTimeOffset CreatedAt, string OutputDirectory, string Title, string State, string FilePath, string Detail);
public sealed record Settings(string OutputDirectory, string Quality);
