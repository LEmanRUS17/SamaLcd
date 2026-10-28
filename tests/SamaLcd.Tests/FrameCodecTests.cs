using System.Drawing;
using SamaLcd.Core.Device;

namespace SamaLcd.Tests;

/// <summary>
/// The files in Reference/ were produced by running the original Sama.exe encoders on the same inputs.
/// </summary>
public class FrameCodecTests
{
    private static string Ref(string name) => Path.Combine(AppContext.BaseDirectory, "Reference", name);

    [Fact]
    public void ToBgra_UsesBgraOrder()
    {
        using var bmp = new Bitmap(2, 1);
        bmp.SetPixel(0, 0, Color.FromArgb(200, 10, 20, 30));
        bmp.SetPixel(1, 0, Color.FromArgb(255, 40, 50, 60));
        // Same bytes Sama produced for this bitmap.
        Assert.Equal(new byte[] { 0x1E, 0x14, 0x0A, 0xC8, 0x3C, 0x32, 0x28, 0xFF }, FrameCodec.ToBgra(bmp));
    }

    [Fact]
    public void EncodeDiff_MatchesSama()
    {
        var prev = File.ReadAllBytes(Ref("diff.prev"));
        var cur = File.ReadAllBytes(Ref("diff.cur"));
        var expected = File.ReadAllBytes(Ref("diff.204"));
        Assert.Equal(expected, FrameCodec.EncodeDiff(prev, cur));
    }

    [Fact]
    public void EncodeDiff_ReproducesFrame()
    {
        var rnd = new Random(5);
        var prev = new byte[480 * 480 * 4];
        rnd.NextBytes(prev);
        var cur = (byte[])prev.Clone();
        for (int i = 0; i < cur.Length; i += 4 * rnd.Next(1, 7)) cur[i] ^= 0x10;
        Array.Fill(cur, (byte)0x44, 4000, 600_000); // one run far longer than 65000 pixels

        var applied = (byte[])prev.Clone();
        FrameCodec.ApplySegments(FrameCodec.EncodeDiff(prev, cur), applied);
        for (int i = 0; i < cur.Length; i += 4)
        {
            Assert.Equal(cur[i] & 0xFC, applied[i] & 0xFC);
            Assert.Equal(cur[i + 1] & 0xFC, applied[i + 1] & 0xFC);
            Assert.Equal(cur[i + 2], applied[i + 2]);
        }
    }

    [Fact]
    public void EncodeDiff_IdenticalFramesGiveNothing()
    {
        var f = new byte[400];
        Assert.Empty(FrameCodec.EncodeDiff(f, f));
    }

    [Fact]
    public void EncodeMask_ListsVisibleRuns()
    {
        // 10 pixels: visible at 2..4 and 7 (alpha > 15), the last run touching the end at 9
        byte[] alpha = [0, 15, 16, 255, 200, 0, 0, 255, 0, 255];
        var bgra = new byte[alpha.Length * 4];
        for (int i = 0; i < alpha.Length; i++) bgra[i * 4 + 3] = alpha[i];
        var mask = FrameCodec.EncodeMask(bgra);
        Assert.Equal(new byte[]
        {
            0, 0, 2, 0, 3,
            0, 0, 7, 0, 1,
            0, 0, 9, 0, 1,
            0, 0, 0, 0, 0, // Sama leaves 5 zero bytes after a run that reaches the last pixel
        }, mask);
    }

    [Fact]
    public void EncodeSparse_MatchesSama()
    {
        using var bmp = new Bitmap(Ref("noise.png"));
        var expected = File.ReadAllBytes(Ref("noise.201"));
        Assert.Equal(expected, FrameCodec.EncodeSparse(FrameCodec.ToBgra(bmp)));
    }

    [Fact]
    public void EncodeSparse_LongRunsKeepEveryPixel()
    {
        using var bmp = new Bitmap(Ref("opaque.png"));
        var bgra = FrameCodec.ToBgra(bmp);

        var mine = new byte[bgra.Length];
        FrameCodec.ApplySegments(FrameCodec.EncodeSparse(bgra), mine);
        var sama = new byte[bgra.Length];
        FrameCodec.ApplySegments(File.ReadAllBytes(Ref("opaque.201")), sama);

        int differences = 0;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            Assert.Equal(bgra[i + 2], mine[i + 2]);
            Assert.Equal(0xF0, mine[i + 3]);
            if (!mine.AsSpan(i, 4).SequenceEqual(sama.AsSpan(i, 4))) differences++;
        }
        // Sama drops one pixel each time a run hits 65000; everything else is identical.
        Assert.Equal(3, differences);
    }
}
