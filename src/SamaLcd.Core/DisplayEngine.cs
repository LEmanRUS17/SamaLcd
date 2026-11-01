using L = SamaLcd.Core.Localization.Localizer;
using SamaLcd.Core.Localization;
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

    private LocalizedMessage? _currentStatus;
    public LocalizedMessage? CurrentStatus => Volatile.Read(ref _currentStatus);
    private void ReportStatus(string key, params object[] args)
    {
        var message = new LocalizedMessage(key, args);
        Volatile.Write(ref _currentStatus, message);
        Status?.Invoke(message.ToString());
    }

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
    public long[] Storage { get; private set; } = [];
    public SdCardState SdCard { get; private set; } = new("", null, false, null);
    public event Action? SdCardChanged;
    private sealed record SdOperation(bool Clear, string Revision, SdVideoFile[] Files);
    private SdOperation? _sdOperation;

    public void RefreshSdFiles() => QueueSdOperation(false, "");
    public void ClearSdFiles(string revision) => QueueSdOperation(true, revision);

    private void QueueSdOperation(bool clear, string revision)
    {
        lock (_sync)
        {
            if (SdCard.Busy || PendingCleanup != null) return;
            if (clear && (revision.Length == 0 || revision != SdCard.Revision || SdCard.Files is not { Length: > 0 })) return;
            _sdOperation = new(clear, revision, SdCard.Files?.ToArray() ?? []);
            SdCard = SdCard with { Busy = true, Error = null };
        }
        SdCardChanged?.Invoke();
        _wake.Set();
    }

    // Run all card commands on the display thread, between transfers, including while paused.
    private void ProcessSdOperation()
    {
        SdOperation? operation;
        lock (_sync) { operation = _sdOperation; _sdOperation = null; }
        if (operation == null) return;
        string? error = null;
        try
        {
            EnsureConnected();
            if (operation.Clear)
            {
                lock (_sync) if (SdCard.Revision != operation.Revision) throw new IOException(L.Get("SD files changed. Please retry and review the new file list."));
                VideoStoragePolicy.DeleteApprovedSnapshot(true, operation.Files,
                    name => _device!.FileSize("/mnt/SDCARD/video/" + name), name =>
                    {
                        if (!_running) throw new OperationCanceledException();
                        // Keep playback paused after clearing; do not immediately upload the chosen video again.
                        if (!Paused || _activeVideo != null) _device!.StopPlayback();
                        Paused = true;
                        _activeVideo = null;
                        _preparedVideo = null;
                        _needFull = true;
                        _device!.TextCommand(LcdCommand.DeleteFile, "/mnt/SDCARD/video/" + name, 500);
                    });
                ReportStatus("SD videos cleared. Playback is paused.");
            }
        }
        catch (Exception e) { error = e.Message; }
        try { ReadSdFiles(error); }
        catch (Exception e)
        {
            lock (_sync) SdCard = new("", null, false, error ?? e.Message);
            SdCardChanged?.Invoke();
        }
    }

    private void ReadSdFiles(string? error = null)
    {
        var dev = _device ?? throw new IOException(L.Get("Display not connected"));
        var files = dev.ListDir("/mnt/SDCARD/video/")
            .Where(n => n.Length > 0 && n is not "." and not ".." && n.IndexOfAny(['/', '\\']) < 0)
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new SdVideoFile(n, dev.FileSize("/mnt/SDCARD/video/" + n))).Where(f => f.Size > 0).ToArray();
        Storage = dev.StorageInfo();
        if (Storage.Length != 6) throw new IOException("Invalid storage reply");
        lock (_sync) SdCard = new(Guid.NewGuid().ToString("N"), files, false, error);
        SdCardChanged?.Invoke();
    }
    public SdCleanupRequest? PendingCleanup { get; private set; }
    public event Action<SdCleanupRequest>? CleanupRequested;
    private TaskCompletionSource<bool>? _cleanupDecision;
    public void ResolveCleanup(string id, bool approved)
    {
        lock (_sync) if (PendingCleanup?.Id == id) _cleanupDecision?.TrySetResult(approved);
    }

    private long VideoBudget(string source)
    {
        Storage = _device!.StorageInfo();
        return VideoStoragePolicy.EncodingBudget(Storage, DeviceVideo.Duration(VideoSource.FindFfmpeg() ?? throw new IOException("FFmpeg unavailable"), source));
    }

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
            _wantedVideo = video is null ? null : new VideoRequest(video, bg.Fit, _settings.Rotation, bg.Crop);
            _needFull = true;
        }
        _wake.Set();
    }

    private sealed record VideoRequest(string Source, ImageFit Fit, int Rotation, SquareCrop? Crop);

    /// <summary>
    /// The animated background the panel plays by itself, or null. Frames are then rendered without it
    /// (transparent), and the preview has to show this file underneath.
    /// </summary>
    public PreviewMedia? Media
    {
        get
        {
            lock (_sync) return _wantedVideo is { } v ? new PreviewMedia(v.Source, v.Fit, _activeVideo == v ? _preparedVideo : null, v.Crop) : null;
        }
    }

    /// <summary>The theme as it will be shown: the app's background choice replaces the theme's own.</summary>
    public static Theme WithBackground(Theme theme, AppSettings s)
    {
        var background = s.BackgroundMode switch
        {
            BackgroundMode.Color => new Background { Color = s.BackgroundColor },
            BackgroundMode.File => new Background { Color = "#000000", Image = s.BackgroundFile, Fit = s.BackgroundFit, Fps = s.VideoFps, Crop = s.BackgroundCrop },
            BackgroundMode.Gallery when s.GalleryFile.Length > 0 && File.Exists(s.GalleryPath) =>
                new Background { Color = "#000000", Image = s.GalleryPath, Fit = ImageFit.Cover, Fps = s.VideoFps, Crop = s.BackgroundCrop },
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
        _cleanupDecision?.TrySetResult(false);
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
                ProcessSdOperation();
                interval = Tick();
            }
            catch (Exception e)
            {
                Log?.Invoke($"Error: {e.Message}");
                ReportStatus("Screen error: {0}", e.Message);
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
                ReportStatus("Background: {0}", err);
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
            lock (_sync) if (Paused || wanted != _wantedVideo) return interval;
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
            ReportStatus("Preparing video {0}…", Path.GetFileName(wanted.Source));
            var budget=VideoBudget(wanted.Source);
            if(budget<=0) throw new IOException(L.Get("Video is too long for the available storage. Choose a shorter clip or free space."));
            ReportStatus("Compressing for display storage: at most {0:0.0} MiB",budget/1048576.0);
            prepared = DeviceVideo.Prepare(wanted.Source, wanted.Fit, wanted.Rotation, _settings.Width, _settings.Height, s => ReportStatus("{0}", s), wanted.Crop,budget);
        }
        catch (Exception e)
        {
            FallBackToStreaming(wanted, e);
            return;
        }
        _preparedVideo = prepared.LocalPath;
        try
        {
            lock (_sync) if (!_running || _wantedVideo != wanted) throw new OperationCanceledException();
            var remote = EnsureUploaded(prepared, wanted);
            _device!.PlayVideo(remote);
            Storage = _device.StorageInfo();
        }
        catch (OperationCanceledException)
        {
            // Do not retry a declined deletion request or silently switch to USB playback.
            lock (_sync) if (_wantedVideo == wanted) Paused = true;
            _activeVideo = null;
            ReportStatus("Video upload canceled.");
            return;
        }
        catch (Exception e) when (++_videoFailures < 2)
        {
            throw new IOException(L.Get("Video: {0}", e.Message), e); // reconnect and try once more
        }
        catch (Exception e)
        {
            FallBackToStreaming(wanted, e);
            return;
        }
        _videoFailures = 0;
        _activeVideo = wanted;
        ReportStatus("Playing video on display: {0}", Path.GetFileName(wanted.Source));
    }

    /// <summary>Shows the video by sending its frames instead (slow, ~4 fps) when the panel cannot play it.</summary>
    private void FallBackToStreaming(VideoRequest wanted, Exception e)
    {
        ReportStatus("Device playback failed ({0}); streaming instead", e.Message);
        Log?.Invoke($"Device video failed: {e}");
        _videoFailures = 0;
        _streamingFallback = wanted.Source;
        ApplyBackgroundSettings();
        _activeVideo = null;
        _preparedVideo = null;
    }

    /// <summary>Copies the converted video to the panel unless it is already there; returns its path on the panel.</summary>
    private string EnsureUploaded(PreparedVideo video, VideoRequest wanted)
    {
        var dev = _device!;
        long needBytes = VideoStoragePolicy.RequiredBytes(video.Size);
        string[] dirs = ["/mnt/SDCARD/video/", "/root/video/"];
        foreach (var dir in dirs)
        {
            if (dev.FileSize(dir + video.RemoteName) == video.Size) return dir + video.RemoteName;
        }
        Storage = dev.StorageInfo(); // Refresh AFTER conversion; free space may have changed.
        if (Storage.Length != 6) throw new IOException("Invalid storage reply");
        string? destination = Storage[5] * 1024 >= needBytes ? dirs[0] : Storage[2] * 1024 >= needBytes ? dirs[1] : null;
        if (destination == null)
        {
            var files = dev.ListDir(dirs[0]).Where(n => n is not "." and not ".." && n.IndexOfAny(['/', '\\']) < 0)
                .Select(n => new SdVideoFile(n, dev.FileSize(dirs[0] + n))).Where(f => f.Size > 0).ToArray();
            if (files.Length == 0 || Storage[5] * 1024 + files.Sum(f => f.Size) < needBytes)
                throw new IOException(L.Get("Deleting SD videos cannot free enough space. Choose a shorter clip or manage the SD card manually."));
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _cleanupDecision = decision;
                PendingCleanup = new SdCleanupRequest(Guid.NewGuid().ToString("N"), needBytes, Storage[5] * 1024, files);
            }
            try
            {
                ReportStatus("Not enough space. Waiting for SD cleanup confirmation…");
                if (CleanupRequested == null) throw new OperationCanceledException();
                CleanupRequested.Invoke(PendingCleanup);
                while (_running && !decision.Task.Wait(200))
                {
                    lock (_sync) if (_wantedVideo != wanted) throw new OperationCanceledException();
                }
                lock (_sync) if (!_running || _wantedVideo != wanted || !decision.Task.IsCompletedSuccessfully || !decision.Task.Result)
                    throw new OperationCanceledException();
                Storage = dev.StorageInfo();
                if (Storage.Length != 6) throw new IOException("Invalid storage reply");
                if (Storage[5] * 1024 < needBytes)
                {
                    // Approval covers exactly the displayed snapshot, never newly discovered files.
                    lock (_sync) SdCard = new("", null, SdCard.Busy, null);
                    SdCardChanged?.Invoke();
                    dev.StopPlayback();
                    VideoStoragePolicy.DeleteApprovedSnapshot(decision.Task.Result, files, name => dev.FileSize(dirs[0] + name), name =>
                    {
                        lock (_sync) if (!_running || _wantedVideo != wanted) throw new OperationCanceledException();
                        dev.TextCommand(LcdCommand.DeleteFile, dirs[0] + name, 500);
                    });
                }
            }
            finally { lock (_sync) { PendingCleanup = null; _cleanupDecision = null; } }
            Storage = dev.StorageInfo();
            if (Storage.Length != 6 || Storage[5] * 1024 < needBytes) throw new IOException(L.Get("SD space is still insufficient after cleanup."));
            destination = dirs[0];
        }
        int lastPct = -1;
        lock (_sync) if (!_running || _wantedVideo != wanted) throw new OperationCanceledException();
        dev.UploadFile(video.LocalPath, destination + video.RemoteName, p =>
        {
            int pct = (int)(p * 100);
            if (pct / 5 == lastPct / 5) return;
            lastPct = pct;
            ReportStatus("Uploading video to display: {0}%", pct);
        });
        // Invalidate the displayed list after a transfer; refresh on the next App-tab visit.
        lock (_sync) SdCard = new("", null, SdCard.Busy, null);
        SdCardChanged?.Invoke();
        return destination + video.RemoteName;
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
        ReportStatus("Searching for display…");
        var port = string.IsNullOrWhiteSpace(_settings.Port) ? null : _settings.Port;
        _device = LcdDevice.Connect(port, _settings.Width, _settings.Height, s => Log?.Invoke(s));
        lock (_sync) SdCard = new("", null, SdCard.Busy, null);
        SdCardChanged?.Invoke();
        _device.FullFrameDelayMs = _settings.FullFrameDelayMs;
        _device.StopPlayback();
        Storage = _device.StorageInfo();
        _device.SetBrightness(_settings.Brightness, _settings.BrightnessScale);
        _pendingBrightness = -1;
        _needFull = true;
        _lastFrame = null;
        ReportStatus("Connected: {0}", DeviceInfo ?? "");
    }

    private void Disconnect()
    {
        lock (_sync) SdCard = new("", null, false, null);
        SdCardChanged?.Invoke();
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
public sealed record PreviewMedia(string Source, ImageFit Fit, string? Prepared, SquareCrop? Crop = null);
