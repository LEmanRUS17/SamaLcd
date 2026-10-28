using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SamaLcd.Core.Device;

/// <summary>
/// Pixel formats understood by the panel firmware.
///
/// Full frame (cmd 200): raw BGRA, 4 bytes per pixel, row-major.
///
/// Segment stream (cmd 204 diff / cmd 201 sparse), a sequence of segments:
///   [idx:24 BE][len:16 BE][len * 3 pixel bytes]   - run of len pixels starting at pixel index idx
///   [idx:24 BE | 0x800000][3 pixel bytes]          - single pixel
/// Each pixel is 3 bytes: (B &amp; 0xFC) | (a4 &gt;&gt; 2), (G &amp; 0xFC) | (a4 &amp; 3), R
/// where a4 is a 4-bit alpha.
/// </summary>
public static class FrameCodec
{
    public const int MaxRun = 65000;

    /// <summary>Converts a bitmap to the raw BGRA buffer used by full frames.</summary>
    public static byte[] ToBgra(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bmp.Width * 4;
            var result = new byte[rowBytes * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, result, y * rowBytes, rowBytes);
            return result;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// Encodes the pixels that differ between two BGRA frames (cmd 204 payload, without the EF 69 trailer).
    /// Matches Sama's encoder byte for byte, except that runs longer than <paramref name="maxRun"/>
    /// are split instead of failing.
    /// </summary>
    public static byte[] EncodeDiff(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur, int maxRun = MaxRun)
    {
        if (prev.Length != cur.Length) throw new ArgumentException("Frames differ in size");
        var w = new SegmentWriter(cur.Length / 4 * 3 + cur.Length / 4 * 5 / 2 + 16);
        int pixels = cur.Length / 4;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            bool changed = prev[o] != cur[o] || prev[o + 1] != cur[o + 1] || prev[o + 2] != cur[o + 2] || prev[o + 3] != cur[o + 3];
            if (!changed)
            {
                w.Close(compactSingle: true);
                continue;
            }
            if (w.RunLength >= maxRun) w.Close(compactSingle: true);
            w.Put(i, cur[o], cur[o + 1], cur[o + 2], cur[o + 3] >> 4);
        }
        w.Close(compactSingle: false);
        return w.ToArray();
    }

    /// <summary>
    /// Encodes only visible pixels of a BGRA frame (cmd 201 payload). Pixels with alpha &lt;= 10 are skipped,
    /// alpha &gt;= 150 counts as opaque. Unlike Sama, no pixel is dropped when a run reaches <paramref name="maxRun"/>.
    /// </summary>
    public static byte[] EncodeSparse(ReadOnlySpan<byte> bgra, int maxRun = MaxRun)
    {
        var w = new SegmentWriter(bgra.Length / 4 * 3 + bgra.Length / 4 * 5 / 2 + 16);
        int pixels = bgra.Length / 4;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            int a = bgra[o + 3];
            if (a <= 10)
            {
                w.Close(compactSingle: true);
                continue;
            }
            if (w.RunLength >= maxRun) w.Close(compactSingle: true);
            if (a >= 150) a = 255;
            w.Put(i, bgra[o], bgra[o + 1], bgra[o + 2], a >> 4);
        }
        w.Close(compactSingle: false);
        return w.ToArray();
    }

    /// <summary>
    /// Mask of the overlay's visible area while a video plays (cmd 208 and the tail of cmd 204):
    /// [idx:24 BE][len:16 BE] for every run of pixels with alpha &gt; 15. Mirrors Sama, including the pixel
    /// skipped after a run of <paramref name="maxRun"/> and the 5 zero bytes after a run that reaches the last pixel.
    /// </summary>
    public static byte[] EncodeMask(ReadOnlySpan<byte> bgra, int maxRun = MaxRun)
    {
        int pixels = bgra.Length / 4;
        var buf = new byte[pixels * 4 + 16];
        int pos = 0, header = 0, run = 0;
        bool inRun = false, skipNext = false;
        for (int i = 0; i < pixels; i++)
        {
            bool visible = bgra[i * 4 + 3] > 15 && !skipNext;
            skipNext = false;
            if (visible)
            {
                if (!inRun)
                {
                    header = pos;
                    buf[pos] = (byte)(i >> 16);
                    buf[pos + 1] = (byte)(i >> 8);
                    buf[pos + 2] = (byte)i;
                    inRun = true;
                }
                if (++run >= maxRun) skipNext = true;
            }
            else if (inRun)
            {
                buf[header + 3] = (byte)(run >> 8);
                buf[header + 4] = (byte)run;
                inRun = false;
                run = 0;
                pos += 5;
            }
        }
        if (inRun)
        {
            buf[header + 3] = (byte)(run >> 8);
            buf[header + 4] = (byte)run;
            pos += 10;
        }
        return buf.AsSpan(0, pos).ToArray();
    }

    /// <summary>Applies a segment stream onto a BGRA buffer (used by tests and the preview).</summary>
    public static void ApplySegments(ReadOnlySpan<byte> segments, Span<byte> bgra)
    {
        int p = 0;
        while (p < segments.Length)
        {
            int idx = (segments[p] << 16) | (segments[p + 1] << 8) | segments[p + 2];
            int len;
            if ((idx & 0x800000) != 0)
            {
                idx &= 0x7FFFFF;
                len = 1;
                p += 3;
            }
            else
            {
                len = (segments[p + 3] << 8) | segments[p + 4];
                p += 5;
            }
            for (int k = 0; k < len; k++, p += 3)
            {
                int o = (idx + k) * 4;
                byte b0 = segments[p], b1 = segments[p + 1];
                bgra[o] = (byte)(b0 & 0xFC);
                bgra[o + 1] = (byte)(b1 & 0xFC);
                bgra[o + 2] = segments[p + 2];
                bgra[o + 3] = (byte)((((b0 & 3) << 2) | (b1 & 3)) << 4);
            }
        }
    }

    private sealed class SegmentWriter(int capacity)
    {
        private byte[] _buf = new byte[capacity];
        private int _pos;
        private int _header = -1;

        public int RunLength { get; private set; }

        public void Put(int index, byte b, byte g, byte r, int alpha4)
        {
            if (_header < 0)
            {
                Ensure(5);
                _header = _pos;
                _buf[_pos] = (byte)(index >> 16);
                _buf[_pos + 1] = (byte)(index >> 8);
                _buf[_pos + 2] = (byte)index;
                _pos += 5;
            }
            Ensure(3);
            _buf[_pos] = (byte)((b & 0xFC) + (alpha4 >> 2));
            _buf[_pos + 1] = (byte)((g & 0xFC) + (alpha4 & 3));
            _buf[_pos + 2] = r;
            _pos += 3;
            RunLength++;
        }

        public void Close(bool compactSingle)
        {
            if (_header < 0) return;
            if (compactSingle && RunLength == 1)
            {
                _buf[_header] |= 0x80;
                _buf[_header + 3] = _buf[_header + 5];
                _buf[_header + 4] = _buf[_header + 6];
                _buf[_header + 5] = _buf[_header + 7];
                _pos -= 2;
            }
            else
            {
                _buf[_header + 3] = (byte)(RunLength >> 8);
                _buf[_header + 4] = (byte)RunLength;
            }
            _header = -1;
            RunLength = 0;
        }

        public byte[] ToArray() => _buf.AsSpan(0, _pos).ToArray();

        private void Ensure(int n)
        {
            if (_pos + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _pos + n));
        }
    }
}
