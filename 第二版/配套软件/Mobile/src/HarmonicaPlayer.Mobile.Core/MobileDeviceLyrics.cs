using System.Text;
using HarmonicaPlayer;

namespace HarmonicaPlayer.Mobile.Core;

public static class MobileDeviceLyrics
{
    public static IReadOnlyList<SongLyricCueDto>? CreateCues(ImportedScore score, string? text, double offsetSeconds)
    {
        if (!double.IsFinite(offsetSeconds)) throw new FormatException("歌词偏移必须是有效秒数。");
        var source = MobileLyricsTimeline.Parse(text, includeEmpty: true);
        if (!source.Any(c => c.Text.Length > 0)) return null;
        if (score.Bpm is not int bpm || bpm < 20 || bpm > 400)
            throw new FormatException("发送LRC歌词前请先填写曲谱的播放BPM。");
        double total = score.Notes.Sum(n => n.Beats);
        var cues = source.Select(c => new SongLyricCueDto(
                Math.Max(0, (c.StartMilliseconds / 1000d + offsetSeconds) * bpm / 60d), c.Text))
            .Where(c => c.StartBeat < total)
            .GroupBy(c => c.StartBeat).Select(g => g.Last()).ToArray();
        if (!cues.Any(c => c.Text.Length > 0))
            throw new FormatException("歌词时间均在曲谱结束之后，请检查BPM或歌词偏移。");
        if (cues.Length > 256 || cues.Any(c => Encoding.UTF8.GetByteCount(c.Text) > 512 ||
                c.Text.Any(ch => char.IsControl(ch))) || cues.Sum(c => Encoding.UTF8.GetByteCount(c.Text)) > 32768)
            throw new FormatException("琴端歌词最多256句，每句512字节、总计32KB，且不能含控制字符。");
        return cues;
    }
}
