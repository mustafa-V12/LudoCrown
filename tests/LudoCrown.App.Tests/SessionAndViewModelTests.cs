using System.Net;
using LudoCrown.App.Api;
using LudoCrown.App.Auth;
using LudoCrown.App.Game;
using LudoCrown.App.Services;
using LudoCrown.App.ViewModels;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.Tests;

internal sealed class AppHarness
{
    public FakeHttp Http { get; } = new();
    public MemorySecureStore Secure { get; } = new();
    public MemorySettings Settings { get; } = new();
    public FakeBrowser Browser { get; } = new();
    public FakeApple Apple { get; } = new();
    public RecordingNavigation Nav { get; } = new();
    public ScriptedDialogs Dialogs { get; } = new();
    public FakeConnection Connection { get; } = new();
    public AppState State { get; } = new();
    public ApiClient Api { get; }
    public SessionService Session { get; }
    public AppConfig Config { get; } = new() { ServerUrl = "https://test.local", IsDevelopment = true };
    public int Refreshes;

    public AppHarness()
    {
        Api = new ApiClient(new HttpClient(Http) { BaseAddress = new Uri("https://test.local") });
        Session = new SessionService(Api, Secure, Browser, Apple, Config);
    }

    public static TokenResponse Tokens(string access = "access-1", string refresh = "refresh-1", bool guest = true, int minutes = 15) =>
        new(access, DateTimeOffset.UtcNow.AddMinutes(minutes), refresh, Guid.Parse("11111111-1111-1111-1111-111111111111"), guest);
}

public class SessionServiceTests
{
    [Fact]
    public async Task Guest_sign_in_stores_the_session_in_secure_storage()
    {
        var h = new AppHarness();
        h.Http.Handler = (_, path, _, _) => path == "/auth/guest" ? (HttpStatusCode.OK, AppHarness.Tokens()) : (HttpStatusCode.NotFound, null);
        var r = await h.Session.SignInAsGuestAsync("Mustafa");
        Assert.True(r.Ok);
        Assert.True(h.Session.IsGuest);
        Assert.Single(h.Secure.Values);
        Assert.Contains("refresh-1", h.Secure.Values.Values.Single());
        Assert.Empty(h.Settings.Values); // tokens never go to plain preferences
    }

    [Fact]
    public async Task Expired_access_tokens_are_refreshed_once_and_requests_retried()
    {
        var h = new AppHarness();
        var refreshes = 0;
        h.Http.Handler = (method, path, body, auth) => path switch
        {
            "/auth/guest" => (HttpStatusCode.OK, AppHarness.Tokens("old", "r1")),
            "/auth/refresh" => (HttpStatusCode.OK, AppHarness.Tokens($"new{++refreshes}", $"r{refreshes + 1}")),
            "/me" => auth == "old" ? (HttpStatusCode.Unauthorized, null) : (HttpStatusCode.OK, new { userId = Guid.Empty, isGuest = true, displayName = "X", avatarId = "crown", friendCode = "ABCDEFGH", level = 1, xp = 0, xpForNextLevel = 250, coins = 0, stats = new { }, linkedProviders = Array.Empty<string>() }),
            _ => (HttpStatusCode.NotFound, null),
        };
        await h.Session.SignInAsGuestAsync(null);
        var me = await h.Api.GetAsync<MeResponse>("/me");
        Assert.True(me.Ok);
        Assert.Equal(1, refreshes);
        Assert.Equal("new1", h.Http.Requests.Last().Auth);
    }

    [Fact]
    public async Task Concurrent_refreshes_use_the_rotating_token_only_once()
    {
        var h = new AppHarness();
        var refreshes = 0;
        h.Http.Handler = (_, path, _, _) => path switch
        {
            "/auth/guest" => (HttpStatusCode.OK, AppHarness.Tokens("a", "r1", minutes: 0)),
            "/auth/refresh" => (HttpStatusCode.OK, AppHarness.Tokens($"b{++refreshes}", $"r{refreshes + 1}")),
            _ => (HttpStatusCode.NotFound, null),
        };
        await h.Session.SignInAsGuestAsync(null);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => h.Session.GetAccessTokenAsync()));
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task Restore_keeps_the_session_when_offline_and_drops_it_when_revoked()
    {
        var h = new AppHarness();
        h.Http.Handler = (_, path, _, _) => path == "/auth/guest" ? (HttpStatusCode.OK, AppHarness.Tokens()) : (HttpStatusCode.Unauthorized, null);
        await h.Session.SignInAsGuestAsync(null);

        var offline = new SessionService(h.Api, h.Secure, h.Browser, h.Apple, h.Config);
        h.Http.Offline = true;
        Assert.True(await offline.RestoreAsync());

        h.Http.Offline = false;
        var revoked = new SessionService(h.Api, h.Secure, h.Browser, h.Apple, h.Config);
        Assert.False(await revoked.RestoreAsync());
        Assert.Empty(h.Secure.Values);
    }

    [Fact]
    public async Task Browser_sign_in_exchanges_the_one_time_code()
    {
        var h = new AppHarness();
        h.Browser.Result = new Dictionary<string, string> { ["code"] = "one-time" };
        h.Http.Handler = (_, path, body, _) => path == "/auth/exchange" && body!.Contains("one-time") ? (HttpStatusCode.OK, AppHarness.Tokens(guest: false)) : (HttpStatusCode.NotFound, null);
        var r = await h.Session.SignInWithBrowserAsync("google");
        Assert.True(r.Ok);
        Assert.Equal("https://test.local/auth/oauth/google/start", h.Browser.LastStart!.ToString());
    }

    [Fact]
    public async Task Browser_errors_and_cancellation_are_reported_clearly()
    {
        var h = new AppHarness();
        h.Browser.Result = null;
        Assert.Equal(AuthOutcome.Cancelled, (await h.Session.SignInWithBrowserAsync("google")).Outcome);
        h.Browser.Result = new Dictionary<string, string> { ["error"] = "already_linked" };
        Assert.Equal(AuthOutcome.AlreadyLinked, (await h.Session.SignInWithBrowserAsync("facebook")).Outcome);
    }

    [Fact]
    public async Task Apple_sign_in_sends_the_identity_token()
    {
        var h = new AppHarness();
        h.Apple.Result = new AppleSignInResult("apple-id-token", "code", "Ana");
        h.Http.Handler = (_, path, body, _) => path == "/auth/apple" && body!.Contains("apple-id-token") ? (HttpStatusCode.OK, AppHarness.Tokens(guest: false)) : (HttpStatusCode.NotFound, null);
        Assert.True((await h.Session.SignInWithAppleAsync()).Ok);
        Assert.False(h.Session.IsGuest);
    }

    [Fact]
    public async Task Unconfigured_providers_are_reported_as_not_configured()
    {
        var h = new AppHarness();
        h.Apple.Result = new AppleSignInResult("t", null, null);
        h.Http.Handler = (_, _, _, _) => (HttpStatusCode.NotFound, new ApiErrorBody("provider_disabled", "Sign in with Apple is not configured on this server."));
        var r = await h.Session.SignInWithAppleAsync();
        Assert.Equal(AuthOutcome.NotConfigured, r.Outcome);
        Assert.Contains("not configured", r.Message);
    }

    [Fact]
    public async Task Network_failures_become_friendly_errors()
    {
        var h = new AppHarness { Http = { Offline = true } };
        var r = await h.Session.SignInAsGuestAsync(null);
        Assert.Equal(AuthOutcome.Network, r.Outcome);
        Assert.Contains("Can't reach", r.Message);
    }

    [Fact]
    public async Task Sign_out_revokes_on_the_server_and_clears_the_device()
    {
        var h = new AppHarness();
        h.Http.Handler = (_, path, _, _) => path == "/auth/guest" ? (HttpStatusCode.OK, AppHarness.Tokens()) : (HttpStatusCode.NoContent, null);
        await h.Session.SignInAsGuestAsync(null);
        await h.Session.SignOutAsync();
        Assert.Contains(h.Http.Requests, r => r.Path == "/auth/logout" && r.Body!.Contains("refresh-1"));
        Assert.False(h.Session.IsSignedIn);
        Assert.Empty(h.Secure.Values);
    }
}

public class ViewModelTests
{
    [Fact]
    public async Task Splash_goes_to_sign_in_without_a_session()
    {
        var h = new AppHarness();
        var vm = new SplashViewModel(h.Session, h.Nav, new InstantDelay());
        await vm.OnAppearingAsync();
        Assert.Equal(Routes.SignIn, h.Nav.Last);
    }

    [Fact]
    public async Task Sign_in_screen_shows_only_configured_providers_and_offline_fallback()
    {
        var h = new AppHarness();
        h.Http.Handler = (_, path, _, _) => path == "/auth/providers" ? (HttpStatusCode.OK, new ProvidersResponse(true, false, true, false, true)) : (HttpStatusCode.NotFound, null);
        var vm = new SignInViewModel(h.Session, h.Nav, h.Dialogs, h.State);
        await vm.OnAppearingAsync();
        Assert.True(vm.ShowGuest);
        Assert.True(vm.ShowGoogle);
        Assert.False(vm.ShowFacebook);
        Assert.False(vm.ShowApple);
        Assert.False(vm.HasServerNotice);

        var offline = new AppHarness { Http = { Offline = true } };
        var vm2 = new SignInViewModel(offline.Session, offline.Nav, offline.Dialogs, offline.State);
        await vm2.OnAppearingAsync();
        Assert.True(vm2.HasServerNotice);
        vm2.OfflineCommand.Execute(null);
        Assert.Equal(Routes.Home, offline.Nav.Last);
    }

    [Fact]
    public async Task Home_rejects_invalid_table_codes_and_asks_signed_out_players_to_sign_in()
    {
        var h = new AppHarness();
        var vm = new HomeViewModel(h.Session, h.Api, h.Nav, h.Dialogs, h.State, h.Settings);
        h.Dialogs.ConfirmAnswers.Enqueue(true);
        await vm.JoinTableAsync("ABCDEF");
        Assert.Equal(Routes.SignIn, h.Nav.Last);

        h.Http.Handler = (_, path, _, _) => path == "/auth/guest" ? (HttpStatusCode.OK, AppHarness.Tokens()) : (HttpStatusCode.NotFound, null);
        await h.Session.SignInAsGuestAsync(null);
        await vm.JoinTableAsync("bad");
        Assert.Contains(h.Dialogs.Alerts, a => a.StartsWith("Invalid code"));
        await vm.JoinTableAsync("abc-def");
        Assert.Equal("ABCDEF", h.State.PrivateRoomCode);
        Assert.Equal(Routes.PrivateRoom, h.Nav.Last);
    }

    [Fact]
    public async Task Matchmaking_opens_the_game_when_matched()
    {
        var h = new AppHarness();
        h.State.RequestedPlayerCount = 4;
        h.Connection.ResyncResult = FakeConnection.Snapshot(0);
        var vm = new MatchmakingViewModel(h.Connection, h.State, h.Nav, new InlineMainThread());
        await vm.OnAppearingAsync();
        Assert.Contains("find:4:Classic:Ranked", h.Connection.Calls);
        Assert.True(vm.Searching);
        h.Connection.PushMatchmaking(new MatchmakingStatus(MatchmakingState.Searching, 4, 3, null, null));
        Assert.Equal("3 of 4 players", vm.PlayersText);
        h.Connection.PushMatchmaking(new MatchmakingStatus(MatchmakingState.Matched, 4, 4, "m1", null));
        Assert.Equal("replace:" + Routes.Game, h.Nav.Last);
        Assert.IsType<OnlineGameSession>(h.State.CurrentSession);
    }

    [Fact]
    public async Task Matchmaking_shows_a_retryable_error_without_connection()
    {
        var h = new AppHarness();
        h.Connection.FailConnect = true;
        var vm = new MatchmakingViewModel(h.Connection, h.State, h.Nav, new InlineMainThread());
        await vm.OnAppearingAsync();
        Assert.True(vm.HasError);
        h.Connection.FailConnect = false;
        vm.RetryCommand.Execute(null);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Private_room_join_errors_and_start_flow()
    {
        var h = new AppHarness();
        h.State.PrivateRoomMode = PrivateRoomMode.Join;
        h.State.PrivateRoomCode = "ZZZZZZ";
        var bad = new PrivateRoomViewModel(h.Connection, h.State, h.Nav, new NoShare(), new InlineMainThread());
        await bad.OnAppearingAsync();
        Assert.Equal("No open table with that code.", bad.Error);

        h.State.PrivateRoomMode = PrivateRoomMode.Create;
        h.Connection.ResyncResult = FakeConnection.Snapshot(0);
        var vm = new PrivateRoomViewModel(h.Connection, h.State, h.Nav, new NoShare(), new InlineMainThread());
        await vm.OnAppearingAsync();
        Assert.Equal("ABCDEF", vm.Code);
        Assert.False(vm.CanStart);
        h.Connection.PushRoom(FakeConnection.Room(true, seated: 2));
        Assert.True(vm.CanStart);
        h.Connection.PushRoom(FakeConnection.Room(true, started: true, seated: 2));
        Assert.Equal("replace:" + Routes.Game, h.Nav.Last);
    }

    [Fact]
    public async Task Offline_setup_validates_names_and_starts_a_saved_game()
    {
        var h = new AppHarness();
        var vm = new OfflineSetupViewModel(h.State, h.Nav, h.Dialogs, h.Settings, new InstantDelay());
        vm.PlayerCount = 2;
        Assert.Equal(2, vm.Seats.Count);
        vm.Seats[1].Name = "x";
        vm.StartCommand.Execute(null);
        Assert.True(vm.HasError);
        vm.Seats[1].Name = "Baron";
        vm.Seats[1].DifficultyIndex = 2;
        vm.StartCommand.Execute(null);
        Assert.Equal(Routes.Game, h.Nav.Last);
        var session = Assert.IsType<OfflineGameSession>(h.State.CurrentSession);
        Assert.Equal(Core.AI.BotDifficulty.Hard, h.State.LastOfflineSeats![1].Difficulty);
        Assert.Equal(2, session.State.Seats.Count);
    }

    [Fact]
    public void Game_status_text_reflects_whose_turn_it_is()
    {
        var seats = new[] { new SeatView(0, PlayerColor.Ruby, "You", "crown", false, true, true, 0), new SeatView(1, PlayerColor.Topaz, "Baron", "knight", true, true, false, 0) };
        BoardState B(int seat, GamePhase phase, int? roll = null) => new("x", 1, phase, seat, roll, roll, new[] { new int[4], new int[4] }, seats, Array.Empty<Core.Engine.GameEvent>(), null, Array.Empty<int>(), null, false);
        Assert.Equal("your turn — roll the die", GameViewModel.StatusFor(B(0, GamePhase.AwaitingRoll)));
        Assert.Equal("tap a glowing piece to move 6", GameViewModel.StatusFor(B(0, GamePhase.AwaitingMove, 6)));
        Assert.Equal("Baron is thinking…", GameViewModel.StatusFor(B(1, GamePhase.AwaitingRoll)));
    }

    [Fact]
    public async Task Finishing_an_offline_game_goes_to_results_with_standings()
    {
        var h = new AppHarness();
        var seats = new[] { new OfflineSeat("A", true, Core.AI.BotDifficulty.Easy, "knight"), new OfflineSeat("B", true, Core.AI.BotDifficulty.Hard, "knight") };
        h.State.CurrentSession = OfflineGameSession.NewGame(seats, new Core.Dice.SeededDiceRoller(1), new InstantDelay(), h.Settings);
        h.State.LastOfflineSeats = seats;
        var vm = new GameViewModel(h.State, new NullFeedback(), h.Nav, h.Dialogs, new InlineMainThread(), h.Settings, new InstantDelay());
        await vm.OnAppearingAsync();
        Assert.Equal("replace:" + Routes.Results, h.Nav.Last);

        var results = new ResultsViewModel(h.State, h.Nav, h.Connection, h.Settings, new InstantDelay());
        await results.OnAppearingAsync();
        Assert.Equal(2, results.Rows.Count);
        Assert.EndsWith("wins!", results.Title);
        Assert.Equal("Play again", results.PlayAgainText);
        results.PlayAgainCommand.Execute(null);
        Assert.Equal("replace:" + Routes.Game, h.Nav.Last);
    }

    private sealed class NoShare : IShareService
    {
        public Task ShareTextAsync(string title, string text) => Task.CompletedTask;
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
    }
}
