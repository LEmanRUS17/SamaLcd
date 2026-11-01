using SamaLcd.Core.Rendering;

namespace SamaLcd.Core;

/// <summary>An immutable editing copy. Only ApplyTo changes the settings used by the display engine.</summary>
public sealed record ImageSettings(BackgroundMode Mode, string Color, string File, string GalleryFile,
    ImageFit Fit, SquareCrop? Crop, int Rotation, bool OnDevice)
{
    public static ImageSettings From(AppSettings settings) => new(settings.BackgroundMode, settings.BackgroundColor,
        settings.BackgroundFile, settings.GalleryFile, settings.BackgroundFit, settings.BackgroundCrop,
        settings.Rotation, settings.PlayVideoOnDevice);

    public string SourcePath(AppSettings settings) => Mode switch
    {
        BackgroundMode.File => File,
        BackgroundMode.Gallery => Path.Combine(settings.ResolvedGalleryDirectory, GalleryFile),
        _ => "",
    };

    public void ApplyTo(AppSettings settings)
    {
        settings.BackgroundMode = Mode;
        settings.BackgroundColor = Color;
        settings.BackgroundFile = File;
        settings.GalleryFile = GalleryFile;
        settings.BackgroundFit = Fit;
        settings.BackgroundCrop = Crop;
        settings.Rotation = Rotation;
        settings.PlayVideoOnDevice = OnDevice;
    }

    public AppSettings PreviewSettings(AppSettings applied)
    {
        var preview = new AppSettings { GalleryDirectory = applied.GalleryDirectory, VideoFps = applied.VideoFps };
        ApplyTo(preview);
        return preview;
    }
}
