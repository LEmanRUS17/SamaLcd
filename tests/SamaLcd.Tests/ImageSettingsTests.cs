using System.Text.Json;
using SamaLcd.Core;
using SamaLcd.Core.Rendering;

namespace SamaLcd.Tests;

public class ImageSettingsTests
{
    [Fact]
    public void ChoosingAndCroppingAVideoLeavesAppliedAndPersistedSettingsUntouched()
    {
        var applied = new AppSettings { BackgroundMode = BackgroundMode.File, BackgroundFile = "current.png" };
        var original = ImageSettings.From(applied);
        var draft = original with { File = "new.mp4", Crop = new SquareCrop(.2,.1,.5), Fit = ImageFit.Contain, Rotation = 90, OnDevice = false };
        var preview = draft.PreviewSettings(applied);
        Assert.Equal("new.mp4", DisplayEngine.WithBackground(new Theme(), preview).Background.Image);
        Assert.Equal(draft.Crop, preview.BackgroundCrop);
        Assert.Equal(90, preview.Rotation);
        Assert.Equal(original, ImageSettings.From(applied));
        var restarted = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(applied, Theme.JsonOptions), Theme.JsonOptions)!;
        Assert.Equal(original, ImageSettings.From(restarted));
    }

    [Fact]
    public void ApplyCommitsTheWholeImageDraftAndKeepsOtherSettings()
    {
        var applied = new AppSettings { Language = "ja", Theme = "Clock", Brightness = 42, Port = "COM3" };
        var draft = ImageSettings.From(applied) with { Mode = BackgroundMode.File, File = "chosen.mp4", Color = "#123456", GalleryFile = "gallery.gif", Crop = new(.1,.2,.6), Fit = ImageFit.Stretch, Rotation = 270, OnDevice = false };
        draft.ApplyTo(applied);
        Assert.Equal(draft, ImageSettings.From(applied));
        Assert.Equal("ja", applied.Language); Assert.Equal("Clock", applied.Theme);
        Assert.Equal(42, applied.Brightness); Assert.Equal("COM3", applied.Port);
    }

    [Fact]
    public void DiscardRestoresAppliedSelectionAndGalleryPreviewUsesTheChosenFolder()
    {
        var applied = new AppSettings { GalleryDirectory = Path.Combine(Path.GetTempPath(), "chosen-gallery"), GalleryFile = "original.png", BackgroundMode = BackgroundMode.Gallery };
        var draft = ImageSettings.From(applied) with { GalleryFile = "new.gif" };
        Assert.Equal(Path.Combine(applied.GalleryDirectory, "new.gif"), draft.SourcePath(draft.PreviewSettings(applied)));
        draft = ImageSettings.From(applied);
        Assert.Equal("original.png", draft.GalleryFile);
    }
}
