using LudoCrown.App.Services;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.Game;

/// <summary>
/// Displays a server-run match. The client never decides dice, moves or results: it sends intents with the
/// state version it is showing, and redraws from the server's snapshots. After a reconnect it asks the server
/// for the current snapshot so the board is always consistent.
/// </summary>
public sealed class OnlineGameSession : IGameSession
{
    private readonly IMatchConnection _connection;
    private readonly IMainThread _main;
    private MatchSnapshot _snapshot;
    private bool _ended;
    // Idempotency keys for paid actions: kept until the server answers, so a retry after a dropped connection reuses them.
    private (long Version, string Id)? _pendingReroll;
    private (long Version, string Id)? _pendingBoost;

    public OnlineGameSession(IMatchConnection connection, MatchSnapshot initial, IMainThread main)
    {
        _connection = connection;
        _main = main;
        _snapshot = initial;
        State = Build(initial);
        _connection.MatchState += OnMatchState;
        _connection.MatchEnded += OnMatchEnded;
        _connection.Reaction += OnReaction;
        _connection.StatusChanged += OnStatusChanged;
    }

    public bool IsOnline => true;
    public BoardState State { get; private set; }
    public string MatchId => _snapshot.MatchId;
    public bool IsPrivate { get; init; }

    public event Action<BoardState>? StateChanged;
    public event Action<GameResult>? Ended;
    public event Action<string>? Notice;
    public event Action<ReactionInfo>? Reaction;

    public Task StartAsync()
    {
        StateChanged?.Invoke(State);
        return Task.CompletedTask;
    }

    public async Task RollAsync()
    {
        if (!State.CanRoll) return;
        Report(await _connection.RollAsync(_snapshot.MatchId, _snapshot.Version));
    }

    public async Task MoveAsync(int pieceIndex)
    {
        if (!State.LegalPieces.Contains(pieceIndex)) return;
        Report(await _connection.MoveAsync(_snapshot.MatchId, pieceIndex, _snapshot.Version));
    }

    public async Task RerollAsync()
    {
        if (State.RerollStatus != RerollStatus.Available) { Notice?.Invoke(RerollText.Reason(State)); return; }
        var version = _snapshot.Version;
        if (_pendingReroll is not { } p || p.Version != version) _pendingReroll = (version, Guid.NewGuid().ToString());
        RerollResult result;
        try { result = await _connection.RerollAsync(_snapshot.MatchId, version, _pendingReroll.Value.Id); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown outcome (connection dropped): keep the request id so tapping again cannot charge twice.
            Notice?.Invoke("Couldn't reach the server. Tap reroll again to retry — you won't be charged twice.");
            return;
        }
        _pendingReroll = null;
        if (result.Error is { } e) { if (e.Code != ErrorCode.StaleVersion) Notice?.Invoke(e.Message); return; }
        Notice?.Invoke($"Rerolled: {result.NewRoll}. {result.GemBalance} gems left · {result.RerollsRemaining} rerolls left this match.");
    }

    public async Task PassAsync()
    {
        if (!State.CanPass) return;
        Report(await _connection.PassTurnAsync(_snapshot.MatchId, _snapshot.Version));
    }

    public async Task UseBoostAsync()
    {
        if (State.BoostStatus != BoostStatus.Available) { Notice?.Invoke(BoostText.Reason(State)); return; }
        var version = _snapshot.Version;
        if (_pendingBoost is not { } p || p.Version != version) _pendingBoost = (version, Guid.NewGuid().ToString());
        BoostResult result;
        try { result = await _connection.UseBoostAsync(_snapshot.MatchId, version, _pendingBoost.Value.Id); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice?.Invoke("Couldn't reach the server. Tap Shield again to retry — only one bottle can be used.");
            return;
        }
        _pendingBoost = null;
        if (result.Error is { } e) { if (e.Code != ErrorCode.StaleVersion) Notice?.Invoke(e.Message); return; }
        Notice?.Invoke($"Shield active until your next turn. {result.BottlesLeft} bottles left.");
    }

    public async Task LeaveAsync()
    {
        if (!_ended) await _connection.LeaveRoomAsync(_snapshot.MatchId);
    }

    public async Task SendReactionAsync(string reactionId)
    {
        var error = await _connection.SendReactionAsync(_snapshot.MatchId, reactionId);
        if (error is { Code: not ErrorCode.RateLimited }) Notice?.Invoke(error.Message);
    }

    public ValueTask DisposeAsync()
    {
        _connection.MatchState -= OnMatchState;
        _connection.MatchEnded -= OnMatchEnded;
        _connection.Reaction -= OnReaction;
        _connection.StatusChanged -= OnStatusChanged;
        return ValueTask.CompletedTask;
    }

    private void Report(ServerError? error)
    {
        // A stale-version rejection means a newer snapshot is on its way; nothing to tell the player.
        if (error is null || error.Code == ErrorCode.StaleVersion) return;
        Notice?.Invoke(error.Message);
    }

    internal void OnMatchState(MatchSnapshot s)
    {
        if (s.MatchId != _snapshot.MatchId || s.Version < _snapshot.Version) return;
        _main.Post(() =>
        {
            if (s.Version < _snapshot.Version) return;
            var isNew = s.Version > _snapshot.Version;
            _snapshot = s;
            State = Build(s);
            if (isNew)
                foreach (var e in s.Events)
                {
                    var text = GameText.Describe(e, State.Seats);
                    if (text.Length > 0 && e.Type != GameEventType.GameWon) Notice?.Invoke(text);
                }
            StateChanged?.Invoke(State);
        });
    }

    internal void OnMatchEnded(MatchEndedInfo info)
    {
        if (info.MatchId != _snapshot.MatchId) return;
        _main.Post(() =>
        {
            if (_ended) return;
            _ended = true;
            Ended?.Invoke(new GameResult(info.Standings, State.Seats, info.YourPlacement, info.CoinsAwarded, info.XpAwarded, true, info.MatchId, IsPrivate || info.IsPrivate, info.Reason,
                info.Mode, info.GemsAwarded, info.LeaguePoints, KindOf(info.IsPrivate || IsPrivate, info.Queue)));
        });
    }

    private void OnReaction(ReactionInfo r) => _main.Post(() => Reaction?.Invoke(r));

    private void OnStatusChanged(ConnectionStatus status) => _main.Post(async () =>
    {
        switch (status)
        {
            case ConnectionStatus.Reconnecting:
                Notice?.Invoke("Connection lost — reconnecting…");
                break;
            case ConnectionStatus.Connected:
                var fresh = await _connection.ResyncAsync(_snapshot.MatchId);
                if (fresh is not null) OnMatchState(fresh);
                Notice?.Invoke("Reconnected.");
                break;
            case ConnectionStatus.Disconnected:
                Notice?.Invoke("Disconnected. Your turns are played automatically until you're back.");
                break;
        }
    });

    private static MatchKind KindOf(bool isPrivate, MatchQueue queue) => isPrivate ? MatchKind.Private : queue == MatchQueue.Casual ? MatchKind.Casual : MatchKind.Ranked;

    private BoardState Build(MatchSnapshot s)
    {
        var seats = s.Seats.Select(x => new SeatView(x.Seat, x.Color, x.DisplayName, x.AvatarId, x.IsBot, x.IsConnected, x.Seat == s.YourSeat,
            s.Pieces[x.Seat].Count(p => p == Core.Board.BoardGeometry.FinishedProgress), x.ShieldActive, x.PublicId, x.FrameId, x.RerollsUsed)).ToList();
        var yourTurn = s.Phase != GamePhase.Finished && s.CurrentSeat == s.YourSeat;
        return new BoardState(s.MatchId, s.Version, s.Phase, s.CurrentSeat, s.PendingRoll, s.LastRoll, s.Pieces, seats, s.Events, s.WinnerSeat,
            yourTurn ? s.LegalPieces : Array.Empty<int>(), s.TurnDeadline, yourTurn && s.Phase == GamePhase.AwaitingRoll,
            s.Mode, KindOf(s.IsPrivate || IsPrivate, s.Queue), s.MaxRerolls, s.RerollStatus, s.YourGems, s.CanPass, s.MaxBoosts, s.BoostStatus, s.YourShieldBottles);
    }
}
