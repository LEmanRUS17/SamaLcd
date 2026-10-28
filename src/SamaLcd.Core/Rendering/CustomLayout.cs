using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SamaLcd.Core.Rendering;

public enum LayoutItemKind { Sensor, Text, Time, Date, Bar }

/// <summary>
/// A layout the user builds in the app: values and captions placed on a grid over the round screen.
/// Stored in %APPDATA%\SamaLcd\custom-layout.json and turned into an ordinary <see cref="Theme"/> for drawing.
/// </summary>
public sealed class CustomLayout
{
    /// <summary>Name under which the layout appears next to the themes.</summary>
    public const string ThemeName = "@custom";

    public int Columns { get; set; } = 8;
    public int Rows { get; set; } = 8;
    public string Background { get; set; } = "#05080D";
    public List<LayoutItem> Items { get; set; } = [];

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SamaLcd", "custom-layout.json");

    public static CustomLayout Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<CustomLayout>(File.ReadAllText(FilePath), Theme.JsonOptions) ?? Default();
        }
        catch
        {
            // fall back to the sample layout on a broken file
        }
        return Default();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Theme.JsonOptions));
    }

    public static CustomLayout Default() => new()
    {
        Items =
        [
            new() { Kind = LayoutItemKind.Text, Text = "SAMA", Col = 3, Row = 1, ColSpan = 2, Color = "#8FA3B8" },
            new() { Kind = LayoutItemKind.Sensor, Key = "cpu.temp", Label = "CPU", Unit = "°", Col = 1, Row = 2, ColSpan = 3, RowSpan = 2, Color = "#00D1FF" },
            new() { Kind = LayoutItemKind.Sensor, Key = "gpu.temp", Label = "GPU", Unit = "°", Col = 4, Row = 2, ColSpan = 3, RowSpan = 2, Color = "#C9B8FF" },
            new() { Kind = LayoutItemKind.Bar, Key = "cpu.load", Label = "CPU {cpu.load:0}%", Col = 1, Row = 4, ColSpan = 6, Size = 10, Color = "#00D1FF" },
            new() { Kind = LayoutItemKind.Bar, Key = "gpu.load", Label = "GPU {gpu.load:0}%", Col = 1, Row = 5, ColSpan = 6, Size = 10, Color = "#8A5CFF" },
            new() { Kind = LayoutItemKind.Time, Text = "HH:mm", Col = 2, Row = 6, ColSpan = 4 },
        ],
    };

    /// <summary>Builds the theme that draws this layout on a <paramref name="width"/>×<paramref name="height"/> screen.</summary>
    public Theme ToTheme(int width = 480, int height = 480)
    {
        var theme = new Theme
        {
            Name = "Своя раскладка",
            IntervalMs = 1000,
            Directory = Path.GetDirectoryName(FilePath)!,
            Background = new Background { Color = Background },
        };
        float cw = width / (float)Math.Max(1, Columns), ch = height / (float)Math.Max(1, Rows);
        foreach (var item in Items)
        {
            var left = item.Col * cw;
            var top = item.Row * ch;
            var w = Math.Max(1, item.ColSpan) * cw;
            var h = Math.Max(1, item.RowSpan) * ch;
            theme.Widgets.AddRange(item.ToWidgets(left, top, w, h, ch));
        }
        return theme;
    }
}

public sealed partial class LayoutItem
{
    public LayoutItemKind Kind { get; set; } = LayoutItemKind.Sensor;
    /// <summary>Sensor key for <see cref="LayoutItemKind.Sensor"/> and <see cref="LayoutItemKind.Bar"/>.</summary>
    public string Key { get; set; } = "cpu.temp";
    /// <summary>Caption for Text (may hold {key:format} placeholders) or the format for Time/Date.</summary>
    public string Text { get; set; } = "";
    /// <summary>Small caption above the value; may hold placeholders too.</summary>
    public string Label { get; set; } = "";
    public int Decimals { get; set; }
    public string Unit { get; set; } = "";
    public float Max { get; set; } = 100;
    public int Col { get; set; }
    public int Row { get; set; }
    public int ColSpan { get; set; } = 2;
    public int RowSpan { get; set; } = 1;
    public string Color { get; set; } = "#FFFFFF";
    public string LabelColor { get; set; } = "#8FA3B8";
    /// <summary>Bar thickness for <see cref="LayoutItemKind.Bar"/>; text is sized to its cells.</summary>
    public float Size { get; set; } = 40;
    public bool Bold { get; set; } = true;
    public TextAlign Align { get; set; } = TextAlign.Center;

    /// <summary>The text shown for the item, with placeholders for the renderer.</summary>
    public string Template => Kind switch
    {
        LayoutItemKind.Sensor => $"{{{Key}:{(Decimals > 0 ? "0." + new string('0', Decimals) : "0")}}}{Unit}",
        LayoutItemKind.Time => $"{{time:{(Text.Length > 0 ? Text : "HH:mm")}}}",
        LayoutItemKind.Date => $"{{date:{(Text.Length > 0 ? Text : "dd.MM.yyyy")}}}",
        _ => Text,
    };

    /// <param name="cellHeight">height of one grid row: a caption takes the top row of a taller item</param>
    internal IEnumerable<Widget> ToWidgets(float left, float top, float w, float h, float cellHeight)
    {
        const float pad = 6;
        float x = Align switch { TextAlign.Left => left + pad, TextAlign.Right => left + w - pad, _ => left + w / 2 };
        float innerW = Math.Max(1, w - pad * 2);

        // the caption gets its own grid row, or the upper part of a one-row item
        float labelH = Label.Length == 0 ? 0 : RowSpan > 1 ? cellHeight : h * 0.42f;
        float bodyTop = top + labelH, bodyH = h - labelH;

        float body = Kind == LayoutItemKind.Bar
            ? Math.Clamp(Size, 2, Math.Max(2, bodyH * 0.7f))
            : TextFit.Size(Sample(Template), Bold, innerW, bodyH);

        if (Label.Length > 0)
        {
            var bold = Kind == LayoutItemKind.Bar && Bold;
            float labelSize = TextFit.Size(Sample(Label), bold, innerW, labelH, Kind == LayoutItemKind.Bar && RowSpan == 1 ? 0.8f : 0.6f);
            if (Kind != LayoutItemKind.Bar) labelSize = Math.Min(labelSize, body * 0.6f);
            // a caption over a value sits at the bottom of its row, next to the value
            float labelY = Kind == LayoutItemKind.Bar ? top + labelH / 2 : top + labelH - labelSize * 0.6f;
            yield return new TextWidget { X = x, Y = labelY, Text = Label, Size = labelSize, Color = LabelColor, Align = Align, Bold = bold };
        }

        if (Kind == LayoutItemKind.Bar)
            yield return new BarWidget { X = left + pad, Y = bodyTop + (bodyH - body) / 2, Width = innerW, Height = body, Value = Key, Max = Max, Color = Color };
        else
            yield return new TextWidget { X = x, Y = bodyTop + bodyH / 2, Text = Template, Size = body, Bold = Bold, Color = Color, Align = Align };
    }

    /// <summary>The widest text a template is likely to show, used to size the font.</summary>
    private static string Sample(string template) => Placeholder().Replace(template, m =>
    {
        var key = m.Groups[1].Value;
        var format = m.Groups[2].Success ? m.Groups[2].Value : "";
        if (key is "time" or "date")
            return new DateTime(2026, 12, 28, 23, 58, 58).ToString(format.Length > 0 ? format : key == "time" ? "HH:mm" : "dd.MM.yyyy", CultureInfo.CurrentCulture);
        var digits = key.Contains("clock") || key.Contains("fan") || key.Contains("rpm") ? "8888" : "100";
        var dot = format.IndexOf('.');
        return dot < 0 ? digits : digits + "." + new string('8', format.Length - dot - 1);
    });

    [GeneratedRegex(@"\{([A-Za-z0-9_./\-]+)(?::([^}]*))?\}")]
    private static partial Regex Placeholder();
}

/// <summary>Picks the font size at which a line fills a box, measured the way the renderer draws it.</summary>
internal static class TextFit
{
    private const float Probe = 100;
    private static readonly object Sync = new();
    private static readonly Bitmap Canvas = new(1, 1);
    private static readonly Graphics G = CreateGraphics();

    private static Graphics CreateGraphics()
    {
        var g = Graphics.FromImage(Canvas);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g;
    }

    /// <param name="fill">share of the box height the letters may take</param>
    public static float Size(string text, bool bold, float width, float height, float fill = 0.85f)
    {
        float byHeight = height * fill;
        if (text.Length == 0) return Math.Max(8, byHeight);
        float measured;
        lock (Sync)
        {
            using var font = new Font("Segoe UI", Probe, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            using var fmt = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
            measured = G.MeasureString(text, font, PointF.Empty, fmt).Width;
        }
        float byWidth = measured > 0 ? width / measured * Probe : byHeight;
        return Math.Max(8, MathF.Floor(Math.Min(byHeight, byWidth)));
    }
}
