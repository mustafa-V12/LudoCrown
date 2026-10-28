using LudoCrown.App.Services;
using LudoCrown.Mobile.Views;

namespace LudoCrown.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(Routes.Matchmaking, typeof(MatchmakingPage));
        Routing.RegisterRoute(Routes.PrivateRoom, typeof(PrivateRoomPage));
        Routing.RegisterRoute(Routes.OfflineSetup, typeof(OfflineSetupPage));
        Routing.RegisterRoute(Routes.Game, typeof(GamePage));
        Routing.RegisterRoute(Routes.Results, typeof(ResultsPage));
        Routing.RegisterRoute(Routes.Profile, typeof(ProfilePage));
        Routing.RegisterRoute(Routes.Friends, typeof(FriendsPage));
        Routing.RegisterRoute(Routes.Settings, typeof(SettingsPage));
        Routing.RegisterRoute(Routes.EmailSignIn, typeof(EmailSignInPage));
        Routing.RegisterRoute(Routes.Rules, typeof(RulesPage));
        Routing.RegisterRoute(Routes.BlockedPlayers, typeof(BlockedPlayersPage));
        Routing.RegisterRoute(Routes.Store, typeof(StorePage));
        Routing.RegisterRoute(Routes.League, typeof(LeaguePage));
        Routing.RegisterRoute(Routes.PlayerProfile, typeof(PlayerProfilePage));
    }
}
