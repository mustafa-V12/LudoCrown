// DEVELOPMENT TOOL ONLY: minimal subset of Microsoft.Maui.Graphics that writes SVG.
using System.Globalization;
using System.Text;

namespace Microsoft.Maui.Graphics
{
    public readonly record struct Point(double X, double Y);
    public readonly record struct PointF(float X, float Y);
    public readonly record struct SizeF(float Width, float Height);
    public readonly record struct RectF(float X, float Y, float Width, float Height)
    {
        public float Left => X; public float Top => Y; public float Right => X + Width; public float Bottom => Y + Height;
        public PointF Center => new(X + Width / 2, Y + Height / 2);
        public RectF Inflate(float dx, float dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);
    }

    public sealed class Color
    {
        public float Red, Green, Blue, Alpha;
        public Color(float r, float g, float b, float a = 1) { Red = r; Green = g; Blue = b; Alpha = a; }
        public static Color FromArgb(string hex)
        {
            hex = hex.TrimStart('#');
            float a = 1; int i = 0;
            if (hex.Length == 8) { a = Convert.ToInt32(hex[..2], 16) / 255f; i = 2; }
            return new Color(Convert.ToInt32(hex.Substring(i, 2), 16) / 255f, Convert.ToInt32(hex.Substring(i + 2, 2), 16) / 255f, Convert.ToInt32(hex.Substring(i + 4, 2), 16) / 255f, a);
        }
        public static Color FromRgba(int r, int g, int b, int a) => new(r / 255f, g / 255f, b / 255f, a / 255f);
        public Color WithAlpha(float a) => new(Red, Green, Blue, a);
        private (float H, float S, float L) Hsl()
        {
            float max = Math.Max(Red, Math.Max(Green, Blue)), min = Math.Min(Red, Math.Min(Green, Blue)), l = (max + min) / 2, h = 0, s = 0;
            if (max != min)
            {
                var d = max - min; s = l > 0.5f ? d / (2 - max - min) : d / (max + min);
                h = max == Red ? (Green - Blue) / d + (Green < Blue ? 6 : 0) : max == Green ? (Blue - Red) / d + 2 : (Red - Green) / d + 4; h /= 6;
            }
            return (h, s, l);
        }
        private static Color FromHsl(float h, float s, float l, float a)
        {
            if (s == 0) return new Color(l, l, l, a);
            float Hue(float p, float q, float t) { if (t < 0) t += 1; if (t > 1) t -= 1; return t < 1f / 6 ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2f / 3 ? p + (q - p) * (2f / 3 - t) * 6 : p; }
            var q2 = l < 0.5f ? l * (1 + s) : l + s - l * s; var p2 = 2 * l - q2;
            return new Color(Hue(p2, q2, h + 1f / 3), Hue(p2, q2, h), Hue(p2, q2, h - 1f / 3), a);
        }
        public float GetLuminosity() => Hsl().L;
        public Color WithLuminosity(float l) { var x = Hsl(); return FromHsl(x.H, x.S, l, Alpha); }
        public Color WithSaturation(float s) { var x = Hsl(); return FromHsl(x.H, s, x.L, Alpha); }
        public string Css => $"rgba({(int)(Red * 255)},{(int)(Green * 255)},{(int)(Blue * 255)},{Alpha.ToString(CultureInfo.InvariantCulture)})";
    }

    public static class Colors { public static readonly Color Gray = new(0.5f, 0.5f, 0.5f); public static readonly Color Transparent = new(0, 0, 0, 0); }

    public abstract class Paint { }
    public sealed class RadialGradientPaint : Paint { public Point Center { get; set; } public double Radius { get; set; } public Color StartColor { get; set; } public Color EndColor { get; set; } }

    public sealed class PathF
    {
        internal readonly StringBuilder D = new();
        public void MoveTo(PointF p) => MoveTo(p.X, p.Y);
        public void MoveTo(float x, float y) => D.Append(FormattableString.Invariant($"M{x:0.##},{y:0.##} "));
        public void LineTo(PointF p) => LineTo(p.X, p.Y);
        public void LineTo(float x, float y) => D.Append(FormattableString.Invariant($"L{x:0.##},{y:0.##} "));
        public void Close() => D.Append("Z ");
    }

    public interface IDrawable { void Draw(ICanvas canvas, RectF dirtyRect); }

    public interface ICanvas
    {
        bool Antialias { get; set; }
        Color FillColor { get; set; }
        Color StrokeColor { get; set; }
        float StrokeSize { get; set; }
        void SetShadow(SizeF offset, float blur, Color? color);
        void SetFillPaint(Paint paint, RectF rect);
        void FillRectangle(RectF r);
        void DrawRectangle(RectF r);
        void FillRoundedRectangle(RectF r, float radius);
        void DrawRoundedRectangle(RectF r, float radius);
        void FillCircle(float x, float y, float radius);
        void DrawCircle(float x, float y, float radius);
        void FillPath(PathF path);
        void DrawPath(PathF path);
    }

    public static class CanvasExtensions
    {
        public static void FillCircle(this ICanvas c, PointF p, float r) => c.FillCircle(p.X, p.Y, r);
        public static void DrawCircle(this ICanvas c, PointF p, float r) => c.DrawCircle(p.X, p.Y, r);
    }

    public sealed class SvgCanvas : ICanvas
    {
        private readonly StringBuilder _sb = new();
        private int _ids;
        private string? _paintFill;
        public bool Antialias { get; set; }
        public Color FillColor { get; set { field = value; _paintFill = null; } } = new(0, 0, 0);
        public Color StrokeColor { get; set; } = new(0, 0, 0);
        public float StrokeSize { get; set; } = 1;
        private string? _shadow;
        public void SetShadow(SizeF offset, float blur, Color? color)
        {
            if (color is null) { _shadow = null; return; }
            var id = $"s{_ids++}";
            _sb.Append(FormattableString.Invariant($"<filter id='{id}' x='-50%' y='-50%' width='200%' height='200%'><feDropShadow dx='{offset.Width}' dy='{offset.Height}' stdDeviation='{blur / 2}' flood-color='{color.Css}'/></filter>"));
            _shadow = $" filter='url(#{id})'";
        }
        public void SetFillPaint(Paint paint, RectF rect)
        {
            if (paint is not RadialGradientPaint g) return;
            var id = $"g{_ids++}";
            _sb.Append(FormattableString.Invariant($"<radialGradient id='{id}' cx='{g.Center.X}' cy='{g.Center.Y}' r='{g.Radius}'><stop offset='0' stop-color='{g.StartColor.Css}'/><stop offset='1' stop-color='{g.EndColor.Css}'/></radialGradient>"));
            _paintFill = $"url(#{id})";
        }
        private string Fill => _paintFill ?? FillColor.Css;
        private string Stroke => FormattableString.Invariant($"fill='none' stroke='{StrokeColor.Css}' stroke-width='{StrokeSize}'");
        private void Add(string s) => _sb.Append(s.Replace("/>", (_shadow ?? "") + "/>"));
        public void FillRectangle(RectF r) => Add(FormattableString.Invariant($"<rect x='{r.X}' y='{r.Y}' width='{r.Width}' height='{r.Height}' fill='{Fill}'/>"));
        public void DrawRectangle(RectF r) => _sb.Append(FormattableString.Invariant($"<rect x='{r.X}' y='{r.Y}' width='{r.Width}' height='{r.Height}' {Stroke}/>"));
        public void FillRoundedRectangle(RectF r, float rad) => Add(FormattableString.Invariant($"<rect x='{r.X}' y='{r.Y}' width='{r.Width}' height='{r.Height}' rx='{rad}' fill='{Fill}'/>"));
        public void DrawRoundedRectangle(RectF r, float rad) => _sb.Append(FormattableString.Invariant($"<rect x='{r.X}' y='{r.Y}' width='{r.Width}' height='{r.Height}' rx='{rad}' {Stroke}/>"));
        public void FillCircle(float x, float y, float r) { Add(FormattableString.Invariant($"<circle cx='{x}' cy='{y}' r='{r}' fill='{Fill}'/>")); _paintFill = null; }
        public void DrawCircle(float x, float y, float r) => _sb.Append(FormattableString.Invariant($"<circle cx='{x}' cy='{y}' r='{r}' {Stroke}/>"));
        public void FillPath(PathF p) => Add($"<path d='{p.D}' fill='{Fill}'/>");
        public void DrawPath(PathF p) => _sb.Append($"<path d='{p.D}' {Stroke} stroke-linejoin='round'/>");
        public string ToSvg(float w, float h) => FormattableString.Invariant($"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{h}' viewBox='0 0 {w} {h}'><rect width='{w}' height='{h}' fill='#140C2E'/>{_sb}</svg>");
    }
}
