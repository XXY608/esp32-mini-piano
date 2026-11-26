using System.Text;

namespace HarmonicaPlayer;

public static class MidiImporter
{
    public sealed record TrackOption(int Index, string Name, int NoteCount);

    private sealed class TrackData
    {
        public List<TimedScoreEvent> Events { get; } = new();
        public List<int> Channels { get; } = new();
        public List<(int Channel, long Tick, bool Down)> Pedal { get; } = new();
        public long EndTick { get; set; }
        public string? Title { get; set; }
    }

    public static ImportedScore Import(byte[] data, string fallbackTitle, bool preserveTiming = false)
        => ImportTrack(data, fallbackTitle, null, preserveTiming);

    public static IReadOnlyList<TrackOption> GetTracks(byte[] data) => ReadFile(data).Tracks
        .Select((track, index) => new TrackOption(index, track.Title ?? $"音轨 {index + 1}", track.Events.Count))
        .Where(track => track.NoteCount > 0).ToArray();

    public static ImportedScore ImportTrack(byte[] data, string fallbackTitle, int? trackIndex,
        bool preserveTiming = false, bool includePerformance = false)
    {
        var (tracks, tempos, division, format) = ReadFile(data);
        if (trackIndex is int index && (index < 0 || index >= tracks.Count))
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        var selected = trackIndex is int chosen ? tracks[chosen] : tracks.Where(t => t.Events.Count > 0)
            .OrderByDescending(t => IsMelodyTrack(t.Title)).ThenByDescending(t => t.Events.Count).FirstOrDefault();
        if (selected == null || selected.Events.Count == 0)
            throw new FormatException("MIDI 中没有可演奏的音符。");
        var events = selected.Events.Select(e => e with
        {
            StartBeats = e.StartBeats / division,
            DurationBeats = e.DurationBeats / division
        });
        var tempoTrack = format == 2 ? tracks.IndexOf(selected) : -1;
        var tempoChanges = tempos.Where(t => tempoTrack < 0 || t.Track == tempoTrack)
            .OrderBy(t => t.Tick).ToArray();
        int microseconds = tempoChanges.FirstOrDefault().Microseconds;
        int? bpm = microseconds > 0 ? (int)Math.Round(60000000.0 / microseconds, MidpointRounding.AwayFromZero) : null;
        string title = selected.Title ?? tracks.Select(t => t.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? fallbackTitle;
        if (!includePerformance)
            return ImportedScoreFactory.Create(title, bpm, events, preserveTiming: preserveTiming);

        var beatAt = ImportedScoreFactory.PlaybackClock(tempoChanges.Select(t => ((double)t.Tick, (double)t.Microseconds)), division, bpm);
        TimedScoreEvent ConvertTime(TimedScoreEvent note)
        {
            double start = beatAt(note.StartBeats);
            return note with { StartBeats = start, DurationBeats = beatAt(note.StartBeats + note.DurationBeats) - start };
        }
        var arrangement = trackIndex.HasValue || format == 2 ? new[] { selected } : tracks.ToArray();
        var performance = arrangement.SelectMany(WithSustain).Select(ConvertTime).ToArray();
        string[] warnings = format == 2 && tracks.Count > 1 ? new[] { "此 MIDI 的音轨为独立曲目，已使用选定曲目。" } : Array.Empty<string>();
        return ImportedScoreFactory.Create(title, bpm, selected.Events.Select(ConvertTime), warnings,
            preserveTiming, performance);
    }

    private static bool IsMelodyTrack(string? title)
    {
        if (title == null) return false;
        return title.Contains("主旋律", StringComparison.Ordinal) || title.Contains("人声", StringComparison.Ordinal) ||
            title.Split(new[] { ' ', '-', '_', '/', '(', ')', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(word => word.Equals("melody", StringComparison.OrdinalIgnoreCase) ||
                             word.Equals("vocal", StringComparison.OrdinalIgnoreCase) ||
                             word.Equals("vocals", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<TimedScoreEvent> WithSustain(TrackData track)
    {
        if (track.Pedal.Count == 0) return track.Events;
        return track.Events.Select((note, index) => (Note: note, Channel: track.Channels[index]))
            .GroupBy(e => e.Channel).SelectMany(channel =>
            {
                var intervals = new List<(long Start, long End)>();
                long? pressed = null;
                foreach (var pedal in track.Pedal.Where(p => p.Channel == channel.Key))
                {
                    if (pedal.Down) pressed ??= pedal.Tick;
                    else if (pressed is long start) { intervals.Add((start, pedal.Tick)); pressed = null; }
                }
                if (pressed is long last) intervals.Add((last, track.EndTick));
                var nextAttack = new Dictionary<int, double>();
                var extended = new List<TimedScoreEvent>();
                foreach (var entry in channel.OrderByDescending(e => e.Note.StartBeats))
                {
                    var note = entry.Note;
                    double end = note.StartBeats + note.DurationBeats;
                    int lo = 0, hi = intervals.Count;
                    while (lo < hi)
                    {
                        int middle = lo + (hi - lo) / 2;
                        if (intervals[middle].End <= end) lo = middle + 1;
                        else hi = middle;
                    }
                    if (lo < intervals.Count && intervals[lo].Start <= end)
                        end = Math.Max(end, Math.Min(intervals[lo].End, nextAttack.GetValueOrDefault(note.MidiPitch, double.PositiveInfinity)));
                    extended.Add(note with { DurationBeats = end - note.StartBeats });
                    nextAttack[note.MidiPitch] = note.StartBeats;
                }
                return extended;
            });
    }

    private readonly record struct TempoChange(long Tick, int Microseconds, int Track);

    private static (List<TrackData> Tracks, List<TempoChange> Tempos, int Division, int Format) ReadFile(byte[] data)
    {
        if (data.Length < 14 || ReadAscii(data, 0, 4) != "MThd")
            throw new FormatException("不是标准 MIDI 文件。");
        int headerLength = ReadInt32(data, 4);
        if (headerLength < 6 || data.Length < 8 + headerLength)
            throw new FormatException("MIDI 文件头损坏。");
        ushort format = ReadUInt16(data, 8);
        ushort trackCount = ReadUInt16(data, 10);
        short division = unchecked((short)ReadUInt16(data, 12));
        if (division <= 0) throw new FormatException("暂不支持 SMPTE 时间格式的 MIDI 文件。");

        var tracks = new List<TrackData>();
        int offset = 8 + headerLength;
        var tempos = new List<TempoChange>();
        for (int trackIndex = 0; trackIndex < trackCount && offset + 8 <= data.Length; trackIndex++)
        {
            if (ReadAscii(data, offset, 4) != "MTrk") throw new FormatException("MIDI 音轨块损坏。");
            int length = ReadInt32(data, offset + 4);
            offset += 8;
            if (length < 0 || offset + length > data.Length) throw new FormatException("MIDI 音轨长度超出文件范围。");
            var track = ParseTrack(data, offset, length, trackIndex, tempos);
            tracks.Add(track);
            offset += length;
        }

        return (tracks, tempos, division, format);
    }

    private static TrackData ParseTrack(byte[] data, int start, int length, int trackIndex, List<TempoChange> tempos)
    {
        int offset = start;
        int end = start + length;
        long tick = 0;
        byte runningStatus = 0;
        var track = new TrackData();
        var active = new Dictionary<(int Channel, int Note), Queue<(long Start, int Velocity)>>();

        while (offset < end)
        {
            tick += ReadVlq(data, ref offset, end);
            if (offset >= end) break;
            byte status = data[offset++];
            if (status < 0x80)
            {
                if (runningStatus == 0) throw new FormatException("MIDI 遇到无效的运行状态。");
                offset--;
                status = runningStatus;
            }
            else if (status < 0xF0)
            {
                runningStatus = status;
            }

            if (status == 0xFF)
            {
                if (offset >= end) break;
                byte metaType = data[offset++];
                int metaLength = ReadVlq(data, ref offset, end);
                if (offset + metaLength > end) throw new FormatException("MIDI 元事件长度损坏。");
                if (metaType == 0x51 && metaLength == 3)
                {
                    int tempo = (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
                    if (tempo > 0) tempos.Add(new TempoChange(tick, tempo, trackIndex));
                }
                else if (metaType is 0x01 or 0x03 && metaLength > 0)
                {
                    string text = Encoding.UTF8.GetString(data, offset, metaLength).Trim('\0', ' ', '\r', '\n');
                    if (metaType == 0x03 && !string.IsNullOrWhiteSpace(text)) track.Title = text;
                }
                offset += metaLength;
                continue;
            }
            if (status is 0xF0 or 0xF7)
            {
                int sysexLength = ReadVlq(data, ref offset, end);
                if (offset + sysexLength > end) throw new FormatException("MIDI 系统独占事件损坏。");
                offset += sysexLength;
                continue;
            }

            int command = status & 0xF0;
            int channel = status & 0x0F;
            if (command is 0x80 or 0x90)
            {
                EnsureAvailable(data, offset, 2, end);
                int note = data[offset++];
                int velocity = data[offset++];
                if (channel == 9) continue;
                var key = (channel, note);
                if (command == 0x90 && velocity > 0)
                {
                    if (!active.TryGetValue(key, out var queue)) active[key] = queue = new Queue<(long, int)>();
                    queue.Enqueue((tick, velocity));
                }
                else if (active.TryGetValue(key, out var notes) && notes.Count > 0)
                {
                    var started = notes.Dequeue();
                    if (tick > started.Start)
                    {
                        track.Events.Add(new TimedScoreEvent(started.Start, tick - started.Start, note, false, track.Events.Count)
                        { Velocity = started.Velocity });
                        track.Channels.Add(channel);
                    }
                }
            }
            else if (command is 0xA0 or 0xB0 or 0xE0)
            {
                EnsureAvailable(data, offset, 2, end);
                if (command == 0xB0 && data[offset] == 64) track.Pedal.Add((channel, tick, data[offset + 1] >= 64));
                offset += 2;
            }
            else if (command is 0xC0 or 0xD0)
            {
                EnsureAvailable(data, offset, 1, end); offset++;
            }
            else throw new FormatException($"MIDI 中存在不支持的事件类型 0x{status:X2}。");
        }
        track.EndTick = tick;
        return track;
    }

    private static int ReadVlq(byte[] data, ref int offset, int end)
    {
        int value = 0;
        for (int i = 0; i < 4; i++)
        {
            if (offset >= end) throw new FormatException("MIDI 可变长度数值损坏。");
            byte current = data[offset++];
            value = (value << 7) | (current & 0x7F);
            if ((current & 0x80) == 0) return value;
        }
        throw new FormatException("MIDI 可变长度数值过长。");
    }

    private static void EnsureAvailable(byte[] data, int offset, int count, int end)
    {
        if (offset < 0 || count < 0 || offset + count > end || offset + count > data.Length)
            throw new FormatException("MIDI 事件数据不完整。");
    }

    private static ushort ReadUInt16(byte[] data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);
    private static int ReadInt32(byte[] data, int offset) => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    private static string ReadAscii(byte[] data, int offset, int length) => Encoding.ASCII.GetString(data, offset, length);
}
