namespace DesktopTool.Features.Wallpapers;

/// <summary>Pure next-slide selection, kept apart from the timer and file system so it can be
/// tested on its own.</summary>
public static class SlideshowPicker
{
    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>The next image to show after current. In order: the file after current by name,
    /// wrapping round at the end (or the first file if current isn't in the list any more).
    /// Shuffle: a random file other than current, whenever there's more than one to choose from.
    /// Null only when files is empty.</summary>
    public static string? Next(IReadOnlyList<string> files, string? current, bool shuffle, Random random)
    {
        if (files.Count == 0)
            return null;
        if (files.Count == 1)
            return files[0];

        var currentIndex = current is null
            ? -1
            : IndexOf(files, current);

        if (!shuffle)
            return files[(currentIndex + 1) % files.Count];

        if (currentIndex < 0)
            return files[random.Next(files.Count)];

        // Pick from every index except currentIndex, without retrying.
        var pick = random.Next(files.Count - 1);
        return files[pick >= currentIndex ? pick + 1 : pick];
    }

    /// <summary>The folder's images (top level only), sorted by name. Empty if the folder is missing
    /// or unreadable.</summary>
    public static IReadOnlyList<string> ListImages(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static int IndexOf(IReadOnlyList<string> files, string path)
    {
        for (var i = 0; i < files.Count; i++)
            if (string.Equals(files[i], path, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
