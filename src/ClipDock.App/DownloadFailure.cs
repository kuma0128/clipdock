namespace ClipDock.App;

public static class DownloadFailure
{
    private const string TwitterHint = "Xの動画情報を取得できませんでした。ログインや投稿の公開条件が影響する場合があります。Chromeで動画を再生し、拡張の「この保存にページのログイン情報を使う」をONにして再送信してください。";

    public static string Describe(string? url, string? detail)
    {
        detail ??= "";
        if (!detail.Contains("No video could be found in this tweet", StringComparison.OrdinalIgnoreCase) ||
            detail.StartsWith(TwitterHint, StringComparison.Ordinal) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https"))
            return detail;

        var host = page.Host;
        if (host.Equals("x.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".x.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".twitter.com", StringComparison.OrdinalIgnoreCase))
            return TwitterHint + "\n" + detail;

        return detail;
    }
}
