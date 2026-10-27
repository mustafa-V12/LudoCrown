using LudoCrown.Core.Board;

namespace LudoCrown.Core.Model;

/// <summary>One seat at the table. Pieces hold progress values (see <see cref="BoardGeometry"/>).</summary>
public sealed class PlayerState
{
    public int Seat { get; set; }
    public PlayerColor Color { get; set; }
    public string DisplayName { get; set; } = "";
    public PlayerKind Kind { get; set; }
    /// <summary>Opaque player id: an account id online, or a local id offline.</summary>
    public string PlayerId { get; set; } = "";
    public int[] Pieces { get; set; } = Enumerable.Repeat(BoardGeometry.Yard, BoardGeometry.PiecesPerPlayer).ToArray();

    public bool HasFinished => Pieces.All(p => p == BoardGeometry.FinishedProgress);
    public int FinishedCount => Pieces.Count(p => p == BoardGeometry.FinishedProgress);
    /// <summary>Total progress, used for ranking players who did not win.</summary>
    public int TotalProgress => Pieces.Sum(p => p < 0 ? 0 : p + 1);

    public PlayerState Clone() => new()
    {
        Seat = Seat,
        Color = Color,
        DisplayName = DisplayName,
        Kind = Kind,
        PlayerId = PlayerId,
        Pieces = (int[])Pieces.Clone(),
    };
}

/// <summary>
/// The single source of truth for a match. Engine operations never mutate an input state;
/// they return a new state with <see cref="Version"/> incremented.
/// </summary>
public sealed class GameState
{
    public string MatchId { get; set; } = "";
    public RuleSet Rules { get; set; } = RuleSet.CrownClassic;
    public List<PlayerState> Players { get; set; } = new();
    /// <summary>Index into <see cref="Players"/> of the player whose turn it is.</summary>
    public int CurrentPlayerIndex { get; set; }
    public GamePhase Phase { get; set; } = GamePhase.AwaitingRoll;
    /// <summary>The die value waiting to be used (set while <see cref="Phase"/> is AwaitingMove).</summary>
    public int? PendingRoll { get; set; }
    /// <summary>The most recent die value, kept for display after the move is made.</summary>
    public int? LastRoll { get; set; }
    public int ConsecutiveSixes { get; set; }
    public int TurnNumber { get; set; } = 1;
    /// <summary>Monotonic version. Online clients send the version they acted on to reject stale commands.</summary>
    public long Version { get; set; }
    public int? WinnerSeat { get; set; }

    public PlayerState CurrentPlayer => Players[CurrentPlayerIndex];

    public PlayerState? PlayerBySeat(int seat) => Players.FirstOrDefault(p => p.Seat == seat);

    public GameState Clone() => new()
    {
        MatchId = MatchId,
        Rules = Rules,
        Players = Players.Select(p => p.Clone()).ToList(),
        CurrentPlayerIndex = CurrentPlayerIndex,
        Phase = Phase,
        PendingRoll = PendingRoll,
        LastRoll = LastRoll,
        ConsecutiveSixes = ConsecutiveSixes,
        TurnNumber = TurnNumber,
        Version = Version,
        WinnerSeat = WinnerSeat,
    };

    /// <summary>Seats ordered by final placement: winner first, then by progress.</summary>
    public IReadOnlyList<int> Standings() => Players
        .OrderByDescending(p => p.Seat == WinnerSeat)
        .ThenByDescending(p => p.FinishedCount)
        .ThenByDescending(p => p.TotalProgress)
        .ThenBy(p => p.Seat)
        .Select(p => p.Seat)
        .ToList();
}

public sealed record SeatSetup(string PlayerId, string DisplayName, PlayerKind Kind);

public static class GameSetup
{
    /// <summary>
    /// Creates a new match. Two players sit opposite each other (Ruby vs Topaz); three use Ruby,
    /// Emerald and Topaz; four use every colour. Seats are numbered in turn order.
    /// </summary>
    public static GameState NewGame(string matchId, IReadOnlyList<SeatSetup> seats, RuleSet? rules = null)
    {
        if (seats.Count is < 2 or > 4)
            throw new ArgumentException("Ludo Crown supports 2 to 4 players.", nameof(seats));

        var colors = seats.Count switch
        {
            2 => new[] { PlayerColor.Ruby, PlayerColor.Topaz },
            3 => new[] { PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz },
            _ => new[] { PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz, PlayerColor.Sapphire },
        };

        return new GameState
        {
            MatchId = matchId,
            Rules = rules ?? RuleSet.CrownClassic,
            Players = seats.Select((s, i) => new PlayerState
            {
                Seat = i,
                Color = colors[i],
                DisplayName = s.DisplayName,
                Kind = s.Kind,
                PlayerId = s.PlayerId,
            }).ToList(),
        };
    }
}
