namespace HarmonicaPlayer;

public sealed record PracticeModeState(
    string ScoreText,
    string SongTitle,
    int Bpm,
    AudioInstrument Instrument = AudioInstrument.Harmonica,
    string TimingJson = "");

public enum PracticePerformanceMode
{
    Simple,
    Normal,
    Rhythm
}

public sealed record PracticePerformanceModeOption(
    PracticePerformanceMode Value,
    string Name,
    string Description)
{
    public override string ToString() => Name;
}

public static class PracticePerformanceModes
{
    public static readonly IReadOnlyList<PracticePerformanceModeOption> Options = Array.AsReadOnly(new[]
    {
        new PracticePerformanceModeOption(
            PracticePerformanceMode.Simple,
            "简单练习",
            "只按 Z/X/C/V/B/N/M；允许提前连续输入，正确音符按谱面时值排队播放，按错不发声。"),
        new PracticePerformanceModeOption(
            PracticePerformanceMode.Rhythm,
            "节奏练习",
            "只按 Z/X/C/V/B/N/M；前音结束后再按下一音，不接受提前输入；自动处理高低音和升号。"),
        new PracticePerformanceModeOption(
            PracticePerformanceMode.Normal,
            "正常演奏模式",
            "按下音符键开始发声，松开音符键停止发声；音符顺序仍按键音判定。")
    });

    public const PracticePerformanceMode Default = PracticePerformanceMode.Simple;

    public static PracticePerformanceModeOption OptionOf(PracticePerformanceMode mode) =>
        Options.First(option => option.Value == mode);
}

public static class PracticeTiming
{
    public const int MinDurationMilliseconds = 80;
    public const int MaxDurationMilliseconds = 8000;

    public static int NoteDurationMilliseconds(ScoreNote note, int bpm)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (bpm is < 20 or > 300) throw new ArgumentOutOfRangeException(nameof(bpm));
        if (!double.IsFinite(note.Beats) || note.Beats <= 0) throw new ArgumentOutOfRangeException(nameof(note));
        return Math.Clamp(
            (int)Math.Round(60000d / bpm * note.Beats, MidpointRounding.AwayFromZero),
            MinDurationMilliseconds,
            MaxDurationMilliseconds);
    }
}

public sealed record PracticeScoreLine(
    int Number,
    int StartNoteIndex,
    int EndNoteIndex,
    IReadOnlyList<ScoreNote> Notes,
    bool MeasureBreakAfter = false)
{
    public string Text => ScoreTextFormatter.Format(Notes) + (MeasureBreakAfter ? "  |" : "");

    public bool IsCurrentNote(int localNoteIndex, int currentNoteIndex) =>
        localNoteIndex >= 0 && localNoteIndex < Notes.Count && StartNoteIndex + localNoteIndex == currentNoteIndex;
}

public sealed class PracticeScoreDocument
{
    public const int MaxNotesPerLine = 16;

    private PracticeScoreDocument(IReadOnlyList<ScoreNote> notes, IReadOnlyList<PracticeScoreLine> lines)
    {
        Notes = notes;
        Lines = lines;
    }

    public IReadOnlyList<ScoreNote> Notes { get; }
    public IReadOnlyList<PracticeScoreLine> Lines { get; }

    public static PracticeScoreDocument Create(string text, string timingJson = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        List<ScoreNote> notes;
        try { notes = timingJson.Length > 0 ? ScoreTiming.Apply(text, timingJson) : ScoreParser.Parse(text, rhythm: true); }
        catch (FormatException) { notes = ScoreParser.Parse(text, rhythm: true); }
        var measureBreaks = FindMeasureBreaks(text, notes);
        var sourceBoundaries = new bool[notes.Count];
        for (int index = 1; index < notes.Count; index++)
            sourceBoundaries[index] = notes[index].SeparatorBefore == ScoreSeparator.LineBreak || measureBreaks[index];

        var lines = new List<PracticeScoreLine>();
        int start = 0;
        for (int index = 1; index <= notes.Count; index++)
        {
            bool sourceBreak = index < notes.Count && sourceBoundaries[index];
            bool full = index - start >= MaxNotesPerLine;
            if (index != notes.Count && !sourceBreak && !full) continue;

            int end = index;
            var lineNotes = notes.GetRange(start, end - start).ToArray();
            lines.Add(new PracticeScoreLine(
                lines.Count + 1,
                start,
                end,
                lineNotes,
                end < measureBreaks.Length && measureBreaks[end]));
            start = end;
        }

        return new PracticeScoreDocument(notes.ToArray(), lines.ToArray());
    }

    public int LineIndexForNote(int noteIndex)
    {
        if (Lines.Count == 0) return 0;
        if (noteIndex >= Notes.Count) return Lines.Count - 1;
        if (noteIndex < 0) return 0;
        for (int index = 0; index < Lines.Count; index++)
        {
            if (noteIndex >= Lines[index].StartNoteIndex && noteIndex < Lines[index].EndNoteIndex)
                return index;
        }
        return Lines.Count - 1;
    }

    private static bool[] FindMeasureBreaks(string text, IReadOnlyList<ScoreNote> notes)
    {
        var result = new bool[notes.Count + 1];
        int nextNote = 0;
        for (int position = 0; position < text.Length; position++)
        {
            if (text[position] is not ('|' or '｜')) continue;
            while (nextNote < notes.Count && notes[nextNote].Position <= position) nextNote++;
            if (nextNote > 0 && nextNote <= notes.Count) result[nextNote] = true;
        }
        return result;
    }
}

public static class PracticeInputMapper
{
    public static bool TryMapSimpleVirtualKey(int virtualKey, out int degree)
    {
        degree = virtualKey switch
        {
            0x5A => 1, // Z
            0x58 => 2, // X
            0x43 => 3, // C
            0x56 => 4, // V
            0x42 => 5, // B
            0x4E => 6, // N
            0x4D => 7, // M
            _ => 0
        };
        return degree != 0;
    }

    public static bool TryMapVirtualKey(int virtualKey, bool low, bool high, bool sharp, out ScoreNote note)
    {
        note = null!;
        if (low && high) return false;

        int degree = virtualKey switch
        {
            0x5A => 1, // Z
            0x58 => 2, // X
            0x43 => 3, // C
            0x56 => 4, // V
            0x42 => 5, // B
            0x4E => 6, // N
            0x4D => 7, // M
            0xBC when !low => 1, // comma, high 1
            _ => 0
        };
        if (degree == 0) return false;

        int octave = virtualKey == 0xBC ? 1 : low ? -1 : high ? 1 : 0;
        note = new ScoreNote(degree, octave, sharp, 0);
        return true;
    }

    public static bool SamePitch(ScoreNote expected, ScoreNote actual) =>
        expected.Degree == actual.Degree && expected.Octave == actual.Octave && expected.Sharp == actual.Sharp;

    public static string DisplayDegreeKey(int degree) => degree is >= 1 and <= 7
        ? new[] { "Z", "X", "C", "V", "B", "N", "M" }[degree - 1]
        : throw new ArgumentOutOfRangeException(nameof(degree));

    public static string DisplayKey(ScoreNote note)
    {
        if (note.Octave == 1 && note.Degree == 1)
            return note.Sharp ? "Alt + ," : ",";
        string baseKey = new[] { "Z", "X", "C", "V", "B", "N", "M" }[note.Degree - 1];
        var modifiers = new List<string>();
        if (note.Octave == -1) modifiers.Add("Ctrl");
        if (note.Octave == 1) modifiers.Add("Shift");
        if (note.Sharp) modifiers.Add("Alt");
        modifiers.Add(baseKey);
        return string.Join(" + ", modifiers);
    }
}

public enum PracticeAdvanceResult
{
    Wrong,
    Correct,
    Complete
}

public sealed class PracticeProgress
{
    public PracticeProgress(PracticeScoreDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
    }

    public PracticeScoreDocument Document { get; }
    public int CurrentNoteIndex { get; private set; }
    public int CorrectCount { get; private set; }
    public int WrongCount { get; private set; }
    public bool IsComplete => CurrentNoteIndex >= Document.Notes.Count;
    public ScoreNote? Expected => IsComplete ? null : Document.Notes[CurrentNoteIndex];
    public int CurrentLineIndex => Document.LineIndexForNote(CurrentNoteIndex);

    public PracticeAdvanceResult Submit(ScoreNote actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (IsComplete) return PracticeAdvanceResult.Complete;
        if (Expected!.Degree == 0 || !PracticeInputMapper.SamePitch(Expected, actual))
        {
            WrongCount++;
            return PracticeAdvanceResult.Wrong;
        }

        CurrentNoteIndex++;
        CorrectCount++;
        return IsComplete ? PracticeAdvanceResult.Complete : PracticeAdvanceResult.Correct;
    }

    public PracticeAdvanceResult SubmitDegree(int degree)
    {
        if (degree is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(degree));
        if (IsComplete) return PracticeAdvanceResult.Complete;
        if (Expected!.Degree == 0 || Expected.Degree != degree)
        {
            WrongCount++;
            return PracticeAdvanceResult.Wrong;
        }

        CurrentNoteIndex++;
        CorrectCount++;
        return IsComplete ? PracticeAdvanceResult.Complete : PracticeAdvanceResult.Correct;
    }

    public int SkipRest()
    {
        int skipped = 0;
        while (!IsComplete && Expected!.Degree == 0)
        {
            CurrentNoteIndex++;
            skipped++;
        }
        return skipped;
    }

    public bool SkipNextRest()
    {
        if (IsComplete || Expected!.Degree != 0) return false;
        CurrentNoteIndex++;
        return true;
    }

    public void Reset()
    {
        CurrentNoteIndex = 0;
        CorrectCount = 0;
        WrongCount = 0;
    }
}
