using System.Runtime.CompilerServices;

// Shared by every check file. A failure names the condition and its source line, so a red run
// points at the broken expectation without a debugger.
internal static class CheckAssert
{
    public static void Assert(bool condition, string? message = null,
        [CallerArgumentExpression(nameof(condition))] string expression = "",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!condition) throw new Exception($"{message ?? "Assertion failed"}: {expression} ({Path.GetFileName(file)}:{line})");
    }

    public static void Throws<T>(Action action, [CallerArgumentExpression(nameof(action))] string expression = "",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        catch (Exception ex) { throw new Exception($"Expected {typeof(T).Name}, got {ex.GetType().Name} ({ex.Message}): {expression} ({Path.GetFileName(file)}:{line})"); }
        throw new Exception($"Expected {typeof(T).Name}: {expression} ({Path.GetFileName(file)}:{line})");
    }

    // An absolute path on this OS (C:\ on Windows, / elsewhere), for checks that never touch the disk.
    public static string Rooted(params string[] parts) => Path.Combine([Path.GetPathRoot(Path.GetTempPath())!, .. parts]);
}

// Thrown by a check that cannot run on this OS. The run still passes; Windows CI runs every check.
internal sealed class SkipCheckException(string reason) : Exception(reason);
