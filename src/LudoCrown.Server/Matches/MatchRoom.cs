using LudoCrown.Core.AI;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Data;

namespace LudoCrown.Server.Matches;

/// <summary>Server-side view of one seat. Holds connection and abandonment state that clients cannot change.</summary>
public sealed class SeatSlot
{
    public int Seat { get; init; }
    /// <summary>Null for bot seats.</summary>
    public Guid? UserId { get; init; }
    public string DisplayName { get; set; } = "";
    public string AvatarId { get; set; } = "crown";
    public bool IsBot { get; init; }
    public bool Connected { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    /// <summary>Human who left or timed out repeatedly; a bot plays the seat for the rest of the match.</summary>
    public bool Abandoned { get; set; }
    public int MissedTurns { get; set; }
    public int LastMissedTurnNumber { get; set; } = -1;
    public int Captures { get; set; }
    public DateTimeOffset LastReactionAt { get; set; } = DateTimeOffset.MinValue;

    public bool IsHuman => !IsBot && UserId is not null;
    /// <summary>True when the server plays this seat (bots and abandoned humans).</summary>
    public bool ServerControlled => IsBot || Abandoned;
}

/// <summary>A live match. All access goes through <see cref="Gate"/> so commands are applied one at a time.</summary>
public sealed class MatchRoom
{
    public required Guid Id { get; init; }
    public required MatchMode Mode { get; init; }
    public string? RoomCode { get; init; }
    public required GameState State { get; set; }
    public required SeatSlot[] Seats { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset Deadline { get; set; }
    public IReadOnlyList<GameEvent> LastEvents { get; set; } = Array.Empty<GameEvent>();
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public bool Ended { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public IReadOnlyList<int> FinalStandings { get; set; } = Array.Empty<int>();
    public required IBotStrategy Bot { get; init; }

    public string MatchIdString => Id.ToString("N");

    public SeatSlot? SeatOf(Guid userId) => Seats.FirstOrDefault(s => s.UserId == userId);

    public IEnumerable<SeatSlot> Humans => Seats.Where(s => s.IsHuman);

    public SeatSlot CurrentSlot => Seats[State.CurrentPlayer.Seat];

    public MatchSnapshot SnapshotFor(Guid? userId)
    {
        var yourSeat = userId is Guid id ? SeatOf(id)?.Seat ?? -1 : -1;
        var legal = !Ended && yourSeat == State.CurrentPlayer.Seat && State.Phase == GamePhase.AwaitingMove
            ? GameEngine.GetLegalMoves(State).Select(m => m.Piece).ToList()
            : new List<int>();
        return new MatchSnapshot(
            MatchIdString,
            State.Version,
            State.Phase,
            State.CurrentPlayer.Seat,
            State.PendingRoll,
            State.LastRoll,
            State.Players.Select(p => (int[])p.Pieces.Clone()).ToArray(),
            SeatInfos(),
            LastEvents,
            State.WinnerSeat,
            yourSeat,
            Deadline,
            legal);
    }

    public IReadOnlyList<SeatInfo> SeatInfos() => Seats.Select(s => new SeatInfo(
        s.Seat,
        State.Players[s.Seat].Color,
        s.DisplayName,
        s.AvatarId,
        s.ServerControlled,
        s.IsBot || (s.Connected && !s.Abandoned))).ToList();
}
