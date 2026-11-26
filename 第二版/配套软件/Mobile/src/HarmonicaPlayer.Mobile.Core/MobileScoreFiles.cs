using System.Text;
using System.Text.Json;
using HarmonicaPlayer;

namespace HarmonicaPlayer.Mobile.Core;

public static class MobileScoreFiles
{
    public static async Task<byte[]> ReadAsync(Stream stream, int limit = CcmzImporter.MaxFileBytes, CancellationToken token = default)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (output.Length + read > limit) throw new IOException("曲谱文件超过大小限制。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static ImportedScore Import(byte[] data, string filename, int? midiTrack = null, bool includePerformance = false)
    {
        string title = Path.GetFileNameWithoutExtension(filename);
        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".mid" or ".midi" => MidiImporter.ImportTrack(data, title, midiTrack, preserveTiming: true, includePerformance: includePerformance) with { Title = title },
            ".ccmz" => CcmzImporter.Import(data, title, preserveTiming: true, includePerformance: includePerformance),
            ".xml" or ".musicxml" => MusicXmlImporter.Import(new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF'), title, preserveTiming: true, includePerformance: includePerformance),
            ".mxl" => ImportMxl(data, title, includePerformance),
            ".txt" => ImportTxt(data, filename),
            _ => throw new FormatException("请选择 TXT、MIDI、MusicXML 或 CCMZ；图片/PDF/EOP 不能直接播放。")
        };
    }

    public static SongPayload ImportDeviceJson(byte[] data)
    {
        if (data.Length > 120 * 1024) throw new FormatException("实体琴 JSON 曲谱超过 120 KiB。");
        SongPayload? song;
        try { song = JsonSerializer.Deserialize<SongPayload>(data, DeviceJson.Options); }
        catch (JsonException error) { throw new FormatException("实体琴 JSON 曲谱格式无效。", error); }
        if (song is null || song.Id is not { Length: >= 1 and <= 64 } ||
            song.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) || song.Id == "demo-scale" ||
            string.IsNullOrWhiteSpace(song.Title) || Encoding.UTF8.GetByteCount(song.Title) > 240 ||
            song.Bpm is < 20 or > 400 || song.Notes is not { Count: >= 1 and <= 1024 } ||
            song.Notes.Any(n => n is null || n.Pitch is < 0 or > 7 || n.Octave is < -1 or > 1 ||
                !double.IsFinite(n.DurationBeats) || n.DurationBeats is < 1.0 / 64 or > 64))
            throw new FormatException("实体琴 JSON 的曲名、音符或速度无效。");
        if (song.Polyphony is { } timeline)
        {
            var bytes = new byte[40960];
            if (!Convert.TryFromBase64String(timeline, bytes, out int length) || length == 0 || length % 10 != 0)
                throw new FormatException("实体琴 JSON 的多音时间轴无效。");
        }
        if (song.Pedal is { } pedal && (song.Polyphony is null || pedal.Count > 512 || pedal.Any(p => p > 125829121)))
            throw new FormatException("实体琴 JSON 的踏板时间轴无效。");
        return song with { SourceType = "device-json" };
    }

    private static ImportedScore ImportMxl(byte[] data, string title, bool includePerformance)
    {
        using var source = new MemoryStream(data, writable: false);
        return MusicXmlImporter.ImportCompressed(source, title, preserveTiming: true, includePerformance: includePerformance);
    }

    private static ImportedScore ImportTxt(byte[] data, string filename)
    {
        var draft = TxtScoreImporter.Parse(filename, new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF'));
        return draft.Score ?? throw new FormatException(draft.ParseError);
    }
}
