using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HarmonicaPlayer;

public sealed record NoteLyric(string Text, bool EndLine = false, bool JoinNext = false)
{
    public bool ExplicitSpan { get; init; }
    public int AlignmentIndex { get; init; } = -1;
    public bool NeedsReview { get; init; }
    public double? ReferenceStartSeconds { get; init; }
    public double? ReferenceEndSeconds { get; init; }
}

// Indices refer to the final monophonic score, including rests. EndNote is exclusive.
public sealed record BoundLyric(int StartNote, int EndNote, string Text)
{
    public bool EndLine { get; init; }
    public int AlignmentIndex { get; init; } = -1;
    public bool NeedsReview { get; init; }
    public double? ReferenceStartSeconds { get; init; }
    public double? ReferenceEndSeconds { get; init; }
    public string ReferenceScoreUrl { get; init; } = "";
    public string ReferenceScoreSha256 { get; init; } = "";
    public string AcousticDiagnostics { get; init; } = "";
    public string PositionEvidence { get; init; } = "";
}

public sealed record ScoreLyrics(string ScoreSignature, string Source, IReadOnlyList<BoundLyric> Cues)
{
    public const int MaxJsonLength = 1024 * 1024;
    public IReadOnlyList<BoundLyric> Syllables { get; init; } = Array.Empty<BoundLyric>();
    public string ReferenceAudioPath { get; init; } = "";
    public string ReferenceUrl { get; init; } = "";
    public string SourceScoreSha256 { get; init; } = "";

    public static string Signature(IReadOnlyList<ScoreNote> notes)
    {
        string canonical = string.Join(';', notes.Select(n =>
            $"{n.Degree},{n.Octave},{n.Sharp},{n.Beats.ToString("R", CultureInfo.InvariantCulture)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static ScoreLyrics Create(IReadOnlyList<ScoreNote> notes, IEnumerable<BoundLyric> cues, string source)
    {
        var result = new ScoreLyrics(Signature(notes), source, cues.ToArray());
        result.Validate(notes);
        return result;
    }

    public void Validate(IReadOnlyList<ScoreNote> notes)
    {
        if (ScoreSignature != Signature(notes))
            throw new FormatException("曲谱音符或时值已改变，请重新校准歌词对应关系。");
        if (Cues == null || Syllables == null || Cues.Count > 20000 || Syllables.Count > 20000 || Source == null || Source.Length > 1000)
            throw new FormatException("歌词绑定数据无效。");
        if (SourceScoreSha256 == null || SourceScoreSha256.Length is not (0 or 64) ||
            SourceScoreSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new FormatException("原始曲谱校验值无效。");
        ValidateRanges(Cues, notes);
        ValidateRanges(Syllables, notes);
        foreach (var word in Syllables)
            if (!Cues.Any(c => c.StartNote <= word.StartNote && c.EndNote >= word.EndNote))
                throw new FormatException("逐字绑定超出所属歌词句子的音符范围。");
    }

    private static void ValidateRanges(IReadOnlyList<BoundLyric> ranges, IReadOnlyList<ScoreNote> notes)
    {
        int end = 0;
        int textLength = 0;
        foreach (var cue in ranges)
        {
            if (cue == null || cue.StartNote < end || cue.EndNote <= cue.StartNote || cue.EndNote > notes.Count ||
                notes[cue.StartNote].Degree == 0 || string.IsNullOrWhiteSpace(cue.Text))
                throw new FormatException("歌词起止音符无效、重叠或落在休止符上，请检查对应关系。");
            textLength += cue.Text.Length;
            if (cue.ReferenceScoreUrl == null || cue.AcousticDiagnostics == null || cue.ReferenceScoreSha256 == null || cue.PositionEvidence == null ||
                (cue.ReferenceScoreUrl.Length == 0) != (cue.ReferenceScoreSha256.Length == 0) || cue.ReferenceScoreSha256.Length is not (0 or 64) ||
                cue.ReferenceScoreSha256.Any(c => !Uri.IsHexDigit(c)))
                throw new FormatException("参考带词谱校验值无效。");
            if (cue.ReferenceStartSeconds != null || cue.ReferenceEndSeconds != null)
                if (cue.ReferenceStartSeconds is not double startTime || cue.ReferenceEndSeconds is not double endTime ||
                    !double.IsFinite(startTime) || !double.IsFinite(endTime) || startTime < 0 || endTime <= startTime)
                    throw new FormatException("原唱参考时间区间无效。");
            end = cue.EndNote;
        }
        if (textLength > 200000) throw new FormatException("歌词文本不得超过 200000 个字符。");
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public static ScoreLyrics Parse(string json, IReadOnlyList<ScoreNote> notes)
    {
        if (json.Length > MaxJsonLength) throw new FormatException("歌词绑定文件不得超过 1MB。");
        try
        {
            var result = JsonSerializer.Deserialize<ScoreLyrics>(json)
                ?? throw new FormatException("歌词绑定文件为空。");
            result.Validate(notes);
            return result;
        }
        catch (JsonException error) { throw new FormatException("歌词绑定文件格式无效。", error); }
    }

    public static string SidecarPath(string scorePath) => Path.ChangeExtension(scorePath, ".lyrics.json");

    public void Save(string path, IReadOnlyList<ScoreNote> notes)
    {
        Validate(notes);
        string json = ToJson();
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonLength) throw new FormatException("歌词绑定文件不得超过 1MB。");
        // Replace only after a complete write so an interrupted save retains the previous binding.
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, json, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static ScoreLyrics? FromSyllables(IReadOnlyList<ScoreNote> notes,
        IReadOnlyList<(int Note, NoteLyric Lyric)> syllables)
    {
        if (syllables.Count == 0) return null;
        if (syllables.All(s => s.Lyric.ExplicitSpan && s.Lyric.AlignmentIndex >= 0))
        {
            var explicitWords = syllables.GroupBy(s => s.Lyric.AlignmentIndex).Select(group =>
            {
                var first = group.First();
                return new BoundLyric(first.Note, group.Last().Note + 1, first.Lyric.Text)
                { AlignmentIndex = first.Lyric.AlignmentIndex, EndLine = first.Lyric.EndLine, NeedsReview = first.Lyric.NeedsReview };
            }).ToArray();
            return FromWordBindings(notes, explicitWords, "人声逐字音符区间");
        }
        var cues = new List<BoundLyric>();
        var text = new StringBuilder();
        int start = 0, last = 0;
        bool join = true;
        void Flush(int end)
        {
            if (text.Length == 0) return;
            cues.Add(new BoundLyric(start, end, text.ToString().Trim()));
            text.Clear();
        }
        foreach (var (index, lyric) in syllables)
        {
            if (string.IsNullOrWhiteSpace(lyric.Text)) continue;
            // Phrase grouping affects display only. Every phrase still starts at its source note.
            bool restGap = text.Length > 0 && Enumerable.Range(last + 1, index - last - 1)
                .Any(i => notes[i].Degree == 0 && notes[i].Beats >= 1);
            if (restGap || text.Length >= 24) Flush(last + 1);
            if (text.Length == 0) start = index;
            if (text.Length > 0 && !join && char.IsAsciiLetter(text[^1]) && char.IsAsciiLetter(lyric.Text[0])) text.Append(' ');
            text.Append(lyric.Text);
            last = index;
            join = lyric.JoinNext;
            if (lyric.EndLine || "。！？!?；;".Contains(lyric.Text[^1])) Flush(index + 1);
        }
        Flush(last + 1);
        // Carry a phrase across its melisma, but clear it at a genuine instrumental rest.
        for (int i = 0; i < cues.Count; i++)
        {
            int end = i + 1 < cues.Count ? cues[i + 1].StartNote : notes.Count;
            for (int n = cues[i].EndNote; n < end; n++)
                if (notes[n].Degree == 0 && notes[n].Beats >= 1) { end = n; break; }
            cues[i] = cues[i] with { EndNote = Math.Max(cues[i].EndNote, end) };
        }
        var words = new List<BoundLyric>();
        for (int i = 0; i < syllables.Count; i++)
        {
            var (index, lyric) = syllables[i];
            var phrase = cues.First(c => c.StartNote <= index && c.EndNote > index);
            int end = i + 1 < syllables.Count ? Math.Min(syllables[i + 1].Note, phrase.EndNote) : phrase.EndNote;
            words.Add(new BoundLyric(index, end, lyric.Text.Trim())
            { EndLine = lyric.EndLine, AlignmentIndex = lyric.AlignmentIndex, NeedsReview = lyric.NeedsReview,
                ReferenceStartSeconds = lyric.ReferenceStartSeconds, ReferenceEndSeconds = lyric.ReferenceEndSeconds });
        }
        var result = new ScoreLyrics(Signature(notes), "源谱内嵌歌词", cues) { Syllables = words };
        result.Validate(notes);
        return result;
    }

    public static IReadOnlyList<BoundLyric> InvalidateChangedPositionAnchors(
        IReadOnlyList<BoundLyric> original, IReadOnlyList<BoundLyric> revised)
    {
        var before = original.ToLookup(w => w.AlignmentIndex);
        var after = revised.ToLookup(w => w.AlignmentIndex);
        bool SameAnchor(int index)
        {
            var left = before[index].ToArray();
            var right = after[index].ToArray();
            return left.Length == 1 && right.Length == 1 && left[0].StartNote == right[0].StartNote &&
                left[0].EndNote == right[0].EndNote && left[0].Text == right[0].Text &&
                left[0].ReferenceStartSeconds == right[0].ReferenceStartSeconds &&
                left[0].ReferenceEndSeconds == right[0].ReferenceEndSeconds;
        }
        return revised.Select(word =>
        {
            if (word.PositionEvidence.Length == 0) return word;
            bool valid = false;
            try
            {
                using var evidence = JsonDocument.Parse(word.PositionEvidence);
                valid = evidence.RootElement.ValueKind == JsonValueKind.Object &&
                    evidence.RootElement.TryGetProperty("anchorIndices", out var anchors) &&
                    anchors.ValueKind == JsonValueKind.Array && (anchors.GetArrayLength() is 4 or 6) &&
                    anchors.EnumerateArray().All(a => a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out int index) && SameAnchor(index));
            }
            catch (JsonException) { }
            return valid ? word : word with { PositionEvidence = "",
                NeedsReview = word.NeedsReview || word.ReferenceScoreUrl.Length == 0 };
        }).ToArray();
    }

    public static ScoreLyrics FromWordBindings(IReadOnlyList<ScoreNote> notes, IReadOnlyList<BoundLyric> words, string source)
    {
        ValidateRanges(words, notes);
        var phrases = new List<BoundLyric>();
        var text = new StringBuilder();
        int start = 0, end = 0;
        void Flush()
        {
            if (text.Length == 0) return;
            phrases.Add(new BoundLyric(start, end, text.ToString()));
            text.Clear();
        }
        foreach (var word in words)
        {
            if (text.Length > 0 && (word.StartNote > end || text.Length >= 24)) Flush();
            if (text.Length == 0) start = word.StartNote;
            text.Append(word.Text); end = word.EndNote;
            if (word.EndLine) Flush();
        }
        Flush();
        var result = new ScoreLyrics(Signature(notes), source, phrases) { Syllables = words.ToArray() };
        result.Validate(notes);
        return result;
    }
}
