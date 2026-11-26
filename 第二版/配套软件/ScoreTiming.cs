using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HarmonicaPlayer;

// Timing belongs to exactly one text revision. Editing the score invalidates it.
public sealed record ScoreTiming(int Version, string TextSha256, double[] Beats)
{
    public static string SidecarPath(string path) => Path.ChangeExtension(path, ".timing.json");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static ScoreTiming Create(string text, IReadOnlyList<ScoreNote> notes)
    {
        var parsed = ScoreParser.Parse(text, true);
        if (parsed.Count != notes.Count || parsed.Where((n, i) =>
            n.Degree != notes[i].Degree || n.Octave != notes[i].Octave || n.Sharp != notes[i].Sharp).Any())
            throw new FormatException("精确时值与谱面音符不一致。");
        var beats = notes.Select(n => n.Beats).ToArray();
        if (beats.Any(b => !double.IsFinite(b) || b < 1d / 64 || b > 64))
            throw new FormatException("精确时值超出设备支持范围。");
        return new ScoreTiming(1, Hash(text), beats);
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static List<ScoreNote> Apply(string text, string json)
    {
        if (json.Length > 1024 * 1024) throw new FormatException("精确时值文件过大。");
        ScoreTiming timing;
        try { timing = JsonSerializer.Deserialize<ScoreTiming>(json) ?? throw new FormatException("精确时值为空。"); }
        catch (JsonException e) { throw new FormatException("精确时值文件损坏。", e); }
        var notes = ScoreParser.Parse(text, true);
        if (timing.Version != 1 || timing.TextSha256 != Hash(text) || timing.Beats == null ||
            timing.Beats.Length != notes.Count || timing.Beats.Any(b => !double.IsFinite(b) || b < 1d / 64 || b > 64))
            throw new FormatException("精确时值与当前谱面不匹配。");
        return notes.Select((note, i) => note with { Beats = timing.Beats[i] }).ToList();
    }
}
