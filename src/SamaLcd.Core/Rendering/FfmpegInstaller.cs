using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SamaLcd.Core.Rendering;

/// <summary>Windows builds supplied by gyan.dev, a provider linked by ffmpeg.org.</summary>
public static class FfmpegInstaller
{
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private static readonly SemaphoreSlim Gate = new(1);
    public static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SamaLcd", "tools", "ffmpeg.exe");

    public static async Task<string> EnsureAsync(Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            var adjacent = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(adjacent)) return adjacent;
            var existing = VideoSource.FindFfmpeg();
            var target = adjacent;
            try { using var probe = File.Create(adjacent + ".write-test"); }
            catch (UnauthorizedAccessException) { target = CachePath; }
            finally { if (File.Exists(adjacent + ".write-test")) File.Delete(adjacent + ".write-test"); }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (existing != null)
            {
                if (!string.Equals(existing, target, StringComparison.OrdinalIgnoreCase)) File.Copy(existing, target, true);
                // PATH may contain a package-manager shim that cannot be relocated.
                if (CanRun(target)) return target;
                File.Delete(target);
            }
            progress?.Invoke("Downloading FFmpeg…");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var checksum = await client.GetStringAsync(DownloadUrl + ".sha256", cancellationToken);
            var expected = Regex.Match(checksum, @"\b[a-fA-F0-9]{64}\b").Value;
            if (expected.Length != 64) throw new IOException("Invalid FFmpeg checksum response.");
            temporary = Path.Combine(Path.GetTempPath(), "samalcd-ffmpeg-" + Guid.NewGuid());
            Directory.CreateDirectory(temporary);
            var zip = Path.Combine(temporary, "ffmpeg.zip");
            await using (var input = await client.GetStreamAsync(DownloadUrl, cancellationToken))
            await using (var output = File.Create(zip)) await input.CopyToAsync(output, cancellationToken);
            await using (var file = File.OpenRead(zip))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("FFmpeg download checksum mismatch.");
            InstallArchive(zip, target);
            return target;
        }
        finally
        {
            try { if (temporary != null && Directory.Exists(temporary)) Directory.Delete(temporary, true); }
            finally { Gate.Release(); }
        }
    }

    private static bool CanRun(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "-version")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(true); return false; }
            return process.ExitCode == 0 && output.GetAwaiter().GetResult().StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    public static void InstallArchive(string archive, string target)
    {
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.Entries.SingleOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("FFmpeg executable missing from archive.");
        var staging = target + "." + Guid.NewGuid() + ".tmp";
        try
        {
            entry.ExtractToFile(staging);
            using (var file = File.OpenRead(staging))
                if (file.ReadByte() != 'M' || file.ReadByte() != 'Z') throw new IOException("Invalid FFmpeg executable.");
            File.Move(staging, target, true);
            var license = zip.Entries.FirstOrDefault(e => e.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase));
            license?.ExtractToFile(Path.Combine(Path.GetDirectoryName(target)!, "FFmpeg-LICENSE.txt"), true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
