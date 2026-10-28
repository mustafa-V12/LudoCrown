using System.Text.Json;
using System.Text.Json.Serialization;
using LudoCrown.Core.Model;

namespace LudoCrown.Core.Serialization;

/// <summary>
/// JSON (de)serialisation of match state. Used to save/resume offline games after the app is
/// interrupted, and by the server to persist live match snapshots for crash recovery.
/// </summary>
public static class GameStateSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(GameState state) => JsonSerializer.Serialize(state, Options);

    public static GameState Deserialize(string json)
    {
        var state = JsonSerializer.Deserialize<GameState>(json, Options)
                    ?? throw new JsonException("Empty game state.");
        Validate(state);
        return state;
    }

    /// <summary>Rejects corrupted or tampered saved states instead of loading them.</summary>
    public static void Validate(GameState s)
    {
        if (s.Players.Count is < 2 or > 4) throw new JsonException("Invalid player count.");
        if (s.CurrentPlayerIndex < 0 || s.CurrentPlayerIndex >= s.Players.Count) throw new JsonException("Invalid current player.");
        foreach (var p in s.Players)
        {
            if (p.Pieces.Length != Board.BoardGeometry.PiecesPerPlayer) throw new JsonException("Invalid piece count.");
            if (p.Pieces.Any(x => x < Board.BoardGeometry.Yard || x > Board.BoardGeometry.FinishedProgress)) throw new JsonException("Invalid piece progress.");
        }
        if (s.Phase == GamePhase.AwaitingMove && s.PendingRoll is not (>= 1 and <= 6)) throw new JsonException("Missing pending roll.");
        if (s.Players.Select(p => p.Color).Distinct().Count() != s.Players.Count) throw new JsonException("Duplicate colours.");
        if (!GameModes.IsAvailable(s.Rules.Mode)) throw new JsonException("Unknown game mode.");
        if (s.Rules.PiecesToWin is < 1 or > Board.BoardGeometry.PiecesPerPlayer) throw new JsonException("Invalid win condition.");
        if (s.Rules.MaxRerolls is < 0 or > 20) throw new JsonException("Invalid reroll limit.");
        if (s.Players.Any(p => p.RerollsUsed < 0 || p.RerollsUsed > s.Rules.MaxRerolls)) throw new JsonException("Invalid reroll count.");
    }
}
