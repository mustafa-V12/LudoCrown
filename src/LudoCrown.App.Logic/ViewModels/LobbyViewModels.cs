using System.Collections.ObjectModel;
using System.Windows.Input;
using LudoCrown.App.Game;
using LudoCrown.App.Mvvm;
using LudoCrown.App.Services;
using LudoCrown.Core.AI;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.ViewModels;

/// <summary>Opens an online match screen once the server says a match exists.</summary>
public static class OnlineMatchLauncher
{
    public static async Task<bool> OpenAsync(IMatchConnection connection, AppState state, IMainThread main, INavigationService nav, string matchId, bool isPrivate)
    {
        var snapshot = await connection.ResyncAsync(matchId);
        if (snapshot is null) return false;
        await state.EndSessionAsync();
        state.CurrentSession = new OnlineGameSession(connection, snapshot, main) { IsPrivate = isPrivate };
        await nav.ReplaceAsync(Routes.Game);
        return true;
    }
}

public sealed class MatchmakingViewModel : ViewModelBase
{
    private readonly IMatchConnection _connection;
    private readonly AppState _state;
    private readonly INavigationService _nav;
    private readonly IMainThread _main;
    private string _status = "Connecting…";
    private int _found;
    private bool _searching;
    private bool _opening;

    public MatchmakingViewModel(IMatchConnection connection, AppState state, INavigationService nav, IMainThread main)
    {
        _connection = connection;
        _state = state;
        _nav = nav;
        _main = main;
        CancelCommand = new AsyncCommand(CancelAsync);
        RetryCommand = new AsyncCommand(StartAsync);
    }

    public int PlayerCount => _state.RequestedPlayerCount;
    public GameModeInfo Mode => GameModes.Get(_state.SelectedMode);
    public string Title => $"{PlayerCount}-player {Mode.Name} · {MatchLabels.Queue(_state.SelectedQueue)}";
    public string RulesText => $"{Mode.WinCondition} {MatchLabels.QueueDescription(_state.SelectedQueue)}";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public int PlayersFound { get => _found; private set { if (Set(ref _found, value)) OnPropertyChanged(nameof(PlayersText)); } }
    public string PlayersText => $"{Math.Max(1, PlayersFound)} of {PlayerCount} players";
    public bool Searching { get => _searching; private set => Set(ref _searching, value); }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }

    public override async Task OnAppearingAsync()
    {
        _connection.MatchmakingStatusChanged += OnStatus;
        await StartAsync();
    }

    public override Task OnDisappearingAsync()
    {
        _connection.MatchmakingStatusChanged -= OnStatus;
        return Task.CompletedTask;
    }

    private async Task StartAsync()
    {
        Error = null;
        Status = "Connecting…";
        try
        {
            await _connection.ConnectAsync();
            Searching = true;
            Status = "Finding players…";
            var status = await _connection.FindMatchAsync(PlayerCount, _state.SelectedMode, _state.SelectedQueue);
            await HandleAsync(status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Searching = false;
            Error = "Couldn't connect to the game server. Check your internet connection and try again.";
            Status = "Not connected";
        }
    }

    private void OnStatus(MatchmakingStatus s) => _main.Post(async () => await HandleAsync(s));

    private async Task HandleAsync(MatchmakingStatus s)
    {
        switch (s.State)
        {
            case MatchmakingState.Searching:
                PlayersFound = s.PlayersFound;
                Status = "Finding players…";
                break;
            case MatchmakingState.Matched when s.MatchId is not null && !_opening:
                _opening = true;
                Status = "Match found!";
                Searching = false;
                if (!await OnlineMatchLauncher.OpenAsync(_connection, _state, _main, _nav, s.MatchId, false))
                {
                    _opening = false;
                    Error = "The match could not be opened. Please try again.";
                }
                break;
            case MatchmakingState.Failed:
                Searching = false;
                Error = s.Message ?? "Matchmaking failed. Please try again.";
                break;
        }
    }

    private async Task CancelAsync()
    {
        Searching = false;
        try { await _connection.CancelMatchmakingAsync(); } catch (Exception) { /* leaving anyway */ }
        await _nav.GoBackAsync();
    }
}

public sealed class PrivateRoomViewModel : ViewModelBase
{
    private readonly IMatchConnection _connection;
    private readonly AppState _state;
    private readonly INavigationService _nav;
    private readonly IShareService _share;
    private readonly IMainThread _main;
    private RoomInfo? _room;
    private bool _opening;

    public PrivateRoomViewModel(IMatchConnection connection, AppState state, INavigationService nav, IShareService share, IMainThread main)
    {
        _connection = connection;
        _state = state;
        _nav = nav;
        _share = share;
        _main = main;
        StartCommand = new AsyncCommand(StartAsync, () => CanStart);
        ShareCommand = new AsyncCommand(ShareAsync);
        LeaveCommand = new AsyncCommand(LeaveAsync);
        RetryCommand = new AsyncCommand(OpenAsync);
    }

    public ObservableCollection<SeatInfo> Seats { get; } = new();
    public RoomInfo? Room { get => _room; private set { Set(ref _room, value); Refresh(); } }
    public string Code => _room?.Code ?? "······";
    public string ModeText => _room is null ? "" : $"{GameModes.Get(_room.Mode).Name}: {GameModes.Get(_room.Mode).WinCondition}";
    public string ModeDetail => _room is null ? "" : $"Up to {GameModes.Get(_room.Mode).MaxRerolls} gem rerolls · 1 Shield bottle per player · no league points.";
    public bool IsHost => _room?.IsHost ?? false;
    public bool CanStart => IsHost && Seats.Count >= 2;
    public string SeatsText => _room is null ? "" : $"{Seats.Count} of {_room.PlayerCount} seated";
    public string HintText => _room is null ? "Opening table…"
        : IsHost ? (Seats.Count >= 2 ? (Seats.Count < _room.PlayerCount ? "Start now — empty seats will be played by bots." : "Everyone's here. Start the game!") : "Share the code with a friend to invite them.")
        : "Waiting for the host to start…";
    public ICommand StartCommand { get; }
    public ICommand ShareCommand { get; }
    public ICommand LeaveCommand { get; }
    public ICommand RetryCommand { get; }

    public override async Task OnAppearingAsync()
    {
        _connection.RoomUpdated += OnRoomUpdated;
        if (_room is null) await OpenAsync();
    }

    public override Task OnDisappearingAsync()
    {
        _connection.RoomUpdated -= OnRoomUpdated;
        return Task.CompletedTask;
    }

    private async Task OpenAsync()
    {
        Error = null;
        try
        {
            await _connection.ConnectAsync();
            HubResult<RoomInfo> result = _state.PrivateRoomMode switch
            {
                PrivateRoomMode.Create => await _connection.CreatePrivateRoomAsync(_state.RequestedPlayerCount, _state.SelectedMode),
                PrivateRoomMode.Join => await _connection.JoinPrivateRoomAsync(_state.PrivateRoomCode ?? ""),
                _ => new HubResult<RoomInfo>(_state.PendingRoom, null),
            };
            if (result.Value is null) { Error = result.Error?.Message ?? "Couldn't open the table."; return; }
            Apply(result.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = "Couldn't connect to the game server. Check your connection and try again.";
        }
    }

    private void OnRoomUpdated(RoomInfo room) => _main.Post(() =>
    {
        if (_room is null || room.RoomId == _room.RoomId) Apply(room);
    });

    private async void Apply(RoomInfo room)
    {
        Seats.Clear();
        foreach (var s in room.Seats) Seats.Add(s);
        Room = room;
        if (room.Started && room.MatchId is not null && !_opening)
        {
            _opening = true;
            if (!await OnlineMatchLauncher.OpenAsync(_connection, _state, _main, _nav, room.MatchId, true))
            {
                _opening = false;
                Error = "The game could not be opened.";
            }
        }
    }

    private void Refresh()
    {
        foreach (var p in new[] { nameof(Code), nameof(IsHost), nameof(CanStart), nameof(SeatsText), nameof(HintText), nameof(ModeText), nameof(ModeDetail) }) OnPropertyChanged(p);
        (StartCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }

    private Task StartAsync() => RunBusyAsync(async () =>
    {
        if (_room is null) return;
        var r = await _connection.StartPrivateRoomAsync(_room.RoomId);
        if (r.Error is not null) Error = r.Error.Message;
    });

    private Task ShareAsync() => _room?.Code is string code
        ? _share.ShareTextAsync("Join my Ludo Crown table", $"Join my Ludo Crown table! Open the app, tap Join table and enter {code} — or tap ludocrown://join/{code}")
        : Task.CompletedTask;

    private async Task LeaveAsync()
    {
        if (_room is not null)
        {
            try { await _connection.LeaveRoomAsync(_room.RoomId); } catch (Exception) { /* leaving anyway */ }
        }
        await _nav.GoBackAsync();
    }
}

public sealed class OfflineSeatOption : ObservableObject
{
    private string _name;
    private bool _isBot;
    private BotDifficulty _difficulty = BotDifficulty.Normal;

    public OfflineSeatOption(int seat, string name, bool isBot)
    {
        Seat = seat;
        _name = name;
        _isBot = isBot;
    }

    public int Seat { get; }
    public string[] Difficulties { get; } = { "Easy", "Normal", "Hard" };
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool IsBot { get => _isBot; set { if (Set(ref _isBot, value)) OnPropertyChanged(nameof(IsHuman)); } }
    public bool IsHuman => !IsBot;
    public bool CanChangeKind => Seat != 0;
    public BotDifficulty Difficulty { get => _difficulty; set => Set(ref _difficulty, value); }
    public int DifficultyIndex { get => (int)Difficulty; set => Difficulty = (BotDifficulty)Math.Clamp(value, 0, 2); }
    public string ColorName { get; set; } = "";
}

public sealed class OfflineSetupViewModel : ViewModelBase
{
    private static readonly string[] BotNames = { "Duchess", "Baron", "Squire" };
    private readonly AppState _state;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settings;
    private readonly IDelay _delay;
    private int _playerCount = 4;
    private bool _hasSave;

    public OfflineSetupViewModel(AppState state, INavigationService nav, IDialogService dialogs, ISettingsStore settings, IDelay delay)
    {
        _state = state;
        _nav = nav;
        _dialogs = dialogs;
        _settings = settings;
        _delay = delay;
        StartCommand = new AsyncCommand(StartAsync);
        ResumeCommand = new AsyncCommand(ResumeAsync);
        SetPlayersCommand = new AsyncCommand(p => { PlayerCount = Convert.ToInt32(p); return Task.CompletedTask; });
        Modes = new ModePicker(state, settings);
        BuildSeats();
    }

    public ModePicker Modes { get; }
    public ObservableCollection<OfflineSeatOption> Seats { get; } = new();
    public string[] Difficulties { get; } = { "Easy", "Normal", "Hard" };
    public int PlayerCount { get => _playerCount; set { if (Set(ref _playerCount, value is 2 or 4 ? value : 4)) { BuildSeats(); OnPropertyChanged(nameof(IsTwo)); OnPropertyChanged(nameof(IsFour)); } } }
    public bool IsTwo => PlayerCount == 2;
    public bool IsFour => PlayerCount == 4;
    public bool HasSave { get => _hasSave; private set => Set(ref _hasSave, value); }
    public ICommand StartCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand SetPlayersCommand { get; }

    public override Task OnAppearingAsync()
    {
        HasSave = OfflineGameSession.LoadSave(_settings) is not null;
        return Task.CompletedTask;
    }

    private void BuildSeats()
    {
        var colors = PlayerCount == 2 ? new[] { "Ruby", "Topaz" } : new[] { "Ruby", "Emerald", "Topaz", "Sapphire" };
        var you = _settings.GetString(SettingKeys.OfflineName) ?? "You";
        Seats.Clear();
        for (var i = 0; i < PlayerCount; i++)
            Seats.Add(new OfflineSeatOption(i, i == 0 ? you : BotNames[(i - 1) % BotNames.Length], i != 0) { ColorName = colors[i] });
    }

    private async Task StartAsync()
    {
        if (HasSave && !await _dialogs.ConfirmAsync("Start a new game?", "Your unfinished offline game will be discarded.", "New game", "Keep it"))
            return;
        foreach (var s in Seats)
        {
            var name = s.Name.Trim();
            if (DisplayNames.Validate(name) is string reason) { Error = $"{s.ColorName}: {reason}"; return; }
            s.Name = name;
        }
        Error = null;
        _settings.SetString(SettingKeys.OfflineName, Seats[0].Name);
        var seats = Seats.Select(s => new OfflineSeat(s.Name, s.IsBot, s.Difficulty, s.IsBot ? "knight" : "crown")).ToList();
        _state.LastOfflineSeats = seats;
        await LaunchAsync(OfflineGameSession.NewGame(seats, new CryptoDiceRoller(), _delay, _settings, mode: _state.SelectedMode));
    }

    private async Task ResumeAsync()
    {
        _state.LastOfflineSeats = OfflineGameSession.LoadSave(_settings)?.Seats;
        var session = OfflineGameSession.TryResume(new CryptoDiceRoller(), _delay, _settings);
        if (session is null)
        {
            HasSave = false;
            await _dialogs.AlertAsync("Can't resume", "The saved game couldn't be loaded. Start a new one.");
            return;
        }
        await LaunchAsync(session);
    }

    private async Task LaunchAsync(OfflineGameSession session)
    {
        await _state.EndSessionAsync();
        _state.CurrentSession = session;
        await _nav.GoToAsync(Routes.Game);
    }
}
