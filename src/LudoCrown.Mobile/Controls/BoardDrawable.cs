using LudoCrown.App.Game;
using LudoCrown.Core.Board;
using LudoCrown.Core.Model;

namespace LudoCrown.Mobile.Controls;

/// <summary>One piece as currently drawn (may be mid-animation).</summary>
public sealed class PieceSprite
{
    public int Seat;
    public int Index;
    public PlayerColor Color;
    public GridPoint Position;
    public int Progress;
    public float Scale = 1f;
}

/// <summary>
/// Draws the whole board with Microsoft.Maui.Graphics: original Ludo Crown art (velvet frame, ivory track,
/// jewel yards, gold stars and crown centre) and glossy gem pieces. Purely visual — reads sprites prepared by
/// <see cref="BoardView"/> and never touches game state.
/// </summary>
public sealed class BoardDrawable : IDrawable
{
    public List<PieceSprite> Sprites { get; } = new();
    public BoardState? State { get; set; }
    /// <summary>0..1 phase for the pulsing highlight on movable pieces.</summary>
    public float Pulse { get; set; }

    public RectF BoardRect { get; private set; }
    public float Cell => BoardRect.Width / BoardGeometry.GridSize;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        // Leave room for the gold frame and its shadow so nothing is clipped at the view's edges.
        var size = Math.Max(0, Math.Min(dirtyRect.Width, dirtyRect.Height) - 14);
        BoardRect = new RectF(dirtyRect.Center.X - size / 2, dirtyRect.Center.Y - size / 2, size, size);
        var c = Cell;
        canvas.Antialias = true;

        DrawFrame(canvas);
        var colors = ActiveColors();
        foreach (var color in Enum.GetValues<PlayerColor>()) DrawYard(canvas, color, colors.Contains(color), c);
        DrawTrack(canvas, c);
        DrawCentre(canvas, c);
        DrawPieces(canvas, c);
    }

    public PointF ToScreen(GridPoint p) => new(BoardRect.Left + (float)(p.X + 0.5) * Cell, BoardRect.Top + (float)(p.Y + 0.5) * Cell);

    private HashSet<PlayerColor> ActiveColors() =>
        State is null ? Enum.GetValues<PlayerColor>().ToHashSet() : State.Seats.Select(s => s.Color).ToHashSet();

    private void DrawFrame(ICanvas canvas)
    {
        var r = BoardRect;
        canvas.SetShadow(new SizeF(0, 6), 18, Color.FromRgba(0, 0, 0, 120));
        canvas.FillColor = Palette.GoldDeep;
        canvas.FillRoundedRectangle(r.Inflate(4, 4), 18);
        canvas.SetShadow(new SizeF(0, 0), 0, null);
        canvas.FillColor = Palette.Gold;
        canvas.FillRoundedRectangle(r.Inflate(1.5f, 1.5f), 16);
        canvas.FillColor = Palette.Ivory;
        canvas.FillRoundedRectangle(r, 14);
    }

    private void DrawYard(ICanvas canvas, PlayerColor color, bool active, float c)
    {
        var o = BoardGeometry.YardOrigin(color);
        var rect = new RectF(BoardRect.Left + (float)o.X * c, BoardRect.Top + (float)o.Y * c, 6 * c, 6 * c);
        var isCurrent = State is not null && !State.IsFinished && State.CurrentSeatView.Color == color;
        var baseColor = active ? Palette.Of(color) : Palette.Of(color).WithSaturation(0.15f).WithLuminosity(0.55f);

        canvas.FillColor = baseColor;
        canvas.FillRoundedRectangle(rect.Inflate(-c * 0.08f, -c * 0.08f), c * 0.5f);

        // Darker recessed tray with four sockets, as in classic Ludo boards.
        var inner = rect.Inflate(-c * 0.8f, -c * 0.8f);
        canvas.FillColor = active ? Palette.Dark(color) : Palette.Of(color).WithSaturation(0.15f).WithLuminosity(0.42f);
        canvas.FillRoundedRectangle(inner, c * 0.4f);
        canvas.StrokeColor = Color.FromRgba(0, 0, 0, 70);
        canvas.StrokeSize = Math.Max(1, c * 0.12f);
        canvas.DrawRoundedRectangle(inner.Inflate(-c * 0.06f, -c * 0.06f), c * 0.36f);
        for (var i = 0; i < 4; i++)
        {
            var p = ToScreen(BoardGeometry.YardSlot(color, i));
            canvas.FillColor = Color.FromRgba(0, 0, 0, 56);
            canvas.FillCircle(p, c * 0.5f);
            canvas.StrokeColor = Palette.Light(color).WithAlpha(active ? 0.45f : 0.2f);
            canvas.StrokeSize = Math.Max(1, c * 0.05f);
            canvas.DrawCircle(p, c * 0.5f);
        }

        if (isCurrent)
        {
            canvas.StrokeColor = Palette.Gold.WithAlpha(0.6f + 0.4f * Pulse);
            canvas.StrokeSize = c * 0.16f;
            canvas.DrawRoundedRectangle(rect.Inflate(-c * 0.12f, -c * 0.12f), c * 0.5f);
        }
    }

    private void DrawTrack(ICanvas canvas, float c)
    {
        canvas.StrokeColor = Color.FromRgba(38, 22, 74, 140);
        canvas.StrokeSize = Math.Max(1, c * 0.035f);

        for (var i = 0; i < BoardGeometry.TrackLength; i++)
        {
            var cell = CellRect(BoardGeometry.TrackCell(i), c);
            var startOf = BoardGeometry.StartSquares.Contains(i) ? (PlayerColor?)(PlayerColor)(i / BoardGeometry.QuarterLength) : null;
            canvas.FillColor = startOf is PlayerColor sc ? Palette.Of(sc)
                : BoardGeometry.StarSquares.Contains(i) ? Color.FromArgb("#E4DDF0") : Color.FromArgb("#FFFDF8");
            canvas.FillRectangle(cell);
            canvas.DrawRectangle(cell);
            if (startOf is PlayerColor s2) DrawArrow(canvas, cell, s2);
            if (BoardGeometry.StarSquares.Contains(i)) DrawStar(canvas, cell.Center, c * 0.34f, Palette.GoldDeep, Palette.Gold);
        }

        foreach (var color in Enum.GetValues<PlayerColor>())
        {
            for (var step = 0; step < 5; step++)
            {
                var cell = CellRect(BoardGeometry.HomeColumnCell(color, step), c);
                canvas.FillColor = step == 4 ? Palette.Of(color).WithLuminosity(Math.Min(0.75f, Palette.Of(color).GetLuminosity() + 0.08f)) : Palette.Of(color);
                canvas.FillRectangle(cell);
                canvas.DrawRectangle(cell);
            }
        }
    }

    private void DrawArrow(ICanvas canvas, RectF cell, PlayerColor color)
    {
        // Small ivory chevron on each colour's start square, pointing along the direction of travel.
        var centre = cell.Center;
        var next = ToScreen(BoardGeometry.TrackCell((BoardGeometry.StartIndex(color) + 1) % BoardGeometry.TrackLength));
        var dx = next.X - centre.X;
        var dy = next.Y - centre.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.01f) return;
        dx /= len; dy /= len;
        var s = cell.Width * 0.22f;
        var path = new PathF();
        path.MoveTo(centre.X + dx * s, centre.Y + dy * s);
        path.LineTo(centre.X - dx * s - dy * s, centre.Y - dy * s + dx * s);
        path.LineTo(centre.X - dx * s + dy * s, centre.Y - dy * s - dx * s);
        path.Close();
        canvas.FillColor = Palette.Ivory.WithAlpha(0.9f);
        canvas.FillPath(path);
    }

    private void DrawCentre(ICanvas canvas, float c)
    {
        var left = BoardRect.Left + 6 * c;
        var top = BoardRect.Top + 6 * c;
        var centre = new PointF(left + 1.5f * c, top + 1.5f * c);
        // Corners of the 3x3 centre block: Ruby's triangle points up from the bottom edge, etc.
        var corners = new[] { new PointF(left, top + 3 * c), new PointF(left, top), new PointF(left + 3 * c, top), new PointF(left + 3 * c, top + 3 * c) };
        var active = ActiveColors();
        for (var i = 0; i < 4; i++)
        {
            var color = (PlayerColor)i;
            var a = corners[i];
            var b = corners[(i + 3) % 4];
            var path = new PathF();
            path.MoveTo(a);
            path.LineTo(b);
            path.LineTo(centre);
            path.Close();
            canvas.FillColor = active.Contains(color) ? Palette.Of(color) : Palette.Of(color).WithSaturation(0.15f).WithLuminosity(0.6f);
            canvas.FillPath(path);
        }
        canvas.FillColor = Palette.Night;
        canvas.FillCircle(centre, c * 0.78f);
        canvas.StrokeColor = Palette.Gold;
        canvas.StrokeSize = c * 0.08f;
        canvas.DrawCircle(centre, c * 0.78f);
        DrawCrown(canvas, centre, c * 0.95f);
    }

    public static void DrawCrown(ICanvas canvas, PointF centre, float size)
    {
        var w = size;
        var h = size * 0.7f;
        var x = centre.X - w / 2;
        var y = centre.Y - h / 2;
        var path = new PathF();
        path.MoveTo(x + w * 0.08f, y + h);
        path.LineTo(x, y + h * 0.18f);
        path.LineTo(x + w * 0.3f, y + h * 0.5f);
        path.LineTo(x + w * 0.5f, y);
        path.LineTo(x + w * 0.7f, y + h * 0.5f);
        path.LineTo(x + w, y + h * 0.18f);
        path.LineTo(x + w * 0.92f, y + h);
        path.Close();
        canvas.FillColor = Palette.Gold;
        canvas.FillPath(path);
        canvas.StrokeColor = Palette.GoldDeep;
        canvas.StrokeSize = Math.Max(1, size * 0.05f);
        canvas.DrawPath(path);
        canvas.FillColor = Color.FromArgb("#D7263D");
        canvas.FillCircle(centre.X, y + h * 0.66f, size * 0.07f);
    }

    private static void DrawStar(ICanvas canvas, PointF centre, float radius, Color stroke, Color fill)
    {
        var path = new PathF();
        for (var i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? radius : radius * 0.45f;
            var angle = -MathF.PI / 2 + i * MathF.PI / 5;
            var p = new PointF(centre.X + r * MathF.Cos(angle), centre.Y + r * MathF.Sin(angle));
            if (i == 0) path.MoveTo(p); else path.LineTo(p);
        }
        path.Close();
        canvas.FillColor = fill;
        canvas.FillPath(path);
        canvas.StrokeColor = stroke;
        canvas.StrokeSize = Math.Max(1, radius * 0.12f);
        canvas.DrawPath(path);
    }

    private void DrawPieces(ICanvas canvas, float c)
    {
        var legal = State?.LegalPieces ?? Array.Empty<int>();
        var currentSeat = State?.CurrentSeat ?? -1;

        // Group sprites by drawn cell so stacked pieces fan out slightly instead of hiding each other.
        var groups = Sprites.GroupBy(s => (Math.Round(s.Position.X * 4), Math.Round(s.Position.Y * 4))).ToList();
        foreach (var group in groups)
        {
            var list = group.ToList();
            for (var k = 0; k < list.Count; k++)
            {
                var s = list[k];
                var offset = list.Count == 1 ? new PointF(0, 0) : StackOffset(k, list.Count, c);
                var p = ToScreen(s.Position);
                p = new PointF(p.X + offset.X, p.Y + offset.Y);
                var radius = c * (list.Count == 1 ? 0.4f : 0.31f) * s.Scale;
                var movable = s.Seat == currentSeat && legal.Contains(s.Index);
                DrawPiece(canvas, p, radius, s.Color, movable);
            }
        }
    }

    private static PointF StackOffset(int k, int n, float c)
    {
        var angle = 2 * MathF.PI * k / n - MathF.PI / 4;
        var d = c * 0.17f;
        return new PointF(d * MathF.Cos(angle), d * MathF.Sin(angle));
    }

    private void DrawPiece(ICanvas canvas, PointF p, float r, PlayerColor color, bool movable)
    {
        if (movable)
        {
            canvas.FillColor = Palette.GoldLight.WithAlpha(0.28f + 0.4f * Pulse);
            canvas.FillCircle(p, r * (1.5f + 0.18f * Pulse));
        }
        // Coin: drop shadow, thick gold rim, coloured face with a shine, white crown emblem.
        canvas.FillColor = Color.FromRgba(0, 0, 0, 80);
        canvas.FillEllipse(p.X - r * 0.95f, p.Y + r * 0.42f - r * 0.5f, r * 1.9f, r);
        canvas.FillColor = Palette.GoldDeep;
        canvas.FillCircle(p.X, p.Y + r * 0.12f, r);
        var rim = new LinearGradientPaint
        {
            StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
            GradientStops = new[] { new PaintGradientStop(0, Color.FromArgb("#FFE69A")), new PaintGradientStop(0.5f, Palette.Gold), new PaintGradientStop(1, Palette.GoldDeep) },
        };
        canvas.SetFillPaint(rim, new RectF(p.X - r, p.Y - r, r * 2, r * 2));
        canvas.FillCircle(p, r);
        var face = new RadialGradientPaint
        {
            Center = new Point(0.35, 0.3), Radius = 0.75,
            GradientStops = new[] { new PaintGradientStop(0, Palette.Light(color)), new PaintGradientStop(0.55f, Palette.Of(color)), new PaintGradientStop(1, Palette.Dark(color)) },
        };
        canvas.SetFillPaint(face, new RectF(p.X - r * 0.76f, p.Y - r * 0.76f, r * 1.52f, r * 1.52f));
        canvas.FillCircle(p, r * 0.76f);
        DrawEmblem(canvas, new PointF(p.X, p.Y + r * 0.05f), r * 0.82f);
        canvas.FillColor = Color.FromRgba(255, 255, 255, 90);
        canvas.FillEllipse(p.X - r * 0.55f, p.Y - r * 0.58f, r * 0.75f, r * 0.32f);

        if (movable)
        {
            canvas.StrokeColor = Palette.Gold;
            canvas.StrokeSize = Math.Max(2, r * 0.16f);
            canvas.DrawCircle(p, r * 1.08f);
        }
    }

    /// <summary>The white crown stamped on each piece.</summary>
    private static void DrawEmblem(ICanvas canvas, PointF centre, float size)
    {
        var w = size * 0.78f;
        var h = w * 0.62f;
        var x = centre.X - w / 2;
        var y = centre.Y - h / 2;
        var path = new PathF();
        path.MoveTo(x + w * 0.1f, y + h);
        path.LineTo(x, y + h * 0.2f);
        path.LineTo(x + w * 0.3f, y + h * 0.52f);
        path.LineTo(x + w * 0.5f, y);
        path.LineTo(x + w * 0.7f, y + h * 0.52f);
        path.LineTo(x + w, y + h * 0.2f);
        path.LineTo(x + w * 0.9f, y + h);
        path.Close();
        canvas.FillColor = Color.FromRgba(255, 253, 248, 242);
        canvas.FillPath(path);
    }

    private RectF CellRect(GridPoint g, float c) => new(BoardRect.Left + (float)g.X * c, BoardRect.Top + (float)g.Y * c, c, c);
}
