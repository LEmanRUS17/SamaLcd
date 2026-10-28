using SamaLcd.Core;
using SamaLcd.Core.Rendering;

namespace SamaLcd.Tests;

public class BackgroundTests
{
    private static readonly Theme Sample = new() { Background = new Background { Color = "#123456" }, Widgets = [new TextWidget { Text = "x" }] };

    [Fact]
    public void Gallery_ReplacesTheBackgroundAndKeepsWidgets()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllBytes(Path.Combine(dir, "bg.png"), []);
        var s = new AppSettings { BackgroundMode = BackgroundMode.Gallery, GalleryDirectory = dir, GalleryFile = "bg.png" };

        var t = DisplayEngine.WithBackground(Sample, s);
        Assert.Equal(Path.Combine(dir, "bg.png"), t.Background.Image);
        Assert.Single(t.Widgets);
    }

    [Fact]
    public void Gallery_WithoutAFile_KeepsTheVariantsBackground()
    {
        var s = new AppSettings { BackgroundMode = BackgroundMode.Gallery, GalleryDirectory = Path.GetTempPath(), GalleryFile = "missing.png" };
        Assert.Same(Sample, DisplayEngine.WithBackground(Sample, s));
    }
}
