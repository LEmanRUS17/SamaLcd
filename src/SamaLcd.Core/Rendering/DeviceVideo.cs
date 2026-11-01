using L = SamaLcd.Core.Localization.Localizer;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SamaLcd.Core.Rendering;

/// <summary>A background video converted for playback by the panel itself.</summary>
public sealed record PreparedVideo(string LocalPath, string RemoteName, string Thumbnail, long Size);

/// <summary>
/// Converts a video or animated GIF into the format Sama uploads to the panel
/// (H.264, W×H, 2400 kbit/s, 24 fps, yuv420p, no audio) and caches the result.
/// </summary>
public static class DeviceVideo
{
    public static int TargetBitrate(long maximumBytes, double seconds)
    {
        if (maximumBytes <= 0 || !double.IsFinite(seconds) || seconds <= 0) throw new ArgumentOutOfRangeException();
        // MP4 overhead and rate-control slack; keep a usable quality floor rather than silently truncating.
        var kbps = Math.Min(2400, Math.Floor(maximumBytes * 8.0 * .90 / seconds / 1000 / 25) * 25);
        if (kbps < 125) throw new IOException(L.Get("Video is too long for the available storage. Choose a shorter clip or free space."));
        return (int)kbps;
    }

    public static double Duration(string ffmpeg, string source)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true };
        foreach(var a in new[]{"-hide_banner","-i",source}) psi.ArgumentList.Add(a);
        using var p=Process.Start(psi)!;
        var text=p.StandardError.ReadToEndAsync();
        if(!p.WaitForExit(30000)) {p.Kill(true);throw new TimeoutException("Video metadata timeout");}
        var m=Regex.Match(text.GetAwaiter().GetResult(), @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if(!m.Success) throw new IOException(L.Get("Cannot determine video duration."));
        return double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)*3600+double.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture)*60+double.Parse(m.Groups[3].Value,CultureInfo.InvariantCulture);
    }
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

    public static string CropPoster(string source, double seconds = 0)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        seconds = Math.Round(seconds, 2);
        var info = new FileInfo(source);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{seconds.ToString("F2", CultureInfo.InvariantCulture)}")))[..16];
        Directory.CreateDirectory(CacheDirectory);
        var path = Path.Combine(CacheDirectory, hash + "-source.png");
        if (!File.Exists(path)) Run(VideoSource.FindFfmpeg() ?? throw new IOException("FFmpeg unavailable"), ["-y", "-ss", seconds.ToString("F2", CultureInfo.InvariantCulture), "-i", source, "-frames:v", "1", path], 30_000);
        if (!File.Exists(path)) throw new IOException(L.Get("Cannot preview this file"));
        return path;
    }

    public static PreparedVideo Prepare(string source, ImageFit fit, int rotation, int width, int height, Action<string>? log = null, SquareCrop? crop = null, long? maximumBytes = null)
    {
        var ffmpeg = VideoSource.FindFfmpeg() ?? throw new FileNotFoundException(L.Get("Video requires ffmpeg.exe next to SamaLcd.exe."));
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException(L.Get("Background file not found"), source);

        int bitrate = maximumBytes.HasValue ? TargetBitrate(maximumBytes.Value, Duration(ffmpeg,source)) : 2400;
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{fit}|{rotation}|{width}x{height}|{crop?.Normalize()}|budget-v1|{bitrate}";
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
        if (crop != null) scale = crop.Filter() + "," + $"scale={width}:{height}";
        // the overlay is rotated in software, so the video has to be turned the same way
        var turn = (((rotation % 360) + 360) % 360) switch
        {
            90 => ",transpose=1",
            180 => ",hflip,vflip",
            270 => ",transpose=2",
            _ => "",
        };

        if (!File.Exists(mp4) || maximumBytes.HasValue && new FileInfo(mp4).Length > maximumBytes.Value)
        {
            log?.Invoke(L.Get("Converting {0} for the panel…", info.Name));
            var tmp = mp4 + ".tmp.mp4";
            var passlog = mp4 + "." + Guid.NewGuid() + ".pass";
            try
            {
                string[] options = ["-y", "-i", source, "-map", "0:v:0", "-vf", $"{scale}{turn},setsar=1:1", "-c:v", "libx264", "-preset", "medium",
                    "-b:v", bitrate+"k", "-maxrate", Math.Min(2400,bitrate*2)+"k", "-bufsize", (bitrate*2)+"k", "-r", "24", "-an", "-sn", "-pix_fmt", "yuv420p"];
                if(maximumBytes.HasValue) Run(ffmpeg,[..options,"-pass","1","-passlogfile",passlog,"-f","null","NUL"]);
                Run(ffmpeg,[..options,..(maximumBytes.HasValue ? new[]{"-pass","2","-passlogfile",passlog} : Array.Empty<string>()),"-movflags","+faststart","-f","mp4",tmp]);
                if(maximumBytes.HasValue && new FileInfo(tmp).Length>maximumBytes.Value)
                    throw new IOException(L.Get("Compressed video still exceeds the storage limit. Choose a shorter clip."));
                File.Move(tmp, mp4, overwrite: true);
            }
            finally
            {
                if(File.Exists(tmp)) File.Delete(tmp);
                foreach(var file in Directory.GetFiles(CacheDirectory,Path.GetFileName(passlog)+"*")) File.Delete(file);
            }
        }
        if (!File.Exists(thumb))
            Run(ffmpeg, ["-y", "-i", source, "-vf", scale, "-frames:v", "1", thumb]);

        return new PreparedVideo(mp4, $"{RemotePrefix}{hash}.mp4", thumb, new FileInfo(mp4).Length);
    }

    private static void Run(string exe, string[] args, int timeoutMs = 10 * 60_000)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill();
            throw new TimeoutException(L.Get("ffmpeg took too long"));
        }
        if (p.ExitCode != 0)
        {
            var lines = err.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            throw new IOException(L.Get("ffmpeg failed: {0}", lines.LastOrDefault()?.Trim() ?? ""));
        }
    }
}
