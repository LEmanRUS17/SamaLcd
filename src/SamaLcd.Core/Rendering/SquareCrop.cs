using System.Drawing;
using System.Globalization;

namespace SamaLcd.Core.Rendering;

/// <summary>Source-relative top-left and size relative to the source's shorter side.</summary>
public sealed record SquareCrop(double X = 0, double Y = 0, double Size = 1)
{
    public SquareCrop Normalize() => new(
        double.IsFinite(X) ? Math.Clamp(X, 0, 1) : 0,
        double.IsFinite(Y) ? Math.Clamp(Y, 0, 1) : 0,
        double.IsFinite(Size) ? Math.Clamp(Size, 0.05, 1) : 1);

    public RectangleF Rectangle(int width, int height)
    {
        var c = Normalize();
        float side = (float)(Math.Min(width, height) * c.Size);
        return new RectangleF((float)Math.Clamp(width * c.X, 0, width - side),
            (float)Math.Clamp(height * c.Y, 0, height - side), side, side);
    }

    public string Filter()
    {
        var c = Normalize();
        string N(double n) => n.ToString("0.########", CultureInfo.InvariantCulture);
        // Even coordinates and dimensions for H.264 / yuv420p, before scaling or rotation.
        string side = $"max(2,trunc(min(iw,ih)*{N(c.Size)}/2)*2)";
        return $"crop=w='{side}':h='{side}':x='trunc(min(max(iw*{N(c.X)},0),iw-ow)/2)*2':y='trunc(min(max(ih*{N(c.Y)},0),ih-oh)/2)*2'";
    }
}
