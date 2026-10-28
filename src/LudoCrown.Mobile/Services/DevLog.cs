namespace LudoCrown.Mobile.Services;

/// <summary>
/// Development diagnostics: when the environment variable LUDOCROWN_APP_LOG names a file (tools/windows/run-dev.cmd
/// sets it), navigation and unhandled errors are appended there. Does nothing otherwise, so release builds on phones
/// never write it.
/// </summary>
public static class DevLog
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("LUDOCROWN_APP_LOG");
    private static readonly object Gate = new();

    public static bool Enabled => !string.IsNullOrWhiteSpace(Path);

    public static void Write(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[LudoCrown] {message}");
        if (!Enabled) return;
        try { lock (Gate) File.AppendAllText(Path!, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch (Exception) { /* diagnostics must never break the app */ }
    }

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"UNHANDLED: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Write($"UNOBSERVED TASK: {e.Exception}");
        Write("App started");
    }
}
