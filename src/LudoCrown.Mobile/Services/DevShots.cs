namespace LudoCrown.Mobile.Services;

/// <summary>
/// Development only: when the environment variable LUDOCROWN_SHOTS names a folder (tools/windows/screenshots.cmd sets it),
/// the app saves a PNG of its window shortly after each screen appears and at key moments (mode panel, game, results).
/// Used to produce README screenshots of the real app. Does nothing otherwise, so phone builds never write it.
/// </summary>
public static class DevShots
{
    private static readonly string? Folder = Environment.GetEnvironmentVariable("LUDOCROWN_SHOTS");
    private static int _count;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool Enabled => !string.IsNullOrWhiteSpace(Folder);

    /// <summary>Captures the app window after <paramref name="delayMs"/> as NN-name.png.</summary>
    public static async void Capture(string name, int delayMs = 1200)
    {
        if (!Enabled || _count >= 40) return;
        try
        {
            await Task.Delay(delayMs);
            await Gate.WaitAsync();
            try
            {
                if (!Screenshot.Default.IsCaptureSupported) return;
                var shot = await MainThread.InvokeOnMainThreadAsync(() => Screenshot.Default.CaptureAsync());
                if (shot is null) return;
                Directory.CreateDirectory(Folder!);
                var file = System.IO.Path.Combine(Folder!, $"{Interlocked.Increment(ref _count):D2}-{name}.png");
                await using var input = await shot.OpenReadAsync(ScreenshotFormat.Png);
                await using var output = File.Create(file);
                await input.CopyToAsync(output);
                DevLog.Write($"Screenshot {file}");
            }
            finally { Gate.Release(); }
        }
        catch (Exception ex) { DevLog.Write($"Screenshot failed: {ex.Message}"); }
    }
}
