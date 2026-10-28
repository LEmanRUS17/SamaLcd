using System.Text.Json;
using SamaLcd.Core.Rendering;

namespace SamaLcd.Tests;

public class CustomLayoutTests
{
    [Fact]
    public void Item_IsCentredInItsCells()
    {
        var layout = new CustomLayout
        {
            Columns = 8, Rows = 8,
            Items = [new() { Kind = LayoutItemKind.Sensor, Key = "gpu.temp", Decimals = 1, Unit = "°", Col = 2, Row = 3, ColSpan = 4, RowSpan = 2 }],
        };
        var text = Assert.IsType<TextWidget>(Assert.Single(layout.ToTheme(480, 480).Widgets));
        Assert.Equal("{gpu.temp:0.0}°", text.Text);
        Assert.Equal(240, text.X); // columns 2..5 of 60 px
        Assert.Equal(240, text.Y); // rows 3..4
    }

    [Fact]
    public void Label_GoesAboveTheValue()
    {
        var layout = new CustomLayout
        {
            Items = [new() { Kind = LayoutItemKind.Time, Text = "HH:mm:ss", Label = "Время", Align = TextAlign.Left, Col = 0, Row = 0, ColSpan = 4, RowSpan = 2 }],
        };
        var widgets = layout.ToTheme().Widgets.Cast<TextWidget>().ToList();
        Assert.Equal(["Время", "{time:HH:mm:ss}"], widgets.Select(w => w.Text));
        Assert.True(widgets[0].Y < widgets[1].Y);
        Assert.All(widgets, w => Assert.Equal(6, w.X)); // left aligned with padding
    }

    [Fact]
    public void Text_GrowsWithItsCells()
    {
        float SizeIn(int colSpan, int rowSpan) => Assert.IsType<TextWidget>(Assert.Single(new CustomLayout
        {
            Items = [new() { Kind = LayoutItemKind.Text, Text = "Привет", ColSpan = colSpan, RowSpan = rowSpan }],
        }.ToTheme().Widgets)).Size;

        Assert.True(SizeIn(4, 1) > SizeIn(2, 1)); // limited by width
        Assert.True(SizeIn(8, 3) > SizeIn(8, 1)); // limited by height
        Assert.True(SizeIn(4, 1) <= 60 * 0.85f);  // never taller than its row
    }

    [Fact]
    public void Label_TakesTheTopRow_AndGivesItBackWhenErased()
    {
        var item = new LayoutItem { Kind = LayoutItemKind.Sensor, Key = "cpu.temp", Unit = "°", Label = "CPU", Col = 2, Row = 2, ColSpan = 4, RowSpan = 2 };
        var layout = new CustomLayout { Items = [item] };
        var withLabel = layout.ToTheme().Widgets.Cast<TextWidget>().ToList();
        Assert.InRange(withLabel[0].Y, 120, 180);  // caption inside row 2
        Assert.InRange(withLabel[1].Y, 180, 240);  // value inside row 3
        Assert.True(withLabel[0].Size < withLabel[1].Size);

        item.Label = "";
        var value = Assert.IsType<TextWidget>(Assert.Single(layout.ToTheme().Widgets));
        Assert.Equal(180, value.Y);                // centred in both rows
        Assert.True(value.Size > withLabel[1].Size);
    }

    [Fact]
    public void Bar_SpansTheCells()
    {
        var layout = new CustomLayout
        {
            Items = [new() { Kind = LayoutItemKind.Bar, Key = "cpu.load", Max = 50, Col = 1, Row = 4, ColSpan = 6, Size = 10 }],
        };
        var bar = Assert.IsType<BarWidget>(Assert.Single(layout.ToTheme().Widgets));
        Assert.Equal(66, bar.X);
        Assert.Equal(348, bar.Width);
        Assert.Equal(50, bar.Max);
    }

    [Fact]
    public void Json_UsesTheNamesOfTheUi()
    {
        var json = JsonSerializer.Serialize(CustomLayout.Default(), Theme.JsonOptions);
        Assert.Contains("\"kind\": \"sensor\"", json);
        Assert.Contains("\"colSpan\"", json);
        var back = JsonSerializer.Deserialize<CustomLayout>(json, Theme.JsonOptions)!;
        Assert.Equal(CustomLayout.Default().Items.Count, back.Items.Count);
    }
}
