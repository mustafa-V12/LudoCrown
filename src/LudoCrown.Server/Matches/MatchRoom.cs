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
    /// <summary>Server-side cache of the player's gem balance (source of truth is the wallet store).</summary>
    public long Gems { get; set; }
    /// <summary>Server-side cache of the player's Shield bottles (source of truth is the economy store).</summary>
    public int ShieldBottles { get; set; }
    /// <summary>Public friend code shown to other players (never the internal account id).</summary>
    public string? PublicId { get; set; }
    public string? FrameId { get; set; }
    /// <summary>The player's chat setting; when off they neither send nor receive quick chat.</summary>
    public bool ChatEnabled { get; set; } = true;
    /// <summary>Recent quick-chat send times for rate limiting.</summary>
    public Queue<DateTimeOffset> RecentChats { get; } = new();

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
    /// <summary>Answers to reroll requests already handled in this match, keyed by user + request id (idempotency).</summary>
    public Dictionary<string, RerollResult> ProcessedRerolls { get; } = new();
    /// <summary>Answers to boost requests already handled, keyed by user + request id (idempotency).</summary>
    public Dictionary<string, BoostResult> ProcessedBoosts { get; } = new();

    /// <summary>Ranked matchmaking match: counts for the league, never allows boosts.</summary>
    public bool IsRanked => Mode == MatchMode.Matchmaking;
    public MatchQueue Queue => Mode == MatchMode.Matchmaking ? MatchQueue.Ranked : MatchQueue.Casual;

    public string MatchIdString => Id.ToString("N");

    public SeatSlot? SeatOf(Guid userId) => Seats.FirstOrDefault(s => s.UserId == userId);

    public IEnumerable<SeatSlot> Humans => Seats.Where(s => s.IsHuman);

    public SeatSlot CurrentSlot => Seats[State.CurrentPlayer.Seat];

    /// <summary>Why the given player can or cannot reroll right now.</summary>
    public RerollStatus RerollStatusFor(SeatSlot? slot)
    {
        if (slot is null || Ended || slot.Abandoned || !slot.IsHuman) return RerollStatus.Unavailable;
        if (State.CurrentPlayer.Seat != slot.Seat) return RerollStatus.NotYourTurn;
        if (State.Phase != GamePhase.AwaitingMove || State.PendingRoll is null) return RerollStatus.RollFirst;
        if (State.Players[slot.Seat].RerollsUsed >= State.Rules.MaxRerolls) return RerollStatus.NoRerollsLeft;
        if (slot.Gems < GameModes.RerollGemCost) return RerollStatus.InsufficientGems;
        return RerollStatus.Available;
    }

    /// <summary>Why the given player can or cannot drink a Shield right now.</summary>
    public BoostStatus BoostStatusFor(SeatSlot? slot)
    {
        if (slot is null || Ended || slot.Abandoned || !slot.IsHuman) return BoostStatus.Unavailable;
        if (State.Rules.MaxBoosts <= 0) return BoostStatus.NotInThisMatch;
        var player = State.Players[slot.Seat];
        if (player.BoostsUsed >= State.Rules.MaxBoosts || player.ShieldActive) return BoostStatus.NoBoostsLeft;
        if (State.CurrentPlayer.Seat != slot.Seat) return BoostStatus.NotYourTurn;
        if (State.Phase != GamePhase.AwaitingRoll) return BoostStatus.OnlyBeforeRolling;
        if (slot.ShieldBottles < 1) return BoostStatus.NoBottles;
        return BoostStatus.Available;
    }

    public MatchSnapshot SnapshotFor(Guid? userId)
    {
        var yourSlot = userId is Guid uid ? SeatOf(uid) : null;
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
            legal,
            State.Rules.Mode,
            State.Rules.PiecesToWin,
            State.Rules.MaxRerolls,
            RerollStatusFor(yourSlot),
            yourSlot is { IsHuman: true } ? (int)yourSlot.Gems : null,
            !Ended && yourSeat == State.CurrentPlayer.Seat && GameEngine.IsHeldWithoutMoves(State),
            Queue,
            Mode == MatchMode.Private,
            State.Rules.MaxBoosts,
            BoostStatusFor(yourSlot),
            yourSlot is { IsHuman: true } ? yourSlot.ShieldBottles : null);
    }

    public IReadOnlyList<SeatInfo> SeatInfos() => Seats.Select(s => new SeatInfo(
        s.Seat,
        State.Players[s.Seat].Color,
        s.DisplayName,
        s.AvatarId,
        s.ServerControlled,
        s.IsBot || (s.Connected && !s.Abandoned),
        State.Players[s.Seat].RerollsUsed,
        s.IsHuman ? s.PublicId : null,
        State.Players[s.Seat].ShieldActive,
        State.Players[s.Seat].BoostsUsed,
        s.IsHuman ? s.FrameId : null)).ToList();
}
