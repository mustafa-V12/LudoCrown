using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;

namespace LudoCrown.Core.Protocol;

/// <summary>Names shared by the SignalR hub and the app so they cannot drift apart.</summary>
public static class HubNames
{
    public const string Path = "/hubs/match";

    // Client → server
    public const string FindMatch = "FindMatch";
    public const string CancelMatchmaking = "CancelMatchmaking";
    public const string CreatePrivateRoom = "CreatePrivateRoom";
    public const string JoinPrivateRoom = "JoinPrivateRoom";
    public const string StartPrivateRoom = "StartPrivateRoom";
    public const string LeaveRoom = "LeaveRoom";
    public const string Roll = "Roll";
    public const string Move = "Move";
    public const string Resync = "Resync";
    public const string SendReaction = "SendReaction";
    public const string RequestRematch = "RequestRematch";
    public const string Reroll = "Reroll";
    public const string PassTurn = "PassTurn";
    public const string UseBoost = "UseBoost";

    // Server → client
    public const string OnMatchState = "MatchState";
    public const string OnRoomUpdated = "RoomUpdated";
    public const string OnMatchmakingStatus = "MatchmakingStatus";
    public const string OnReaction = "Reaction";
    public const string OnMatchEnded = "MatchEnded";
    public const string OnError = "Error";
}

/// <summary>
/// Seat as shown to clients. No internal account ids: <see cref="PublicId"/> is the player's public friend code
/// (null for bots), used to open their public profile.
/// </summary>
public sealed record SeatInfo(int Seat, PlayerColor Color, string DisplayName, string AvatarId, bool IsBot, bool IsConnected, int RerollsUsed = 0,
    string? PublicId = null, bool ShieldActive = false, int BoostsUsed = 0, string? FrameId = null);

/// <summary>
/// Matchmaking queue. Ranked matches count for the league and never allow boost bottles; casual matches allow
/// boosts (1 per player) and give no league points. Private tables are always casual.
/// </summary>
public enum MatchQueue { Ranked = 0, Casual = 1 }

public static class MatchQueues
{
    public static MatchQueue? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "ranked" or "0" => MatchQueue.Ranked,
        "casual" or "1" => MatchQueue.Casual,
        _ => null,
    };
}

/// <summary>Why the boost button is (un)available for the receiving player. Computed by the server.</summary>
public enum BoostStatus
{
    Available,
    NotYourTurn,
    /// <summary>Boosts are drunk at the start of your turn, before rolling.</summary>
    OnlyBeforeRolling,
    /// <summary>Already used the match's boost allowance (or a shield is still active).</summary>
    NoBoostsLeft,
    /// <summary>The player owns no Shield bottle.</summary>
    NoBottles,
    /// <summary>This match does not allow boosts (ranked league match).</summary>
    NotInThisMatch,
    Unavailable,
}

/// <summary>Result of a boost request. On error nothing was consumed.</summary>
public sealed record BoostResult(ServerError? Error, int? BottlesLeft, int BoostsUsed, int BoostsRemaining);

/// <summary>Why the reroll button is (un)available for the receiving player. Computed by the server.</summary>
public enum RerollStatus
{
    /// <summary>A roll is pending, rerolls remain, and the player has enough gems.</summary>
    Available,
    NotYourTurn,
    /// <summary>Nothing to reroll: roll first (or the roll was already used for a move).</summary>
    RollFirst,
    NoRerollsLeft,
    InsufficientGems,
    /// <summary>Match over, player left, or rerolls not offered (offline/bots).</summary>
    Unavailable,
}

/// <summary>Result of a reroll request. On error nothing was charged.</summary>
public sealed record RerollResult(ServerError? Error, int? GemBalance, int RerollsUsed, int RerollsRemaining, int? NewRoll);

/// <summary>Full authoritative match state pushed to clients after every change.</summary>
public sealed record MatchSnapshot(
    string MatchId,
    long Version,
    GamePhase Phase,
    int CurrentSeat,
    int? PendingRoll,
    int? LastRoll,
    int[][] Pieces,
    IReadOnlyList<SeatInfo> Seats,
    IReadOnlyList<GameEvent> Events,
    int? WinnerSeat,
    int YourSeat,
    DateTimeOffset TurnDeadline,
    IReadOnlyList<int> LegalPieces,
    GameMode Mode = GameMode.Classic,
    int PiecesToWin = 4,
    int MaxRerolls = 7,
    RerollStatus RerollStatus = RerollStatus.Unavailable,
    int? YourGems = null,
    bool CanPass = false,
    MatchQueue Queue = MatchQueue.Ranked,
    bool IsPrivate = false,
    int MaxBoosts = 0,
    BoostStatus BoostStatus = BoostStatus.Unavailable,
    int? YourShieldBottles = null);

public sealed record RoomInfo(string RoomId, string? Code, int PlayerCount, IReadOnlyList<SeatInfo> Seats, bool IsHost, bool Started, string? MatchId, GameMode Mode = GameMode.Classic);

public enum MatchmakingState { Searching, Matched, Cancelled, Failed }

public sealed record MatchmakingStatus(MatchmakingState State, int PlayerCount, int PlayersFound, string? MatchId, string? Message, GameMode Mode = GameMode.Classic, MatchQueue Queue = MatchQueue.Ranked);

/// <summary><see cref="LeaguePoints"/> is null when the match did not count for the league (casual, private, fewer than 2 humans).</summary>
public sealed record MatchEndedInfo(string MatchId, IReadOnlyList<int> Standings, int YourPlacement, int CoinsAwarded, int XpAwarded, string Reason, int GemsAwarded = 0, GameMode Mode = GameMode.Classic,
    int? LeaguePoints = null, MatchQueue Queue = MatchQueue.Ranked, bool IsPrivate = false);

public sealed record ServerError(ErrorCode Code, string Message);

/// <summary>Result of a hub call that returns a value or an error.</summary>
public sealed record HubResult<T>(T? Value, ServerError? Error);

/// <summary>A quick-chat message delivered to the other players: a preset phrase or emoji, by id.</summary>
public sealed record ReactionInfo(int Seat, string ReactionId);

public enum QuickChatKind { Phrase, Emoji }

public sealed record QuickChatItem(string Id, QuickChatKind Kind, string Text);

/// <summary>
/// Match chat is preset phrases and emoji only — no free text, so nothing personal can be shared and there is nothing
/// to moderate. Clients send an id; the server validates it, rate-limits, and skips players who blocked each other
/// or turned chat off. Text is shown from this shared table.
/// </summary>
public static class Reactions
{
    public static readonly IReadOnlyList<QuickChatItem> Items = new QuickChatItem[]
    {
        new("hello", QuickChatKind.Phrase, "Hello!"),
        new("good_luck", QuickChatKind.Phrase, "Good luck!"),
        new("nice_move", QuickChatKind.Phrase, "Nice move!"),
        new("well_played", QuickChatKind.Phrase, "Well played!"),
        new("so_close", QuickChatKind.Phrase, "So close!"),
        new("oops", QuickChatKind.Phrase, "Oops!"),
        new("sorry", QuickChatKind.Phrase, "Sorry!"),
        new("thanks", QuickChatKind.Phrase, "Thanks!"),
        new("your_move", QuickChatKind.Phrase, "Your move!"),
        new("thinking", QuickChatKind.Phrase, "Thinking…"),
        new("gg", QuickChatKind.Phrase, "Good game!"),
        new("rematch", QuickChatKind.Phrase, "Rematch?"),
        new("laugh", QuickChatKind.Emoji, "😂"),
        new("wow", QuickChatKind.Emoji, "😮"),
        new("love", QuickChatKind.Emoji, "😍"),
        new("cry", QuickChatKind.Emoji, "😢"),
        new("angry", QuickChatKind.Emoji, "😠"),
        new("cool", QuickChatKind.Emoji, "😎"),
        new("clap", QuickChatKind.Emoji, "👏"),
        new("crown", QuickChatKind.Emoji, "👑"),
    };

    public static readonly IReadOnlyList<string> All = Items.Select(i => i.Id).ToList();

    public static bool IsValid(string? id) => id is not null && All.Contains(id);

    public static string TextFor(string id) => Items.FirstOrDefault(i => i.Id == id)?.Text ?? "";

    /// <summary>At most this many messages per sender in <see cref="WindowSeconds"/>, and never faster than <see cref="MinGapSeconds"/>.</summary>
    public const int MaxPerWindow = 6;
    public const int WindowSeconds = 20;
    public const double MinGapSeconds = 1.5;
}

/// <summary>
/// Shape validation for client commands, applied by the server before touching match state.
/// Turn and legality rules are checked by <see cref="GameEngine"/>.
/// </summary>
public static class CommandValidator
{
    public static ErrorCode ValidateVersion(GameState state, long expectedVersion) =>
        expectedVersion == state.Version ? ErrorCode.None : ErrorCode.StaleVersion;

    public static ErrorCode ValidatePlayerCount(int playerCount) =>
        playerCount is 2 or 4 ? ErrorCode.None : ErrorCode.InvalidRequest;

    /// <summary>Client-generated idempotency key for retryable actions (a GUID).</summary>
    public static ErrorCode ValidateRequestId(string? requestId) =>
        Guid.TryParse(requestId, out var g) && g != Guid.Empty ? ErrorCode.None : ErrorCode.InvalidRequest;

    public static ErrorCode ValidateMatchId(string? matchId) =>
        !string.IsNullOrWhiteSpace(matchId) && matchId.Length <= 64 && matchId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? ErrorCode.None
            : ErrorCode.InvalidRequest;
}

public static class RoomCodes
{
    /// <summary>No 0/O, 1/I/L to avoid confusion when reading a code aloud.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 6;

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Normalises user input (trim, upper-case, drop spaces/dashes). Returns null if invalid.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var cleaned = new string(input.Trim().ToUpperInvariant().Where(c => c != ' ' && c != '-').ToArray());
        return cleaned.Length == Length && cleaned.All(c => Alphabet.Contains(c)) ? cleaned : null;
    }
}

public static partial class DisplayNames
{
    public const int MinLength = 3;
    public const int MaxLength = 16;

    [GeneratedRegex(@"^[\p{L}\p{N}_ .\-]+$")]
    private static partial Regex AllowedChars();

    /// <summary>Validates a display name. Returns null when valid, otherwise a user-facing reason.</summary>
    public static string? Validate(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length < MinLength || n.Length > MaxLength) return $"Name must be {MinLength}–{MaxLength} characters.";
        if (!AllowedChars().IsMatch(n)) return "Use letters, numbers, spaces, dots, dashes or underscores.";
        if (n.Contains("  ")) return "Avoid double spaces.";
        return null;
    }
}

public static class Avatars
{
    /// <summary>Built-in avatar ids (bundled artwork). No photo uploads, so no image moderation needed.</summary>
    public static readonly IReadOnlyList<string> All = new[] { "crown", "knight", "rook", "jester", "lion", "swan", "star", "gem" };
    public static bool IsValid(string? id) => id is not null && All.Contains(id);
}
