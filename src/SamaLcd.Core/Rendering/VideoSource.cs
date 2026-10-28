using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SamaLcd.Core.Rendering;

/// <summary>Something that can be drawn as a (possibly moving) picture: an image, a GIF or a video.</summary>
public interface IFrameSource : IDisposable
{
    /// <summary>
    /// The frame to show <paramref name="elapsedMs"/> after start; null while nothing is decoded yet.
    /// The bitmap stays owned by the source and is valid until the next call.
    /// </summary>
    Bitmap? FrameAt(long elapsedMs);
    /// <summary>Redraw interval needed for smooth playback, or null for a still picture.</summary>
    int? FrameIntervalMs { get; }
}

public static class FrameSources
{
    public static readonly string[] VideoExtensions = [".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v", ".wmv"];

    public static bool IsVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IFrameSource Open(string path, int width, int height, ImageFit fit, int fps) =>
        IsVideo(path) ? new VideoSource(path, width, height, fit, fps) : new AnimatedImage(path);
}

/// <summary>
/// Plays a video file in a loop by piping raw BGRA frames out of ffmpeg, already scaled to the panel size.
/// ffmpeg paces itself (-re), so the newest decoded frame is always the one to show.
/// </summary>
public sealed class VideoSource : IFrameSource
{
    private readonly Process _ffmpeg;
    private readonly Thread _reader;
    private readonly int _width, _height;
    private readonly object _lock = new();
    private Bitmap? _pending; // newest decoded frame, not shown yet
    private Bitmap? _shown;   // frame handed out by FrameAt
    private volatile bool _disposed;

    public int? FrameIntervalMs { get; }
    public string? Error { get; private set; }

    /// <summary>Path of ffmpeg.exe: next to the program, on PATH, or the copy installed with SAMA.</summary>
    public static string? FindFfmpeg()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "ffmpeg.exe")));
        candidates.Add(@"C:\Program Files (x86)\SAMA\ffmpeg.exe");
        return candidates.FirstOrDefault(File.Exists);
    }

    public VideoSource(string path, int width, int height, ImageFit fit, int fps)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Video not found", path);
        var ffmpeg = FindFfmpeg() ?? throw new FileNotFoundException("ffmpeg.exe not found: put it next to SamaLcd.exe");
        _width = width;
        _height = height;
        fps = Math.Clamp(fps, 1, 60);
        FrameIntervalMs = 1000 / fps;

        var scale = fit switch
        {
            ImageFit.Stretch => $"scale={width}:{height}",
            ImageFit.Contain => $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black",
            _ => $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}",
        };
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-re", "-stream_loop", "-1", "-i", path, "-an",
                     "-vf", $"fps={fps},{scale}", "-f", "rawvideo", "-pix_fmt", "bgra", "-" })
            psi.ArgumentList.Add(a);
        _ffmpeg = Process.Start(psi)!;
        _ffmpeg.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Error = e.Data; };
        _ffmpeg.BeginErrorReadLine();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "video decode" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var stream = _ffmpeg.StandardOutput.BaseStream;
        var buf = new byte[_width * _height * 4];
        try
        {
            while (!_disposed)
            {
                int got = 0;
                while (got < buf.Length)
                {
                    int n = stream.Read(buf, got, buf.Length - got);
                    if (n <= 0) return;
                    got += n;
                }
                var bmp = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < _height; y++)
                    Marshal.Copy(buf, y * _width * 4, data.Scan0 + y * data.Stride, _width * 4);
                bmp.UnlockBits(data);
                Bitmap? old;
                lock (_lock)
                {
                    old = _pending;
                    _pending = bmp;
                }
                old?.Dispose(); // decoded faster than drawn: drop the skipped frame
            }
        }
        catch (Exception) when (_disposed)
        {
            // pipe closed on shutdown
        }
    }

    public Bitmap? FrameAt(long elapsedMs)
    {
        lock (_lock)
        {
            if (_pending != null)
            {
                _shown?.Dispose();
                _shown = _pending;
                _pending = null;
            }
            return _shown;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            if (!_ffmpeg.HasExited) _ffmpeg.Kill();
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        _reader.Join(2000);
        _ffmpeg.Dispose();
        lock (_lock)
        {
            _pending?.Dispose();
            _shown?.Dispose();
        }
    }
}
