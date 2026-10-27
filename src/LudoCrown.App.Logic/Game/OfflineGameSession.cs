using System.Text.Json;
using LudoCrown.App.Services;
using LudoCrown.Core.AI;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using LudoCrown.Core.Serialization;

namespace LudoCrown.App.Game;

/// <summary>Seat configuration for an offline game. Several local humans = pass-and-play on one phone.</summary>
public sealed record OfflineSeat(string Name, bool IsBot, BotDifficulty Difficulty, string AvatarId);

/// <summary>What is saved on the device so an interrupted offline game can be resumed.</summary>
public sealed record OfflineSave(string StateJson, IReadOnlyList<OfflineSeat> Seats, DateTimeOffset SavedAt);

/// <summary>
/// Offline match against computer opponents (and/or pass-and-play). Runs entirely on the device — no account
/// or network needed. The game is saved after every change and can be resumed after the app is closed.
/// </summary>
public sealed class OfflineGameSession : IGameSession
{
    private readonly IReadOnlyList<OfflineSeat> _seats;
    private readonly Dictionary<int, IBotStrategy> _bots = new();
    private readonly IDiceRoller _dice;
    private readonly IDelay _delay;
    private readonly ISettingsStore _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private GameState _state;
    private IReadOnlyList<GameEvent> _lastEvents = Array.Empty<GameEvent>();
    private bool _endedRaised;

    public static readonly TimeSpan BotRollDelay = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan BotMoveDelay = TimeSpan.FromMilliseconds(650);

    public OfflineGameSession(GameState state, IReadOnlyList<OfflineSeat> seats, IDiceRoller dice, IDelay delay, ISettingsStore settings, Random? random = null)
    {
        if (state.Players.Count != seats.Count) throw new ArgumentException("Seat count mismatch.");
        _state = state;
        _seats = seats;
        _dice = dice;
        _delay = delay;
        _settings = settings;
        random ??= new Random();
        for (var i = 0; i < seats.Count; i++)
            if (seats[i].IsBot) _bots[i] = BotFactory.Create(seats[i].Difficulty, new Random(random.Next()));
        State = Build();
    }

    public static OfflineGameSession NewGame(IReadOnlyList<OfflineSeat> seats, IDiceRoller dice, IDelay delay, ISettingsStore settings, Random? random = null)
    {
        var state = GameSetup.NewGame("offline-" + Guid.NewGuid().ToString("N")[..8],
            seats.Select((s, i) => new SeatSetup($"local-{i}", s.Name, s.IsBot ? PlayerKind.Bot : PlayerKind.Human)).ToList());
        return new OfflineGameSession(state, seats, dice, delay, settings, random);
    }

    /// <summary>Loads the saved offline game, or null if there is none or it is unreadable.</summary>
    public static OfflineGameSession? TryResume(IDiceRoller dice, IDelay delay, ISettingsStore settings)
    {
        var save = LoadSave(settings);
        if (save is null)
        {
            settings.SetString(SettingKeys.OfflineSave, null); // unreadable save: drop it so it isn't retried forever
            return null;
        }
        try
        {
            var state = GameStateSerializer.Deserialize(save.StateJson);
            if (state.Phase == GamePhase.Finished) { settings.SetString(SettingKeys.OfflineSave, null); return null; }
            return new OfflineGameSession(state, save.Seats, dice, delay, settings);
        }
        catch (JsonException)
        {
            settings.SetString(SettingKeys.OfflineSave, null);
            return null;
        }
    }

    public static OfflineSave? LoadSave(ISettingsStore settings)
    {
        var json = settings.GetString(SettingKeys.OfflineSave);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<OfflineSave>(json, GameStateSerializer.Options); }
        catch (JsonException) { return null; }
    }

    public bool IsOnline => false;
    public BoardState State { get; private set; }
    public GameState EngineState => _state;

    public event Action<BoardState>? StateChanged;
    public event Action<GameResult>? Ended;
    public event Action<string>? Notice;
    public event Action<ReactionInfo>? Reaction { add { } remove { } }

    public Task StartAsync()
    {
        Publish();
        _ = RunBotsAsync();
        return Task.CompletedTask;
    }

    public async Task RollAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsLocalHumanTurn() || _state.Phase != GamePhase.AwaitingRoll) return;
            Apply(GameEngine.Roll(_state, _state.CurrentPlayer.Seat, _dice));
            await AutoMoveIfSingleAsync();
        }
        finally { _gate.Release(); }
        _ = RunBotsAsync();
    }

    public async Task MoveAsync(int pieceIndex)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsLocalHumanTurn()) return;
            var r = GameEngine.Move(_state, _state.CurrentPlayer.Seat, pieceIndex);
            if (!r.Ok) return; // tapping a piece that can't move does nothing
            Apply(r);
        }
        finally { _gate.Release(); }
        _ = RunBotsAsync();
    }

    public Task LeaveAsync()
    {
        _cts.Cancel();
        // The saved game is kept so the player can resume it from the offline setup screen.
        return Task.CompletedTask;
    }

    public Task SendReactionAsync(string reactionId) => Task.CompletedTask;

    public void DiscardSave() => _settings.SetString(SettingKeys.OfflineSave, null);

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        return ValueTask.CompletedTask;
    }

    private bool IsLocalHumanTurn() => _state.Phase != GamePhase.Finished && !_bots.ContainsKey(_state.CurrentPlayer.Seat);

    private async Task AutoMoveIfSingleAsync()
    {
        if (_state.Phase != GamePhase.AwaitingMove || !_settings.GetBool(SettingKeys.AutoMoveSingle, true)) return;
        var moves = GameEngine.GetLegalMoves(_state);
        if (moves.Count != 1) return;
        await _delay.Delay(TimeSpan.FromMilliseconds(450), _cts.Token);
        Apply(GameEngine.Move(_state, _state.CurrentPlayer.Seat, moves[0].Piece));
    }

    private async Task RunBotsAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _gate.WaitAsync(_cts.Token);
                try
                {
                    if (_state.Phase == GamePhase.Finished || !_bots.TryGetValue(_state.CurrentPlayer.Seat, out var bot)) return;
                    var seat = _state.CurrentPlayer.Seat;
                    if (_state.Phase == GamePhase.AwaitingRoll)
                    {
                        await _delay.Delay(BotRollDelay, _cts.Token);
                        Apply(GameEngine.Roll(_state, seat, _dice));
                    }
                    else
                    {
                        await _delay.Delay(BotMoveDelay, _cts.Token);
                        var choice = bot.ChooseMove(_state);
                        Apply(choice is int p ? GameEngine.Move(_state, seat, p) : GameEngine.ForfeitTurn(_state, seat));
                    }
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Apply(EngineResult r)
    {
        if (!r.Ok) return;
        _state = r.State;
        _lastEvents = r.Events;
        foreach (var e in r.Events)
        {
            var text = GameText.Describe(e, State.Seats);
            if (text.Length > 0 && e.Type != GameEventType.GameWon) Notice?.Invoke(text);
        }
        Save();
        Publish();
        if (_state.Phase == GamePhase.Finished && !_endedRaised)
        {
            _endedRaised = true;
            _settings.SetString(SettingKeys.OfflineSave, null);
            var standings = _state.Standings();
            var humanSeats = Enumerable.Range(0, _seats.Count).Where(i => !_seats[i].IsBot).ToList();
            int? placement = humanSeats.Count == 1 ? standings.ToList().IndexOf(humanSeats[0]) + 1 : null;
            Ended?.Invoke(new GameResult(standings, State.Seats, placement, 0, 0, false, null, false, "won"));
        }
    }

    private void Save()
    {
        if (_state.Phase == GamePhase.Finished) return;
        var save = new OfflineSave(GameStateSerializer.Serialize(_state), _seats, DateTimeOffset.UtcNow);
        _settings.SetString(SettingKeys.OfflineSave, JsonSerializer.Serialize(save, GameStateSerializer.Options));
    }

    private void Publish()
    {
        State = Build();
        StateChanged?.Invoke(State);
    }

    private BoardState Build()
    {
        var humanTurn = IsLocalHumanTurn();
        var seats = _state.Players.Select(p => new SeatView(p.Seat, p.Color, _seats[p.Seat].Name, _seats[p.Seat].AvatarId,
            _seats[p.Seat].IsBot, true, !_seats[p.Seat].IsBot, p.FinishedCount)).ToList();
        return new BoardState(
            _state.MatchId,
            _state.Version,
            _state.Phase,
            _state.CurrentPlayer.Seat,
            _state.PendingRoll,
            _state.LastRoll,
            _state.Players.Select(p => (int[])p.Pieces.Clone()).ToArray(),
            seats,
            _lastEvents,
            _state.WinnerSeat,
            humanTurn ? GameEngine.GetLegalMoves(_state).Select(m => m.Piece).ToList() : new List<int>(),
            null,
            humanTurn && _state.Phase == GamePhase.AwaitingRoll);
    }
}
