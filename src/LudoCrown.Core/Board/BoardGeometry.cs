using LudoCrown.Core.Model;

namespace LudoCrown.Core.Board;

/// <summary>A position on the 15x15 board grid, in cell units. (0,0) is the top-left cell.</summary>
public readonly record struct GridPoint(double X, double Y)
{
    /// <summary>Rotates 90° clockwise around the board centre (screen coordinates, y grows down).</summary>
    public GridPoint RotateClockwise() => new(BoardGeometry.GridSize - 1 - Y, X);
}

/// <summary>
/// Pure board math shared by the rules engine and the renderer.
///
/// Piece progress is measured relative to the owner's start square:
///   -1        piece is in the yard
///   0..50     main track (0 = the owner's start square)
///   51..55    the owner's home column (5 squares)
///   56        finished (on the crown in the centre)
/// </summary>
public static class BoardGeometry
{
    public const int GridSize = 15;
    public const int TrackLength = 52;
    public const int PiecesPerPlayer = 4;

    public const int Yard = -1;
    public const int StartProgress = 0;
    public const int LastTrackProgress = 50;
    public const int HomeColumnStart = 51;
    public const int FinishedProgress = 56;
    public const int QuarterLength = 13;

    /// <summary>Absolute track indices of the star safe squares (one per quarter).</summary>
    public static readonly IReadOnlySet<int> StarSquares = new HashSet<int> { 8, 21, 34, 47 };

    /// <summary>Absolute track indices of the four start squares (also safe).</summary>
    public static readonly IReadOnlySet<int> StartSquares = new HashSet<int> { 0, 13, 26, 39 };

    private static readonly GridPoint[] TrackCells = BuildTrack();

    public static int StartIndex(PlayerColor color) => (int)color * QuarterLength;

    /// <summary>Absolute main-track index for a piece of <paramref name="color"/> at <paramref name="progress"/> (0..50).</summary>
    public static int AbsoluteIndex(PlayerColor color, int progress)
    {
        if (progress < StartProgress || progress > LastTrackProgress)
            throw new ArgumentOutOfRangeException(nameof(progress), progress, "Only main-track progress has an absolute index.");
        return (StartIndex(color) + progress) % TrackLength;
    }

    public static bool IsOnMainTrack(int progress) => progress >= StartProgress && progress <= LastTrackProgress;

    public static bool IsSafeSquare(int absoluteIndex) =>
        StarSquares.Contains(absoluteIndex) || StartSquares.Contains(absoluteIndex);

    /// <summary>Grid cell of an absolute main-track index.</summary>
    public static GridPoint TrackCell(int absoluteIndex)
    {
        if (absoluteIndex < 0 || absoluteIndex >= TrackLength)
            throw new ArgumentOutOfRangeException(nameof(absoluteIndex));
        return TrackCells[absoluteIndex];
    }

    /// <summary>Grid cell of one of a colour's home-column squares (0..4).</summary>
    public static GridPoint HomeColumnCell(PlayerColor color, int step)
    {
        if (step < 0 || step > 4) throw new ArgumentOutOfRangeException(nameof(step));
        // Ruby's home column runs up the middle of the bottom arm: (7,13) .. (7,9).
        return Rotate(new GridPoint(7, 13 - step), color);
    }

    /// <summary>Centre point of the crown (finish) where finished pieces are drawn, offset per colour.</summary>
    public static GridPoint FinishPoint(PlayerColor color) => Rotate(new GridPoint(7, 8.1), color);

    /// <summary>Yard slot (0..3) for a piece waiting to enter play.</summary>
    public static GridPoint YardSlot(PlayerColor color, int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= PiecesPerPlayer) throw new ArgumentOutOfRangeException(nameof(pieceIndex));
        // Ruby's yard is the bottom-left 6x6 block (cells 0..5, 9..14); centre is (2.5, 11.5).
        var slots = new[] { new GridPoint(1.6, 10.6), new GridPoint(3.4, 10.6), new GridPoint(1.6, 12.4), new GridPoint(3.4, 12.4) };
        return Rotate(slots[pieceIndex], color);
    }

    /// <summary>Where a piece should be drawn for the given progress.</summary>
    public static GridPoint PiecePosition(PlayerColor color, int pieceIndex, int progress) => progress switch
    {
        Yard => YardSlot(color, pieceIndex),
        >= StartProgress and <= LastTrackProgress => TrackCell(AbsoluteIndex(color, progress)),
        >= HomeColumnStart and < FinishedProgress => HomeColumnCell(color, progress - HomeColumnStart),
        FinishedProgress => FinishPoint(color),
        _ => throw new ArgumentOutOfRangeException(nameof(progress)),
    };

    /// <summary>Top-left cell of a colour's 6x6 yard.</summary>
    public static GridPoint YardOrigin(PlayerColor color) => color switch
    {
        PlayerColor.Ruby => new GridPoint(0, 9),
        PlayerColor.Emerald => new GridPoint(0, 0),
        PlayerColor.Topaz => new GridPoint(9, 0),
        PlayerColor.Sapphire => new GridPoint(9, 9),
        _ => throw new ArgumentOutOfRangeException(nameof(color)),
    };

    private static GridPoint Rotate(GridPoint p, PlayerColor color)
    {
        for (var i = 0; i < (int)color; i++) p = p.RotateClockwise();
        return p;
    }

    private static GridPoint[] BuildTrack()
    {
        // One quarter (13 squares) starting at Ruby's start square, then rotated for the other quarters.
        var quarter = new List<GridPoint>
        {
            new(6, 13), new(6, 12), new(6, 11), new(6, 10), new(6, 9),          // up the bottom arm
            new(5, 8), new(4, 8), new(3, 8), new(2, 8), new(1, 8), new(0, 8),   // left along the left arm
            new(0, 7),                                                           // turn
        };
        // The 13th square of each quarter is the corner just before the next colour's start (Ruby's: (0,6)).
        quarter.Add(new GridPoint(0, 6));

        var track = new GridPoint[TrackLength];
        for (var q = 0; q < 4; q++)
        {
            for (var i = 0; i < QuarterLength; i++)
            {
                var p = quarter[i];
                for (var r = 0; r < q; r++) p = p.RotateClockwise();
                track[q * QuarterLength + i] = p;
            }
        }
        return track;
    }
}
