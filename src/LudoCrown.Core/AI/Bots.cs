using LudoCrown.Core.Board;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;

namespace LudoCrown.Core.AI;

public enum BotDifficulty
{
    /// <summary>Picks any legal move at random.</summary>
    Easy = 0,
    /// <summary>Greedy: prefers finishing, capturing and entering, ignores danger.</summary>
    Normal = 1,
    /// <summary>Weighs danger from opponents behind it and escapes threats.</summary>
    Hard = 2,
}

/// <summary>Chooses a piece to move. Implementations only ever return a legal move.</summary>
public interface IBotStrategy
{
    /// <summary>Returns the index of the piece to move, or null if there are no legal moves.</summary>
    int? ChooseMove(GameState state);
}

/// <summary>Scoring weights. Exposed so behaviour profiles (aggressive, cautious…) are data, not code.</summary>
public sealed record BotWeights
{
    public double Finish { get; init; } = 100;
    public double Capture { get; init; } = 60;
    public double CapturedProgressFactor { get; init; } = 0.5;
    public double Enter { get; init; } = 45;
    public double ProgressFactor { get; init; } = 1;
    public double LandOnSafe { get; init; } = 20;
    public double EnterHomeColumn { get; init; } = 25;
    /// <summary>Penalty per opponent piece that could hit the destination next turn (0 disables danger).</summary>
    public double DangerPerThreat { get; init; } = 0;
    /// <summary>Bonus per threat removed by moving a piece out of danger.</summary>
    public double EscapePerThreat { get; init; } = 0;
    /// <summary>Random noise added to scores to vary play (0 = fully deterministic).</summary>
    public double Noise { get; init; } = 2;

    public static BotWeights Normal { get; } = new();
    public static BotWeights Hard { get; } = new() { DangerPerThreat = 18, EscapePerThreat = 14, Noise = 0.5 };
    public static BotWeights Aggressive { get; } = new() { Capture = 110, DangerPerThreat = 6, EscapePerThreat = 4 };
    public static BotWeights Cautious { get; } = new() { Capture = 40, LandOnSafe = 40, DangerPerThreat = 28, EscapePerThreat = 22 };
}

public sealed class RandomBot(Random random) : IBotStrategy
{
    public int? ChooseMove(GameState state)
    {
        var moves = GameEngine.GetLegalMoves(state);
        return moves.Count == 0 ? null : moves[random.Next(moves.Count)].Piece;
    }
}

public sealed class HeuristicBot(BotWeights weights, Random random) : IBotStrategy
{
    public int? ChooseMove(GameState state)
    {
        var moves = GameEngine.GetLegalMoves(state);
        if (moves.Count == 0) return null;
        if (moves.Count == 1) return moves[0].Piece;

        return moves
            .Select(m => (m.Piece, Score: Score(state, m) + random.NextDouble() * weights.Noise))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Piece)
            .First().Piece;
    }

    public double Score(GameState state, LegalMove move)
    {
        var player = state.CurrentPlayer;
        double score = 0;

        if (move.Finishes) score += weights.Finish;
        if (move.Enters) score += weights.Enter;
        if (move.Captures)
        {
            score += weights.Capture;
            score += CapturedProgress(state, player, move.To) * weights.CapturedProgressFactor;
        }
        if (!move.Enters) score += (move.To - move.From) * weights.ProgressFactor;
        if (move.From <= BoardGeometry.LastTrackProgress && move.To >= BoardGeometry.HomeColumnStart && !move.Finishes)
            score += weights.EnterHomeColumn;

        if (BoardGeometry.IsOnMainTrack(move.To))
        {
            var abs = BoardGeometry.AbsoluteIndex(player.Color, move.To);
            if (BoardGeometry.IsSafeSquare(abs)) score += weights.LandOnSafe;
        }

        if (weights.DangerPerThreat > 0 || weights.EscapePerThreat > 0)
        {
            // Simulate the move so captured opponents no longer count as threats.
            var after = GameEngine.Move(state, player.Seat, move.Piece);
            var afterState = after.Ok ? after.State : state;
            score -= ThreatsAt(afterState, player.Color, player.Seat, move.To) * weights.DangerPerThreat;
            if (!move.Enters)
                score += ThreatsAt(state, player.Color, player.Seat, move.From) * weights.EscapePerThreat;
        }

        return score;
    }

    private static int CapturedProgress(GameState state, PlayerState player, int to)
    {
        var abs = BoardGeometry.AbsoluteIndex(player.Color, to);
        return state.Players.Where(p => p.Seat != player.Seat)
            .SelectMany(o => o.Pieces.Where(p => BoardGeometry.IsOnMainTrack(p) && BoardGeometry.AbsoluteIndex(o.Color, p) == abs))
            .Sum();
    }

    /// <summary>
    /// Counts opponent pieces that could land on this progress square with a single roll (1–6).
    /// Pieces in the home column, on safe squares, or in the yard cannot be captured.
    /// Opponent yard pieces threaten their own start square (they enter with a 6).
    /// </summary>
    public static int ThreatsAt(GameState state, PlayerColor color, int seat, int progress)
    {
        if (!BoardGeometry.IsOnMainTrack(progress)) return 0;
        var abs = BoardGeometry.AbsoluteIndex(color, progress);
        if (BoardGeometry.IsSafeSquare(abs)) return 0;

        var threats = 0;
        foreach (var o in state.Players.Where(p => p.Seat != seat && !p.HasFinished))
        {
            foreach (var p in o.Pieces)
            {
                if (!BoardGeometry.IsOnMainTrack(p)) continue;
                var oAbs = BoardGeometry.AbsoluteIndex(o.Color, p);
                var distance = (abs - oAbs + BoardGeometry.TrackLength) % BoardGeometry.TrackLength;
                // The opponent must stay on the main track to reach it (not past its own last track square).
                if (distance is >= 1 and <= 6 && p + distance <= BoardGeometry.LastTrackProgress) threats++;
            }
        }
        return threats;
    }
}

public static class BotFactory
{
    public static IBotStrategy Create(BotDifficulty difficulty, Random? random = null)
    {
        random ??= Random.Shared;
        return difficulty switch
        {
            BotDifficulty.Easy => new RandomBot(random),
            BotDifficulty.Normal => new HeuristicBot(BotWeights.Normal, random),
            BotDifficulty.Hard => new HeuristicBot(BotWeights.Hard, random),
            _ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
        };
    }
}
