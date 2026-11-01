using L = SamaLcd.Core.Localization.Localizer;
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
            L.Language = AppSettings.Load().Language;
            MessageBox.Show(L.Get("SAMA LCD is already running (see the tray icon)."), "SAMA LCD");
            return;
        }

        using var log = new DiagnosticLog(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SamaLcd", "logs"));
        AppLog.Current = log;
        Exception? fatal = null;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            fatal = e.Exception;
            log.Write("FATAL", "exception.ui_thread", exception: e.Exception);
            Application.Exit();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Write("FATAL", "exception.unhandled", new { e.IsTerminating }, e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error("exception.unobserved_task", e.Exception); e.SetObserved(); };
        try
        {
            var settings = AppSettings.Load();
            L.Language = settings.Language;
            log.DebugEnabled = settings.DebugLogEnabled;
            AppLog.Info("settings.loaded", new { settings.Language, settings.DebugLogEnabled, settings.BackgroundMode, settings.Rotation, settings.PlayVideoOnDevice });
            ApplicationConfiguration.Initialize();
            bool minimized = (args.Contains("--minimized") || settings.StartMinimized && args.Contains("--autostart")) && !args.Contains("--screenshot");
            using (var sensors = SensorHub.CreateDefault())
            using (var engine = new DisplayEngine(settings, sensors))
            {
                int shot = Array.IndexOf(args, "--screenshot");
                using var form = new MainForm(settings, engine, sensors) { ScreenshotPath = shot >= 0 && shot + 1 < args.Length ? args[shot + 1] : null };
                if (!minimized) form.Show();
                engine.Start();
                Application.Run(form.TrayContext);
            }
            if (fatal == null) log.CompleteSession();
        }
        catch (Exception e) { fatal = e; log.Write("FATAL", "exception.main", exception: e); }
        finally
        {
            if (fatal != null) MessageBox.Show(L.Get("An unexpected error occurred. Logs were saved in:\n{0}", log.DirectoryPath), "SAMA LCD", MessageBoxButtons.OK, MessageBoxIcon.Error);
            AppLog.Current = null;
        }
    }
}
