using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopTool.Features.Commitments;

/// <summary>Pulls the optional estimate and tag out of one typed line, so committing stays a single
/// line of text: "draft the intro 45m #writing" is Text "draft the intro", 45 minutes, tag "writing".
/// Only a whole word counts as a duration ("45m", "2h", "1.5h", "1h30m") - "3m" inside a longer word
/// is left alone.</summary>
public static partial class CommitmentParser
{
    [GeneratedRegex(@"^(?:(?<h>\d+(?:\.\d+)?)h(?:rs?)?)?(?:(?<m>\d+)m(?:ins?)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"^#(?<tag>[\w-]+)$")]
    private static partial Regex TagRegex();

    public static (string Text, int? EstimateMinutes, string? Tag) Parse(string input)
    {
        var all = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // The last duration is the estimate - an earlier one ("fix the 3m timeout 20m") is part of
        // what's being described.
        var estimateIndex = Array.FindLastIndex(all, word => TryParseDuration(word) is not null);

        var words = new List<string>();
        string? tag = null;
        for (var i = 0; i < all.Length; i++)
        {
            if (i == estimateIndex)
                continue;
            if (tag is null && TagRegex().Match(all[i]) is { Success: true } tagMatch)
                tag = tagMatch.Groups["tag"].Value.ToLowerInvariant();
            else
                words.Add(all[i]);
        }

        return (string.Join(' ', words), estimateIndex >= 0 ? TryParseDuration(all[estimateIndex]) : null, tag);
    }

    private static int? TryParseDuration(string word)
    {
        var match = DurationRegex().Match(word);
        if (!match.Success || (!match.Groups["h"].Success && !match.Groups["m"].Success))
            return null;

        var minutes = 0d;
        if (match.Groups["h"].Success)
            minutes += double.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) * 60;
        if (match.Groups["m"].Success)
            minutes += int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);

        return minutes > 0 ? (int)Math.Round(minutes) : null;
    }
}
