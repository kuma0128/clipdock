using ClipDock.App;
using static CheckAssert;

internal static class BrowserOverlayChecks
{
    internal static void Run(Action<string, Action> check)
    {
        check("browser URL candidates preserve explicit web schemes and require review for an added scheme", () =>
        {
            foreach (var source in new[] { "https://example.com/watch?v=123#part", "http://example.com/video.mp4", "https://例え.jp/動画" })
                Assert(BrowserUrlCandidate.TryParse(source, out var url, out var added) && url == source && !added);
            foreach (var source in new[] { "example.com/watch?v=123", "www.example.com:8443/video", "[::1]:8443/video" })
                Assert(BrowserUrlCandidate.TryParse(source, out var url, out var added) && url == "https://" + source && added);
        });
        check("browser URL candidates reject search text, internal schemes, credentials and control characters", () =>
        {
            foreach (var source in new string?[] { null, "", "search words", "example.com some title", "example.com\n/private", "example.com\t/private", "https://example.com/\0bad", "https://user:secret@example.com/v", "user@example.com/v", "example.com\\secret", "chrome://settings", "edge://downloads", "file:///C:/private", "javascript:alert(document.cookie)", "data:text/html,hello", "blob:https://example.com/id", "ftp://example.com/video", "https://", "just-search-words", "https://" + new string('a', 8192) })
                Assert(!BrowserUrlCandidate.TryParse(source, out _, out _));
        });
    }
}
