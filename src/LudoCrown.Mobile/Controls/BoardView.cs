using LudoCrown.App.Game;
using LudoCrown.Core.Board;
using LudoCrown.Core.Model;

namespace LudoCrown.Mobile.Controls;

/// <summary>
/// Interactive board. Animates pieces along their path when a new <see cref="BoardState"/> arrives and reports
/// taps on movable pieces. Animations are cosmetic: the authoritative state is applied immediately in the
/// view model; if a newer state arrives mid-animation the board simply snaps to it.
/// </summary>
public sealed class BoardView : GraphicsView
{
    private const string MoveAnimation = "ludo-moves";
    private readonly BoardDrawable _drawable = new();
    private IDispatcherTimer? _pulseTimer;
    private DateTime _pulseStart = DateTime.UtcNow;

    public BoardView()
    {
        Drawable = _drawable;
        BackgroundColor = Colors.Transparent;
        StartInteraction += OnTouch;
        SemanticProperties.SetDescription(this, "Ludo board");
    }

    public event Action<int>? PieceTapped;

    public bool ReduceMotion { get; set; }

    public void SetState(BoardState? previous, BoardState next)
    {
        this.AbortAnimation(MoveAnimation);
        _drawable.State = next;
        var count = next.Pieces.Length;

        if (_drawable.Sprites.Count != count * BoardGeometry.PiecesPerPlayer || previous is null || previous.MatchId != next.MatchId)
        {
            ResetSprites(next);
            UpdatePulse();
            Invalidate();
            return;
        }

        var movers = new List<(PieceSprite Sprite, List<GridPoint> Path)>();
        var captured = new List<(PieceSprite Sprite, List<GridPoint> Path)>();
        foreach (var s in _drawable.Sprites)
        {
            var target = next.Pieces[s.Seat][s.Index];
            if (target == s.Progress) { s.Position = BoardGeometry.PiecePosition(s.Color, s.Index, target); continue; }
            var path = PathFor(s, s.Progress, target);
            if (target == BoardGeometry.Yard) captured.Add((s, path)); else movers.Add((s, path));
            s.Progress = target;
        }

        if (ReduceMotion || (movers.Count == 0 && captured.Count == 0))
        {
            foreach (var (s, path) in movers.Concat(captured)) s.Position = path[^1];
            UpdatePulse();
            Invalidate();
            return;
        }

        const double stepMs = 105;
        var moveMs = movers.Count == 0 ? 0 : movers.Max(m => (m.Path.Count - 1) * stepMs);
        var captureMs = captured.Count == 0 ? 0 : 320;
        var total = Math.Max(1, moveMs + captureMs);

        var animation = new Animation(v =>
        {
            var t = v * total;
            foreach (var (s, path) in movers) s.Position = Along(path, Math.Min(1, t / Math.Max(1, (path.Count - 1) * stepMs)), hop: true, s);
            foreach (var (s, path) in captured) s.Position = Along(path, Math.Clamp((t - moveMs) / captureMs, 0, 1), hop: false, s);
            Invalidate();
        }, 0, 1);
        animation.Commit(this, MoveAnimation, 16, (uint)total, Easing.Linear, (_, _) =>
        {
            foreach (var (s, path) in movers.Concat(captured)) { s.Position = path[^1]; s.Scale = 1; }
            UpdatePulse();
            Invalidate();
        });
    }

    private void ResetSprites(BoardState state)
    {
        _drawable.Sprites.Clear();
        foreach (var seat in state.Seats)
            for (var i = 0; i < BoardGeometry.PiecesPerPlayer; i++)
            {
                var progress = state.Pieces[seat.Seat][i];
                _drawable.Sprites.Add(new PieceSprite
                {
                    Seat = seat.Seat,
                    Index = i,
                    Color = seat.Color,
                    Progress = progress,
                    Position = BoardGeometry.PiecePosition(seat.Color, i, progress),
                });
            }
    }

    private static List<GridPoint> PathFor(PieceSprite s, int from, int to)
    {
        var path = new List<GridPoint> { s.Position };
        if (to == BoardGeometry.Yard || from == BoardGeometry.Yard || to < from)
        {
            path.Add(BoardGeometry.PiecePosition(s.Color, s.Index, to));
            return path;
        }
        for (var p = from + 1; p <= to; p++) path.Add(BoardGeometry.PiecePosition(s.Color, s.Index, p));
        return path;
    }

    private static GridPoint Along(List<GridPoint> path, double t, bool hop, PieceSprite sprite)
    {
        if (path.Count == 1) return path[0];
        var segments = path.Count - 1;
        var x = t * segments;
        var i = Math.Min(segments - 1, (int)Math.Floor(x));
        var local = x - i;
        var a = path[i];
        var b = path[i + 1];
        // A little hop on each square makes movement easy to follow.
        sprite.Scale = hop ? 1f + 0.18f * (float)Math.Sin(local * Math.PI) : 1f;
        var eased = local < 0.5 ? 2 * local * local : 1 - Math.Pow(-2 * local + 2, 2) / 2;
        return new GridPoint(a.X + (b.X - a.X) * eased, a.Y + (b.Y - a.Y) * eased);
    }

    private void UpdatePulse()
    {
        var needsPulse = _drawable.State is { IsFinished: false } s && (s.LegalPieces.Count > 0 || s.CanRoll || s.CurrentSeatView.IsLocalHuman);
        if (needsPulse && !ReduceMotion)
        {
            if (_pulseTimer is null)
            {
                _pulseStart = DateTime.UtcNow;
                _pulseTimer = Dispatcher.CreateTimer();
                _pulseTimer.Interval = TimeSpan.FromMilliseconds(50);
                _pulseTimer.Tick += (_, _) =>
                {
                    var secs = (DateTime.UtcNow - _pulseStart).TotalSeconds;
                    _drawable.Pulse = (float)(0.5 + 0.5 * Math.Sin(secs * Math.PI * 1.6));
                    Invalidate();
                };
                _pulseTimer.Start();
            }
        }
        else
        {
            _pulseTimer?.Stop();
            _pulseTimer = null;
            _drawable.Pulse = 1;
        }
    }

    private void OnTouch(object? sender, TouchEventArgs e)
    {
        var state = _drawable.State;
        if (state is null || state.LegalPieces.Count == 0 || e.Touches.Length == 0) return;
        var touch = e.Touches[0];
        var c = _drawable.Cell;
        var best = -1;
        var bestDistance = float.MaxValue;
        foreach (var s in _drawable.Sprites.Where(s => s.Seat == state.CurrentSeat && state.LegalPieces.Contains(s.Index)))
        {
            var p = _drawable.ToScreen(s.Position);
            var d = MathF.Sqrt((p.X - touch.X) * (p.X - touch.X) + (p.Y - touch.Y) * (p.Y - touch.Y));
            if (d < bestDistance) { bestDistance = d; best = s.Index; }
        }
        // Generous hit area (about one and a half cells) keeps tapping easy on small screens.
        if (best >= 0 && bestDistance <= c * 1.5f) PieceTapped?.Invoke(best);
    }

    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        base.OnHandlerChanging(args);
        if (args.NewHandler is null) { _pulseTimer?.Stop(); _pulseTimer = null; }
    }
}

/// <summary>Draws a die face (1–6) with ivory body and jewel pips.</summary>
public sealed class DiceDrawable : IDrawable
{
    public int Value { get; set; } = 1;
    public bool Enabled { get; set; }
    public PlayerColor Color { get; set; } = PlayerColor.Ruby;
    public float Glow { get; set; }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var size = Math.Min(rect.Width, rect.Height) * 0.86f;
        var r = new RectF(rect.Center.X - size / 2, rect.Center.Y - size / 2, size, size);
        if (Enabled)
        {
            canvas.FillColor = Palette.Gold.WithAlpha(0.25f + 0.35f * Glow);
            canvas.FillRoundedRectangle(r.Inflate(size * 0.08f, size * 0.08f), size * 0.28f);
        }
        canvas.SetShadow(new SizeF(0, size * 0.05f), size * 0.12f, Microsoft.Maui.Graphics.Color.FromRgba(0, 0, 0, 120));
        canvas.FillColor = Enabled ? Palette.Ivory : Palette.Parchment.WithAlpha(0.75f);
        canvas.FillRoundedRectangle(r, size * 0.22f);
        canvas.SetShadow(new SizeF(0, 0), 0, null);
        canvas.StrokeColor = Enabled ? Palette.Gold : Palette.Line;
        canvas.StrokeSize = size * 0.05f;
        canvas.DrawRoundedRectangle(r, size * 0.22f);

        var pip = Enabled ? Palette.Of(Color) : Palette.Ink.WithAlpha(0.5f);
        canvas.FillColor = pip;
        float q = size / 4;
        var cx = r.Center.X;
        var cy = r.Center.Y;
        var pr = size * 0.09f;
        void P(float dx, float dy) => canvas.FillCircle(cx + dx * q, cy + dy * q, pr);
        switch (Value)
        {
            case 1: P(0, 0); break;
            case 2: P(-1, -1); P(1, 1); break;
            case 3: P(-1, -1); P(0, 0); P(1, 1); break;
            case 4: P(-1, -1); P(1, -1); P(-1, 1); P(1, 1); break;
            case 5: P(-1, -1); P(1, -1); P(0, 0); P(-1, 1); P(1, 1); break;
            default: P(-1, -1); P(1, -1); P(-1, 0); P(1, 0); P(-1, 1); P(1, 1); break;
        }
    }
}

/// <summary>Tappable die with a short tumble animation. Raises <see cref="Tapped"/> when enabled.</summary>
public sealed class DiceView : GraphicsView
{
    private readonly DiceDrawable _drawable = new();
    private readonly Random _random = new();
    private IDispatcherTimer? _glowTimer;

    public DiceView()
    {
        Drawable = _drawable;
        WidthRequest = 84;
        HeightRequest = 84;
        StartInteraction += (_, _) => { if (_drawable.Enabled) Tapped?.Invoke(); };
        SemanticProperties.SetDescription(this, "Die");
        SemanticProperties.SetHint(this, "Double tap to roll when it's your turn");
    }

    public event Action? Tapped;

    public void SetState(int value, bool enabled, PlayerColor color, bool reduceMotion)
    {
        var changed = value != _drawable.Value;
        _drawable.Enabled = enabled;
        _drawable.Color = color;
        SemanticProperties.SetDescription(this, enabled ? "Die. Your turn to roll." : $"Die showing {value}");
        if (enabled && !reduceMotion) StartGlow(); else StopGlow();

        if (changed && !reduceMotion)
        {
            // Tumble through random faces, then land on the server/engine value.
            var frames = 7;
            var i = 0;
            var timer = Dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(55);
            timer.Tick += (_, _) =>
            {
                i++;
                _drawable.Value = i >= frames ? value : _random.Next(1, 7);
                Rotation = i >= frames ? 0 : _random.Next(-14, 15);
                Invalidate();
                if (i >= frames) timer.Stop();
            };
            timer.Start();
        }
        else
        {
            _drawable.Value = value;
            Invalidate();
        }
    }

    private void StartGlow()
    {
        if (_glowTimer is not null) return;
        var start = DateTime.UtcNow;
        _glowTimer = Dispatcher.CreateTimer();
        _glowTimer.Interval = TimeSpan.FromMilliseconds(60);
        _glowTimer.Tick += (_, _) =>
        {
            _drawable.Glow = (float)(0.5 + 0.5 * Math.Sin((DateTime.UtcNow - start).TotalSeconds * Math.PI * 1.6));
            Invalidate();
        };
        _glowTimer.Start();
    }

    private void StopGlow()
    {
        _glowTimer?.Stop();
        _glowTimer = null;
        _drawable.Glow = 0;
        Invalidate();
    }
}
