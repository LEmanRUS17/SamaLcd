using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SamaLcd.Core.Rendering;

/// <summary>A background video converted for playback by the panel itself.</summary>
public sealed record PreparedVideo(string LocalPath, string RemoteName, string Thumbnail, long Size);

/// <summary>
/// Converts a video or animated GIF into the format Sama uploads to the panel
/// (H.264, W×H, 2400 kbit/s, 24 fps, yuv420p, no audio) and caches the result.
/// </summary>
public static class DeviceVideo
{
    public const string RemotePrefix = "samalcd_";

    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SamaLcd", "video");

    /// <summary>True for files the panel should play itself: videos and animated GIFs.</summary>
    public static bool Supports(string path)
    {
        if (FrameSources.IsVideo(path)) return true;
        if (!path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        using var img = new AnimatedImage(path);
        return img.IsAnimated;
    }

    public static PreparedVideo Prepare(string source, ImageFit fit, int rotation, int width, int height, Action<string>? log = null)
    {
        var ffmpeg = VideoSource.FindFfmpeg() ?? throw new FileNotFoundException("ffmpeg.exe not found: put it next to SamaLcd.exe");
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("Background file not found", source);

        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{fit}|{rotation}|{width}x{height}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant();
        Directory.CreateDirectory(CacheDirectory);
        var mp4 = Path.Combine(CacheDirectory, $"{hash}.mp4");
        var thumb = Path.Combine(CacheDirectory, $"{hash}.png");

        var scale = fit switch
        {
            ImageFit.Stretch => $"scale={width}:{height}",
            ImageFit.Contain => $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black",
            _ => $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}",
        };
        // the overlay is rotated in software, so the video has to be turned the same way
        var turn = (((rotation % 360) + 360) % 360) switch
        {
            90 => ",transpose=1",
            180 => ",hflip,vflip",
            270 => ",transpose=2",
            _ => "",
        };

        if (!File.Exists(mp4))
        {
            log?.Invoke($"Converting {info.Name} for the panel...");
            var tmp = mp4 + ".tmp.mp4";
            Run(ffmpeg, ["-y", "-i", source, "-vf", $"{scale}{turn},setsar=1:1", "-c:v", "libx264", "-b:v", "2400k", "-bufsize", "2400k",
                "-r", "24", "-an", "-pix_fmt", "yuv420p", "-f", "mp4", tmp]);
            File.Move(tmp, mp4, overwrite: true);
        }
        if (!File.Exists(thumb))
            Run(ffmpeg, ["-y", "-i", source, "-vf", scale, "-frames:v", "1", thumb]);

        return new PreparedVideo(mp4, $"{RemotePrefix}{hash}.mp4", thumb, new FileInfo(mp4).Length);
    }

    private static void Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10 * 60_000))
        {
            p.Kill();
            throw new TimeoutException("ffmpeg took too long");
        }
        if (p.ExitCode != 0)
        {
            var lines = err.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            throw new IOException($"ffmpeg failed: {lines.LastOrDefault()?.Trim()}");
        }
    }
}
