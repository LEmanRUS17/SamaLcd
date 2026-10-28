using System.Text.Json;
using System.Text.Json.Serialization;

namespace SamaLcd.Core.Rendering;

/// <summary>
/// A screen layout loaded from themes/&lt;name&gt;/theme.json. Paths are relative to the theme folder.
/// Text fields may contain placeholders: {cpu.temp:0}, {gpu.load}, {time:HH:mm}, {date:dd.MM}.
/// </summary>
public sealed class Theme
{
    public string Name { get; set; } = "Theme";
    /// <summary>Redraw interval in milliseconds.</summary>
    public int IntervalMs { get; set; } = 1000;
    public Background Background { get; set; } = new();
    public List<Widget> Widgets { get; set; } = [];

    [JsonIgnore] public string Directory { get; set; } = "";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Theme Load(string themeDirOrFile)
    {
        var file = File.Exists(themeDirOrFile) ? themeDirOrFile : Path.Combine(themeDirOrFile, "theme.json");
        var theme = JsonSerializer.Deserialize<Theme>(File.ReadAllText(file), JsonOptions) ?? new Theme();
        theme.Directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        return theme;
    }

    public string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(Directory, path);

    /// <summary>Lists theme folders (folders containing theme.json) under <paramref name="root"/>.</summary>
    public static IEnumerable<string> Discover(string root) =>
        System.IO.Directory.Exists(root)
            ? System.IO.Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "theme.json"))).OrderBy(d => d)
            : [];
}

public sealed class Background
{
    public string Color { get; set; } = "#000000";
    /// <summary>PNG/JPG/BMP, animated GIF or a video (MP4, MKV, WEBM, MOV, AVI — decoded with ffmpeg).</summary>
    public string? Image { get; set; }
    public ImageFit Fit { get; set; } = ImageFit.Cover;
    /// <summary>Frame rate for video backgrounds.</summary>
    public int Fps { get; set; } = 20;
}

public enum ImageFit { Cover, Contain, Stretch }

public enum TextAlign { Left, Center, Right }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextWidget), "text")]
[JsonDerivedType(typeof(ArcWidget), "arc")]
[JsonDerivedType(typeof(BarWidget), "bar")]
[JsonDerivedType(typeof(ImageWidget), "image")]
[JsonDerivedType(typeof(CircleWidget), "circle")]
public abstract class Widget
{
    public float X { get; set; }
    public float Y { get; set; }
    public bool Visible { get; set; } = true;
}

public sealed class TextWidget : Widget
{
    public string Text { get; set; } = "";
    public string Font { get; set; } = "Segoe UI";
    public float Size { get; set; } = 24;
    public bool Bold { get; set; }
    public string Color { get; set; } = "#FFFFFF";
    /// <summary>X is the left edge, centre or right edge depending on alignment; Y is the vertical centre.</summary>
    public TextAlign Align { get; set; } = TextAlign.Center;
}

/// <summary>Circular gauge centred on (X, Y). Angles in degrees, 0 = 3 o'clock, clockwise.</summary>
public sealed class ArcWidget : Widget
{
    public string Value { get; set; } = "cpu.load";
    public float Min { get; set; }
    public float Max { get; set; } = 100;
    public float Radius { get; set; } = 200;
    public float Thickness { get; set; } = 16;
    public float StartAngle { get; set; } = 135;
    public float SweepAngle { get; set; } = 270;
    public string Color { get; set; } = "#00C8FF";
    /// <summary>Optional second colour; the arc fades from Color to ColorEnd as the value rises.</summary>
    public string? ColorEnd { get; set; }
    public string Track { get; set; } = "#26FFFFFF";
    public bool RoundCaps { get; set; } = true;
}

public sealed class BarWidget : Widget
{
    public string Value { get; set; } = "cpu.load";
    public float Min { get; set; }
    public float Max { get; set; } = 100;
    public float Width { get; set; } = 200;
    public float Height { get; set; } = 12;
    public string Color { get; set; } = "#00C8FF";
    public string Track { get; set; } = "#26FFFFFF";
}

public sealed class ImageWidget : Widget
{
    public string Path { get; set; } = "";
    public float Width { get; set; }
    public float Height { get; set; }
}

public sealed class CircleWidget : Widget
{
    public float Radius { get; set; } = 50;
    public string Color { get; set; } = "#80000000";
}
