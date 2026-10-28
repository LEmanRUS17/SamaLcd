using System.Text.Json;

namespace SamaLcd.Core;

public enum BackgroundMode { Theme, Color, File, Gallery }

/// <summary>User settings, stored in %APPDATA%\SamaLcd\settings.json.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "Dashboard";
    /// <summary>Folder with themes; empty means the "themes" folder next to the program.</summary>
    public string ThemesDirectory { get; set; } = "";
    public int Brightness { get; set; } = 80;
    /// <summary>Sama never sends more than 76 % of the raw 0-255 value.</summary>
    public double BrightnessScale { get; set; } = 0.76;
    /// <summary>0, 90, 180 or 270 degrees.</summary>
    public int Rotation { get; set; }
    /// <summary>COM port, or empty to detect the panel automatically.</summary>
    public string Port { get; set; } = "";
    public int Width { get; set; } = 480;
    public int Height { get; set; } = 480;
    public int FullFrameDelayMs { get; set; } = 500;
    public bool StartMinimized { get; set; } = true;

    /// <summary>Background chosen in the app, drawn instead of the theme's own background.</summary>
    public BackgroundMode BackgroundMode { get; set; } = BackgroundMode.Theme;
    public string BackgroundColor { get; set; } = "#000000";
    /// <summary>Picture, GIF or video used when <see cref="BackgroundMode"/> is File.</summary>
    public string BackgroundFile { get; set; } = "";
    public Rendering.ImageFit BackgroundFit { get; set; } = Rendering.ImageFit.Cover;
    public int VideoFps { get; set; } = 20;
    /// <summary>
    /// Upload videos and animated GIFs to the panel's SD card and let it play them (smooth, like Sama);
    /// otherwise they are streamed frame by frame (~4 fps).
    /// </summary>
    public bool PlayVideoOnDevice { get; set; } = true;
    /// <summary>File name in the gallery folder used when <see cref="BackgroundMode"/> is Gallery.</summary>
    public string GalleryFile { get; set; } = "";
    /// <summary>Folder with ready backgrounds; empty means the "backgrounds" folder next to the program.</summary>
    public string GalleryDirectory { get; set; } = "";

    public string ResolvedGalleryDirectory =>
        string.IsNullOrWhiteSpace(GalleryDirectory) ? Path.Combine(AppContext.BaseDirectory, "backgrounds") : GalleryDirectory;
    public string GalleryPath => Path.Combine(ResolvedGalleryDirectory, GalleryFile);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SamaLcd", "settings.json");

    public string ResolvedThemesDirectory =>
        string.IsNullOrWhiteSpace(ThemesDirectory) ? Path.Combine(AppContext.BaseDirectory, "themes") : ThemesDirectory;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Rendering.Theme.JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // fall back to defaults on a broken file
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Rendering.Theme.JsonOptions));
    }
}
