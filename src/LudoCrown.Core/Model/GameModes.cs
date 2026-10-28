namespace LudoCrown.Core.Model;

/// <summary>
/// Selectable game modes. The numeric values are stable (stored in saves and the database).
/// The registry has room for <see cref="GameModes.MaxModes"/> modes; only modes that are fully implemented and
/// tested are listed in <see cref="GameModes.All"/>.
/// </summary>
public enum GameMode
{
    Classic = 0,
    Quick = 1,
}

/// <summary>Everything the UI and server need to know about a mode. Rules live in <see cref="Rules"/>, not in UI code.</summary>
public sealed record GameModeInfo(
    GameMode Mode,
    string Name,
    string Tagline,
    string WinCondition,
    string Description,
    RuleSet Rules)
{
    public string Id => Mode.ToString().ToLowerInvariant();
    public int MaxRerolls => Rules.MaxRerolls;
    public int PiecesToWin => Rules.PiecesToWin;
}

public static class GameModes
{
    /// <summary>The product supports up to five modes in total.</summary>
    public const int MaxModes = 5;

    /// <summary>Gems charged for one reroll.</summary>
    public const int RerollGemCost = 1;

    public static readonly GameModeInfo Classic = new(
        GameMode.Classic,
        "Classic",
        "The full royal race",
        "Bring all 4 pieces to the crown to win.",
        "Standard Ludo Crown rules. Enter with a 6, capture by landing on opponents, stars and start squares are safe. " +
        "The first player with all four pieces on the crown wins. Up to 7 gem rerolls per player in online matches.",
        new RuleSet { Mode = GameMode.Classic, PiecesToWin = 4, MaxRerolls = 7 });

    public static readonly GameModeInfo Quick = new(
        GameMode.Quick,
        "Quick",
        "First crown wins",
        "Bring just 1 piece to the crown to win.",
        "Same board and rules as Classic, but the match ends as soon as any player brings one piece to the crown. " +
        "Great for short games. Up to 5 gem rerolls per player in online matches.",
        new RuleSet { Mode = GameMode.Quick, PiecesToWin = 1, MaxRerolls = 5 });

    /// <summary>Implemented, selectable modes in display order. Never contains placeholders.</summary>
    public static readonly IReadOnlyList<GameModeInfo> All = new[] { Classic, Quick };

    public static GameModeInfo Get(GameMode mode) =>
        All.FirstOrDefault(m => m.Mode == mode) ?? throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown game mode.");

    public static bool IsAvailable(GameMode mode) => All.Any(m => m.Mode == mode);

    /// <summary>Parses a mode id from a client ("classic", "Quick", "1"). Returns null for unknown or unavailable modes.</summary>
    public static GameMode? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 20) return null;
        if (int.TryParse(value, out var n)) return IsAvailable((GameMode)n) ? (GameMode)n : null;
        return Enum.TryParse<GameMode>(value.Trim(), ignoreCase: true, out var mode) && IsAvailable(mode) ? mode : null;
    }
}

/// <summary>Boost bottles: inventory items that give a small, temporary, clearly described advantage in casual matches.</summary>
public static class Boosts
{
    public const string ShieldId = "boost_shield";
    public const string ShieldName = "Shield Bottle";
    public const string ShieldEffect =
        "Drink before rolling on your turn. Until your next turn starts, none of your pieces can be captured — " +
        "opponents who land on them just share the square. Dice, moves and turn order are unchanged.";
    public const string ShieldLimits = "1 per match. Casual matches and private tables only — never in ranked league matches or offline.";
    /// <summary>Boosts per player per match where boosts are allowed.</summary>
    public const int PerMatchInCasual = 1;
}
