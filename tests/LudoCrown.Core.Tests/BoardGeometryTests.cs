using LudoCrown.Core.Board;
using LudoCrown.Core.Model;

namespace LudoCrown.Core.Tests;

public class BoardGeometryTests
{
    [Fact]
    public void Track_has_52_unique_cells_each_adjacent_to_the_next()
    {
        var cells = Enumerable.Range(0, BoardGeometry.TrackLength).Select(BoardGeometry.TrackCell).ToList();
        Assert.Equal(52, cells.Distinct().Count());
        for (var i = 0; i < cells.Count; i++)
        {
            var a = cells[i];
            var b = cells[(i + 1) % cells.Count];
            var manhattan = Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
            // Straight neighbours, or a diagonal step around the inner corners of the cross.
            Assert.True(manhattan == 1 || (Math.Abs(a.X - b.X) == 1 && Math.Abs(a.Y - b.Y) == 1), $"Cells {i} and {i + 1} are not adjacent: {a} -> {b}");
        }
    }

    [Fact]
    public void Track_cells_stay_on_the_cross_and_off_the_yards_and_centre()
    {
        foreach (var c in Enumerable.Range(0, 52).Select(BoardGeometry.TrackCell))
        {
            var inArm = (c.X >= 6 && c.X <= 8) || (c.Y >= 6 && c.Y <= 8);
            var inCentre = c.X >= 6 && c.X <= 8 && c.Y >= 6 && c.Y <= 8;
            Assert.True(inArm && !inCentre, $"{c} is not on the track arms");
        }
    }

    [Theory]
    [InlineData(PlayerColor.Ruby, 0)]
    [InlineData(PlayerColor.Emerald, 13)]
    [InlineData(PlayerColor.Topaz, 26)]
    [InlineData(PlayerColor.Sapphire, 39)]
    public void Start_squares_are_a_quarter_apart_and_safe(PlayerColor color, int expected)
    {
        Assert.Equal(expected, BoardGeometry.StartIndex(color));
        Assert.True(BoardGeometry.IsSafeSquare(BoardGeometry.AbsoluteIndex(color, 0)));
    }

    [Fact]
    public void Last_track_square_is_next_to_the_home_column_entrance()
    {
        foreach (var color in Enum.GetValues<PlayerColor>())
        {
            var last = BoardGeometry.TrackCell(BoardGeometry.AbsoluteIndex(color, BoardGeometry.LastTrackProgress));
            var firstHome = BoardGeometry.HomeColumnCell(color, 0);
            Assert.Equal(1, Math.Abs(last.X - firstHome.X) + Math.Abs(last.Y - firstHome.Y));
        }
    }

    [Fact]
    public void Home_columns_point_to_the_centre_and_do_not_overlap()
    {
        var all = Enum.GetValues<PlayerColor>().SelectMany(c => Enumerable.Range(0, 5).Select(s => BoardGeometry.HomeColumnCell(c, s))).ToList();
        Assert.Equal(20, all.Distinct().Count());
        foreach (var color in Enum.GetValues<PlayerColor>())
        {
            var d0 = Distance(BoardGeometry.HomeColumnCell(color, 0));
            var d4 = Distance(BoardGeometry.HomeColumnCell(color, 4));
            Assert.True(d4 < d0);
            Assert.Equal(2, d4); // the last home square touches the centre block
        }

        static double Distance(GridPoint p) => Math.Abs(p.X - 7) + Math.Abs(p.Y - 7);
    }

    [Fact]
    public void Yard_slots_are_inside_their_yard()
    {
        foreach (var color in Enum.GetValues<PlayerColor>())
        {
            var origin = BoardGeometry.YardOrigin(color);
            for (var i = 0; i < 4; i++)
            {
                var s = BoardGeometry.YardSlot(color, i);
                Assert.InRange(s.X, origin.X, origin.X + 5);
                Assert.InRange(s.Y, origin.Y, origin.Y + 5);
            }
        }
    }

    [Fact]
    public void Each_colour_has_one_star_square_in_its_quarter()
    {
        Assert.Equal(4, BoardGeometry.StarSquares.Count);
        foreach (var color in Enum.GetValues<PlayerColor>())
            Assert.True(BoardGeometry.IsSafeSquare(BoardGeometry.AbsoluteIndex(color, 8)));
    }
}
