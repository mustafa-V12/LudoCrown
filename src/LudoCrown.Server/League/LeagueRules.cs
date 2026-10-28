using LudoCrown.Server.Data;

namespace LudoCrown.Server.League;

public sealed record Division(int Index, string Name, string Crest);

public sealed record RewardBand(string Label, int FromRank, int ToRank);

/// <summary>
/// The Ludo Crown league: six original divisions, seasons of fixed length, groups of up to 30 players per division,
/// server-owned points from ranked results, promotion/demotion at season end, and fixed (non-random) rewards.
/// Every number here is documented in docs/LEAGUE.md — change both together.
/// </summary>
public static class LeagueRules
{
    public static readonly IReadOnlyList<Division> Divisions = new Division[]
    {
        new(0, "Squire", "shield"),
        new(1, "Knight", "helm"),
        new(2, "Baron", "banner"),
        new(3, "Earl", "seal"),
        new(4, "Duke", "orb"),
        new(5, "Monarch", "crown"),
    };

    public const int TopDivision = 5;
    public const int GroupSize = 30;
    public const int PromoteTop = 5;
    public const int DemoteBottom = 5;
    /// <summary>Ranked matches a player must finish in the season to be promoted.</summary>
    public const int MinMatchesForPromotion = 5;
    /// <summary>Demotion only applies in groups at least this big, so small early groups never demote anyone.</summary>
    public const int MinGroupSizeForDemotion = 15;
    /// <summary>League points need at least this many human players in the match (no points for matches against bots only).</summary>
    public const int MinHumansForPoints = 2;
    public const int DefaultSeasonDays = 14;

    public static Division DivisionAt(int index) => Divisions[Math.Clamp(index, 0, TopDivision)];

    /// <summary>Points for one ranked result. Totals never drop below 0. Leaving a match costs more than losing it.</summary>
    public static int PointsFor(int playerCount, int placement, ParticipantResult result)
    {
        if (result == ParticipantResult.Abandoned) return -25;
        return (playerCount, placement) switch
        {
            (2, 1) => 20,
            (2, _) => -10,
            (_, 1) => 30,
            (_, 2) => 10,
            (_, 3) => -5,
            _ => -15,
        };
    }

    public static readonly IReadOnlyList<RewardBand> Bands = new RewardBand[]
    {
        new("#1", 1, 1),
        new("#2–5", 2, 5),
        new("#6–10", 6, 10),
    };

    // [division, band] — coins, gems, shield bottles. Monotonic: a higher division or better rank never pays less.
    private static readonly LeagueReward[,] Table =
    {
        { new(1500, 5, 0), new(800, 3, 0), new(400, 1, 0) },    // Squire
        { new(2500, 8, 1), new(1300, 4, 0), new(600, 2, 0) },   // Knight
        { new(4000, 12, 1), new(2000, 6, 1), new(900, 3, 0) },  // Baron
        { new(6000, 18, 2), new(3000, 9, 1), new(1300, 4, 0) }, // Earl
        { new(8500, 25, 2), new(4200, 12, 1), new(1800, 6, 1) },// Duke
        { new(12000, 35, 3), new(6000, 18, 2), new(2500, 8, 1) },// Monarch
    };

    /// <summary>Season reward for a final group rank (1-based). Only the top 10 of a group who played at least once are rewarded.</summary>
    public static LeagueReward? RewardFor(int division, int groupRank, int matchesPlayed)
    {
        if (matchesPlayed <= 0) return null;
        var band = Bands.ToList().FindIndex(b => groupRank >= b.FromRank && groupRank <= b.ToRank);
        return band < 0 ? null : Table[Math.Clamp(division, 0, TopDivision), band];
    }

    public static LeagueReward RewardCell(int division, int band) => Table[division, band];

    /// <summary>Ordering inside a group: points, then wins, then whoever reached their total first, then a stable id order.</summary>
    public static IReadOnlyList<StandingRecord> Rank(IEnumerable<StandingRecord> group) => group
        .OrderByDescending(s => s.Points)
        .ThenByDescending(s => s.Wins)
        .ThenBy(s => s.LastPointsAt ?? DateTimeOffset.MaxValue)
        .ThenBy(s => s.UserId)
        .ToList();

    /// <summary>Final results for one group: rank, promotion/demotion and reward for every member.</summary>
    public static IReadOnlyList<SeasonResult> ResultsForGroup(IEnumerable<StandingRecord> group)
    {
        var ranked = Rank(group);
        var n = ranked.Count;
        return ranked.Select((s, i) =>
        {
            var rank = i + 1;
            var outcome = SeasonOutcome.Stayed;
            if (s.Division < TopDivision && rank <= PromoteTop && s.MatchesPlayed >= MinMatchesForPromotion) outcome = SeasonOutcome.Promoted;
            else if (s.Division > 0 && n >= MinGroupSizeForDemotion && rank > n - DemoteBottom) outcome = SeasonOutcome.Demoted;
            var next = outcome switch
            {
                SeasonOutcome.Promoted => s.Division + 1,
                SeasonOutcome.Demoted => s.Division - 1,
                _ => s.Division,
            };
            return new SeasonResult(s.UserId, rank, outcome, next, RewardFor(s.Division, rank, s.MatchesPlayed));
        }).ToList();
    }
}
