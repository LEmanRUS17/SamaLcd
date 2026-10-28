using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SamaLcd.Core.Rendering;

/// <summary>Draws a <see cref="Theme"/> into a bitmap. Values come from <c>valueOf</c> (usually SensorHub.Get).</summary>
public sealed partial class ThemeRenderer : IDisposable
{
    private readonly Theme _theme;
    private readonly Func<string, float?> _valueOf;
    private readonly Dictionary<string, AnimatedImage> _images = [];
    private IFrameSource? _background;
    private readonly Dictionary<(string, float, bool), Font> _fonts = [];
    private readonly PrivateFontCollection _privateFonts = new();
    private readonly long _start = Environment.TickCount64;

    public int Width { get; }
    public int Height { get; }
    public Theme Theme => _theme;

    public ThemeRenderer(Theme theme, Func<string, float?> valueOf, int width = 480, int height = 480)
    {
        _theme = theme;
        _valueOf = valueOf;
        Width = width;
        Height = height;
        foreach (var f in Directory.Exists(theme.Directory) ? Directory.GetFiles(theme.Directory, "*.*tf") : [])
            _privateFonts.AddFontFile(f);
    }

    /// <summary>Redraw interval needed by an animated GIF or video background, or null for a static theme.</summary>
    public int? AnimationIntervalMs => Background()?.FrameIntervalMs;

    /// <summary>Error from the background file (missing file, no ffmpeg, ...), if any.</summary>
    public string? BackgroundError { get; private set; }

    private IFrameSource? Background()
    {
        if (_background != null || BackgroundError != null || _theme.Background.Image is not { Length: > 0 } path) return _background;
        try
        {
            _background = FrameSources.Open(_theme.Resolve(path), Width, Height, _theme.Background.Fit, _theme.Background.Fps);
        }
        catch (Exception e)
        {
            BackgroundError = e.Message;
        }
        return _background;
    }

    public Bitmap Render()
    {
        var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        g.Clear(ParseColor(_theme.Background.Color));
        if (Background()?.FrameAt(Environment.TickCount64 - _start) is { } bgFrame)
            DrawFitted(g, bgFrame, new RectangleF(0, 0, Width, Height), _theme.Background.Fit);

        foreach (var w in _theme.Widgets.Where(w => w.Visible))
        {
            switch (w)
            {
                case CircleWidget c: DrawCircle(g, c); break;
                case ImageWidget i: DrawImage(g, i); break;
                case ArcWidget a: DrawArc(g, a); break;
                case BarWidget b: DrawBar(g, b); break;
                case TextWidget t: DrawText(g, t); break;
            }
        }
        return bmp;
    }

    private void DrawCircle(Graphics g, CircleWidget c)
    {
        using var brush = new SolidBrush(ParseColor(c.Color));
        g.FillEllipse(brush, c.X - c.Radius, c.Y - c.Radius, c.Radius * 2, c.Radius * 2);
    }

    private void DrawImage(Graphics g, ImageWidget i)
    {
        var frame = Image(i.Path).FrameAt(Environment.TickCount64 - _start);
        float w = i.Width > 0 ? i.Width : frame.Width, h = i.Height > 0 ? i.Height : frame.Height;
        g.DrawImage(frame, i.X, i.Y, w, h);
    }

    private void DrawArc(Graphics g, ArcWidget a)
    {
        float frac = Fraction(a.Value, a.Min, a.Max);
        var rect = new RectangleF(a.X - a.Radius, a.Y - a.Radius, a.Radius * 2, a.Radius * 2);
        var cap = a.RoundCaps ? LineCap.Round : LineCap.Flat;
        using (var track = new Pen(ParseColor(a.Track), a.Thickness) { StartCap = cap, EndCap = cap })
            g.DrawArc(track, rect, a.StartAngle, a.SweepAngle);
        if (frac <= 0) return;
        var color = a.ColorEnd is null ? ParseColor(a.Color) : Lerp(ParseColor(a.Color), ParseColor(a.ColorEnd), frac);
        using var pen = new Pen(color, a.Thickness) { StartCap = cap, EndCap = cap };
        g.DrawArc(pen, rect, a.StartAngle, Math.Max(0.5f, a.SweepAngle * frac));
    }

    private void DrawBar(Graphics g, BarWidget b)
    {
        float frac = Fraction(b.Value, b.Min, b.Max);
        using (var track = new SolidBrush(ParseColor(b.Track))) FillRounded(g, track, new RectangleF(b.X, b.Y, b.Width, b.Height));
        if (frac <= 0) return;
        using var fill = new SolidBrush(ParseColor(b.Color));
        FillRounded(g, fill, new RectangleF(b.X, b.Y, Math.Max(b.Height, b.Width * frac), b.Height));
    }

    private void DrawText(Graphics g, TextWidget t)
    {
        var text = Format(t.Text);
        var font = Font(t.Font, t.Size, t.Bold);
        using var brush = new SolidBrush(ParseColor(t.Color));
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = t.Align switch { TextAlign.Left => StringAlignment.Near, TextAlign.Right => StringAlignment.Far, _ => StringAlignment.Center },
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        };
        g.DrawString(text, font, brush, new PointF(t.X, t.Y), fmt);
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_./\-]+)(?::([^}]*))?\}")]
    private static partial Regex Placeholder();

    /// <summary>Expands {key:format} placeholders.</summary>
    public string Format(string template) => Placeholder().Replace(template, m =>
    {
        var key = m.Groups[1].Value;
        var fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
        switch (key)
        {
            case "time": return DateTime.Now.ToString(fmt ?? "HH:mm", CultureInfo.CurrentCulture);
            case "date": return DateTime.Now.ToString(fmt ?? "dd.MM.yyyy", CultureInfo.CurrentCulture);
        }
        var v = _valueOf(key);
        return v is null ? "--" : v.Value.ToString(fmt ?? "0", CultureInfo.CurrentCulture);
    });

    private float Fraction(string key, float min, float max)
    {
        var v = _valueOf(key);
        if (v is null || max <= min) return 0;
        return Math.Clamp((v.Value - min) / (max - min), 0, 1);
    }

    private AnimatedImage Image(string path)
    {
        var full = _theme.Resolve(path);
        if (!_images.TryGetValue(full, out var img)) _images[full] = img = new AnimatedImage(full);
        return img;
    }

    private Font Font(string family, float size, bool bold)
    {
        if (_fonts.TryGetValue((family, size, bold), out var f)) return f;
        var style = bold ? FontStyle.Bold : FontStyle.Regular;
        var priv = _privateFonts.Families.FirstOrDefault(x => x.Name.Equals(family, StringComparison.OrdinalIgnoreCase));
        f = priv != null && priv.IsStyleAvailable(style)
            ? new Font(priv, size, style, GraphicsUnit.Pixel)
            : priv != null ? new Font(priv, size, FontStyle.Regular, GraphicsUnit.Pixel)
            : new Font(family, size, style, GraphicsUnit.Pixel);
        return _fonts[(family, size, bold)] = f;
    }

    private static void DrawFitted(Graphics g, Image img, RectangleF area, ImageFit fit)
    {
        if (fit == ImageFit.Stretch)
        {
            g.DrawImage(img, area);
            return;
        }
        float sx = area.Width / img.Width, sy = area.Height / img.Height;
        float s = fit == ImageFit.Cover ? Math.Max(sx, sy) : Math.Min(sx, sy);
        float w = img.Width * s, h = img.Height * s;
        g.DrawImage(img, area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    private static void FillRounded(Graphics g, Brush brush, RectangleF r)
    {
        float d = Math.Min(r.Height, r.Width);
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    /// <summary>Parses #RGB, #RRGGBB, #AARRGGBB or a named colour.</summary>
    public static Color ParseColor(string s)
    {
        s = s.Trim();
        if (!s.StartsWith('#')) return Color.FromName(s);
        var hex = s[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        uint v = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return hex.Length == 8 ? Color.FromArgb((int)v) : Color.FromArgb(255, Color.FromArgb((int)v));
    }

    private static Color Lerp(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public void Dispose()
    {
        _background?.Dispose();
        foreach (var i in _images.Values) i.Dispose();
        foreach (var f in _fonts.Values) f.Dispose();
        _privateFonts.Dispose();
    }
}
