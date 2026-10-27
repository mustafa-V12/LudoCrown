using LudoCrown.Core.Model;

namespace LudoCrown.Core.Tests;

/// <summary>Helpers for building precise board positions in tests.</summary>
internal static class TestBoards
{
    public static GameState TwoPlayer() => GameSetup.NewGame("t2", new[]
    {
        new SeatSetup("a", "Alice", PlayerKind.Human),
        new SeatSetup("b", "Bob", PlayerKind.Human),
    });

    public static GameState FourPlayer() => GameSetup.NewGame("t4", new[]
    {
        new SeatSetup("a", "Alice", PlayerKind.Human),
        new SeatSetup("b", "Bob", PlayerKind.Bot),
        new SeatSetup("c", "Cara", PlayerKind.Bot),
        new SeatSetup("d", "Dan", PlayerKind.Bot),
    });

    /// <summary>Sets pieces for a seat (unspecified pieces stay in the yard).</summary>
    public static GameState WithPieces(this GameState s, int seat, params int[] pieces)
    {
        var copy = s.Clone();
        var p = copy.PlayerBySeat(seat)!;
        for (var i = 0; i < 4; i++) p.Pieces[i] = i < pieces.Length ? pieces[i] : -1;
        return copy;
    }

    public static GameState WithTurn(this GameState s, int seat)
    {
        var copy = s.Clone();
        copy.CurrentPlayerIndex = copy.Players.FindIndex(p => p.Seat == seat);
        copy.Phase = GamePhase.AwaitingRoll;
        copy.PendingRoll = null;
        return copy;
    }
}
