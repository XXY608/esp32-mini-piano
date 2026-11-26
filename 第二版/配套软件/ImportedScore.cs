using System.Globalization;
using System.Text;

namespace HarmonicaPlayer;

public sealed record ImportedScore(string Title, int? Bpm, string Text,
    IReadOnlyList<ScoreNote> Notes, IReadOnlyList<string> Warnings)
{
    public ScoreLyrics? Lyrics { get; init; }
    public IReadOnlyList<TimedScoreEvent>? Performance { get; init; }
    public IReadOnlyList<(double Beat, bool Down)>? SustainPedal { get; init; }
    // Bpm is the value declared by the source. Keep it unchanged so generated
    // filenames and diagnostics never mistake a clamped playback value for
    // the source tempo.
    public int? PlaybackBpm => Bpm is int value ? Math.Clamp(value, 20, 300) : null;
}

public sealed record TimedScoreEvent(double StartBeats, double DurationBeats,
    int MidiPitch = 0, bool IsRest = false, int Order = 0)
{
    public NoteLyric? Lyric { get; init; }
    public int Velocity { get; init; } = 100;
}

public static class ImportedScoreFactory
{
    private const double BeatTolerance = 0.000001;
    private const double MinExactBeats = 1d / 64;

    internal static Func<double, double> PlaybackClock(IEnumerable<(double Position, double Microseconds)> changes,
        double unitsPerBeat, int? bpm)
    {
        double scale = Math.Clamp(bpm ?? 120, 20, 300) / (60000000.0 * unitsPerBeat);
        var map = new List<(double Position, double Elapsed, double Microseconds)> { (0, 0, 500000) };
        foreach (var change in changes.OrderBy(t => t.Position))
        {
            var previous = map[^1];
            var point = (change.Position, previous.Elapsed + (change.Position - previous.Position) * previous.Microseconds,
                change.Microseconds);
            if (previous.Position == change.Position) map[^1] = point;
            else map.Add(point);
        }
        return position =>
        {
            int lo = 0, hi = map.Count;
            while (lo + 1 < hi)
            {
                int middle = lo + (hi - lo) / 2;
                if (map[middle].Position <= position) lo = middle;
                else hi = middle;
            }
            var tempo = map[lo];
            return (tempo.Elapsed + (position - tempo.Position) * tempo.Microseconds) * scale;
        };
    }

    public static ImportedScore Create(string title, int? bpm, IEnumerable<TimedScoreEvent> source,
        IEnumerable<string>? additionalWarnings = null, bool preserveTiming = false,
        IReadOnlyList<TimedScoreEvent>? performance = null)
        => Create(title, bpm, source, additionalWarnings, preserveTiming, performance,
            filterShortOverlaps: true);

    internal static ImportedScore Create(string title, int? bpm, IEnumerable<TimedScoreEvent> source,
        IEnumerable<string>? additionalWarnings, bool preserveTiming,
        IReadOnlyList<TimedScoreEvent>? performance, bool filterShortOverlaps,
        double connectGapBeats = 0, bool centerMelody = false, Func<double, double>? playbackClock = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var warnings = new HashSet<string>(additionalWarnings ?? Array.Empty<string>(), StringComparer.Ordinal);
        var events = source.Where(e => e.DurationBeats > 0)
            .OrderBy(e => e.StartBeats)
            .ThenBy(e => e.Order);
        var melody = SelectMelodyLine(events, filterShortOverlaps, connectGapBeats, warnings);
        int octaveShift = centerMelody ? SelectOctaveShift(melody) : 0;
        if (octaveShift != 0)
            warnings.Add("已根据主旋律音区整体调整八度。");
        var timedMelody = playbackClock == null ? melody : melody.Select(e =>
        {
            double start = playbackClock(e.StartBeats);
            return e with { StartBeats = start,
                DurationBeats = playbackClock(e.StartBeats + e.DurationBeats) - start };
        }).ToList();
        if (timedMelody.Count == 0) throw new FormatException("导入文件中没有可演奏的音符。");

        if (preserveTiming && timedMelody.Any(e => Math.Abs(e.DurationBeats * 4 - Math.Round(e.DurationBeats * 4)) > BeatTolerance))
            warnings.Add("精确时值随曲谱另存；仅使用 TXT 时，节奏为近似值。");
        var notes = new List<ScoreNote>();
        var syllables = new List<(int Note, NoteLyric Lyric)>();
        double cursor = 0;
        int order = 0;

        foreach (var eventToPlay in timedMelody)
        {
            double start = eventToPlay.StartBeats;
            if (start > cursor + BeatTolerance)
                AddNote(notes, new ScoreNote(0, 0, false, order++), start - cursor, warnings, preserveTiming);
            double duration = Math.Max(preserveTiming ? MinExactBeats : 0.25, eventToPlay.DurationBeats);
            if (!preserveTiming && eventToPlay.DurationBeats < 0.25)
                warnings.Add("发现短于四分之一拍的音符，已量化为四分之一拍。");

            if (eventToPlay.IsRest)
            {
                AddNote(notes, new ScoreNote(0, 0, false, order++), duration, warnings, preserveTiming);
            }
            else
            {
                int firstNote = notes.Count;
                var mapped = ImportedPitch.ToScoreNote(eventToPlay.MidiPitch + octaveShift, order++, warnings);
                AddNote(notes, mapped, duration, warnings, preserveTiming);
                if (eventToPlay.Lyric is { } lyric)
                    for (int index = firstNote; index < notes.Count; index++)
                    {
                        syllables.Add((index, lyric));
                        if (!lyric.ExplicitSpan) break;
                    }
            }
            cursor = start + eventToPlay.DurationBeats;
        }

        if (notes.Count == 0) throw new FormatException("导入文件中没有可演奏的音符。");
        if (notes.Count > 20000) throw new FormatException("导入后的音符超过 20000 个，无法安全播放。");

        int? playbackBpm = bpm is null ? null : Math.Clamp(bpm.Value, 20, 300);
        if (bpm is < 20 or > 300)
            warnings.Add($"源谱 BPM 为 {bpm}，播放器播放时使用 {playbackBpm}（支持范围 20～300）。");

        return new ImportedScore(
            string.IsNullOrWhiteSpace(title) ? "未命名曲谱" : title.Trim(),
            bpm,
            ScoreTextFormatter.Format(notes),
            notes,
            warnings.ToArray()) { Lyrics = ScoreLyrics.FromSyllables(notes, syllables), Performance = performance };
    }

    private static List<TimedScoreEvent> SelectMelodyLine(IEnumerable<TimedScoreEvent> sortedEvents,
        bool filterShortOverlaps, double connectGapBeats, HashSet<string> warnings)
    {
        var melody = new List<TimedScoreEvent>();
        TimedScoreEvent? atOnset = null;
        foreach (var candidate in sortedEvents)
        {
            if (atOnset != null && candidate.StartBeats - atOnset.StartBeats <= BeatTolerance)
            {
                if (!candidate.IsRest && !atOnset.IsRest && candidate.MidiPitch != atOnset.MidiPitch)
                    warnings.Add("单音预览的和弦只显示带词音符或最高音。");
                if (CompareAtOnset(candidate, atOnset) > 0)
                    atOnset = candidate with { StartBeats = atOnset.StartBeats };
                continue;
            }
            if (atOnset != null)
                AddMelodyEvent(atOnset);
            atOnset = candidate;
        }
        if (atOnset != null)
            AddMelodyEvent(atOnset);
        return melody;

        void AddMelodyEvent(TimedScoreEvent next)
        {
            if (melody.Count > 0)
            {
                var held = melody[^1];
                double toNext = next.StartBeats - held.StartBeats;
                double gap = toNext - held.DurationBeats;
                if (gap < -BeatTolerance)
                {
                    if (next.IsRest && !held.IsRest)
                    {
                        double restEnd = next.StartBeats + next.DurationBeats;
                        double heldEnd = held.StartBeats + held.DurationBeats;
                        if (restEnd > heldEnd + BeatTolerance)
                            melody.Add(next with { StartBeats = heldEnd, DurationBeats = restEnd - heldEnd });
                        return;
                    }
                    int pitchDrop = held.MidiPitch - next.MidiPitch;
                    // An isolated short attack fully inside a sustained upper note is
                    // evidence of accompaniment only when the source voice is ambiguous.
                    if (filterShortOverlaps && !held.IsRest && !next.IsRest && next.Lyric == null &&
                        held.DurationBeats >= 0.5 && next.DurationBeats <= 0.25 + BeatTolerance &&
                        gap + next.DurationBeats <= -1d / 32 && (pitchDrop == 0 || pitchDrop >= 3))
                    {
                        warnings.Add("已略过持续旋律音内重叠的同音或下方短音。");
                        return;
                    }
                    melody[^1] = held with { DurationBeats = toNext };
                }
                else if (gap > BeatTolerance && gap <= connectGapBeats + BeatTolerance &&
                         !held.IsRest && !next.IsRest)
                {
                    melody[^1] = held with { DurationBeats = toNext };
                }
            }
            melody.Add(next);
        }
    }

    private static int CompareAtOnset(TimedScoreEvent left, TimedScoreEvent right)
    {
        if (left.IsRest != right.IsRest) return right.IsRest.CompareTo(left.IsRest);
        if ((left.Lyric != null) != (right.Lyric != null)) return (left.Lyric != null).CompareTo(right.Lyric != null);
        int pitch = left.MidiPitch.CompareTo(right.MidiPitch);
        return pitch != 0 ? pitch : left.DurationBeats.CompareTo(right.DurationBeats);
    }

    private static int SelectOctaveShift(IEnumerable<TimedScoreEvent> melody)
    {
        int[] pitches = melody.Where(e => !e.IsRest).Select(e => e.MidiPitch).Order().ToArray();
        if (pitches.Length == 0) return 0;
        double median = (pitches[(pitches.Length - 1) / 2] + pitches[pitches.Length / 2]) / 2d;
        int shift = 0;
        while (median + shift > 71) shift -= 12;
        while (median + shift < 60) shift += 12;
        return shift;
    }

    private static void AddNote(List<ScoreNote> notes, ScoreNote note, double beats, HashSet<string> warnings, bool preserveTiming)
    {
        if (preserveTiming)
        {
            double remainingBeats = Math.Max(MinExactBeats, beats);
            while (remainingBeats > 64)
            {
                notes.Add(note with { Beats = 64, Position = notes.Count });
                remainingBeats -= 64;
            }
            if (remainingBeats > 0)
                notes.Add(note with { Beats = Math.Max(MinExactBeats, remainingBeats), Position = notes.Count });
            return;
        }
        int units = Math.Max(1, (int)Math.Round(beats * 4, MidpointRounding.AwayFromZero));
        if (Math.Abs(beats - units / 4.0) > 0.000001)
            warnings.Add("部分音符时值无法用播放器支持的四分之一拍精确表示，已量化。");
        if (units > 64 * 4)
            warnings.Add("发现超过 64 拍的单个时值，已拆分为可播放时值。");

        int remaining = units;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, 64 * 4);
            notes.Add(note with { Beats = chunk / 4.0, Position = notes.Count });
            remaining -= chunk;
        }
    }
}

public static class ScoreTextFormatter
{
    public static string Format(IReadOnlyList<ScoreNote> notes)
    {
        var builder = new StringBuilder();
        foreach (var note in notes)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(FormatNote(note));
        }
        return builder.ToString();
    }

    public static string FormatWithMeasures(IReadOnlyList<ScoreNote> notes, int beatsPerMeasure)
    {
        if (beatsPerMeasure <= 0) return Format(notes);
        var builder = new StringBuilder();
        double beatInMeasure = 0;
        for (int index = 0; index < notes.Count; index++)
        {
            var note = notes[index];
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(FormatNote(note));
            beatInMeasure += note.Beats;
            if (beatInMeasure >= beatsPerMeasure - 0.000001)
            {
                builder.Append(" |");
                if (index + 1 < notes.Count) builder.AppendLine();
                beatInMeasure %= beatsPerMeasure;
            }
        }
        return builder.ToString();
    }

    public static string FormatNote(ScoreNote note)
    {
        string prefix = note.Octave switch { 1 => "\u3010", -1 => "\uff08", _ => "" };
        string suffix = note.Octave switch { 1 => "\u3011", -1 => "\uff09", _ => "" };
        string pitch = note.Degree == 0 ? "0" : (note.Sharp ? "#" : "") + note.Degree.ToString(CultureInfo.InvariantCulture);
        int units = Math.Max(1, (int)Math.Round(note.Beats * 4, MidpointRounding.AwayFromZero));
        int full = units / 4;
        int remainder = units % 4;
        string reduction = remainder switch { 1 => "__", 2 => "_", 3 => "_.", _ => "" };
        int extensions = remainder == 0 ? Math.Max(0, full - 1) : full;
        return prefix + pitch + reduction + string.Concat(Enumerable.Repeat(" —", extensions)) + suffix;
    }
}

internal static class ImportedPitch
{
    public static ScoreNote ToScoreNote(int midiPitch, int position, HashSet<string> warnings)
    {
        int original = midiPitch;
        int octave = (int)Math.Floor((midiPitch - 60) / 12.0);
        while (octave < -1) { midiPitch += 12; octave++; }
        while (octave > 1) { midiPitch -= 12; octave--; }
        if (midiPitch != original)
            warnings.Add("部分音符超出电子琴可播放音域，已自动折返八度。");

        int pitchClass = ((midiPitch % 12) + 12) % 12;
        (int degree, bool sharp) = pitchClass switch
        {
            0 => (1, false), 1 => (1, true),
            2 => (2, false), 3 => (2, true),
            4 => (3, false),
            5 => (4, false), 6 => (4, true),
            7 => (5, false), 8 => (5, true),
            9 => (6, false), 10 => (6, true),
            11 => (7, false),
            _ => throw new InvalidOperationException()
        };
        return new ScoreNote(degree, octave, sharp, position);
    }
}
