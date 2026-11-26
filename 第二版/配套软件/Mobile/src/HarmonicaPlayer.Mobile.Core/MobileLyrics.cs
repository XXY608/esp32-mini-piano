using System.Globalization;
using System.Text.RegularExpressions;

namespace HarmonicaPlayer.Mobile.Core;

public sealed record MobileLyricCue(double StartMilliseconds, string Text);

/// <summary>
/// Small Android-safe LRC reader. The Windows LyricsTimeline uses Win32
/// collation helpers, so the mobile client keeps timestamp parsing independent
/// and uses the same BPM/note timeline as the practice player.
/// </summary>
public static class MobileLyricsTimeline
{
    private static readonly Regex Timestamp = new(
        @"^\s*\[(?<minute>\d{1,3}):(?<second>\d{2})(?:[.:](?<fraction>\d{1,3}))?\]\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<MobileLyricCue> Parse(string? text, bool includeEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<MobileLyricCue>();
        var cues = new List<MobileLyricCue>();
        foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            Match match = Timestamp.Match(rawLine);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["minute"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minute) ||
                !int.TryParse(match.Groups["second"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int second) ||
                second is < 0 or > 59) continue;

            double fraction = 0;
            string fractionText = match.Groups["fraction"].Value;
            if (fractionText.Length > 0 && double.TryParse("0." + fractionText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double parsedFraction))
                fraction = parsedFraction;
            string lyric = match.Groups["text"].Value.Trim();
            if (lyric.Length == 0 && !includeEmpty) continue;
            cues.Add(new MobileLyricCue((minute * 60 + second + fraction) * 1000d, lyric));
        }
        return cues.OrderBy(cue => cue.StartMilliseconds).ToArray();
    }

    public static string? At(IReadOnlyList<MobileLyricCue> cues, double elapsedMilliseconds)
    {
        if (cues.Count == 0 || elapsedMilliseconds < cues[0].StartMilliseconds) return null;
        int low = 0;
        int high = cues.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (cues[middle].StartMilliseconds <= elapsedMilliseconds) low = middle + 1;
            else high = middle;
        }
        return low == 0 ? null : cues[low - 1].Text;
    }
}
