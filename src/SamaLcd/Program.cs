using SamaLcd.Core;
using SamaLcd.Core.Sensors;

namespace SamaLcd;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\SamaLcd.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("SAMA LCD is already running (see the tray icon).", "SAMA LCD");
            return;
        }

        ApplicationConfiguration.Initialize();
        var settings = AppSettings.Load();
        bool minimized = (args.Contains("--minimized") || settings.StartMinimized && args.Contains("--autostart")) && !args.Contains("--screenshot");

        using var sensors = SensorHub.CreateDefault();
        using var engine = new DisplayEngine(settings, sensors);
        int shot = Array.IndexOf(args, "--screenshot");
        using var form = new MainForm(settings, engine, sensors) { ScreenshotPath = shot >= 0 && shot + 1 < args.Length ? args[shot + 1] : null };
        if (!minimized) form.Show();
        engine.Start();
        Application.Run(form.TrayContext);
    }
}
