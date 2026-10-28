using System.Drawing;
using SamaLcd.Core.Device;
using SamaLcd.Core.Rendering;
using SamaLcd.Core.Sensors;

namespace SamaLcd.Core;

/// <summary>
/// Background loop: renders the current theme, sends changed pixels to the panel,
/// resends a full frame when the panel asks for it and reconnects after USB errors.
/// </summary>
public sealed class DisplayEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SensorHub _sensors;
    private readonly object _sync = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _running;
    private ThemeRenderer? _renderer;
    private Theme? _baseTheme;
    private string? _reportedBackgroundError;
    private VideoRequest? _wantedVideo;   // what should play on the panel
    private VideoRequest? _activeVideo;   // what the panel is playing now
    private string? _preparedVideo;       // the converted file the panel plays
    private string? _streamingFallback;   // a file the panel failed to play, streamed instead
    private int _videoFailures;
    private LcdDevice? _device;
    private byte[]? _lastFrame;
    private bool _needFull = true;
    private volatile int _pendingBrightness = -1;
    private long _lastSensorUpdate;
    private long _lastStatusQuery;

    public event Action<string>? Status;
    public event Action<string>? Log;
    /// <summary>Raised with each rendered frame (the receiver owns the bitmap).</summary>
    public event Action<Bitmap>? FrameRendered;

    public bool Connected => _device != null;
    public string? DeviceInfo => _device is { } d ? $"{d.PortName}: {d.Identity}" : null;
    public bool Paused { get; set; }
    /// <summary>Frames sent to the panel and their encoded size since start (for diagnostics).</summary>
    public long FramesSent { get; private set; }
    public long BytesSent { get; private set; }

    public DisplayEngine(AppSettings settings, SensorHub sensors)
    {
        _settings = settings;
        _sensors = sensors;
    }

    public void SetTheme(Theme theme)
    {
        _baseTheme = theme;
        ApplyBackgroundSettings();
    }

    /// <summary>Rebuilds the scene after the background settings changed.</summary>
    public void ApplyBackgroundSettings()
    {
        if (_baseTheme is not { } theme) return;
        var effective = WithBackground(theme, _settings);

        // Videos and animated GIFs are played by the panel itself; we only send the widgets as an overlay.
        string? video = null;
        var bg = effective.Background;
        if (_settings.PlayVideoOnDevice && bg.Image is { Length: > 0 } img)
        {
            var path = effective.Resolve(img);
            try
            {
                if (path != _streamingFallback && DeviceVideo.Supports(path)) video = path;
            }
            catch (Exception e)
            {
                Log?.Invoke($"Background check failed: {e.Message}");
            }
        }
        if (video != null)
        {
            effective = new Theme
            {
                Name = effective.Name,
                IntervalMs = effective.IntervalMs,
                Directory = effective.Directory,
                Widgets = effective.Widgets,
                Background = new Background { Color = "#00000000" },
            };
        }

        lock (_sync)
        {
            _renderer?.Dispose();
            _renderer = new ThemeRenderer(effective, _sensors.Get, _settings.Width, _settings.Height);
            _wantedVideo = video is null ? null : new VideoRequest(video, bg.Fit, _settings.Rotation);
            _needFull = true;
        }
        _wake.Set();
    }

    private sealed record VideoRequest(string Source, ImageFit Fit, int Rotation);

    /// <summary>
    /// The animated background the panel plays by itself, or null. Frames are then rendered without it
    /// (transparent), and the preview has to show this file underneath.
    /// </summary>
    public PreviewMedia? Media
    {
        get
        {
            lock (_sync) return _wantedVideo is { } v ? new PreviewMedia(v.Source, v.Fit, _activeVideo == v ? _preparedVideo : null) : null;
        }
    }

    /// <summary>The theme as it will be shown: the app's background choice replaces the theme's own.</summary>
    public static Theme WithBackground(Theme theme, AppSettings s)
    {
        var background = s.BackgroundMode switch
        {
            BackgroundMode.Color => new Background { Color = s.BackgroundColor },
            BackgroundMode.File => new Background { Color = "#000000", Image = s.BackgroundFile, Fit = s.BackgroundFit, Fps = s.VideoFps },
            BackgroundMode.Gallery when s.GalleryFile.Length > 0 && File.Exists(s.GalleryPath) =>
                new Background { Color = "#000000", Image = s.GalleryPath, Fit = ImageFit.Cover, Fps = s.VideoFps },
            _ => null,
        };
        if (background == null) return theme;
        return new Theme
        {
            Name = theme.Name,
            IntervalMs = theme.IntervalMs,
            Directory = theme.Directory,
            Widgets = theme.Widgets,
            Background = background,
        };
    }

    public void SetBrightness(int percent)
    {
        _pendingBrightness = percent;
        _wake.Set();
    }

    /// <summary>Forces a complete redraw on the panel with the next frame.</summary>
    public void Invalidate()
    {
        _needFull = true;
        _wake.Set();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "LCD display loop" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _wake.Set();
        _thread?.Join(5000);
        _thread = null;
        Disconnect();
    }

    private void Run()
    {
        while (_running)
        {
            var started = Environment.TickCount64;
            int interval = 1000;
            try
            {
                interval = Tick();
            }
            catch (Exception e)
            {
                Log?.Invoke($"Error: {e.Message}");
                Status?.Invoke($"Ошибка экрана: {e.Message}");
                Disconnect();
                interval = 3000;
            }
            var wait = interval - (int)(Environment.TickCount64 - started);
            if (wait > 0) _wake.WaitOne(wait); // settings changes redraw at once
        }
    }

    private int Tick()
    {
        if (Environment.TickCount64 - _lastSensorUpdate >= 1000)
        {
            _sensors.Update();
            _lastSensorUpdate = Environment.TickCount64;
        }

        Bitmap frame;
        int interval;
        lock (_sync)
        {
            if (_renderer == null) return 500;
            frame = _renderer.Render();
            interval = Math.Max(33, Math.Min(_renderer.Theme.IntervalMs, _renderer.AnimationIntervalMs ?? int.MaxValue));
            if (_renderer.BackgroundError is { } err && err != _reportedBackgroundError)
            {
                _reportedBackgroundError = err;
                Status?.Invoke($"Фон: {err}");
            }
        }

        using (frame)
        {
            VideoRequest? wanted;
            lock (_sync) wanted = _wantedVideo;
            FrameRendered?.Invoke(PreviewOf(frame));
            if (Paused) return interval;

            EnsureConnected();
            if (_pendingBrightness >= 0)
            {
                _device!.SetBrightness(_pendingBrightness, _settings.BrightnessScale);
                _pendingBrightness = -1;
            }
            if (wanted != _activeVideo) SwitchVideo(wanted);
            bool overlay = _activeVideo != null;
            if (overlay) interval = Math.Max(interval, 200); // the panel draws the video; widgets need no high rate

            ApplyRotation(frame);
            var bgra = FrameCodec.ToBgra(frame);
            if (_needFull || _lastFrame == null)
            {
                if (overlay) _device!.SendOverlayFull(bgra);
                else _device!.SendFullFrame(bgra);
                _needFull = false;
                FramesSent++;
                BytesSent += bgra.Length;
            }
            else
            {
                var diff = FrameCodec.EncodeDiff(_lastFrame, bgra);
                if (diff.Length > 0)
                {
                    if (overlay) _device!.SendOverlayDiff(diff, bgra);
                    else _device!.SendDiff(diff);
                    FramesSent++;
                    BytesSent += diff.Length;
                    // a status round trip costs ~70 ms, so ask at most once a second (matters for video)
                    if (Environment.TickCount64 - _lastStatusQuery >= 1000)
                    {
                        _lastStatusQuery = Environment.TickCount64;
                        var status = _device.QueryStatus();
                        if (status.NeedResend)
                        {
                            Log?.Invoke($"Panel asked for a full frame ({status.Raw})");
                            _needFull = true;
                        }
                    }
                }
            }
            _lastFrame = bgra;
        }
        return interval;
    }

    /// <summary>Starts, changes or stops the video played by the panel.</summary>
    private void SwitchVideo(VideoRequest? wanted)
    {
        _needFull = true;
        _lastFrame = null;
        if (wanted == null)
        {
            _device!.StopPlayback();
            _activeVideo = null;
            _preparedVideo = null;
            return;
        }
        PreparedVideo prepared;
        try
        {
            Status?.Invoke($"Подготовка видео {Path.GetFileName(wanted.Source)}...");
            prepared = DeviceVideo.Prepare(wanted.Source, wanted.Fit, wanted.Rotation, _settings.Width, _settings.Height, s => Status?.Invoke(s));
        }
        catch (Exception e)
        {
            FallBackToStreaming(wanted, e);
            return;
        }
        _preparedVideo = prepared.LocalPath;
        try
        {
            var remote = EnsureUploaded(prepared);
            _device!.PlayVideo(remote);
        }
        catch (Exception e) when (++_videoFailures < 2)
        {
            throw new IOException($"Видео: {e.Message}", e); // reconnect and try once more
        }
        catch (Exception e)
        {
            FallBackToStreaming(wanted, e);
            return;
        }
        _videoFailures = 0;
        _activeVideo = wanted;
        Status?.Invoke($"Видео играет на экране: {Path.GetFileName(wanted.Source)}");
    }

    /// <summary>Shows the video by sending its frames instead (slow, ~4 fps) when the panel cannot play it.</summary>
    private void FallBackToStreaming(VideoRequest wanted, Exception e)
    {
        Status?.Invoke($"Видео на экране не запустилось ({e.Message}), показываю потоком");
        Log?.Invoke($"Device video failed: {e}");
        _videoFailures = 0;
        _streamingFallback = wanted.Source;
        ApplyBackgroundSettings();
        _activeVideo = null;
        _preparedVideo = null;
    }

    /// <summary>Copies the converted video to the panel unless it is already there; returns its path on the panel.</summary>
    private string EnsureUploaded(PreparedVideo video)
    {
        var dev = _device!;
        long[] storage = dev.StorageInfo(); // KB: total, used, free, sdTotal, sdUsed, sdFree
        long needKb = video.Size / 1024 + 512;
        string[] dirs = ["/mnt/SDCARD/video/", "/root/video/"];
        foreach (var dir in dirs)
        {
            if (dev.FileSize(dir + video.RemoteName) == video.Size) return dir + video.RemoteName;
        }
        foreach (var (dir, freeKb) in new[] { (dirs[0], storage.ElementAtOrDefault(5)), (dirs[1], storage.ElementAtOrDefault(2)) })
        {
            // remove videos we uploaded earlier, then check space
            foreach (var old in dev.ListDir(dir).Where(f => f.StartsWith(DeviceVideo.RemotePrefix, StringComparison.Ordinal)))
                dev.TextCommand(LcdCommand.DeleteFile, dir + old, 500);
            storage = dev.StorageInfo();
            long free = dir == dirs[0] ? storage.ElementAtOrDefault(5) : storage.ElementAtOrDefault(2);
            if (free < needKb) continue;
            int lastPct = -1;
            dev.UploadFile(video.LocalPath, dir + video.RemoteName, p =>
            {
                int pct = (int)(p * 100);
                if (pct / 5 == lastPct / 5) return;
                lastPct = pct;
                Status?.Invoke($"Загрузка видео на экран: {pct}%");
            });
            return dir + video.RemoteName;
        }
        throw new IOException($"На экране нет места для видео ({video.Size / 1024 / 1024.0:0.0} МБ)");
    }

    /// <summary>
    /// Over a video played by the panel the frame is transparent; the preview shows it on top of <see cref="Media"/>.
    /// </summary>
    private static Bitmap PreviewOf(Bitmap frame) => (Bitmap)frame.Clone();

    private void ApplyRotation(Bitmap frame)
    {
        switch (((_settings.Rotation % 360) + 360) % 360)
        {
            case 90: frame.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            case 180: frame.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 270: frame.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
        }
    }

    private void EnsureConnected()
    {
        if (_device != null) return;
        Status?.Invoke("Поиск экрана...");
        var port = string.IsNullOrWhiteSpace(_settings.Port) ? null : _settings.Port;
        _device = LcdDevice.Connect(port, _settings.Width, _settings.Height, s => Log?.Invoke(s));
        _device.FullFrameDelayMs = _settings.FullFrameDelayMs;
        _device.StopPlayback();
        _device.SetBrightness(_settings.Brightness, _settings.BrightnessScale);
        _pendingBrightness = -1;
        _needFull = true;
        _lastFrame = null;
        Status?.Invoke($"Подключено: {DeviceInfo}");
    }

    private void Disconnect()
    {
        _device?.Dispose();
        _device = null;
        _activeVideo = null; // started again after reconnecting
        _lastFrame = null;
        _needFull = true;
    }

    public void Dispose()
    {
        Stop();
        _renderer?.Dispose();
    }
}

/// <param name="Source">The file chosen by the user.</param>
/// <param name="Prepared">The converted MP4 the panel plays, once it is ready.</param>
public sealed record PreviewMedia(string Source, ImageFit Fit, string? Prepared);
