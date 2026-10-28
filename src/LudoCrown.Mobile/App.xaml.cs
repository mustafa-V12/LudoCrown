using LudoCrown.App.Mvvm;
using LudoCrown.Mobile.Services;

namespace LudoCrown.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        UserAppTheme = AppTheme.Dark; // the game is designed for a dark "night" table
        DevLog.Install();
        AsyncCommand.UnhandledError = ex => DevLog.Write($"Unhandled command error: {ex}");
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<AppShell>()) { Title = "Ludo Crown" };
        // Reconnect to the game server when the app comes back to the foreground.
        window.Resumed += async (_, _) =>
        {
            if (_services.GetService<global::LudoCrown.App.Services.IMatchConnection>() is SignalRMatchConnection conn)
                await conn.TryRestartAsync();
        };
        return window;
    }
}
