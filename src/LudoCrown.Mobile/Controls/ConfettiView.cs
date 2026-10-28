namespace LudoCrown.Mobile.Controls;

/// <summary>Falling confetti for victories. Purely decorative; call <see cref="Burst"/> to start it.</summary>
public sealed class ConfettiView : GraphicsView, IDrawable
{
    private sealed class Bit { public float X, Y, Vx, Vy, Spin, Angle, Size; public Color Color = Colors.White; }
    private static readonly Color[] Colours =
    {
        Color.FromArgb("#E9B949"), Color.FromArgb("#D7263D"), Color.FromArgb("#17A673"), Color.FromArgb("#2F6FE0"), Color.FromArgb("#F2A71B"), Color.FromArgb("#FBF3E4"),
    };
    private readonly List<Bit> _bits = new();
    private readonly Random _random = new();
    private IDispatcherTimer? _timer;

    public ConfettiView()
    {
        Drawable = this;
        InputTransparent = true;
        BackgroundColor = Colors.Transparent;
    }

    public void Burst(int count = 140)
    {
        var w = (float)Math.Max(Width, 300);
        for (var i = 0; i < count; i++)
        {
            _bits.Add(new Bit
            {
                X = (float)(_random.NextDouble() * w), Y = (float)(-_random.NextDouble() * 300),
                Vx = (float)(_random.NextDouble() - 0.5) * 2.4f, Vy = 2.5f + (float)_random.NextDouble() * 3.5f,
                Spin = (float)(_random.NextDouble() - 0.5) * 14, Angle = (float)(_random.NextDouble() * 360),
                Size = 6 + (float)_random.NextDouble() * 6, Color = Colours[i % Colours.Length],
            });
        }
        if (_timer is not null) return;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) =>
        {
            var h = (float)Height;
            foreach (var b in _bits)
            {
                b.X += b.Vx + (float)Math.Sin(b.Y / 40) * 0.6f;
                b.Y += b.Vy;
                b.Angle += b.Spin;
            }
            _bits.RemoveAll(b => b.Y > h + 20);
            Invalidate();
            if (_bits.Count == 0) { _timer?.Stop(); _timer = null; }
        };
        _timer.Start();
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        foreach (var b in _bits)
        {
            canvas.SaveState();
            canvas.Translate(b.X, b.Y);
            canvas.Rotate(b.Angle);
            canvas.FillColor = b.Color;
            canvas.FillRectangle(-b.Size / 2, -b.Size / 4, b.Size, b.Size / 2);
            canvas.RestoreState();
        }
    }
}
