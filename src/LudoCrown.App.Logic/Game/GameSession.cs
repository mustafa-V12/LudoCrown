using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.Game;

/// <summary>One seat at the table. <see cref="PublicId"/> (online humans only) opens the player's public profile.</summary>
public sealed record SeatView(int Seat, PlayerColor Color, string Name, string AvatarId, bool IsBot, bool IsConnected, bool IsLocalHuman, int FinishedCount,
    bool ShieldActive = false, string? PublicId = null, string? FrameId = null, int RerollsUsed = 0);

/// <summary>How the match was set up, for labels: offline, ranked/casual matchmaking, or a private table.</summary>
public enum MatchKind { Offline, Ranked, Casual, Private }

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
    bool CanRoll,
    GameMode Mode = GameMode.Classic,
    MatchKind Kind = MatchKind.Offline,
    int MaxRerolls = 0,
    RerollStatus RerollStatus = RerollStatus.Unavailable,
    int? Gems = null,
    bool CanPass = false,
    int MaxBoosts = 0,
    BoostStatus BoostStatus = BoostStatus.Unavailable,
    int? ShieldBottles = null)
{
    public GameModeInfo ModeInfo => GameModes.Get(Mode);
    /// <summary>The local player's seat (first local human), or null when spectating/bots only.</summary>
    public SeatView? You => Seats.FirstOrDefault(s => s.IsLocalHuman);
    public int RerollsUsed => You?.RerollsUsed ?? 0;
    public int RerollsLeft => Math.Max(0, MaxRerolls - RerollsUsed);
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
    string Reason,
    GameMode Mode = GameMode.Classic,
    int GemsAwarded = 0,
    int? LeaguePoints = null,
    MatchKind Kind = MatchKind.Offline);

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
    /// <summary>Spends a gem to replace the pending roll (online only). Feedback arrives via <see cref="Notice"/>.</summary>
    Task RerollAsync();
    /// <summary>Ends the turn after a held roll with no legal move (online only).</summary>
    Task PassAsync();
    /// <summary>Drinks a Shield bottle before rolling (casual online matches only).</summary>
    Task UseBoostAsync();
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
        GameEventType.DiceRerolled => $"{seats[e.Seat].Name} rerolled {e.From} → {e.Value} (1 gem).",
        GameEventType.ShieldActivated => $"{seats[e.Seat].Name} drank a Shield — their pieces can't be captured until their next turn.",
        GameEventType.ShieldExpired => $"{seats[e.Seat].Name}'s Shield has worn off.",
        GameEventType.CaptureBlockedByShield => $"{seats[e.Seat].Name}'s Shield blocked a capture!",
        _ => "",
    };
}

/// <summary>User-facing text for the reroll control, computed from server-provided state.</summary>
public static class RerollText
{
    public static string Reason(BoardState b) => b.RerollStatus switch
    {
        RerollStatus.Available => $"Reroll for {GameModes.RerollGemCost} gem — {b.RerollsLeft} of {b.MaxRerolls} left this match.",
        RerollStatus.NotYourTurn => "You can reroll only on your own turn.",
        RerollStatus.RollFirst => "Roll first — a reroll replaces a roll you haven't used yet.",
        RerollStatus.NoRerollsLeft => $"You've used all {b.MaxRerolls} rerolls for this {b.ModeInfo.Name} match.",
        RerollStatus.InsufficientGems => "Not enough gems. Get more in the Store.",
        _ => b.Kind == MatchKind.Offline ? "Rerolls use gems and are available in online matches." : "Rerolls aren't available right now.",
    };
}

/// <summary>User-facing text for the Shield boost control.</summary>
public static class BoostText
{
    public static string Reason(BoardState b) => b.BoostStatus switch
    {
        BoostStatus.Available => "Drink a Shield: none of your pieces can be captured until your next turn starts. 1 per match.",
        BoostStatus.NotYourTurn => "Shields are used at the start of your own turn.",
        BoostStatus.OnlyBeforeRolling => "Use a Shield before you roll.",
        BoostStatus.NoBoostsLeft => "You've used this match's Shield.",
        BoostStatus.NoBottles => "You have no Shield bottles. Get one in the Store.",
        BoostStatus.NotInThisMatch => "Boosts are off in ranked league matches.",
        _ => b.Kind == MatchKind.Offline ? "Boosts are available in casual online matches and private tables." : "Boosts aren't available right now.",
    };
}
