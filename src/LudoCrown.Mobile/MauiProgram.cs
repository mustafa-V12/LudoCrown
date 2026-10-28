using LudoCrown.App.Api;
using LudoCrown.App.Auth;
using LudoCrown.App.Services;
using LudoCrown.App.ViewModels;
using LudoCrown.Mobile.Services;
using LudoCrown.Mobile.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

namespace LudoCrown.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.ConfigureFonts(fonts =>
        {
            // Baloo 2 (SIL Open Font License, Resources/Fonts/OFL-Baloo2.txt): the rounded game font used across the app.
            fonts.AddFont("Baloo2-SemiBold.ttf", "BalooSemi");
            fonts.AddFont("Baloo2-Bold.ttf", "BalooBold");
            fonts.AddFont("Baloo2-ExtraBold.ttf", "BalooHeavy");
        });

        builder.ConfigureLifecycleEvents(events =>
        {
#if IOS
            // ludocrown://join/CODE invites opened from Messages, Mail, etc.
            events.AddiOS(ios => ios.OpenUrl((app, url, options) =>
            {
                DeepLinks.Handle(url?.AbsoluteString);
                return true;
            }));
#endif
        });

#if DEBUG
        builder.Logging.AddDebug();
#endif
        var services = builder.Services;

        // ----- configuration -----
        services.AddSingleton(_ =>
        {
            var settings = new MauiSettingsStore();
#if DEBUG
            // Development: the local server started from Visual Studio. The Android emulator reaches the host as 10.0.2.2.
            var fallback = DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5080" : "http://localhost:5080";
            return new AppConfig { ServerUrl = settings.GetString(SettingKeys.ServerUrlOverride) ?? fallback, IsDevelopment = true };
#else
            return new AppConfig { ServerUrl = AppConfig.ProductionServerUrl, IsDevelopment = false };
#endif
        });

        // ----- platform services -----
        services.AddSingleton<ISecureStore, MauiSecureStore>();
        services.AddSingleton<ISettingsStore, MauiSettingsStore>();
        services.AddSingleton<INavigationService, ShellNavigationService>();
        services.AddSingleton<IDialogService, MauiDialogService>();
        services.AddSingleton<IShareService, MauiShareService>();
        services.AddSingleton<IConnectivityService, MauiConnectivityService>();
        services.AddSingleton<IMainThread, MauiMainThread>();
        services.AddSingleton<IDelay, RealDelay>();
        services.AddSingleton<IAuthBrowser, MauiAuthBrowser>();
        services.AddSingleton<IAppleSignIn, MauiAppleSignIn>();
#if IOS
        services.AddSingleton<IAudioPlayer, IosAudioPlayer>();
#else
        services.AddSingleton<IAudioPlayer, SilentAudioPlayer>();
#endif
        services.AddSingleton<IFeedbackService, FeedbackService>();
#if IOS
        services.AddSingleton<IInAppPurchaseService, LudoCrown.Mobile.Platforms.iOS.StoreKitPurchaseService>();
#else
        services.AddSingleton<IInAppPurchaseService, UnsupportedInAppPurchases>(); // Windows/Android: dev sandbox or gold only
#endif

        // ----- app services -----
        services.AddSingleton(sp => new HttpClient
        {
            BaseAddress = new Uri(sp.GetRequiredService<AppConfig>().ServerUrl),
            Timeout = TimeSpan.FromSeconds(15),
        });
        services.AddSingleton<ApiClient>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<IMatchConnection, SignalRMatchConnection>();
        services.AddSingleton<AppState>();

        // ----- screens -----
        services.AddSingleton<AppShell>();
        AddScreen<SplashPage, SplashViewModel>(services);
        AddScreen<SignInPage, SignInViewModel>(services);
        AddScreen<EmailSignInPage, EmailSignInViewModel>(services);
        AddScreen<HomePage, HomeViewModel>(services);
        AddScreen<MatchmakingPage, MatchmakingViewModel>(services);
        AddScreen<PrivateRoomPage, PrivateRoomViewModel>(services);
        AddScreen<OfflineSetupPage, OfflineSetupViewModel>(services);
        AddScreen<GamePage, GameViewModel>(services);
        AddScreen<ResultsPage, ResultsViewModel>(services);
        AddScreen<ProfilePage, ProfileViewModel>(services);
        AddScreen<FriendsPage, FriendsViewModel>(services);
        AddScreen<SettingsPage, SettingsViewModel>(services);
        AddScreen<BlockedPlayersPage, BlockedPlayersViewModel>(services);
        AddScreen<StorePage, StoreViewModel>(services);
        AddScreen<LeaguePage, LeagueViewModel>(services);
        AddScreen<PlayerProfilePage, PlayerProfileViewModel>(services);
        services.AddTransient<RulesPage>();

        return builder.Build();
    }

    private static void AddScreen<TPage, TViewModel>(IServiceCollection services)
        where TPage : Page where TViewModel : class
    {
        services.AddTransient<TViewModel>();
        services.AddTransient<TPage>();
    }
}
