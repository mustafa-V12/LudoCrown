using LudoCrown.Core.Board;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Model;

namespace LudoCrown.Core.Engine;

/// <summary>
/// The Ludo Crown rules. Stateless and deterministic: every method takes a state and returns a new
/// one, never mutating its input. Dice values are supplied from outside (an <see cref="IDiceRoller"/>),
/// so the online server owns randomness and tests can script exact rolls.
/// </summary>
public static class GameEngine
{
    /// <summary>Rolls the die for <paramref name="seat"/> using <paramref name="dice"/>.</summary>
    public static EngineResult Roll(GameState state, int seat, IDiceRoller dice)
    {
        var error = ValidateTurn(state, seat, GamePhase.AwaitingRoll);
        if (error != ErrorCode.None) return EngineResult.Fail(state, error);
        return ApplyRoll(state, seat, dice.Roll());
    }

    /// <summary>Applies a specific die value. Only trusted code (the engine host / server / tests) may call this.</summary>
    public static EngineResult ApplyRoll(GameState state, int seat, int value)
    {
        var error = ValidateTurn(state, seat, GamePhase.AwaitingRoll);
        if (error != ErrorCode.None) return EngineResult.Fail(state, error);
        if (value is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(value));

        var next = state.Clone();
        var events = new List<GameEvent> { new(GameEventType.DiceRolled, seat, Value: value) };
        next.LastRoll = value;
        next.ConsecutiveSixes = value == 6 ? state.ConsecutiveSixes + 1 : 0;

        if (value == 6 && next.ConsecutiveSixes >= 3 && next.Rules.ThreeSixesForfeitsTurn)
        {
            events.Add(new GameEvent(GameEventType.ThreeSixesForfeit, seat, Value: value));
            PassTurn(next, events);
            return Done(next, events);
        }

        next.PendingRoll = value;
        next.Phase = GamePhase.AwaitingMove;

        if (GetLegalMoves(next).Count == 0)
        {
            // Documented rule: with no legal move the turn passes, even after a 6.
            events.Add(new GameEvent(GameEventType.NoLegalMoves, seat, Value: value));
            PassTurn(next, events);
        }

        return Done(next, events);
    }

    /// <summary>Legal moves for the current player and pending roll. Empty when not awaiting a move.</summary>
    public static IReadOnlyList<LegalMove> GetLegalMoves(GameState state)
    {
        if (state.Phase != GamePhase.AwaitingMove || state.PendingRoll is not int roll) return Array.Empty<LegalMove>();
        var player = state.CurrentPlayer;
        var moves = new List<LegalMove>();
        for (var i = 0; i < player.Pieces.Length; i++)
        {
            var move = TryGetMove(state, player, i, roll);
            if (move is not null) moves.Add(move);
        }
        return moves;
    }

    /// <summary>Moves piece <paramref name="pieceIndex"/> of <paramref name="seat"/> by the pending roll.</summary>
    public static EngineResult Move(GameState state, int seat, int pieceIndex)
    {
        var error = ValidateTurn(state, seat, GamePhase.AwaitingMove);
        if (error != ErrorCode.None) return EngineResult.Fail(state, error);
        if (pieceIndex < 0 || pieceIndex >= BoardGeometry.PiecesPerPlayer) return EngineResult.Fail(state, ErrorCode.InvalidPiece);

        var roll = state.PendingRoll!.Value;
        var move = TryGetMove(state, state.CurrentPlayer, pieceIndex, roll);
        if (move is null) return EngineResult.Fail(state, ErrorCode.IllegalMove);

        var next = state.Clone();
        var player = next.CurrentPlayer;
        var events = new List<GameEvent>();
        player.Pieces[pieceIndex] = move.To;

        events.Add(move.Enters
            ? new GameEvent(GameEventType.PieceEntered, seat, pieceIndex, move.From, move.To, roll)
            : new GameEvent(GameEventType.PieceMoved, seat, pieceIndex, move.From, move.To, roll));

        var captured = false;
        if (BoardGeometry.IsOnMainTrack(move.To))
        {
            var abs = BoardGeometry.AbsoluteIndex(player.Color, move.To);
            if (!BoardGeometry.IsSafeSquare(abs))
            {
                foreach (var other in next.Players.Where(p => p.Seat != player.Seat))
                {
                    for (var j = 0; j < other.Pieces.Length; j++)
                    {
                        var p = other.Pieces[j];
                        if (BoardGeometry.IsOnMainTrack(p) && BoardGeometry.AbsoluteIndex(other.Color, p) == abs)
                        {
                            other.Pieces[j] = BoardGeometry.Yard;
                            captured = true;
                            events.Add(new GameEvent(GameEventType.PieceCaptured, other.Seat, j, p, BoardGeometry.Yard, player.Seat));
                        }
                    }
                }
            }
        }

        var finished = move.To == BoardGeometry.FinishedProgress;
        if (finished) events.Add(new GameEvent(GameEventType.PieceFinished, seat, pieceIndex, move.From, move.To));

        next.PendingRoll = null;

        if (player.HasFinished)
        {
            next.Phase = GamePhase.Finished;
            next.WinnerSeat = player.Seat;
            events.Add(new GameEvent(GameEventType.GameWon, seat));
            return Done(next, events);
        }

        var extra = (roll == 6 && next.Rules.SixGrantsExtraTurn)
                    || (captured && next.Rules.CaptureGrantsExtraTurn)
                    || (finished && next.Rules.FinishGrantsExtraTurn);

        if (extra)
        {
            if (roll != 6) next.ConsecutiveSixes = 0; // only an unbroken run of sixes counts toward the forfeit
            next.Phase = GamePhase.AwaitingRoll;
            events.Add(new GameEvent(GameEventType.ExtraTurn, seat));
        }
        else
        {
            PassTurn(next, events);
        }

        return Done(next, events);
    }

    /// <summary>
    /// Used by the server when a player times out or leaves: passes the turn without moving.
    /// Works in either phase.
    /// </summary>
    public static EngineResult ForfeitTurn(GameState state, int seat)
    {
        if (state.Phase == GamePhase.Finished) return EngineResult.Fail(state, ErrorCode.GameOver);
        if (state.CurrentPlayer.Seat != seat) return EngineResult.Fail(state, ErrorCode.NotYourTurn);
        var next = state.Clone();
        var events = new List<GameEvent>();
        PassTurn(next, events);
        return Done(next, events);
    }

    public static bool IsLegal(GameState state, int seat, int pieceIndex) =>
        ValidateTurn(state, seat, GamePhase.AwaitingMove) == ErrorCode.None
        && GetLegalMoves(state).Any(m => m.Piece == pieceIndex);

    private static LegalMove? TryGetMove(GameState state, PlayerState player, int pieceIndex, int roll)
    {
        var from = player.Pieces[pieceIndex];
        if (from == BoardGeometry.FinishedProgress) return null;

        if (from == BoardGeometry.Yard)
        {
            if (!state.Rules.IsEntryRoll(roll)) return null;
            var captures = WouldCapture(state, player, BoardGeometry.StartProgress);
            return new LegalMove(pieceIndex, from, BoardGeometry.StartProgress, captures, false, true);
        }

        var to = from + roll;
        if (to > BoardGeometry.FinishedProgress) return null; // exact roll needed to finish
        return new LegalMove(pieceIndex, from, to, WouldCapture(state, player, to), to == BoardGeometry.FinishedProgress, false);
    }

    private static bool WouldCapture(GameState state, PlayerState player, int to)
    {
        if (!BoardGeometry.IsOnMainTrack(to)) return false;
        var abs = BoardGeometry.AbsoluteIndex(player.Color, to);
        if (BoardGeometry.IsSafeSquare(abs)) return false;
        return state.Players.Where(p => p.Seat != player.Seat)
            .Any(o => o.Pieces.Any(p => BoardGeometry.IsOnMainTrack(p) && BoardGeometry.AbsoluteIndex(o.Color, p) == abs));
    }

    private static ErrorCode ValidateTurn(GameState state, int seat, GamePhase expected)
    {
        if (state.Phase == GamePhase.Finished) return ErrorCode.GameOver;
        if (state.CurrentPlayer.Seat != seat) return ErrorCode.NotYourTurn;
        if (state.Phase != expected) return ErrorCode.WrongPhase;
        return ErrorCode.None;
    }

    private static void PassTurn(GameState next, List<GameEvent> events)
    {
        var from = next.CurrentPlayer.Seat;
        next.PendingRoll = null;
        next.ConsecutiveSixes = 0;
        next.Phase = GamePhase.AwaitingRoll;
        var count = next.Players.Count;
        for (var step = 1; step <= count; step++)
        {
            var candidate = (next.CurrentPlayerIndex + step) % count;
            if (!next.Players[candidate].HasFinished)
            {
                next.CurrentPlayerIndex = candidate;
                break;
            }
        }
        next.TurnNumber++;
        events.Add(new GameEvent(GameEventType.TurnPassed, from, Value: next.CurrentPlayer.Seat));
    }

    private static EngineResult Done(GameState next, List<GameEvent> events)
    {
        next.Version++;
        return new EngineResult(true, next, events);
    }
}
