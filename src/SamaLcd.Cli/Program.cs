using System.Drawing;
using SamaLcd.Core;
using SamaLcd.Core.Device;
using SamaLcd.Core.Rendering;
using SamaLcd.Core.Sensors;

// Command line tool for testing the panel and building themes.
var argv = args.ToList();
string? port = TakeOption(argv, "--port");
int brightness = int.TryParse(TakeOption(argv, "--brightness"), out var b) ? b : -1;
string? background = TakeOption(argv, "--bg");
var cmd = argv.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var rest = argv.Skip(1).ToArray();
void Log(string s) => Console.WriteLine(s);

try
{
    switch (cmd)
    {
        case "ports":
            foreach (var p in PortLocator.ListPorts())
            {
                var kind = PortLocator.AwakeIds.Any(p.Matches) ? "  <- panel" : PortLocator.SleepingIds.Any(p.Matches) ? "  <- panel (sleeping)" : "";
                Console.WriteLine($"{p.Port,-6} {p.PnpId}{kind}");
            }
            break;

        case "info":
            using (var dev = LcdDevice.Connect(port, log: Log))
            {
                Console.WriteLine($"Port: {dev.PortName}\nIdentity: {dev.Identity}\nROM: {dev.RomVersion}");
                Console.WriteLine($"Status: {dev.QueryStatus().Raw}");
            }
            break;

        case "brightness":
            using (var dev = LcdDevice.Connect(port, log: Log))
                dev.SetBrightness(int.Parse(rest[0]));
            break;

        case "color":
            using (var dev = LcdDevice.Connect(port, log: Log))
            using (var bmp = new Bitmap(dev.Width, dev.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.Clear(ThemeRenderer.ParseColor(rest[0]));
                Show(dev, bmp);
            }
            break;

        case "image":
            using (var dev = LcdDevice.Connect(port, log: Log))
            using (var img = new AnimatedImage(rest[0]))
            using (var bmp = new Bitmap(dev.Width, dev.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img.FrameAt(0), 0, 0, dev.Width, dev.Height);
                }
                Show(dev, bmp);
            }
            break;

        case "theme":
        {
            // theme <folder> [seconds]: runs a theme in the foreground
            var settings = AppSettings.Load();
            if (port != null) settings.Port = port;
            if (brightness >= 0) settings.Brightness = brightness;
            if (background != null)
            {
                settings.BackgroundMode = background.StartsWith('#') ? BackgroundMode.Color : BackgroundMode.File;
                if (background.StartsWith('#')) settings.BackgroundColor = background;
                else settings.BackgroundFile = Path.GetFullPath(background);
            }
            using var sensors = SensorHub.CreateDefault();
            using var engine = new DisplayEngine(settings, sensors);
            engine.Log += Log;
            engine.Status += Log;
            engine.SetTheme(Theme.Load(rest[0]));
            engine.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (rest.Length > 1) Thread.Sleep(int.Parse(rest[1]) * 1000);
            else
            {
                Console.WriteLine("Press Enter to stop.");
                Console.ReadLine();
            }
            double secs = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"Sent {engine.FramesSent} frames in {secs:0.0} s ({engine.FramesSent / secs:0.0} fps, {engine.BytesSent / secs / 1024:0} KB/s)");
            break;
        }

        case "render":
        {
            // render <theme folder> <out.png>: draws one frame without touching the panel
            using var sensors = SensorHub.CreateDefault();
            using var r = new ThemeRenderer(Theme.Load(rest[0]), sensors.Get);
            using var bmp = r.Render();
            bmp.Save(rest[1]);
            Console.WriteLine($"Saved {rest[1]}");
            break;
        }

        case "upload":
            // upload <local file> <remote path>
            using (var dev = LcdDevice.Connect(port, log: Log))
            {
                if (rest.Length > 2) dev.UploadBlockDelayMs = int.Parse(rest[2]);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int last = -1;
                dev.UploadFile(rest[0], rest[1], p =>
                {
                    int pct = (int)(p * 100);
                    if (pct / 10 != last / 10) Console.WriteLine($"  {pct}%");
                    last = pct;
                });
                Console.WriteLine($"Uploaded in {sw.Elapsed.TotalSeconds:0.0} s; on panel: {dev.FileSize(rest[1])} bytes");
            }
            break;

        case "play":
            // play <remote path>: loops a video stored on the panel
            using (var dev = LcdDevice.Connect(port, log: Log))
            {
                dev.PlayVideo(rest[0]);
                Console.WriteLine("Playing.");
            }
            break;

        case "ls":
            using (var dev = LcdDevice.Connect(port, log: Log))
            {
                var dir = rest.Length > 0 ? rest[0] : "/root/video/";
                foreach (var f in dev.ListDir(dir)) Console.WriteLine($"{f,-40} {dev.FileSize(dir + f)}");
                Console.WriteLine($"Storage (MB): {string.Join(" / ", dev.StorageInfo())}");
            }
            break;

        case "raw":
        {
            // raw <cmd> [text]: sends a command with an ASCII payload and prints the reply (protocol experiments)
            using var dev = LcdDevice.Connect(port, log: Log);
            var text = rest.Length > 1 ? rest[1] : "";
            var payload = System.Text.Encoding.ASCII.GetBytes(text);
            dev.SendCommand((LcdCommand)byte.Parse(rest[0]), Math.Max(1, payload.Length), payload);
            Console.WriteLine($"reply: '{dev.ReadReply(3000)}'");
            break;
        }

        case "bench":
        {
            // bench [frames]: measures how fast full-screen changes reach the panel
            int n = rest.Length > 0 ? int.Parse(rest[0]) : 10;
            using var dev = LcdDevice.Connect(port, log: Log);
            dev.StopPlayback();
            var rnd = new Random(1);
            var prev = new byte[dev.Width * dev.Height * 4];
            dev.SendFullFrame(prev);
            long send = 0, status = 0, bytes = 0;
            for (int i = 0; i < n; i++)
            {
                var cur = new byte[prev.Length];
                rnd.NextBytes(cur);
                var diff = FrameCodec.EncodeDiff(prev, cur);
                var t = System.Diagnostics.Stopwatch.StartNew();
                dev.SendDiff(diff);
                send += t.ElapsedMilliseconds;
                t.Restart();
                var st = dev.QueryStatus();
                status += t.ElapsedMilliseconds;
                bytes += diff.Length;
                if (st.NeedResend) Console.WriteLine($"frame {i}: {st.Raw}");
                prev = cur;
            }
            Console.WriteLine($"{n} frames of {bytes / n / 1024} KB: send {send / n} ms/frame ({bytes / 1024.0 / 1024 / (send / 1000.0):0.0} MB/s), status {status / n} ms/frame");
            break;
        }

        case "sensors":
            using (var sensors = SensorHub.CreateDefault())
                foreach (var s in sensors.List())
                    Console.WriteLine($"{s.Id,-45} {s.Value?.ToString("0.##") ?? "-",8}  {s.Type} {s.Hardware} {s.Name}");
            break;

        default:
            Console.WriteLine("""
                samalcd-cli <command> [--port COMx] [--brightness 0-100] [--bg #RRGGBB|file.png|file.gif|file.mp4]

                  ports                     list serial ports and mark the panel
                  info                      connect and print panel identity
                  brightness <0-100>        set backlight
                  color <#RRGGBB>           fill the screen with a colour
                  image <file>              show a picture (first frame of a GIF)
                  theme <folder> [seconds]  run a theme until Enter (or for N seconds)
                  render <folder> <out.png> render one theme frame to a file
                  sensors                   list sensor keys usable in themes (run as admin)
                """);
            break;
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    return 1;
}

void Show(LcdDevice dev, Bitmap bmp)
{
    dev.StopPlayback();
    if (brightness >= 0) dev.SetBrightness(brightness);
    dev.SendFullFrame(FrameCodec.ToBgra(bmp));
}

static string? TakeOption(List<string> a, string name)
{
    int i = a.IndexOf(name);
    if (i < 0 || i + 1 >= a.Count) return null;
    var v = a[i + 1];
    a.RemoveRange(i, 2);
    return v;
}
