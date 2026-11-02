using System.Drawing;
using DesktopTool.Features.Wallpapers;
using Xunit;

namespace DesktopTool.Tests;

public sealed class MonitorMapLayoutTests
{
    [Fact]
    public void SingleMonitorFillsAreaKeepingAspectRatio()
    {
        var tiles = MonitorMapLayout.Fit(new[] { new Rectangle(0, 0, 1920, 1080) }, new Rectangle(0, 0, 400, 400), gap: 0);

        var tile = Assert.Single(tiles);
        Assert.Equal(400, tile.Width);
        Assert.Equal(225, tile.Height);
        // Centered vertically.
        Assert.Equal((400 - 225) / 2, tile.Y, tolerance: 1);
    }

    [Fact]
    public void RealThreeMonitorLayoutKeepsOrderOffsetsAndStaysInside()
    {
        var monitors = new[]
        {
            new Rectangle(-1080, 81, 1080, 1920),   // portrait, left
            new Rectangle(0, 0, 2560, 1440),        // main, middle
            new Rectangle(2560, 174, 1920, 1080),   // right
        };
        var area = new Rectangle(10, 38, 300, 140);

        var tiles = MonitorMapLayout.Fit(monitors, area, gap: 6);

        Assert.Equal(3, tiles.Count);
        Assert.All(tiles, t => Assert.True(area.Contains(t), $"{t} outside {area}"));
        Assert.True(tiles[0].Right <= tiles[1].Left);
        Assert.True(tiles[1].Right <= tiles[2].Left);
        // The main monitor starts highest, the right monitor lower than the left one.
        Assert.True(tiles[1].Top < tiles[0].Top);
        Assert.True(tiles[0].Top < tiles[2].Top);
        // Portrait monitor stays taller than wide.
        Assert.True(tiles[0].Height > tiles[0].Width);
    }

    [Fact]
    public void EmptyInputGivesEmptyOutput()
    {
        Assert.Empty(MonitorMapLayout.Fit(Array.Empty<Rectangle>(), new Rectangle(0, 0, 100, 100), gap: 4));
    }
}

public sealed class SlideshowPickerTests
{
    private static readonly string[] Files = { @"C:\pics\a.jpg", @"C:\pics\b.jpg", @"C:\pics\c.jpg" };

    [Fact]
    public void InOrderAdvancesAndWrapsAround()
    {
        var random = new Random(1);
        Assert.Equal(Files[0], SlideshowPicker.Next(Files, null, shuffle: false, random));
        Assert.Equal(Files[1], SlideshowPicker.Next(Files, Files[0], shuffle: false, random));
        Assert.Equal(Files[0], SlideshowPicker.Next(Files, Files[2], shuffle: false, random));
    }

    [Fact]
    public void InOrderRestartsWhenCurrentIsGone()
    {
        Assert.Equal(Files[0], SlideshowPicker.Next(Files, @"C:\pics\deleted.jpg", shuffle: false, new Random(1)));
    }

    [Fact]
    public void ShuffleNeverRepeatsCurrent()
    {
        var random = new Random(42);
        for (var i = 0; i < 200; i++)
            Assert.NotEqual(Files[1], SlideshowPicker.Next(Files, Files[1], shuffle: true, random));
    }

    [Fact]
    public void SingleFileAlwaysReturnsIt()
    {
        var one = new[] { @"C:\pics\only.png" };
        Assert.Equal(one[0], SlideshowPicker.Next(one, one[0], shuffle: true, new Random(1)));
    }

    [Fact]
    public void EmptyFolderReturnsNull()
    {
        Assert.Null(SlideshowPicker.Next(Array.Empty<string>(), null, shuffle: false, new Random(1)));
    }
}
