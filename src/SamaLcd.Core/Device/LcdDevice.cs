using System.IO.Ports;
using System.Text;

namespace SamaLcd.Core.Device;

/// <summary>Command codes (byte 0 of every 250-byte packet).</summary>
public enum LcdCommand : byte
{
    Hello = 1,              // payload C5 D3, reply e.g. "chs_5inch.dev1_rom1.89"
    StorageInfo = 100,      // reply "total-used-free-sdTotal-sdUsed-sdFree" in MB
    ListDir = 101,          // payload: folder path; reply "result:dir:file:a.mp4/b.mp4/" (creates the folder if missing)
    DeleteFile = 102,       // payload: file path
    FileSize = 110,         // payload: file path; reply: size in bytes, "0" if missing
    CreateFile = 111,       // payload: path + size (LE32); reply "create_success", then the file bytes follow
    PlayVideo = 120,        // payload: path, flag 1; reply "play_video_success"
    StopVideo = 121,
    Brightness = 123,       // payload: 1 byte
    SaveSettings = 125,     // payload: brightness, startMode, 0, imgFlip, sleepDelay
    Power = 131,            // length 1 = off, length 0 = on
    Restart = 132,
    StopMedia = 150,
    FullFrame = 200,        // followed by W*H*4 bytes of BGRA
    SparseFrame = 201,      // followed by a segment stream (see FrameCodec)
    OverlayFrame = 202,     // like 200 while a video plays: BGRA whose alpha blends over the video
    DiffFrame = 204,        // payload: seq BE32 [+ mask length BE32]; followed by segments [+ mask] + EF 69
    OverlayMask = 208,      // followed by the overlay's opaque-area mask + EF 69 (after 202)
    QueryRender = 206,      // reply "seqImgIndex:N|renderCnt:M"
    QueryStatus = 207,      // reply "needReSend:X|renderCnt:M"
}

public sealed record DeviceStatus(bool NeedResend, int RenderCount, string Raw);

/// <summary>
/// Low level driver for the SAMA / Turing "rev C" family of USB LCD panels.
/// Every command is a 250-byte packet: [cmd][EF 69][length BE32][flag][0 0][payload...].
/// Bulk data follows in 250-byte packets, each carrying 249 data bytes.
/// </summary>
public sealed class LcdDevice : IDisposable
{
    public const int PacketSize = 250;
    public const int ChunkSize = 249;
    private const ushort Magic = 0xEF69;

    private readonly SerialPort _port;
    private readonly object _lock = new();
    private uint _sequence;

    public int Width { get; }
    public int Height { get; }
    public string PortName => _port.PortName;
    public string? Identity { get; private set; }
    public double RomVersion { get; private set; }

    /// <summary>Delay between a full-frame header and its data. Sama uses 500 ms.</summary>
    public int FullFrameDelayMs { get; set; } = 500;

    public Action<string>? Log { get; set; }

    public LcdDevice(string portName, int width = 480, int height = 480)
    {
        Width = width;
        Height = height;
        _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 2000,
            WriteTimeout = 5000,
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 1024 * 1024,
        };
    }

    /// <summary>Finds the panel, opens it and performs the handshake.</summary>
    public static LcdDevice Connect(string? portName = null, int width = 480, int height = 480, Action<string>? log = null)
    {
        portName ??= PortLocator.Locate(log) ?? throw new IOException("LCD panel not found (no USB device 1D6B:0121 or 1A86:CA21).");
        var dev = new LcdDevice(portName, width, height) { Log = log };
        try
        {
            dev.Open();
            return dev;
        }
        catch
        {
            dev.Dispose();
            throw;
        }
    }

    public void Open()
    {
        _port.Open();
        Hello();
    }

    public string Hello()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            lock (_lock)
            {
                _port.DiscardInBuffer();
                SendCommand(LcdCommand.Hello, 1, [0xC5, 0xD3]);
                var reply = ReadReply(1500);
                if (reply.Contains("chs", StringComparison.Ordinal))
                {
                    Identity = new string(reply.Where(c => c >= ' ' && c < 127).ToArray());
                    var parts = Identity.Split("rom", StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1 && double.TryParse(new string(parts[1].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()),
                            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        RomVersion = v;
                    Log?.Invoke($"Connected to {PortName}: {Identity}");
                    return Identity;
                }
                Log?.Invoke($"Hello attempt {attempt + 1}: '{reply}'");
            }
            Thread.Sleep(1000);
        }
        throw new IOException($"Panel on {PortName} did not answer the handshake.");
    }

    /// <summary>Sets backlight brightness, 0..100 %. <paramref name="scale"/> mirrors Sama's 0.76 cap.</summary>
    public void SetBrightness(int percent, double scale = 0.76)
    {
        percent = Math.Clamp(percent, 0, 100);
        byte value = (byte)(percent * 255 / 100 * scale);
        lock (_lock) SendCommand(LcdCommand.Brightness, 1, [value]);
    }

    /// <summary>Stops video/media playback stored on the panel so streamed frames are visible.</summary>
    public void StopPlayback()
    {
        lock (_lock)
        {
            SendCommand(LcdCommand.StopVideo, 1);
            SendCommand(LcdCommand.StopMedia, 1);
            ReadReply(500);
        }
    }

    public void SetPower(bool on)
    {
        lock (_lock) SendCommand(LcdCommand.Power, on ? 0 : 1);
    }

    /// <summary>Sends a complete BGRA frame (Width*Height*4 bytes).</summary>
    public void SendFullFrame(byte[] bgra)
    {
        if (bgra.Length != Width * Height * 4) throw new ArgumentException($"Expected {Width * Height * 4} bytes, got {bgra.Length}");
        lock (_lock)
        {
            _port.DiscardInBuffer();
            SendCommand(LcdCommand.FullFrame, bgra.Length);
            if (FullFrameDelayMs > 0) Thread.Sleep(FullFrameDelayMs);
            SendData(bgra);
            // diffs are numbered from 0 after every full frame, otherwise the panel answers needReSend:1
            _sequence = 0;
            var reply = ReadReply(1000);
            if (reply.Length > 0) Log?.Invoke($"full frame: {reply}");
        }
    }

    /// <summary>Sends a pre-encoded diff segment stream (see <see cref="FrameCodec.EncodeDiff"/>).</summary>
    public void SendDiff(byte[] segments)
    {
        if (segments.Length == 0) return;
        var data = new byte[segments.Length + 2];
        segments.CopyTo(data, 0);
        data[^2] = Magic >> 8;
        data[^1] = Magic & 0xFF;
        uint seq = _sequence++;
        lock (_lock)
        {
            SendCommand(LcdCommand.DiffFrame, data.Length, [(byte)(seq >> 24), (byte)(seq >> 16), (byte)(seq >> 8), (byte)seq, 0, 0, 0, 0]);
            SendData(data);
        }
    }

    /// <summary>
    /// While a video plays on the panel: sends the overlay picture (BGRA with alpha) and its mask (cmd 202 + 208).
    /// </summary>
    public void SendOverlayFull(byte[] bgra)
    {
        if (bgra.Length != Width * Height * 4) throw new ArgumentException($"Expected {Width * Height * 4} bytes, got {bgra.Length}");
        var mask = WithMagic(FrameCodec.EncodeMask(bgra));
        lock (_lock)
        {
            _port.DiscardInBuffer();
            SendCommand(LcdCommand.OverlayFrame, bgra.Length);
            if (FullFrameDelayMs > 0) Thread.Sleep(FullFrameDelayMs);
            SendData(bgra);
            _sequence = 0;
            SendCommand(LcdCommand.OverlayMask, mask.Length);
            SendData(mask);
            var reply = ReadReply(1000);
            if (reply.Length > 0) Log?.Invoke($"overlay: {reply}");
        }
    }

    /// <summary>Overlay update while a video plays: changed pixels plus the new mask (cmd 204 with mask).</summary>
    public void SendOverlayDiff(byte[] segments, byte[] currentBgra)
    {
        var mask = FrameCodec.EncodeMask(currentBgra);
        var data = new byte[segments.Length + mask.Length + 2];
        segments.CopyTo(data, 0);
        mask.CopyTo(data, segments.Length);
        data[^2] = Magic >> 8;
        data[^1] = Magic & 0xFF;
        uint seq = _sequence++;
        int m = mask.Length;
        lock (_lock)
        {
            SendCommand(LcdCommand.DiffFrame, data.Length,
                [(byte)(seq >> 24), (byte)(seq >> 16), (byte)(seq >> 8), (byte)seq, (byte)(m >> 24), (byte)(m >> 16), (byte)(m >> 8), (byte)m]);
            SendData(data);
        }
    }

    private static byte[] WithMagic(byte[] payload)
    {
        var r = new byte[payload.Length + 2];
        payload.CopyTo(r, 0);
        r[^2] = Magic >> 8;
        r[^1] = Magic & 0xFF;
        return r;
    }

    /// <summary>Storage in KB: internal total/used/free, SD total/used/free (internal is only ~2.6 MB).</summary>
    public long[] StorageInfo()
    {
        var reply = TextCommand(LcdCommand.StorageInfo, "", 2000);
        return reply.Split('-').Select(s => long.TryParse(s.Trim(), out var v) ? v : 0).ToArray();
    }

    /// <summary>Lists file names in a folder on the panel (the panel creates the folder if it does not exist).</summary>
    public IReadOnlyList<string> ListDir(string folder)
    {
        var reply = TextCommand(LcdCommand.ListDir, folder, 3000);
        var idx = reply.IndexOf("file:", StringComparison.Ordinal);
        return idx < 0 ? [] : reply[(idx + 5)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Size of a file on the panel, 0 when it does not exist.</summary>
    public long FileSize(string path) =>
        long.TryParse(TextCommand(LcdCommand.FileSize, path, 3000).Trim(), out var v) ? v : 0;

    /// <summary>Pause after each 249 KB block of an upload; the panel drops data if it arrives faster than it can write.</summary>
    public int UploadBlockDelayMs { get; set; }

    /// <summary>Copies a local file to the panel, e.g. to /mnt/SDCARD/video/name.mp4.</summary>
    public void UploadFile(string localPath, string remotePath, Action<double>? progress = null)
    {
        StopPlayback(); // Sama stops media before uploading as well
        var dir = remotePath[..(remotePath.LastIndexOf('/') + 1)];
        ListDir(dir);
        using var file = File.OpenRead(localPath);
        if (file.Length >= uint.MaxValue) throw new IOException("File is too large (4 GB max)");
        var name = Encoding.ASCII.GetBytes(remotePath);
        var payload = new byte[name.Length + 4];
        name.CopyTo(payload, 0);
        for (int i = 0; i < 4; i++) payload[name.Length + i] = (byte)(file.Length >> (8 * i)); // little-endian here
        lock (_lock)
        {
            _port.DiscardInBuffer();
            SendCommand(LcdCommand.CreateFile, name.Length, payload);
            var reply = ReadReply(3000);
            if (!reply.Contains("create_success", StringComparison.Ordinal))
                throw new IOException($"Panel refused to create {remotePath}: '{reply}'");

            var buf = new byte[ChunkSize * 1024];
            long sent = 0;
            int n;
            while ((n = file.Read(buf, 0, buf.Length)) > 0)
            {
                SendData(buf.AsSpan(0, n));
                if (UploadBlockDelayMs > 0) Thread.Sleep(UploadBlockDelayMs);
                sent += n;
                progress?.Invoke((double)sent / file.Length);
            }

            reply = "";
            var deadline = Environment.TickCount64 + 60_000;
            while (!reply.Contains("file_rev_done", StringComparison.Ordinal) && Environment.TickCount64 < deadline)
                reply += ReadReply(1000);
            if (!reply.Contains("file_rev_done", StringComparison.Ordinal))
                throw new IOException($"Panel did not confirm the upload: '{reply}'");
        }
        var size = FileSize(remotePath);
        if (size != file.Length) throw new IOException($"Uploaded size mismatch: {size} on panel, {file.Length} local");
    }

    /// <summary>Starts looping playback of a video stored on the panel.</summary>
    public void PlayVideo(string remotePath)
    {
        var reply = TextCommand(LcdCommand.PlayVideo, remotePath, 6000, flag: 1, until: "play_video_success");
        if (!reply.Contains("play_video_success", StringComparison.Ordinal))
            throw new IOException($"Panel could not play {remotePath}: '{reply}'");
    }

    /// <summary>Sends a command whose payload is a string and returns the text reply.</summary>
    public string TextCommand(LcdCommand cmd, string text, int timeoutMs, byte flag = 0, string? until = null)
    {
        var payload = Encoding.ASCII.GetBytes(text);
        lock (_lock)
        {
            _port.DiscardInBuffer();
            SendCommand(cmd, Math.Max(1, payload.Length), payload, flag);
            if (until == null) return ReadReply(timeoutMs);
            var reply = "";
            var deadline = Environment.TickCount64 + timeoutMs;
            while (!reply.Contains(until, StringComparison.Ordinal) && Environment.TickCount64 < deadline)
                reply += ReadReply(500);
            return reply;
        }
    }

    /// <summary>Sends visible pixels only, drawn over what is already on screen.</summary>
    public void SendSparse(byte[] segments)
    {
        if (segments.Length == 0) return;
        lock (_lock)
        {
            SendCommand(LcdCommand.SparseFrame, segments.Length);
            Thread.Sleep(10);
            SendData(segments);
        }
    }

    public DeviceStatus QueryStatus()
    {
        lock (_lock)
        {
            _port.DiscardInBuffer();
            SendCommand(LcdCommand.QueryStatus, 1);
            var reply = ReadReply(1000);
            bool resend = reply.Contains("needReSend:1", StringComparison.Ordinal);
            int count = -1;
            var idx = reply.IndexOf("renderCnt:", StringComparison.Ordinal);
            if (idx >= 0) int.TryParse(new string(reply[(idx + 10)..].TakeWhile(char.IsDigit).ToArray()), out count);
            return new DeviceStatus(resend, count, reply);
        }
    }

    /// <summary>Sends an arbitrary command packet. Exposed for experiments and future features.</summary>
    public void SendCommand(LcdCommand cmd, int length, ReadOnlySpan<byte> payload = default, byte flag = 0)
    {
        var p = new byte[PacketSize];
        p[0] = (byte)cmd;
        p[1] = Magic >> 8;
        p[2] = Magic & 0xFF;
        p[3] = (byte)(length >> 24);
        p[4] = (byte)(length >> 16);
        p[5] = (byte)(length >> 8);
        p[6] = (byte)length;
        p[7] = flag;
        payload.CopyTo(p.AsSpan(10));
        lock (_lock) _port.Write(p, 0, p.Length);
    }

    /// <summary>Sends bulk data as 250-byte packets with 249 bytes of payload each.</summary>
    public void SendData(ReadOnlySpan<byte> data)
    {
        int packets = (data.Length + ChunkSize - 1) / ChunkSize;
        var buf = new byte[packets * PacketSize];
        for (int i = 0; i < packets; i++)
        {
            int n = Math.Min(ChunkSize, data.Length - i * ChunkSize);
            data.Slice(i * ChunkSize, n).CopyTo(buf.AsSpan(i * PacketSize));
        }
        lock (_lock) _port.BaseStream.Write(buf, 0, buf.Length);
    }

    /// <summary>Reads a text reply: waits up to <paramref name="timeoutMs"/> for the first byte, then until the line goes quiet.</summary>
    public string ReadReply(int timeoutMs)
    {
        var sb = new StringBuilder();
        var deadline = Environment.TickCount64 + timeoutMs;
        var buf = new byte[1024];
        long quietUntil = long.MaxValue;
        while (true)
        {
            long now = Environment.TickCount64;
            if (now > deadline || now > quietUntil) break;
            if (_port.BytesToRead > 0)
            {
                int n = _port.Read(buf, 0, Math.Min(buf.Length, _port.BytesToRead));
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                quietUntil = Environment.TickCount64 + 40;
            }
            else
            {
                Thread.Sleep(5);
            }
        }
        return sb.ToString().TrimEnd('\0', '\r', '\n', ' ');
    }

    public void Dispose()
    {
        try
        {
            if (_port.IsOpen) _port.Close();
        }
        catch
        {
            // port may already be gone (USB unplugged)
        }
        _port.Dispose();
    }
}
