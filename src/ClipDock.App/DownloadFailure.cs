namespace ClipDock.App;

public static class DownloadFailure
{
    private const string TwitterHint = "Xの動画情報を取得できませんでした。ログインや投稿の公開条件が影響する場合があります。「ログイン」からClipDock内でXにログインし、「この保存にログイン情報を使う」をONにして再試行してください。接続済みならログインし直すか、ClipDockを開き直して保存エンジンを更新してください。";

    public static string Describe(string? url, string? detail)
    {
        detail ??= "";
        if (!detail.Contains("No video could be found in this tweet", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https"))
            return detail;

        var host = page.Host;
        if (host.Equals("x.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".x.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".twitter.com", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var language in ClipDock.Core.Localization.Languages)
            {
                var hint = ClipDock.Core.Localization.Text(TwitterHint, language) + "\n";
                while (detail.StartsWith(hint, StringComparison.Ordinal)) detail = detail[hint.Length..];
            }
            while (detail.StartsWith("Xの動画情報を取得できませんでした。", StringComparison.Ordinal) && detail.IndexOf('\n') is var newline && newline >= 0)
                detail = detail[(newline + 1)..];
            return ClipDock.Core.Localization.Text(TwitterHint) + "\n" + detail;
        }

        return detail;
    }
}
