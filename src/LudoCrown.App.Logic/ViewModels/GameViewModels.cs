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
        ReactCommand = new AsyncCommand(p => SendChatAsync(p as string ?? ""));
        RerollCommand = new AsyncCommand(RerollAsync);
        PassCommand = new AsyncCommand(() => _session?.PassAsync() ?? Task.CompletedTask);
        BoostCommand = new AsyncCommand(BoostAsync);
        ToggleChatCommand = new AsyncCommand(() => { IsChatOpen = !IsChatOpen; return Task.CompletedTask; });
        ToggleMuteCommand = new AsyncCommand(p => { ToggleMute(Convert.ToInt32(p)); return Task.CompletedTask; });
        OpenPlayerCommand = new AsyncCommand(p => OpenPlayerAsync(Convert.ToInt32(p)));
        RulesInfoCommand = new AsyncCommand(ShowRulesAsync);
    }

    private bool _chatOpen;
    private readonly HashSet<int> _muted = new();

    public ICommand RerollCommand { get; }
    public ICommand PassCommand { get; }
    public ICommand BoostCommand { get; }
    public ICommand ToggleChatCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand OpenPlayerCommand { get; }
    public ICommand RulesInfoCommand { get; }

    // ----- mode & match labels (shown in the match UI) -----
    public string ModeText => Board is null ? "" : $"{Board.ModeInfo.Name} · {Board.ModeInfo.WinCondition}";
    public string ModeChip => Board is null ? "" : $"{Board.ModeInfo.Name} · {MatchLabels.Kind(Board.Kind)}";

    // ----- gem rerolls -----
    public bool ShowRerollBar => Board is { Kind: not MatchKind.Offline } && Board.MaxRerolls > 0;
    public bool CanReroll => Board?.RerollStatus == RerollStatus.Available;
    public bool CanPass => Board?.CanPass ?? false;
    public string GemsText => Board?.Gems is int g ? $"{g:N0}" : "—";
    public string RerollButtonText => Board is null ? "" : $"Reroll · {GameModes.RerollGemCost} gem";
    public string RerollCountText => Board is null ? "" : $"Rerolls {Board.RerollsUsed}/{Board.MaxRerolls}";
    public string RerollHint => Board is null ? "" : RerollText.Reason(Board);
    public bool ShowOfflineNote => Board is { Kind: MatchKind.Offline };
    public string OfflineNote => "Offline game: gem rerolls and boosts are only available online, because gems are kept safely on the server.";

    // ----- Shield boost -----
    public bool ShowBoost => Board is { MaxBoosts: > 0 };
    public bool CanBoost => Board?.BoostStatus == BoostStatus.Available;
    public string BoostButtonText => Board?.ShieldBottles is int n ? $"🛡 Shield ({n})" : "🛡 Shield";
    public string BoostHint => Board is null ? "" : BoostText.Reason(Board);
    public bool BoostsOffNotice => Board is { Kind: MatchKind.Ranked };

    // ----- quick chat -----
    public bool IsChatOpen { get => _chatOpen; private set => Set(ref _chatOpen, value); }
    public IReadOnlyList<QuickChatItem> ChatPhrases => Reactions.Items.Where(i => i.Kind == QuickChatKind.Phrase).ToList();
    public IReadOnlyList<QuickChatItem> ChatEmoji => Reactions.Items.Where(i => i.Kind == QuickChatKind.Emoji).ToList();
    public bool IsMuted(int seat) => _muted.Contains(seat);

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
    public event Action? MuteChanged;
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
        _muted.Clear();
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
        foreach (var p in new[] { nameof(CanRoll), nameof(DiceValue), nameof(ModeText), nameof(ModeChip), nameof(ShowRerollBar), nameof(CanReroll), nameof(CanPass), nameof(GemsText),
                     nameof(RerollButtonText), nameof(RerollCountText), nameof(RerollHint), nameof(ShowOfflineNote), nameof(ShowBoost), nameof(CanBoost), nameof(BoostButtonText),
                     nameof(BoostHint), nameof(BoostsOffNotice) })
            OnPropertyChanged(p);
        Status = StatusFor(Board);
    }

    private async Task RerollAsync()
    {
        if (_session is null || Board is null) return;
        if (!CanReroll) { OnNotice(RerollHint); return; }
        if (_settings.GetBool(SettingKeys.ConfirmGemSpend, true))
        {
            var ok = await _dialogs.ConfirmAsync("Reroll the die?",
                $"Spend {GameModes.RerollGemCost} gem to replace your {Board.PendingRoll} with a new roll from the server.\n\nYou have {GemsText} gems · {Board.RerollsLeft} of {Board.MaxRerolls} rerolls left this match.",
                $"Reroll ({GameModes.RerollGemCost} gem)", "Keep my roll");
            if (!ok) return;
        }
        _feedback.Play(SoundEffect.DiceRoll);
        await _session.RerollAsync();
    }

    private async Task BoostAsync()
    {
        if (_session is null || Board is null) return;
        if (!CanBoost) { OnNotice(BoostHint); return; }
        var ok = await _dialogs.ConfirmAsync("Drink a Shield?", $"{Boosts.ShieldEffect}\n\n{Boosts.ShieldLimits}\nYou have {Board.ShieldBottles} bottles.", "Use Shield", "Not now");
        if (!ok) return;
        await _session.UseBoostAsync();
    }

    private Task SendChatAsync(string id)
    {
        if (_session is null || !Reactions.IsValid(id)) return Task.CompletedTask;
        IsChatOpen = false;
        return _session.SendReactionAsync(id);
    }

    private void ToggleMute(int seat)
    {
        if (!_muted.Remove(seat)) _muted.Add(seat);
        MuteChanged?.Invoke();
    }

    private async Task OpenPlayerAsync(int seat)
    {
        if (Board is null || seat < 0 || seat >= Board.Seats.Count) return;
        var s = Board.Seats[seat];
        if (s.PublicId is null) { OnNotice(s.IsBot ? $"{s.Name} is a computer player." : $"{s.Name} has no public profile."); return; }
        var options = new List<string> { "View profile" };
        if (!s.IsLocalHuman) options.Add(IsMuted(seat) ? "Unmute chat" : "Mute chat");
        var choice = await _dialogs.ActionSheetAsync(s.Name, "Cancel", options.ToArray());
        if (choice == "View profile") { _state.ViewedPlayerId = s.PublicId; await _nav.GoToAsync(Routes.PlayerProfile); }
        else if (choice is "Mute chat" or "Unmute chat") ToggleMute(seat);
    }

    private Task ShowRulesAsync() => Board is null ? Task.CompletedTask
        : _dialogs.AlertAsync($"{Board.ModeInfo.Name} rules", $"{Board.ModeInfo.WinCondition}\n\n{Board.ModeInfo.Description}" +
            (Board.Kind == MatchKind.Offline ? "" : $"\n\n{MatchLabels.Kind(Board.Kind)} match." + (Board.MaxBoosts > 0 ? $" Shield: {Boosts.ShieldLimits}" : " Boosts are off.")));

    public static string StatusFor(BoardState? b)
    {
        if (b is null) return "";
        var seat = b.CurrentSeatView;
        if (b.IsFinished) return b.WinnerSeat is int w ? $"{b.Seats[w].Name} wins the crown!" : "Game over";
        if (seat.IsLocalHuman)
        {
            var who = b.Seats.Count(s => s.IsLocalHuman) > 1 ? $"{seat.Name}, " : "";
            if (b.CanPass) return $"{who}no move with {b.PendingRoll} — reroll or pass";
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
        if (Board is null || r.Seat < 0 || r.Seat >= Board.Seats.Count || !ShowReactions || _muted.Contains(r.Seat)) return;
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
    public string ModeText { get; private set; } = "";
    public string LeagueText { get; private set; } = "";
    public bool HasLeague { get; private set; }
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
        var info = GameModes.Get(_result.Mode);
        Subtitle = _result.IsOnline ? $"{MatchLabels.Kind(_result.Kind)} · {info.Name}" : $"Offline game · {info.Name}";
        ModeText = $"{info.Name}: {info.WinCondition}";
        HasRewards = _result.CoinsAwarded > 0 || _result.XpAwarded > 0 || _result.GemsAwarded > 0;
        RewardsText = HasRewards ? string.Join("   ", new[]
        {
            _result.CoinsAwarded > 0 ? $"+{_result.CoinsAwarded} gold" : null,
            _result.GemsAwarded > 0 ? $"+{_result.GemsAwarded} gem" : null,
            _result.XpAwarded > 0 ? $"+{_result.XpAwarded} XP" : null,
        }.Where(x => x is not null)) : "";
        HasLeague = _result.LeaguePoints is not null;
        LeagueText = _result.LeaguePoints is int lp ? $"League: {(lp >= 0 ? "+" : "")}{lp} points" : "";
        PlayAgainText = _result.IsOnline ? (_result.IsPrivate ? "Rematch" : "Find another match") : "Play again";
        foreach (var n in new[] { nameof(Title), nameof(Subtitle), nameof(HasRewards), nameof(RewardsText), nameof(Won), nameof(PlayAgainText), nameof(ModeText), nameof(LeagueText), nameof(HasLeague) }) OnPropertyChanged(n);
    }

    private Task PlayAgainAsync() => RunBusyAsync(async () =>
    {
        if (_result is null) return;
        if (!_result.IsOnline)
        {
            var seats = _state.LastOfflineSeats;
            if (seats is null) { await _nav.ReplaceAsync(Routes.OfflineSetup); return; }
            _state.CurrentSession = OfflineGameSession.NewGame(seats, new CryptoDiceRoller(), _delay, _settings, mode: _result.Mode);
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
            _state.SelectedMode = _result.Mode;
            _state.SelectedQueue = _result.Kind == MatchKind.Casual ? MatchQueue.Casual : MatchQueue.Ranked;
            await _nav.ReplaceAsync(Routes.Matchmaking);
        }
    });

    private Task HomeAsync() => _nav.GoToAsync(Routes.Home);
}
