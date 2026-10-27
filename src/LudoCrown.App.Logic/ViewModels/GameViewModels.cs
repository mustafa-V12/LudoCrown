using System.Collections.ObjectModel;
using System.Windows.Input;
using LudoCrown.App.Game;
using LudoCrown.App.Mvvm;
using LudoCrown.App.Services;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.ViewModels;

/// <summary>
/// The board screen. Holds the latest <see cref="BoardState"/> from the session (single source of truth)
/// and exposes it for drawing. Animations and sounds react to state changes but never change state.
/// </summary>
public sealed class GameViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly IFeedbackService _feedback;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly IMainThread _main;
    private readonly ISettingsStore _settings;
    private readonly IDelay _delay;
    private IGameSession? _session;
    private BoardState? _board;
    private string _status = "";
    private string? _notice;
    private int _noticeId;
    private bool _leaving;

    public GameViewModel(AppState state, IFeedbackService feedback, INavigationService nav, IDialogService dialogs, IMainThread main, ISettingsStore settings, IDelay delay)
    {
        _state = state;
        _feedback = feedback;
        _nav = nav;
        _dialogs = dialogs;
        _main = main;
        _settings = settings;
        _delay = delay;
        RollCommand = new AsyncCommand(RollAsync);
        LeaveCommand = new AsyncCommand(LeaveAsync);
        ReactCommand = new AsyncCommand(p => _session?.SendReactionAsync(p as string ?? "") ?? Task.CompletedTask);
    }

    /// <summary>Raised with (previous, next) so the board view can animate moves.</summary>
    public event Action<BoardState?, BoardState>? BoardChanged;
    public event Action<ReactionInfo, SeatView>? ReactionReceived;

    public BoardState? Board { get => _board; private set { Set(ref _board, value); RefreshDerived(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string? Notice { get => _notice; private set { if (Set(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool IsOnline => _session?.IsOnline ?? false;
    public bool CanRoll => Board?.CanRoll ?? false;
    public int DiceValue => Board?.PendingRoll ?? Board?.LastRoll ?? 1;
    public bool ShowReactions => IsOnline && _settings.GetBool(SettingKeys.ShowReactions, true);
    public IReadOnlyList<string> ReactionIds => Reactions.All;
    public ObservableCollection<SeatView> Seats { get; } = new();
    public ICommand RollCommand { get; }
    public ICommand LeaveCommand { get; }
    public ICommand ReactCommand { get; }

    /// <summary>Seconds left for the current online turn, or null offline.</summary>
    public int? SecondsLeft(DateTimeOffset now) =>
        Board?.TurnDeadline is DateTimeOffset d && !Board.IsFinished ? Math.Max(0, (int)Math.Ceiling((d - now).TotalSeconds)) : null;

    public override async Task OnAppearingAsync()
    {
        if (_session is not null && ReferenceEquals(_session, _state.CurrentSession)) return;
        _session = _state.CurrentSession;
        if (_session is null) { await _nav.GoToAsync(Routes.Home); return; }
        _session.StateChanged += OnStateChanged;
        _session.Ended += OnEnded;
        _session.Notice += OnNotice;
        _session.Reaction += OnReaction;
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(ShowReactions));
        Apply(_session.State);
        await _session.StartAsync();
    }

    public void Detach()
    {
        if (_session is null) return;
        _session.StateChanged -= OnStateChanged;
        _session.Ended -= OnEnded;
        _session.Notice -= OnNotice;
        _session.Reaction -= OnReaction;
        _session = null;
    }

    /// <summary>Called by the board view when the player taps a piece.</summary>
    public async Task TapPieceAsync(int pieceIndex)
    {
        if (_session is null || Board is null || !Board.LegalPieces.Contains(pieceIndex)) return;
        _feedback.Haptic();
        await _session.MoveAsync(pieceIndex);
    }

    private async Task RollAsync()
    {
        if (_session is null || !CanRoll) return;
        _feedback.Play(SoundEffect.DiceRoll);
        _feedback.Haptic();
        await _session.RollAsync();
    }

    private async Task LeaveAsync()
    {
        if (_session is null) return;
        var online = _session.IsOnline && !(Board?.IsFinished ?? false);
        var message = online
            ? "If you leave, a bot plays your seat and you won't get rewards for this match."
            : "Your game is saved. You can resume it from Play offline.";
        if (!await _dialogs.ConfirmAsync("Leave the game?", message, "Leave", "Stay")) return;
        _leaving = true;
        await _session.LeaveAsync();
        Detach();
        await _state.EndSessionAsync();
        await _nav.GoToAsync(Routes.Home);
    }

    private void OnStateChanged(BoardState s) => _main.Post(() => Apply(s));

    private void Apply(BoardState next)
    {
        var previous = Board;
        Board = next;
        if (Seats.Count != next.Seats.Count) { Seats.Clear(); foreach (var s in next.Seats) Seats.Add(s); }
        else for (var i = 0; i < next.Seats.Count; i++) if (Seats[i] != next.Seats[i]) Seats[i] = next.Seats[i];
        PlayFeedback(previous, next);
        BoardChanged?.Invoke(previous, next);
    }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(CanRoll));
        OnPropertyChanged(nameof(DiceValue));
        Status = StatusFor(Board);
    }

    public static string StatusFor(BoardState? b)
    {
        if (b is null) return "";
        var seat = b.CurrentSeatView;
        if (b.IsFinished) return b.WinnerSeat is int w ? $"{b.Seats[w].Name} wins the crown!" : "Game over";
        if (seat.IsLocalHuman)
        {
            var who = b.Seats.Count(s => s.IsLocalHuman) > 1 ? $"{seat.Name}, " : "";
            return b.Phase == GamePhase.AwaitingRoll ? $"{who}your turn — roll the die" : $"{who}tap a glowing piece to move {b.PendingRoll}";
        }
        if (!seat.IsConnected) return $"{seat.Name} is reconnecting…";
        return seat.IsBot ? $"{seat.Name} is thinking…" : $"{seat.Name}'s turn";
    }

    private void PlayFeedback(BoardState? previous, BoardState next)
    {
        if (previous is null || next.Version == previous.Version) return;
        foreach (var e in next.Events)
        {
            switch (e.Type)
            {
                case GameEventType.PieceCaptured: _feedback.Play(SoundEffect.Capture); _feedback.Haptic(true); break;
                case GameEventType.PieceEntered: _feedback.Play(SoundEffect.Enter); break;
                case GameEventType.PieceMoved: _feedback.Play(SoundEffect.Move); break;
                case GameEventType.PieceFinished: _feedback.Play(SoundEffect.Finish); break;
                case GameEventType.DiceRolled when !next.Seats[e.Seat].IsLocalHuman: _feedback.Play(SoundEffect.DiceRoll); break;
            }
        }
        if (next.CurrentSeatView.IsLocalHuman && next.CurrentSeat != previous.CurrentSeat && next.Phase == GamePhase.AwaitingRoll)
            _feedback.Play(SoundEffect.Turn);
    }

    private void OnNotice(string text) => _main.Post(async () =>
    {
        Notice = text;
        var id = ++_noticeId;
        await _delay.Delay(TimeSpan.FromSeconds(2.5));
        if (id == _noticeId) Notice = null;
    });

    private void OnReaction(ReactionInfo r) => _main.Post(() =>
    {
        if (Board is null || r.Seat < 0 || r.Seat >= Board.Seats.Count || !ShowReactions) return;
        ReactionReceived?.Invoke(r, Board.Seats[r.Seat]);
    });

    private void OnEnded(GameResult result) => _main.Post(async () =>
    {
        if (_leaving) return;
        var won = result.YourPlacement == 1 || (result.YourPlacement is null && result.Reason == "won");
        _feedback.Play(won ? SoundEffect.Win : SoundEffect.Lose);
        _state.LastResult = result;
        await _delay.Delay(TimeSpan.FromSeconds(1.6)); // let the final move and celebration play
        Detach();
        await _nav.ReplaceAsync(Routes.Results);
    });
}

public sealed record ResultRow(int Placement, string Name, PlayerColor Color, bool IsYou, bool IsBot)
{
    public string PlacementText => Placement switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{Placement}th" };
}

public sealed class ResultsViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly INavigationService _nav;
    private readonly IMatchConnection _connection;
    private readonly ISettingsStore _settings;
    private readonly IDelay _delay;
    private GameResult? _result;

    public ResultsViewModel(AppState state, INavigationService nav, IMatchConnection connection, ISettingsStore settings, IDelay delay)
    {
        _state = state;
        _nav = nav;
        _connection = connection;
        _settings = settings;
        _delay = delay;
        PlayAgainCommand = new AsyncCommand(PlayAgainAsync);
        HomeCommand = new AsyncCommand(HomeAsync);
    }

    public ObservableCollection<ResultRow> Rows { get; } = new();
    public string Title { get; private set; } = "";
    public string Subtitle { get; private set; } = "";
    public string RewardsText { get; private set; } = "";
    public bool HasRewards { get; private set; }
    public bool Won { get; private set; }
    public string PlayAgainText { get; private set; } = "Play again";
    public ICommand PlayAgainCommand { get; }
    public ICommand HomeCommand { get; }

    public override async Task OnAppearingAsync()
    {
        _result = _state.LastResult;
        await _state.EndSessionAsync();
        if (_result is null) { await _nav.GoToAsync(Routes.Home); return; }

        Rows.Clear();
        for (var i = 0; i < _result.Standings.Count; i++)
        {
            var seat = _result.Seats[_result.Standings[i]];
            Rows.Add(new ResultRow(i + 1, seat.Name, seat.Color, seat.IsLocalHuman, seat.IsBot));
        }
        var winner = Rows[0];
        Won = _result.YourPlacement == 1;
        Title = _result.Reason == "abandoned" ? "Match ended"
            : _result.YourPlacement is int p ? (p == 1 ? "You won the crown!" : $"You finished {Rows[p - 1].PlacementText}")
            : $"{winner.Name} wins!";
        Subtitle = _result.IsOnline ? (_result.IsPrivate ? "Private table" : "Online match") : "Offline game";
        HasRewards = _result.CoinsAwarded > 0 || _result.XpAwarded > 0;
        RewardsText = HasRewards ? $"+{_result.CoinsAwarded} coins   +{_result.XpAwarded} XP" : "";
        PlayAgainText = _result.IsOnline ? (_result.IsPrivate ? "Rematch" : "Find another match") : "Play again";
        foreach (var n in new[] { nameof(Title), nameof(Subtitle), nameof(HasRewards), nameof(RewardsText), nameof(Won), nameof(PlayAgainText) }) OnPropertyChanged(n);
    }

    private Task PlayAgainAsync() => RunBusyAsync(async () =>
    {
        if (_result is null) return;
        if (!_result.IsOnline)
        {
            var seats = _state.LastOfflineSeats;
            if (seats is null) { await _nav.ReplaceAsync(Routes.OfflineSetup); return; }
            _state.CurrentSession = OfflineGameSession.NewGame(seats, new CryptoDiceRoller(), _delay, _settings);
            await _nav.ReplaceAsync(Routes.Game);
        }
        else if (_result.IsPrivate && _result.MatchId is not null)
        {
            var r = await _connection.RequestRematchAsync(_result.MatchId);
            if (r.Value is null) { Error = r.Error?.Message ?? "Rematch isn't available."; return; }
            _state.PrivateRoomMode = PrivateRoomMode.Existing;
            _state.PendingRoom = r.Value;
            await _nav.ReplaceAsync(Routes.PrivateRoom);
        }
        else
        {
            _state.RequestedPlayerCount = _result.Seats.Count;
            await _nav.ReplaceAsync(Routes.Matchmaking);
        }
    });

    private Task HomeAsync() => _nav.GoToAsync(Routes.Home);
}
