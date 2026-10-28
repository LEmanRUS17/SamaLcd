using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SamaLcd.Core;
using SamaLcd.Core.Rendering;
using SamaLcd.Core.Sensors;

namespace SamaLcd;

/// <summary>
/// Settings window: the UI is the web page in wwwroot (daisyUI) hosted in WebView2.
/// Closing the window hides the app to the tray.
/// </summary>
sealed class MainForm : Form
{
    private const string HostName = "app.samalcd";
    // Folders the page may load pictures and videos from: the gallery, the themes and the folder of the user's file.
    // Any other file is reached through its drive: https://drive-c.samalcd/... (mappings only work when set before navigation).
    private const string GalleryHost = "gallery.samalcd", ThemesHost = "themes.samalcd";
    private static readonly string[] BrowserMedia = [".gif", ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".mp4", ".webm", ".m4v", ".mov"];
    private static readonly string[] GalleryExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", .. FrameSources.VideoExtensions];

    private readonly AppSettings _settings;
    private readonly DisplayEngine _engine;
    private readonly SensorHub _sensors;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _themesMenu = new("Вариант");
    private readonly ToolStripMenuItem _pauseMenu = new("Пауза");
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly object _frameLock = new();
    private Bitmap? _lastFrame;
    private CustomLayout _layout = CustomLayout.Load();
    private int _layoutVersion = 1;
    private List<string> _themes = [];
    private Dictionary<string, Thumb> _thumbs = [];
    private bool _webReady;
    private bool _exiting;
    private string _status = "";

    public ApplicationContext TrayContext { get; } = new();

    /// <summary>Set by --screenshot file.png[#tab]: save a picture of the window once it is loaded and exit (for docs and checks).</summary>
    public string? ScreenshotPath { get; init; }

    public MainForm(AppSettings settings, DisplayEngine engine, SensorHub sensors)
    {
        _settings = settings;
        _engine = engine;
        _sensors = sensors;

        Text = "SAMA LCD";
        Icon = MakeIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(760, 560);
        BackColor = Color.FromArgb(29, 35, 42); // daisyUI "dark" base, so the window does not flash white while loading
        Controls.Add(_web);

        _tray = new NotifyIcon { Icon = Icon, Text = "SAMA LCD", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowWindow());
        menu.Items.Add(_themesMenu);
        menu.Items.Add(_pauseMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());
        _pauseMenu.Click += (_, _) => SetPaused(!_engine.Paused);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        // The current variant goes to the screen right away, before the window is even shown.
        LoadThemeList();

        _engine.Status += s => UI(() =>
        {
            _status = s;
            _tray.Text = s.Length > 63 ? s[..63] : s;
            PostStatus();
        });
        _engine.FrameRendered += OnFrame;
    }

    // ---------- WebView2 ----------

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UseDarkTitleBar(true);
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SamaLcd", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            ShowWebViewMissing(ex);
            return;
        }
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = Debugger.IsAttached;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        _web.DefaultBackgroundColor = BackColor;
        core.SetVirtualHostNameToFolderMapping(HostName, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Allow);
        Directory.CreateDirectory(_settings.ResolvedGalleryDirectory);
        core.SetVirtualHostNameToFolderMapping(GalleryHost, _settings.ResolvedGalleryDirectory, CoreWebView2HostResourceAccessKind.Allow);
        if (Directory.Exists(_settings.ResolvedThemesDirectory))
            core.SetVirtualHostNameToFolderMapping(ThemesHost, _settings.ResolvedThemesDirectory, CoreWebView2HostResourceAccessKind.Allow);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            core.SetVirtualHostNameToFolderMapping(DriveHost(drive.RootDirectory.FullName), drive.RootDirectory.FullName, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += (_, a) => OnMessage(a.WebMessageAsJson);
        core.NavigationStarting += (_, _) => _webReady = false;
        var tab = ScreenshotPath?.Split('#') is [_, var t] ? "#" + t : "";
        core.Navigate($"https://{HostName}/index.html{tab}");
    }

    private void ShowWebViewMissing(Exception ex)
    {
        var link = new LinkLabel
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, LinkColor = Color.DeepSkyBlue,
            Text = $"Для окна настроек нужен Microsoft Edge WebView2 Runtime.\nСкачать: https://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n{ex.Message}",
        };
        link.LinkArea = new LinkArea(link.Text.IndexOf("https", StringComparison.Ordinal), "https://go.microsoft.com/fwlink/p/?LinkId=2124703".Length);
        link.LinkClicked += (_, _) => OpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        Controls.Remove(_web);
        Controls.Add(link);
    }

    private void Post(object message)
    {
        if (!_webReady || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Theme.JsonOptions));
    }

    private void OnMessage(string json)
    {
        JsonNode? m;
        try { m = JsonNode.Parse(json); }
        catch (JsonException) { return; }
        try
        {
            switch ((string?)m?["cmd"])
            {
                case "ready":
                    _webReady = true;
                    PostState();
                    PostStatus();
                    PostSensors(true);
                    PostFrame();
                    if (ScreenshotPath != null) _ = SaveScreenshot();
                    break;
                case "set": Set((string)m!["key"]!, m["value"]); break;
                case "pickFile": PickBackgroundFile(); break;
                case "layout":
                    _layout = m!["layout"].Deserialize<CustomLayout>(Theme.JsonOptions) ?? _layout;
                    _layout.Save();
                    if (_settings.Theme == CustomLayout.ThemeName) ApplyTheme(CustomLayout.ThemeName);
                    RenderThumbnail(CustomLayout.ThemeName);
                    break;
                case "resetLayout":
                    _layout = CustomLayout.Default();
                    _layout.Save();
                    _layoutVersion++;
                    if (_settings.Theme == CustomLayout.ThemeName) ApplyTheme(CustomLayout.ThemeName);
                    PostState();
                    break;
                case "sensors": PostSensors((bool?)m!["full"] ?? true); break;
                case "openThemes":
                    Directory.CreateDirectory(_settings.ResolvedThemesDirectory);
                    Process.Start("explorer.exe", _settings.ResolvedThemesDirectory);
                    break;
                case "openGallery":
                    Directory.CreateDirectory(_settings.ResolvedGalleryDirectory);
                    Process.Start("explorer.exe", _settings.ResolvedGalleryDirectory);
                    break;
                case "reloadGallery": PostState(); break;
                case "reloadThemes":
                    LoadThemeList();
                    _engine.Invalidate();
                    PostState();
                    break;
                case "openUrl":
                    if ((string?)m!["url"] is { } url && url.StartsWith("https://", StringComparison.Ordinal)) OpenUrl(url);
                    break;
                case "uiTheme":
                    bool dark = (bool?)m!["dark"] ?? true;
                    UseDarkTitleBar(dark);
                    BackColor = dark ? Color.FromArgb(29, 35, 42) : Color.FromArgb(248, 248, 248);
                    break;
            }
        }
        catch (Exception e)
        {
            Post(new { type = "toast", kind = "error", text = e.Message });
        }
    }

    private void Set(string key, JsonNode? value)
    {
        switch (key)
        {
            case "theme":
                ApplyTheme((string)value!, chosen: true);
                break;
            case "brightness":
                _settings.Brightness = Math.Clamp((int)value!, 0, 100);
                _engine.SetBrightness(_settings.Brightness);
                break;
            case "rotation":
                _settings.Rotation = (int)value!;
                _engine.ApplyBackgroundSettings(); // a video on the panel is converted for the new angle
                _engine.Invalidate();
                break;
            case "bgMode":
                var mode = Enum.Parse<BackgroundMode>((string)value!, true);
                if (mode == BackgroundMode.File && !File.Exists(_settings.BackgroundFile))
                {
                    PickBackgroundFile();
                    return;
                }
                if (mode == BackgroundMode.Gallery && !File.Exists(_settings.GalleryPath)) mode = BackgroundMode.Theme;
                _settings.BackgroundMode = mode;
                _engine.ApplyBackgroundSettings();
                break;
            case "gallery":
                // an empty name is the tile with the variant's own background
                _settings.GalleryFile = Path.GetFileName((string?)value ?? "");
                _settings.BackgroundMode = _settings.GalleryFile.Length > 0 ? BackgroundMode.Gallery : BackgroundMode.Theme;
                _engine.ApplyBackgroundSettings();
                break;
            case "bgColor":
                _settings.BackgroundColor = (string)value!;
                _settings.BackgroundMode = BackgroundMode.Color;
                _engine.ApplyBackgroundSettings();
                break;
            case "fit":
                _settings.BackgroundFit = Enum.Parse<ImageFit>((string)value!, true);
                _engine.ApplyBackgroundSettings();
                break;
            case "onDevice":
                _settings.PlayVideoOnDevice = (bool)value!;
                _engine.ApplyBackgroundSettings();
                break;
            case "paused":
                SetPaused((bool)value!);
                return;
            case "autostart":
                try { Autostart.Set((bool)value!); }
                catch (Exception e) { Post(new { type = "toast", kind = "error", text = "Автозапуск: " + e.Message }); }
                break;
        }
        _settings.Save();
        if (key != "brightness") PostState();
    }

    private void PostState()
    {
        Post(new
        {
            type = "state",
            themes = _themes.Select(n => new { name = n, title = n, thumb = _thumbs.GetValueOrDefault(n) }),
            customThumb = _thumbs.GetValueOrDefault(CustomLayout.ThemeName),
            gallery = GalleryItems(),
            galleryFile = _settings.BackgroundMode == BackgroundMode.Gallery ? _settings.GalleryFile : "",
            theme = _settings.Theme,
            brightness = _settings.Brightness,
            rotation = ((_settings.Rotation % 360) + 360) % 360,
            bgMode = _settings.BackgroundMode,
            bgColor = _settings.BackgroundColor,
            bgFileName = File.Exists(_settings.BackgroundFile) ? Path.GetFileName(_settings.BackgroundFile) : "",
            bgFileKind = BackgroundKind(_settings.BackgroundFile),
            fit = _settings.BackgroundFit,
            onDevice = _settings.PlayVideoOnDevice,
            autostart = SafeAutostartState(),
            paused = _engine.Paused,
            cpuHint = _sensors.CpuSensorsUnavailable,
            device = _engine.DeviceInfo,
            layout = _layout,
            layoutVersion = _layoutVersion,
        });
    }

    private void PostStatus() => Post(new
    {
        type = "status",
        text = _status,
        connected = _engine.Connected,
        paused = _engine.Paused,
        device = _engine.DeviceInfo,
        traffic = _engine.FramesSent > 0 ? $"Отправлено кадров: {_engine.FramesSent}, {_engine.BytesSent / 1024.0 / 1024:0.0} МБ" : null,
    });

    private static readonly string[] AliasKeys =
    [
        "cpu.temp", "cpu.load", "cpu.clock", "cpu.power", "gpu.temp", "gpu.hotspot", "gpu.load", "gpu.clock", "gpu.power",
        "gpu.mem.load", "gpu.mem.used", "gpu.fan", "ram.load", "ram.used", "ram.free", "board.temp", "pump.rpm", "fan.rpm",
    ];

    private void PostSensors(bool full)
    {
        if (!_webReady) return;
        var aliases = AliasKeys.ToDictionary(k => k, k => _sensors.Get(k));
        Post(new
        {
            type = "sensors",
            aliases,
            list = full ? _sensors.List().Select(s => new { id = s.Id, name = s.Name, hardware = s.Hardware, type = s.Type, value = s.Value }) : null,
        });
    }

    private async Task SaveScreenshot()
    {
        await Task.Delay(3000);
        await using (var file = File.Create(ScreenshotPath!.Split('#')[0]))
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        ExitApp();
    }

    // ---------- preview ----------

    private void OnFrame(Bitmap frame)
    {
        lock (_frameLock)
        {
            _lastFrame?.Dispose();
            _lastFrame = frame; // kept so the window shows the current picture as soon as it opens
        }
        UI(() =>
        {
            if (Visible && WindowState != FormWindowState.Minimized) PostFrame();
            PostStatus();
        });
    }

    private void PostFrame()
    {
        if (!_webReady) return;
        // Over a video played by the panel itself the frame is transparent: the page plays the file underneath.
        var media = _engine.Media is { } m ? MediaOf(m.Source, m.Fit, m.Prepared) : null;
        string data;
        lock (_frameLock)
        {
            if (_lastFrame == null) return;
            data = ToDataUrl(_lastFrame, transparent: _engine.Media != null);
        }
        Post(new { type = "frame", data, media });
    }

    private sealed record Media(string Url, string Kind, ImageFit Fit);
    private sealed record Thumb(string Image, Media? Media);
    private sealed record GalleryItem(string File, string Name, Media Media);

    /// <summary>How the page can show <paramref name="path"/>, or null when the browser cannot play it.</summary>
    private Media? MediaOf(string path, ImageFit fit, string? converted = null)
    {
        if (!BrowserMedia.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            if (converted == null) return null;
            path = converted;
        }
        var url = UrlOf(Path.GetFullPath(path));
        return url == null ? null : new Media(url, FrameSources.IsVideo(path) ? "video" : "image", fit);
    }

    private string? UrlOf(string path)
    {
        foreach (var (root, host) in new[] { (_settings.ResolvedGalleryDirectory, GalleryHost), (_settings.ResolvedThemesDirectory, ThemesHost) })
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(root), path);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) return $"https://{host}/{Escape(rel)}";
        }
        var drive = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(drive) || drive.StartsWith(@"\\", StringComparison.Ordinal)) return null; // network paths are not mapped
        return $"https://{DriveHost(drive)}/{Escape(Path.GetRelativePath(drive, path))}";
    }

    private static string DriveHost(string root) => $"drive-{char.ToLowerInvariant(root[0])}.samalcd";

    private static string Escape(string relative) => string.Join("/", relative.Split('\\', '/').Select(Uri.EscapeDataString));

    private List<GalleryItem> GalleryItems()
    {
        var dir = _settings.ResolvedGalleryDirectory;
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir)
            .Where(f => GalleryExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => (file: f, media: MediaOf(f, ImageFit.Cover)))
            .Where(x => x.media != null)
            .Select(x => new GalleryItem(Path.GetFileName(x.file), Path.GetFileNameWithoutExtension(x.file), x.media!))
            .ToList();
    }

    private static string ToDataUrl(Image image, int size = 0, bool transparent = false)
    {
        using var ms = new MemoryStream();
        using var scaled = size > 0 ? new Bitmap(image, size, size) : null;
        if (transparent)
        {
            (scaled ?? image).Save(ms, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        (scaled ?? image).Save(ms, jpeg, p);
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Small pictures of every theme for the variant cards.</summary>
    private void RenderThumbnails()
    {
        _thumbs = [];
        foreach (var name in _themes.Append(CustomLayout.ThemeName)) RenderThumbnail(name);
    }

    private void RenderThumbnail(string name)
    {
        try
        {
            var theme = LoadTheme(name);
            // an animated background is played by the page itself, under the rendered widgets
            Media? media = null;
            if (theme.Background.Image is { Length: > 0 } img && DeviceVideo.Supports(theme.Resolve(img)))
            {
                media = MediaOf(theme.Resolve(img), theme.Background.Fit);
                if (media != null) theme.Background = new Background { Color = "#00000000" };
            }
            using var r = new ThemeRenderer(theme, _sensors.Get, _settings.Width, _settings.Height);
            using var bmp = r.Render();
            _thumbs[name] = new Thumb(ToDataUrl(bmp, 160, transparent: media != null), media);
        }
        catch
        {
            _thumbs.Remove(name); // a broken theme just gets no picture
        }
    }

    // ---------- themes ----------

    private Theme LoadTheme(string name) => name == CustomLayout.ThemeName
        ? _layout.ToTheme(_settings.Width, _settings.Height)
        : Theme.Load(Path.Combine(_settings.ResolvedThemesDirectory, name));

    private void LoadThemeList()
    {
        _themes = Theme.Discover(_settings.ResolvedThemesDirectory).Select(d => Path.GetFileName(d)!).ToList();
        _themesMenu.DropDownItems.Clear();
        foreach (var n in _themes.Append(CustomLayout.ThemeName))
        {
            var item = new ToolStripMenuItem(n == CustomLayout.ThemeName ? "Своя раскладка" : n) { Tag = n };
            item.Click += (_, _) =>
            {
                ApplyTheme((string)item.Tag!, chosen: true);
                PostState();
            };
            _themesMenu.DropDownItems.Add(item);
        }
        RenderThumbnails();
        var current = _settings.Theme == CustomLayout.ThemeName || _themes.Contains(_settings.Theme)
            ? _settings.Theme
            : _themes.FirstOrDefault() ?? CustomLayout.ThemeName;
        ApplyTheme(current);
    }

    /// <param name="chosen">the user picked the variant just now</param>
    private void ApplyTheme(string name, bool chosen = false)
    {
        try
        {
            var theme = LoadTheme(name);
            // A variant built around its own picture (Animation) is pointless over another background, so picking it brings the picture back.
            if (chosen && theme.Background.Image is { Length: > 0 }) _settings.BackgroundMode = BackgroundMode.Theme;
            _engine.SetTheme(theme);
            _settings.Theme = name;
            _settings.Save();
            foreach (ToolStripMenuItem i in _themesMenu.DropDownItems) i.Checked = Equals(i.Tag, name);
        }
        catch (Exception e)
        {
            Post(new { type = "toast", kind = "error", text = $"Не удалось загрузить «{name}»: {e.Message}" });
        }
    }

    // ---------- background file ----------

    private void PickBackgroundFile()
    {
        var videos = string.Join(";", FrameSources.VideoExtensions.Select(e => "*" + e));
        using var dlg = new OpenFileDialog
        {
            Title = "Фон экрана",
            Filter = $"Картинки, GIF и видео|*.png;*.jpg;*.jpeg;*.bmp;*.gif;{videos}|Видео|{videos}|GIF|*.gif|Все файлы|*.*",
        };
        if (File.Exists(_settings.BackgroundFile)) dlg.InitialDirectory = Path.GetDirectoryName(_settings.BackgroundFile);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            if (FrameSources.IsVideo(dlg.FileName) && VideoSource.FindFfmpeg() == null)
            {
                Post(new { type = "toast", kind = "error", text = "Для видео нужен ffmpeg.exe: положите его рядом с SamaLcd.exe." });
            }
            else
            {
                _settings.BackgroundFile = dlg.FileName;
                _settings.BackgroundMode = BackgroundMode.File;
                _settings.Save();
                _engine.ApplyBackgroundSettings();
            }
        }
        PostState();
    }

    private static string BackgroundKind(string path)
    {
        if (!File.Exists(path)) return "";
        var size = new FileInfo(path).Length / 1024.0 / 1024;
        var kind = FrameSources.IsVideo(path) ? "видео"
            : Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase) ? "GIF" : "картинка";
        return $"{kind}, {size:0.0} МБ";
    }

    // ---------- window and tray ----------

    private void SetPaused(bool paused)
    {
        _engine.Paused = paused;
        _pauseMenu.Checked = paused;
        if (!paused) _engine.Invalidate();
        PostState();
        PostStatus();
    }

    private void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        PostFrame();
    }

    private void ExitApp()
    {
        _exiting = true;
        _tray.Visible = false;
        _engine.Stop();
        Close();
        TrayContext.ExitThread();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _web.Dispose();
            lock (_frameLock) _lastFrame?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void UI(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static bool SafeAutostartState()
    {
        try { return Autostart.IsEnabled; }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void UseDarkTitleBar(bool dark)
    {
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = new Pen(Color.FromArgb(0, 209, 255), 4);
            g.FillEllipse(Brushes.Black, 1, 1, 30, 30);
            g.DrawArc(ring, 4, 4, 24, 24, 135, 270);
            g.FillEllipse(Brushes.White, 12, 12, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
