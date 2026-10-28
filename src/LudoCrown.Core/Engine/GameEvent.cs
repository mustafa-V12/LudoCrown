namespace LudoCrown.Core.Engine;

public enum GameEventType
{
    DiceRolled,
    /// <summary>The pending roll was replaced by a gem reroll. From = old value, Value = new value.</summary>
    DiceRerolled,
    NoLegalMoves,
    ThreeSixesForfeit,
    PieceEntered,
    PieceMoved,
    PieceCaptured,
    PieceFinished,
    ExtraTurn,
    TurnPassed,
    GameWon,
    /// <summary>A player drank a Shield bottle. Seat = that player.</summary>
    ShieldActivated,
    /// <summary>A Shield ran out because its owner's next turn began. Seat = owner.</summary>
    ShieldExpired,
    /// <summary>A piece landed on a shielded piece, which was not captured. Seat = shielded owner, Piece = their piece, Value = the moving seat.</summary>
    CaptureBlockedByShield,
}

/// <summary>
/// Something that happened as a result of an action. Events drive animation and sound on the
/// client but never change state themselves — state always comes from <see cref="Model.GameState"/>.
/// </summary>
public sealed record GameEvent(GameEventType Type, int Seat, int Piece = -1, int From = 0, int To = 0, int Value = 0);

public enum ErrorCode
{
    None = 0,
    NotYourTurn,
    WrongPhase,
    InvalidPiece,
    IllegalMove,
    GameOver,
    StaleVersion,
    UnknownMatch,
    NotInMatch,
    InvalidRoomCode,
    RoomFull,
    RoomNotFound,
    RateLimited,
    InvalidRequest,
    Unauthorized,
    /// <summary>The player has used all rerolls allowed in this mode.</summary>
    NoRerollsLeft,
    /// <summary>Not enough gems for a reroll.</summary>
    InsufficientGems,
    /// <summary>Reroll is not possible right now (e.g. no roll to replace, or the move was already made).</summary>
    RerollNotAllowed,
    /// <summary>The mode is unknown or not available.</summary>
    InvalidMode,
    /// <summary>Boosts are not allowed in this match (ranked league, offline) or not at this moment (only before rolling).</summary>
    BoostNotAllowed,
    /// <summary>The player has used all boosts allowed in this match.</summary>
    NoBoostsLeft,
    /// <summary>The player owns no boost bottle of that kind.</summary>
    NoBoostItem,
    /// <summary>Chat is turned off (by the player, or on this server).</summary>
    ChatDisabled,
}

public sealed record EngineResult(bool Ok, Model.GameState State, IReadOnlyList<GameEvent> Events, ErrorCode Error = ErrorCode.None)
{
    public static EngineResult Fail(Model.GameState unchanged, ErrorCode error) => new(false, unchanged, Array.Empty<GameEvent>(), error);
}

/// <summary>A legal move for the pending roll.</summary>
public sealed record LegalMove(int Piece, int From, int To, bool Captures, bool Finishes, bool Enters);
