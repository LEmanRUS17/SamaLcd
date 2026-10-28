using System.Drawing;
using System.Drawing.Imaging;

namespace SamaLcd.Core.Rendering;

/// <summary>A still image or an animated GIF with its frames decoded up front.</summary>
public sealed class AnimatedImage : IFrameSource
{
    private readonly Bitmap[] _frames;
    private readonly int[] _ends; // cumulative end time of each frame, ms

    public bool IsAnimated => _frames.Length > 1;
    public int MinDelayMs { get; }
    public int? FrameIntervalMs => IsAnimated ? MinDelayMs : null;

    public AnimatedImage(string path)
    {
        using var src = Image.FromFile(path);
        int count = src.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? src.GetFrameCount(FrameDimension.Time) : 1;
        _frames = new Bitmap[count];
        _ends = new int[count];
        int[] delays = ReadDelays(src, count);
        int t = 0;
        for (int i = 0; i < count; i++)
        {
            if (count > 1) src.SelectActiveFrame(FrameDimension.Time, i);
            _frames[i] = new Bitmap(src);
            t += delays[i];
            _ends[i] = t;
        }
        MinDelayMs = delays.Min();
    }

    private static int[] ReadDelays(Image img, int count)
    {
        var result = Enumerable.Repeat(100, count).ToArray();
        if (count <= 1) return result;
        try
        {
            var prop = img.GetPropertyItem(0x5100); // FrameDelay, 1/100 s, 4 bytes per frame
            for (int i = 0; i < count && i * 4 + 3 < prop!.Value!.Length; i++)
            {
                int d = BitConverter.ToInt32(prop.Value, i * 4) * 10;
                result[i] = d < 20 ? 100 : d; // browsers treat tiny delays as 100 ms
            }
        }
        catch (ArgumentException)
        {
            // no delay metadata
        }
        return result;
    }

    public Bitmap FrameAt(long elapsedMs)
    {
        if (_frames.Length == 1) return _frames[0];
        long t = elapsedMs % _ends[^1];
        int i = Array.FindIndex(_ends, e => t < e);
        return _frames[i < 0 ? 0 : i];
    }

    public void Dispose()
    {
        foreach (var f in _frames) f.Dispose();
    }
}
