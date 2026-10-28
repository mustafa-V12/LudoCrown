namespace LudoCrown.Core.Model;

/// <summary>
/// The configurable parts of the Ludo Crown ruleset. Defaults are the documented "Crown Classic"
/// rules (see docs/RULES.md). Online matches always use <see cref="ForMode"/> so every player in a match
/// plays by the same server-chosen rules; the rules are stored inside the match state and cannot change.
/// </summary>
public sealed record RuleSet
{
    /// <summary>The game mode these rules belong to.</summary>
    public GameMode Mode { get; init; } = GameMode.Classic;

    /// <summary>Pieces a player must bring to the crown to win: 4 in Classic, 1 in Quick.</summary>
    public int PiecesToWin { get; init; } = 4;

    /// <summary>Maximum gem rerolls each player may use in one match (7 Classic, 5 Quick).</summary>
    public int MaxRerolls { get; init; } = 7;

    /// <summary>Die values that let a piece leave the yard. Default: only 6.</summary>
    public IReadOnlyList<int> EntryRolls { get; init; } = new[] { 6 };

    /// <summary>A roll of 6 grants another roll after moving.</summary>
    public bool SixGrantsExtraTurn { get; init; } = true;

    /// <summary>Capturing an opponent grants another roll.</summary>
    public bool CaptureGrantsExtraTurn { get; init; } = true;

    /// <summary>Bringing a piece to the crown (finish) grants another roll.</summary>
    public bool FinishGrantsExtraTurn { get; init; } = true;

    /// <summary>Rolling three 6s in a row forfeits the third roll and ends the turn.</summary>
    public bool ThreeSixesForfeitsTurn { get; init; } = true;

    public static RuleSet CrownClassic { get; } = new();

    /// <summary>The standard rules of a mode (from <see cref="GameModes"/>).</summary>
    public static RuleSet ForMode(GameMode mode) => GameModes.Get(mode).Rules;

    public bool IsEntryRoll(int value) => EntryRolls.Contains(value);
}
