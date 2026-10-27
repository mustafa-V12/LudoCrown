using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.Game;

public sealed record SeatView(int Seat, PlayerColor Color, string Name, string AvatarId, bool IsBot, bool IsConnected, bool IsLocalHuman, int FinishedCount);

/// <summary>Everything the board screen needs to draw one moment of a match. Immutable.</summary>
public sealed record BoardState(
    string MatchId,
    long Version,
    GamePhase Phase,
    int CurrentSeat,
    int? PendingRoll,
    int? LastRoll,
    int[][] Pieces,
    IReadOnlyList<SeatView> Seats,
    IReadOnlyList<GameEvent> Events,
    int? WinnerSeat,
    IReadOnlyList<int> LegalPieces,
    DateTimeOffset? TurnDeadline,
    bool CanRoll)
{
    public SeatView CurrentSeatView => Seats[CurrentSeat];
    public bool IsFinished => Phase == GamePhase.Finished;
}

public sealed record GameResult(
    IReadOnlyList<int> Standings,
    IReadOnlyList<SeatView> Seats,
    int? YourPlacement,
    int CoinsAwarded,
    int XpAwarded,
    bool IsOnline,
    string? MatchId,
    bool IsPrivate,
    string Reason);

/// <summary>
/// A match as seen by the board screen. Offline sessions run the rules engine locally; online sessions
/// forward intents to the server and display its authoritative snapshots. The screen code is identical.
/// </summary>
public interface IGameSession : IAsyncDisposable
{
    bool IsOnline { get; }
    BoardState State { get; }
    event Action<BoardState>? StateChanged;
    event Action<GameResult>? Ended;
    /// <summary>Short user-facing messages ("No moves with a 3", "Reconnecting…").</summary>
    event Action<string>? Notice;
    event Action<ReactionInfo>? Reaction;

    Task StartAsync();
    Task RollAsync();
    Task MoveAsync(int pieceIndex);
    Task LeaveAsync();
    Task SendReactionAsync(string reactionId);
}

public static class GameText
{
    public static string ColorName(PlayerColor c) => c.ToString();

    public static string Describe(GameEvent e, IReadOnlyList<SeatView> seats) => e.Type switch
    {
        GameEventType.NoLegalMoves => $"{seats[e.Seat].Name} rolled {e.Value} — no moves.",
        GameEventType.ThreeSixesForfeit => $"Three sixes! {seats[e.Seat].Name}'s turn is forfeited.",
        GameEventType.PieceCaptured => $"{seats[e.Value].Name} captured {seats[e.Seat].Name}!",
        GameEventType.PieceFinished => $"{seats[e.Seat].Name} crowned a piece!",
        GameEventType.GameWon => $"{seats[e.Seat].Name} wins the crown!",
        _ => "",
    };
}
