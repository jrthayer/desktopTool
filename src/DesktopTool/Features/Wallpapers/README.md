# Wallpapers

The **Wallpapers** widget gives each monitor its own desktop background. It shows a small map of
your monitors, laid out the way Windows arranges them, and each tile shows that monitor's current
background. A number badge names the display (★ marks the main one), and a corner badge shows ▶
for a slideshow or ● for a solid color. Hover over a tile for details. Click a tile to change it:

- **Image...** - pick any image file for that monitor.
- **Solid Color** - neutrals, the app's color presets, **Custom...** (full color picker) or the
  **Eyedropper** (pick a color from anywhere on screen).
- **Slideshow** - **Choose Folder...** starts a slideshow of that folder's images (top level only,
  sorted by name) and shows the first one straight away. Also here: **Change Every** (1 minute to
  1 day), **Shuffle** (random order, never the same image twice in a row) and **Next Image**.
  Each monitor has its own folder, interval and shuffle setting.

**Settings > Fit (all monitors)** chooses how images are sized: Fill, Fit, Stretch, Center or Tile.

The widget is toggled from its own row in [Widget Manager](../WidgetManager/README.md).

## How it works

Everything goes through the real Windows wallpaper, using the Shell's `IDesktopWallpaper` interface
([`Native/DesktopWallpaper.cs`](Native/DesktopWallpaper.cs)). It's the only Windows API that can set
a different image on each monitor. That interface only does images per monitor; its background
color and built-in slideshow apply to every monitor at once. So
[`WallpaperService`](WallpaperService.cs) builds the other two kinds on top of images:

- **Solid color** writes a PNG of that color at the monitor's resolution to
  `%APPDATA%\DesktopTool\Wallpapers\` and sets it as that monitor's image. Each color/size pair is
  generated once and reused after that.
- **Slideshow** is this app's own timer. It checks every 30 seconds and sets the next image on each
  monitor whose interval has run out. The folder is re-read every time, so new images get picked up.

Each monitor's choice is saved to `%APPDATA%\DesktopTool\wallpapers.json`. It's keyed by the
monitor's device path, which stays the same across reboots. The widget's own position and look are
saved to `wallpaper-widget.json`. The image currently on each monitor isn't stored here at all;
the widget reads it from Windows each time, so it stays correct when you change the wallpaper in
Windows Settings.

## Limitations

- **Slideshows only advance while Desktop Tool is running.** After you exit, the last image just
  stays. When the app starts again, a monitor that's overdue changes right away, and one that isn't
  waits out the rest of its interval.
- **Fit mode is shared by every monitor.** It's a single Windows setting. Span isn't offered,
  because it stretches one image across all monitors and would override every per-monitor choice.
- Picking a background here replaces any Windows slideshow or Windows Spotlight background.
- If a slideshow folder is moved or emptied, that monitor's tile shows a warning icon, and the tile
  tooltip says what went wrong. It retries once per interval.
- Changing a monitor's wallpaper in Windows Settings while it's on a slideshow doesn't stop the
  slideshow. Its next change will replace your pick.
