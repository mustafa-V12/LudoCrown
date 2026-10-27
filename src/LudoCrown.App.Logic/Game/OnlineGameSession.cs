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
            Ended?.Invoke(new GameResult(info.Standings, State.Seats, info.YourPlacement, info.CoinsAwarded, info.XpAwarded, true, info.MatchId, IsPrivate, info.Reason));
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

    private static BoardState Build(MatchSnapshot s)
    {
        var seats = s.Seats.Select(x => new SeatView(x.Seat, x.Color, x.DisplayName, x.AvatarId, x.IsBot, x.IsConnected, x.Seat == s.YourSeat,
            s.Pieces[x.Seat].Count(p => p == Core.Board.BoardGeometry.FinishedProgress))).ToList();
        var yourTurn = s.Phase != GamePhase.Finished && s.CurrentSeat == s.YourSeat;
        return new BoardState(s.MatchId, s.Version, s.Phase, s.CurrentSeat, s.PendingRoll, s.LastRoll, s.Pieces, seats, s.Events, s.WinnerSeat,
            yourTurn ? s.LegalPieces : Array.Empty<int>(), s.TurnDeadline, yourTurn && s.Phase == GamePhase.AwaitingRoll);
    }
}
