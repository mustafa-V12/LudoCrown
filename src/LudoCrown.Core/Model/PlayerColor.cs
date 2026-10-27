namespace LudoCrown.Core.Model;

/// <summary>
/// Player colours in clockwise turn order around the board.
/// Ruby sits bottom-left, Emerald top-left, Topaz top-right, Sapphire bottom-right.
/// The numeric value is the colour's quarter index used by <see cref="Board.BoardGeometry"/>.
/// </summary>
public enum PlayerColor
{
    Ruby = 0,
    Emerald = 1,
    Topaz = 2,
    Sapphire = 3,
}

public enum PlayerKind
{
    Human = 0,
    Bot = 1,
    Remote = 2,
}

public enum GamePhase
{
    AwaitingRoll = 0,
    AwaitingMove = 1,
    Finished = 2,
}
