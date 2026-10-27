namespace LudoCrown.Core.Model;

/// <summary>
/// The configurable parts of the Ludo Crown ruleset. Defaults are the documented "Crown Classic"
/// rules (see docs/RULES.md). Online matches always use <see cref="CrownClassic"/> so every player
/// plays by the same rules.
/// </summary>
public sealed record RuleSet
{
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

    public bool IsEntryRoll(int value) => EntryRolls.Contains(value);
}
