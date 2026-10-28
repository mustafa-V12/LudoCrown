using System.Windows.Input;
using LudoCrown.App.Api;
using LudoCrown.App.Auth;
using LudoCrown.App.Game;
using LudoCrown.App.Mvvm;
using LudoCrown.App.Services;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.ViewModels;

public sealed class SplashViewModel(SessionService session, INavigationService nav, IDelay delay) : ViewModelBase
{
    private bool _started;

    public override async Task OnAppearingAsync()
    {
        if (_started) return;
        _started = true;
        var minimum = delay.Delay(TimeSpan.FromMilliseconds(900));
        var providers = session.LoadProvidersAsync();
        var restored = await session.RestoreAsync();
        await providers;
        await minimum;
        await nav.GoToAsync(restored ? Routes.Home : Routes.SignIn);
    }
}

public sealed class SignInViewModel : ViewModelBase
{
    private readonly SessionService _session;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;
    private ProvidersResponse? _providers;
    private string? _serverNotice;

    public SignInViewModel(SessionService session, INavigationService nav, IDialogService dialogs, AppState state)
    {
        _session = session;
        _nav = nav;
        _dialogs = dialogs;
        AppleCommand = new AsyncCommand(() => RunAsync(() => _session.SignInWithAppleAsync()));
        GoogleCommand = new AsyncCommand(() => RunAsync(() => _session.SignInWithBrowserAsync("google")));
        FacebookCommand = new AsyncCommand(() => RunAsync(() => _session.SignInWithBrowserAsync("facebook")));
        EmailCommand = new AsyncCommand(() => { state.EmailLinkMode = false; return _nav.GoToAsync(Routes.EmailSignIn); });
        GuestCommand = new AsyncCommand(GuestAsync);
        OfflineCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.Home));
        RetryCommand = new AsyncCommand(LoadAsync);
    }

    public ICommand AppleCommand { get; }
    public ICommand GoogleCommand { get; }
    public ICommand FacebookCommand { get; }
    public ICommand EmailCommand { get; }
    public ICommand GuestCommand { get; }
    public ICommand OfflineCommand { get; }
    public ICommand RetryCommand { get; }

    public bool ShowApple => _session.AppleAvailable;
    public bool ShowGoogle => _providers is not null && _session.GoogleAvailable;
    public bool ShowFacebook => _providers is not null && _session.FacebookAvailable;
    public bool ShowEmail => _providers?.Email ?? false;
    public bool ShowGuest => _providers?.Guest ?? false;
    public bool ServerReachable => _providers is not null;
    public string? ServerNotice { get => _serverNotice; private set { if (Set(ref _serverNotice, value)) OnPropertyChanged(nameof(HasServerNotice)); } }
    public bool HasServerNotice => !string.IsNullOrEmpty(ServerNotice);

    public override Task OnAppearingAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _providers = _session.Providers ?? await _session.LoadProvidersAsync();
        ServerNotice = _providers is null
            ? "Can't reach the Ludo Crown server right now. You can still play offline against the computer."
            : null;
        foreach (var p in new[] { nameof(ShowApple), nameof(ShowGoogle), nameof(ShowFacebook), nameof(ShowEmail), nameof(ShowGuest), nameof(ServerReachable) })
            OnPropertyChanged(p);
    }

    private async Task GuestAsync()
    {
        var name = await _dialogs.PromptAsync("Play as guest", "Choose a display name (you can change it later).", "Continue", null, DisplayNames.MaxLength);
        if (name is null) return;
        if (name.Length > 0 && DisplayNames.Validate(name) is string reason)
        {
            await _dialogs.AlertAsync("Display name", reason);
            return;
        }
        await RunAsync(() => _session.SignInAsGuestAsync(name.Length > 0 ? name : null));
    }

    private Task RunAsync(Func<Task<AuthResult>> signIn) => RunBusyAsync(async () =>
    {
        var result = await signIn();
        if (result.Ok) await _nav.GoToAsync(Routes.Home);
        else if (result.Outcome != AuthOutcome.Cancelled) Error = result.Message ?? "Sign-in failed. Please try again.";
    });
}

public sealed class EmailSignInViewModel : ViewModelBase
{
    private readonly SessionService _session;
    private readonly INavigationService _nav;
    private readonly AppState _state;
    private string _email = "";
    private string _code = "";
    private bool _codeSent;

    public EmailSignInViewModel(SessionService session, INavigationService nav, AppState state)
    {
        _session = session;
        _nav = nav;
        _state = state;
        SendCodeCommand = new AsyncCommand(SendCodeAsync);
        VerifyCommand = new AsyncCommand(VerifyAsync);
        ChangeEmailCommand = new AsyncCommand(() => { CodeSent = false; Code = ""; return Task.CompletedTask; });
    }

    public string Email { get => _email; set => Set(ref _email, value); }
    public string Code { get => _code; set => Set(ref _code, value); }
    public bool CodeSent { get => _codeSent; private set { if (Set(ref _codeSent, value)) OnPropertyChanged(nameof(CodeNotSent)); } }
    public bool CodeNotSent => !CodeSent;
    public string Title => _state.EmailLinkMode ? "Add email sign-in" : "Sign in with email";
    public ICommand SendCodeCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand ChangeEmailCommand { get; }

    private Task SendCodeAsync() => RunBusyAsync(async () =>
    {
        var r = await _session.StartEmailAsync(Email.Trim());
        if (r.Ok) CodeSent = true;
        else Error = r.Message;
    });

    private Task VerifyAsync() => RunBusyAsync(async () =>
    {
        var r = await _session.VerifyEmailAsync(Email.Trim(), Code.Trim(), _state.EmailLinkMode);
        if (r.Ok)
        {
            var link = _state.EmailLinkMode;
            _state.EmailLinkMode = false;
            if (link) await _nav.GoBackAsync();
            else await _nav.GoToAsync(Routes.Home);
        }
        else Error = r.Message;
    });
}

public sealed class HomeViewModel : ViewModelBase
{
    private readonly SessionService _session;
    private readonly ApiClient _api;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly AppState _state;
    private readonly ISettingsStore _settings;
    private readonly AppConfig? _config;
    private MeResponse? _me;
    private bool _hasSavedGame;
    private bool _serverReachable = true;

    public HomeViewModel(SessionService session, ApiClient api, INavigationService nav, IDialogService dialogs, AppState state, ISettingsStore settings, AppConfig? config = null)
    {
        _config = config;
        Modes = new ModePicker(state, settings);
        if (Enum.TryParse<MatchQueue>(settings.GetString(SettingKeys.LastQueue), out var q)) state.SelectedQueue = q;
        SelectQueueCommand = new AsyncCommand(p =>
        {
            _state.SelectedQueue = p?.ToString() == "casual" ? MatchQueue.Casual : MatchQueue.Ranked;
            _settings.SetString(SettingKeys.LastQueue, _state.SelectedQueue.ToString());
            OnPropertyChanged(nameof(IsRanked));
            OnPropertyChanged(nameof(IsCasual));
            OnPropertyChanged(nameof(QueueDescription));
            return Task.CompletedTask;
        });
        StoreCommand = new AsyncCommand(() => RequireAccountAsync(Routes.Store));
        LeagueCommand = new AsyncCommand(() => RequireAccountAsync(Routes.League));
        _session = session;
        _api = api;
        _nav = nav;
        _dialogs = dialogs;
        _state = state;
        _settings = settings;
        PlayOnlineCommand = new AsyncCommand(p => PlayOnlineAsync(Convert.ToInt32(p ?? 2)));
        CreateTableCommand = new AsyncCommand(CreateTableAsync);
        JoinTableCommand = new AsyncCommand(() => JoinTableAsync(null));
        PlayOfflineCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.OfflineSetup));
        ProfileCommand = new AsyncCommand(() => RequireAccountAsync(Routes.Profile));
        FriendsCommand = new AsyncCommand(() => RequireAccountAsync(Routes.Friends));
        SettingsCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.Settings));
        SignInCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.SignIn));
        RulesCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.Rules));
    }

    public ICommand PlayOnlineCommand { get; }
    public ICommand CreateTableCommand { get; }
    public ICommand JoinTableCommand { get; }
    public ICommand PlayOfflineCommand { get; }
    public ICommand ProfileCommand { get; }
    public ICommand FriendsCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand SignInCommand { get; }
    public ICommand RulesCommand { get; }
    public ICommand SelectQueueCommand { get; }
    public ICommand StoreCommand { get; }
    public ICommand LeagueCommand { get; }

    public ModePicker Modes { get; }
    public bool IsRanked => _state.SelectedQueue == MatchQueue.Ranked;
    public bool IsCasual => _state.SelectedQueue == MatchQueue.Casual;
    public string QueueDescription => MatchLabels.QueueDescription(_state.SelectedQueue);
    public string GemsText => _me is null ? "" : $"{_me.Gems:N0}";

    /// <summary>Which backend the app is using — offline, a local development server, or the live service. Never hidden.</summary>
    public string EnvironmentText => !_session.IsSignedIn ? "Offline — sign in to play online"
        : !_serverReachable ? "Server unreachable — offline play still works"
        : _config is null ? "Online" : _config.EnvironmentLabel;
    public bool IsDevEnvironment => _session.IsSignedIn && (_config?.IsDevelopment == true || _config?.IsLocalServer == true);

    public bool IsSignedIn => _session.IsSignedIn;
    public bool IsSignedOut => !_session.IsSignedIn;
    public bool IsGuest => _session.IsGuest;
    public string DisplayName => _me?.DisplayName ?? (IsSignedIn ? "Player" : "Offline player");
    public string AvatarId => _me?.AvatarId ?? "crown";
    public string LevelText => _me is null ? (IsSignedIn ? "" : "Sign in to play online") : $"Level {_me.Level}";
    public string CoinsText => _me is null ? "" : $"{_me.Coins:N0}";
    public bool HasSavedGame { get => _hasSavedGame; private set => Set(ref _hasSavedGame, value); }

    public override async Task OnAppearingAsync()
    {
        HasSavedGame = OfflineGameSession.LoadSave(_settings) is not null;
        Refresh();
        if (_session.IsSignedIn)
        {
            var me = await _api.GetAsync<MeResponse>("/me");
            _serverReachable = me.Kind != ApiErrorKind.Network;
            if (me.Ok) { _me = me.Value; Refresh(); }
            else if (me.Kind == ApiErrorKind.Unauthorized) { await _session.SignOutAsync(); Refresh(); }
            else Refresh();
        }
        if (_state.PendingJoinCode is string code)
        {
            _state.PendingJoinCode = null;
            await JoinTableAsync(code);
        }
    }

    private void Refresh()
    {
        foreach (var p in new[] { nameof(IsSignedIn), nameof(IsSignedOut), nameof(IsGuest), nameof(DisplayName), nameof(AvatarId), nameof(LevelText), nameof(CoinsText), nameof(GemsText),
                     nameof(EnvironmentText), nameof(IsDevEnvironment), nameof(IsRanked), nameof(IsCasual), nameof(QueueDescription) })
            OnPropertyChanged(p);
    }

    private async Task<bool> EnsureOnlineAsync()
    {
        if (_session.IsSignedIn) return true;
        if (await _dialogs.ConfirmAsync("Online play", "Sign in or continue as a guest to play online.", "Sign in", "Not now"))
            await _nav.GoToAsync(Routes.SignIn);
        return false;
    }

    private async Task RequireAccountAsync(string route)
    {
        if (await EnsureOnlineAsync()) await _nav.GoToAsync(route);
    }

    private async Task PlayOnlineAsync(int players)
    {
        if (!await EnsureOnlineAsync()) return;
        _state.RequestedPlayerCount = players;
        await _nav.GoToAsync(Routes.Matchmaking);
    }

    private async Task CreateTableAsync()
    {
        if (!await EnsureOnlineAsync()) return;
        var choice = await _dialogs.ActionSheetAsync($"Private table · {Modes.Selected.Name}", "Cancel", "2 players", "4 players");
        if (choice is null || choice == "Cancel") return;
        _state.RequestedPlayerCount = choice.StartsWith('4') ? 4 : 2;
        _state.PrivateRoomMode = PrivateRoomMode.Create;
        await _nav.GoToAsync(Routes.PrivateRoom);
    }

    public async Task JoinTableAsync(string? code)
    {
        if (!await EnsureOnlineAsync()) return;
        code ??= await _dialogs.PromptAsync("Join a table", "Enter the 6-character table code.", "Join", null, 8);
        if (string.IsNullOrWhiteSpace(code)) return;
        var normalized = RoomCodes.Normalize(code);
        if (normalized is null)
        {
            await _dialogs.AlertAsync("Invalid code", "Table codes have 6 letters and numbers, like K7M2PQ.");
            return;
        }
        _state.PrivateRoomMode = PrivateRoomMode.Join;
        _state.PrivateRoomCode = normalized;
        await _nav.GoToAsync(Routes.PrivateRoom);
    }
}
