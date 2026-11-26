using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Globalization;

namespace HarmonicaPlayer;

public sealed record LyricsLayout
{
    // Values are normalized to the primary work area so the layout survives
    // resolution changes and can be previewed without opening a real overlay.
    public double Left { get; init; } = 0.15;
    public double Top { get; init; } = 0.78;
    public double Width { get; init; } = 0.70;
    public double Height { get; init; } = 0.14;
    public double FontSize { get; init; } = 36;
    public string CurrentColor { get; init; } = "#FFA6AC";
    public string NextColor { get; init; } = "#FFFFFF";

    public LyricsLayout Normalize()
    {
        double width = Math.Clamp(Width, 0.15, 1.0);
        double height = Math.Clamp(Height, 0.08, 0.60);
        return this with
        {
            Left = Math.Clamp(Left, 0, 1 - width),
            Top = Math.Clamp(Top, 0, 1 - height),
            Width = width,
            Height = height,
            FontSize = Math.Clamp(FontSize, 16, 72),
            CurrentColor = NormalizeColor(CurrentColor, "#FFA6AC"),
            NextColor = NormalizeColor(NextColor, "#FFFFFF")
        };
    }

    public void Validate()
    {
        if (double.IsNaN(Left) || double.IsInfinity(Left) ||
            double.IsNaN(Top) || double.IsInfinity(Top) ||
            double.IsNaN(Width) || double.IsInfinity(Width) ||
            double.IsNaN(Height) || double.IsInfinity(Height) ||
            double.IsNaN(FontSize) || double.IsInfinity(FontSize))
            throw new FormatException("歌词窗口布局包含无效数值。");

        var normalized = Normalize();
        if (Math.Abs(Left - normalized.Left) > 0.000001 ||
            Math.Abs(Top - normalized.Top) > 0.000001 ||
            Math.Abs(Width - normalized.Width) > 0.000001 ||
            Math.Abs(Height - normalized.Height) > 0.000001 ||
            Math.Abs(FontSize - normalized.FontSize) > 0.000001 ||
            !string.Equals(CurrentColor, normalized.CurrentColor, StringComparison.Ordinal) ||
            !string.Equals(NextColor, normalized.NextColor, StringComparison.Ordinal))
            throw new FormatException("歌词窗口位置、大小或字号超出允许范围。");
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        string candidate = value?.Trim() ?? "";
        return Regex.IsMatch(candidate, "^#[0-9a-fA-F]{6}$")
            ? candidate.ToUpperInvariant()
            : fallback;
    }
}

public sealed record LyricsCue(double StartMilliseconds, string Text);
public sealed record LyricHighlight(int Start, int Length);

public sealed record LyricsAlignment(
    bool Applied,
    int MatchedCueCount,
    double StartCorrectionMilliseconds,
    double EndCorrectionMilliseconds,
    double MaxCorrectionMilliseconds,
    double Confidence,
    string Message)
{
    public static LyricsAlignment None(string message = "未进行歌词时间校正") =>
        new(false, 0, 0, 0, 0, 0, message);
}

public sealed class LyricsTimeline
{
    private const uint LcMapSimplifiedChinese = 0x02000000;
    private static readonly IntPtr NullPointer = IntPtr.Zero;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string localeName, uint mapFlags, string source, int sourceLength,
        StringBuilder destination, int destinationCapacity, IntPtr version,
        IntPtr reserved, IntPtr sortHandle);

    private readonly string?[] cues;
    private readonly IReadOnlyList<LyricsCue> timedCues;
    private readonly IReadOnlyList<LyricsCue> displayCues;
    private LyricHighlight?[] highlights = Array.Empty<LyricHighlight?>();

    private LyricsTimeline(string?[] cues, int tokenCount, int mappedCount,
        IReadOnlyList<LyricsCue>? timedCues = null, LyricsAlignment? alignment = null,
        IReadOnlyList<LyricsCue>? displayCues = null)
    {
        this.cues = cues;
        TokenCount = tokenCount;
        MappedCount = mappedCount;
        this.timedCues = timedCues ?? Array.Empty<LyricsCue>();
        this.displayCues = displayCues ?? this.timedCues;
        Alignment = alignment ?? LyricsAlignment.None();
    }

    public int TokenCount { get; }
    public int MappedCount { get; }
    public int ExtraCount => Math.Max(0, TokenCount - MappedCount);
    public bool IsTimed => timedCues.Count > 0;
    public bool HasLyrics => MappedCount > 0;
    public IReadOnlyList<LyricsCue> TimedCues => timedCues;
    public LyricsAlignment Alignment { get; }
    public IReadOnlyList<LyricsCue> DisplayCues => displayCues;

    public string? ForNote(int noteIndex) =>
        noteIndex >= 0 && noteIndex < cues.Length ? cues[noteIndex] : null;
    public LyricHighlight? HighlightForNote(int noteIndex) =>
        noteIndex >= 0 && noteIndex < highlights.Length ? highlights[noteIndex] : null;

    public string? NextAfterNote(int noteIndex)
    {
        for (int index = Math.Max(0, noteIndex + 1); index < cues.Length; index++)
            if (!string.IsNullOrWhiteSpace(cues[index])) return cues[index];
        return null;
    }

    public string? TextAt(double elapsedMilliseconds)
    {
        if (elapsedMilliseconds < 0 || displayCues.Count == 0) return null;
        int low = 0;
        int high = displayCues.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (displayCues[middle].StartMilliseconds <= elapsedMilliseconds) low = middle + 1;
            else high = middle;
        }
        if (low == 0) return null;
        string text = displayCues[low - 1].Text;
        return text.Length == 0 ? null : text;
    }

    public static LyricsTimeline Create(
        string text,
        IReadOnlyList<ScoreNote> notes,
        double beatMs = 0,
        int spaceMs = 0,
        int lineMs = 0,
        string? timingText = null,
        string? bindingJson = null)
    {
        if (!string.IsNullOrWhiteSpace(bindingJson))
        {
            var boundCues = new string?[notes.Count];
            try
            {
                var binding = ScoreLyrics.Parse(bindingJson, notes);
                var display = new List<LyricsCue>();
                double[] starts = new double[notes.Count + 1];
                double elapsed = 0;
                for (int i = 0; i < notes.Count; i++)
                {
                    elapsed += notes[i].PauseBefore(spaceMs, lineMs);
                    starts[i] = elapsed;
                    elapsed += notes[i].Beats * beatMs;
                }
                starts[notes.Count] = elapsed;
                foreach (var cue in binding.Cues)
                {
                    if (cue.EndNote < notes.Count) boundCues[cue.EndNote] = "";
                    string normalized = NormalizeText(cue.Text);
                    boundCues[cue.StartNote] = normalized;
                    display.Add(new LyricsCue(starts[cue.StartNote], normalized));
                    display.Add(new LyricsCue(starts[cue.EndNote], ""));
                }
                var ordered = display.OrderBy(c => c.StartMilliseconds).GroupBy(c => c.StartMilliseconds)
                    .Select(g => g.Last()).ToArray();
                var timeline = new LyricsTimeline(boundCues, binding.Cues.Count, binding.Cues.Count, ordered,
                    LyricsAlignment.None(binding.Syllables.Count > 0
                        ? $"已保存 {binding.Syllables.Count} 个字／音节的音符区间，待核对 {binding.Syllables.Count(s => s.NeedsReview)} 项，随 BPM 变速"
                        : $"已按音符绑定 {binding.Cues.Count} 句歌词，随 BPM 变速"), ordered);
                timeline.highlights = new LyricHighlight?[notes.Count];
                foreach (var phrase in binding.Cues)
                {
                    string phraseText = NormalizeText(phrase.Text);
                    int offset = 0;
                    foreach (var word in binding.Syllables.Where(w => w.StartNote >= phrase.StartNote && w.EndNote <= phrase.EndNote))
                    {
                        string wordText = NormalizeText(word.Text);
                        int position = phraseText.IndexOf(wordText, offset, StringComparison.Ordinal);
                        if (position < 0) continue;
                        offset = position + wordText.Length;
                        for (int n = word.StartNote; n < word.EndNote; n++)
                            timeline.highlights[n] = new LyricHighlight(position, wordText.Length);
                    }
                }
                return timeline;
            }
            catch (FormatException error)
            {
                return new LyricsTimeline(boundCues, Tokenize(text).Count, 0,
                    alignment: LyricsAlignment.None(error.Message));
            }
        }
        string source = string.IsNullOrWhiteSpace(timingText) ? text : timingText;
        var timed = ParseTimedCues(source);
        var cues = new string?[notes.Count];
        if (timed.Count == 0)
            return new LyricsTimeline(cues, Tokenize(text).Count, 0);

        if (beatMs > 0)
        {
            double[] noteStarts = new double[notes.Count];
            double elapsed = 0;
            for (int i = 0; i < notes.Count; i++)
            {
                elapsed += notes[i].PauseBefore(spaceMs, lineMs);
                noteStarts[i] = elapsed;
                elapsed += notes[i].Beats * beatMs;
            }

            int mapped = 0;
            var playable = Enumerable.Range(0, notes.Count).Where(i => notes[i].Degree != 0).ToArray();
            foreach (var cue in timed)
            {
                if (playable.Length == 0 || cue.StartMilliseconds > elapsed) continue;
                int index = FindNearestPlayable(playable, noteStarts, cue.StartMilliseconds);
                if (cues[index] == null) mapped++;
                cues[index] = cue.Text;
            }
            return new LyricsTimeline(cues, timed.Count, mapped, timed,
                LyricsAlignment.None("使用原录音 LRC 时间；尚未确认与当前曲谱对应，可点击“校准歌词”"), timed);
        }
        return new LyricsTimeline(cues, timed.Count, timed.Count, timed,
            LyricsAlignment.None("缺少简谱速度，未进行歌词时间校正"), timed);
    }

    // Lyrics are an optional display layer.  A score (especially instrumental
    // music) must remain playable when no timestamped lyric file is available.
    public static LyricsTimeline? CreateOptional(
        string text,
        IReadOnlyList<ScoreNote> notes,
        double beatMs = 0,
        int spaceMs = 0,
        int lineMs = 0,
        string? timingText = null,
        string? bindingJson = null)
    {
        LyricsTimeline timeline = Create(text, notes, beatMs, spaceMs, lineMs, timingText, bindingJson);
        return timeline.HasLyrics ? timeline : null;
    }

    public static bool HasTiming(string text) => ParseTimedCues(text).Count > 0;

    private static int FindNearestPlayable(int[] playable, IReadOnlyList<double> noteStarts, double targetMilliseconds) =>
        playable[FindNearestPlayablePosition(playable, noteStarts, targetMilliseconds, 0, playable.Length)];

    private static int FindNearestPlayablePosition(
        int[] playable,
        IReadOnlyList<double> noteStarts,
        double targetMilliseconds,
        int minimumPosition,
        int maximumExclusive)
    {
        minimumPosition = Math.Clamp(minimumPosition, 0, playable.Length);
        maximumExclusive = Math.Clamp(maximumExclusive, minimumPosition, playable.Length);
        if (minimumPosition >= maximumExclusive) return -1;

        int low = minimumPosition;
        int high = maximumExclusive;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (noteStarts[playable[middle]] < targetMilliseconds) low = middle + 1;
            else high = middle;
        }

        int right = Math.Min(maximumExclusive - 1, low);
        int left = Math.Max(minimumPosition, right - 1);
        return Math.Abs(noteStarts[playable[left]] - targetMilliseconds) <=
               Math.Abs(noteStarts[playable[right]] - targetMilliseconds) ? left : right;
    }

    public static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var lines = new List<string>();
        foreach (string physicalLine in NormalizeText(text).Split(
            new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string line in physicalLine.Split(
                new[] { '|', '｜' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.Length > 0) lines.Add(line);
                if (lines.Count >= 20000) break;
            }
            if (lines.Count >= 20000) break;
        }
        return lines.ToArray();
    }

    private static List<LyricsCue> ParseTimedCues(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<LyricsCue>();
        const string timestampPattern = @"\[(?<minutes>\d{1,3}):(?<seconds>\d{2})(?:[\.:](?<fraction>\d{1,3}))?\]";
        var timestampRegex = new Regex(timestampPattern, RegexOptions.Compiled);
        var result = new List<LyricsCue>();
        foreach (string sourceLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            MatchCollection matches = timestampRegex.Matches(sourceLine);
            if (matches.Count == 0) continue;
            string line = timestampRegex.Replace(sourceLine, "").Trim();
            line = RemoveLeadingNumberPrefix(line).Trim();
            if (line.Length == 0) continue;
            line = ConvertTraditionalChinese(line);
            foreach (Match match in matches)
            {
                int minutes = int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture);
                int seconds = int.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture);
                string fractionText = match.Groups["fraction"].Value;
                double fraction = fractionText.Length switch
                {
                    1 => int.Parse(fractionText, CultureInfo.InvariantCulture) / 10d,
                    2 => int.Parse(fractionText, CultureInfo.InvariantCulture) / 100d,
                    3 => int.Parse(fractionText, CultureInfo.InvariantCulture) / 1000d,
                    _ => 0d
                };
                result.Add(new LyricsCue((minutes * 60 + seconds + fraction) * 1000d, line));
            }
        }
        return result.OrderBy(cue => cue.StartMilliseconds).ToList();
    }

    public static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return ConvertTraditionalChinese(RemoveLrcTags(text));
    }

    private static string RemoveLrcTags(string text)
    {
        var lines = new List<string>();
        foreach (string sourceLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = sourceLine.Trim();
            if (line.Length > 0 && line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close >= 0 && IsLrcMetadataTag(line[1..close])) continue;
            }
            line = RemoveTimestampTags(line).Trim();
            line = RemoveLeadingNumberPrefix(line).Trim();
            if (line.Length > 0) lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    private static string RemoveLeadingNumberPrefix(string line)
    {
        line = Regex.Replace(line, @"^\s*\d{1,3}:\d{2}(?:[.:]\d{1,3})?\s*(?:[-—:：、.]\s*)?", "");
        return Regex.Replace(line, @"^\s*\d{1,3}\s*[.)、:：-]\s*", "");
    }

    private static string RemoveTimestampTags(string line)
    {
        var cleaned = new StringBuilder(line.Length);
        int cursor = 0;
        while (cursor < line.Length)
        {
            int open = line.IndexOf('[', cursor);
            if (open < 0) { cleaned.Append(line[cursor..]); break; }
            cleaned.Append(line[cursor..open]);
            int close = line.IndexOf(']', open + 1);
            if (close < 0) { cleaned.Append(line[open..]); break; }
            string tag = line[(open + 1)..close];
            if (!IsTimestampTag(tag)) cleaned.Append(line[open..(close + 1)]);
            cursor = close + 1;
        }
        return cleaned.ToString();
    }

    private static bool IsTimestampTag(string tag)
    {
        int colon = tag.IndexOf(':');
        if (colon <= 0 || colon == tag.Length - 1) return false;
        return tag[..colon].All(char.IsDigit) &&
               tag[(colon + 1)..].All(c => char.IsDigit(c) || c == '.');
    }

    private static bool IsLrcMetadataTag(string tag) =>
        tag.Contains(':') && tag.Length > 1 && char.IsLetter(tag[0]);

    private static string ConvertTraditionalChinese(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (string line in text.Split('\n'))
        {
            if (output.Length > 0) output.Append('\n');
            if (ContainsNonChineseEastAsianText(line))
            {
                output.Append(line);
                continue;
            }

            int cursor = 0;
            while (cursor < line.Length)
            {
                if (!IsHanCharacter(line[cursor]))
                {
                    output.Append(line[cursor++]);
                    continue;
                }

                int start = cursor++;
                while (cursor < line.Length && IsHanCharacter(line[cursor])) cursor++;
                string hanRun = line[start..cursor];
                output.Append(ConvertHanRunToSimplified(hanRun));
            }
        }
        return output.ToString();
    }

    private static string ConvertHanRunToSimplified(string hanRun)
    {
        if (!OperatingSystem.IsWindows()) return hanRun;
        var destination = new StringBuilder(hanRun.Length * 2);
        int length = LCMapStringEx("zh-CN", LcMapSimplifiedChinese, hanRun, hanRun.Length,
            destination, destination.Capacity, NullPointer, NullPointer, NullPointer);
        return length > 0 ? destination.ToString(0, Math.Min(length, destination.Length)) : hanRun;
    }

    private static bool ContainsNonChineseEastAsianText(string line) =>
        line.Any(c => (c >= '\u3040' && c <= '\u30ff') ||
                      (c >= '\u31f0' && c <= '\u31ff') ||
                      (c >= '\uac00' && c <= '\ud7af') ||
                      (c >= '\uff65' && c <= '\uff9f'));

    private static bool IsHanCharacter(char c) =>
        (c >= '\u3400' && c <= '\u4dbf') ||
        (c >= '\u4e00' && c <= '\u9fff') ||
        (c >= '\uf900' && c <= '\ufaff');
}
