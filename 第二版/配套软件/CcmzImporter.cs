using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HarmonicaPlayer;

public static class CcmzImporter
{
    public const int MaxFileBytes = 50 * 1024 * 1024;
    private const long MaxJsonEntryBytes = 16 * 1024 * 1024;
    private const int MaxZipEntries = 1000;
    private const int MaxEvents = 500000;

    public static ImportedScore Import(byte[] data, string fallbackTitle, bool preserveTiming = false, bool includePerformance = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 2) throw new FormatException("CCMZ 文件为空或已损坏。");
        if (data.Length > MaxFileBytes) throw new FormatException("CCMZ 文件不得超过 50MB。");
        byte type = data[0];
        if (type is not (1 or 2)) throw new FormatException($"文件解析失败：不支持的 CCMZ 加密方式（{type}）。");

        byte[] zipBytes = DecodeZip(data, type);
        try
        {
            using var stream = new MemoryStream(zipBytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count > MaxZipEntries)
                throw new FormatException("CCMZ 压缩包包含过多文件。");

            string midiName = type == 1 ? "data.ccmid" : "midi.json";
            string scoreName = type == 1 ? "data.ccxml" : "score.json";
            using JsonDocument midi = ReadJson(zip, midiName);
            using JsonDocument score = ReadJson(zip, scoreName);
            return CreateImportedScore(midi.RootElement, score.RootElement, fallbackTitle, preserveTiming, includePerformance);
        }
        catch (FormatException)
        {
            throw;
        }
        catch (InvalidDataException error)
        {
            throw new FormatException("CCMZ 压缩包已损坏或不是有效的 CCMZ 文件。", error);
        }
        catch (JsonException error)
        {
            throw new FormatException("CCMZ 中的 JSON 数据已损坏。", error);
        }
    }

    private static byte[] DecodeZip(byte[] data, byte type)
    {
        var zip = new byte[data.Length - 1];
        for (int i = 1; i < data.Length; i++)
            zip[i - 1] = type == 1 ? data[i] : data[i] % 2 == 0 ? (byte)(data[i] + 1) : (byte)(data[i] - 1);
        return zip;
    }

    private static JsonDocument ReadJson(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new FormatException($"CCMZ 压缩包缺少 {name}。");
        if (entry.Length < 0 || entry.Length > MaxJsonEntryBytes)
            throw new FormatException($"CCMZ 文件中的 {name} 超过安全大小限制。");
        using var stream = entry.Open();
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
    }

    private static ImportedScore CreateImportedScore(JsonElement midi, JsonElement score, string fallbackTitle, bool preserveTiming, bool includePerformance)
    {
        if (!midi.TryGetProperty("events", out var eventsElement) || eventsElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("CCMZ 中没有可识别的音符事件。");
        if (eventsElement.GetArrayLength() > MaxEvents)
            throw new FormatException("CCMZ 音符事件过多，无法安全解析。");

        int ticksPerBeat = TicksPerBeat(midi);
        var sourceNotes = ReadSourceNotes(score);
        int preferredTrack = midi.TryGetProperty("rightHandTrack", out var rightHand) && rightHand.TryGetInt32(out int track) ? track : -1;
        var events = new List<TimedScoreEvent>();
        var trackCounts = new Dictionary<int, int>();
        var all = new List<(int Track, TimedScoreEvent Event, string? Voice)>();
        var performance = includePerformance ? new List<TimedScoreEvent>() : null;
        var pedals = new List<(double Beat, bool Down)>();
        int order = 0;
        foreach (var item in eventsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("event", out var raw) || raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() < 3) continue;
            if (!item.TryGetProperty("tick", out var tickValue) || !TryGetFiniteDouble(tickValue, out double tick) || tick < 0) continue;
            int status = raw[0].TryGetInt32(out int statusValue) ? statusValue : 0;
            if (includePerformance && (status & 0xF0) == 0xB0 && raw[1].TryGetInt32(out int controller) && controller == 64 && raw[2].TryGetInt32(out int pedalValue))
                pedals.Add((tick / ticksPerBeat, pedalValue >= 64));
            if (!item.TryGetProperty("duration", out var durationValue) || !TryGetFiniteDouble(durationValue, out double duration) || duration <= 0) continue;
            if ((status & 0xF0) != 0x90 || (status & 0x0F) == 9 ||
                !raw[1].TryGetInt32(out int midiPitch) || midiPitch is < 0 or > 127 ||
                !raw[2].TryGetInt32(out int velocity) || velocity <= 0) continue;
            int eventTrack = item.TryGetProperty("track", out var trackValue) && trackValue.TryGetInt32(out int parsedTrack) ? parsedTrack : 0;
            SourceNote? sourceNote = null;
            int sourcePart = item.TryGetProperty("part", out var partValue) && partValue.TryGetInt32(out int p) ? p : 0;
            string partPrefix = sourcePart + ":";
            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                sourceNotes.TryGetValue(partPrefix + id.GetString(), out sourceNote);
            bool hasElementIds = item.TryGetProperty("elem_ids", out var ids) && ids.ValueKind == JsonValueKind.Array;
            if (sourceNote == null && hasElementIds)
                foreach (var candidate in ids.EnumerateArray())
                    if (candidate.ValueKind == JsonValueKind.String && sourceNotes.TryGetValue(partPrefix + candidate.GetString(), out sourceNote)) break;
            int repeat = item.TryGetProperty("repeatIndex", out var repetition) && repetition.TryGetInt32(out int r) ? r : 0;
            var sourceLyric = sourceNote?.Lyrics.FirstOrDefault(l => l.Verse == repeat)
                ?? sourceNote?.Lyrics.FirstOrDefault();
            var timedEvent = new TimedScoreEvent(tick / ticksPerBeat, duration / ticksPerBeat, midiPitch, false, order++)
            { Lyric = sourceLyric?.Lyric, Velocity = Math.Min(127, velocity) };
            performance?.Add(timedEvent);
            if (sourceNote is { IsArpeggioAccompaniment: true } or { IsGrace: true }) continue;
            if (sourceNote is { IsArpeggiated: true, WrittenTick: double writtenTick } &&
                item.TryGetProperty("meas_start_tick", out var startTick) && TryGetFiniteDouble(startTick, out double measureStart) &&
                measureStart >= 0 &&
                Math.Abs(tick - measureStart - writtenTick) <= ticksPerBeat / 4d)
                timedEvent = timedEvent with { StartBeats = (measureStart + writtenTick) / ticksPerBeat };
            if (sourceNote is not null &&
                WrittenDuration(sourceNote, ids, partPrefix, sourceNotes, ticksPerBeat) is double written &&
                timedEvent.DurationBeats < written &&
                (sourceNote.IsArpeggiated || timedEvent.DurationBeats >= 2d / 3 * written - 0.000001))
            {
                // CCMZ's rendered MIDI can release ordinary notes before their
                // notated end. Keep written phrasing on the simple practice line.
                timedEvent = timedEvent with { DurationBeats = written };
            }
            trackCounts[eventTrack] = trackCounts.GetValueOrDefault(eventTrack) + 1;
            all.Add((eventTrack, timedEvent, sourceNote?.Voice));
        }
        if (all.Count == 0) throw new FormatException("CCMZ 中没有可演奏的音符。");
        if (!trackCounts.ContainsKey(preferredTrack)) preferredTrack = trackCounts.OrderByDescending(pair => pair.Value).First().Key;

        var lyricVoice = all.Where(e => e.Event.Lyric != null).GroupBy(e => (e.Track, e.Voice))
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        if (lyricVoice != null) preferredTrack = lyricVoice.Key.Track;
        var melodyTrack = all.Where(e => e.Track == preferredTrack).ToList();
        string? melodyVoice = lyricVoice?.Key.Voice;
        if (lyricVoice == null && melodyTrack.All(e => e.Voice != null))
        {
            var upperVoices = melodyTrack.Select(e => e.Voice!).Distinct(StringComparer.Ordinal)
                .Where(voice => voice.EndsWith(":1:1", StringComparison.Ordinal)).Take(2).ToArray();
            if (upperVoices.Length == 1) melodyVoice = upperVoices[0];
        }
        events.AddRange(melodyTrack.Where(e => melodyVoice == null || e.Voice == melodyVoice)
            .Select(e => e.Event).OrderBy(e => e.StartBeats).ThenBy(e => e.Order));
        if (events.Count == 0) throw new FormatException("CCMZ 主旋律轨道中没有可演奏的音符。");

        int? bpm = null;
        var tempoValues = new List<double>();
        var tempoChanges = new List<(double Position, double Microseconds)>();
        if (midi.TryGetProperty("tempos", out var tempos) && tempos.ValueKind == JsonValueKind.Array)
        {
            foreach (var tempo in tempos.EnumerateArray())
            {
                if (tempo.ValueKind != JsonValueKind.Object ||
                    !tempo.TryGetProperty("tempo", out var microseconds) ||
                    !TryGetFiniteDouble(microseconds, out double value) || value <= 0) continue;
                tempoValues.Add(value);
                if (tempo.TryGetProperty("tick", out var position) && TryGetFiniteDouble(position, out double tick) && tick >= 0)
                    tempoChanges.Add((tick / ticksPerBeat, value));
                else if (tempoValues.Count == 1) tempoChanges.Add((0, value));
            }
            if (tempoValues.Count > 0)
                bpm = (int)Math.Round(60000000d / tempoValues[0], MidpointRounding.AwayFromZero);
        }
        var warnings = (!includePerformance || tempoChanges.Count != tempoValues.Count) && tempoValues.Select(value => (int)Math.Round(60000000d / value, MidpointRounding.AwayFromZero))
            .Distinct().Skip(1).Any()
            ? new[] { includePerformance ? "源谱部分速度标记缺少位置，已采用有定位的速度标记。" : "源谱包含多个速度标记，播放器使用第一个速度。" }
            : Array.Empty<string>();
        string title = ReadTitle(score) ?? fallbackTitle;
        var arrangementWarnings = warnings.ToList();
        if (!includePerformance && events.FirstOrDefault(e => e.Lyric != null) is { } firstLyric && firstLyric.StartBeats > events[0].StartBeats)
        {
            events = events.Where(e => e.StartBeats >= firstLyric.StartBeats).ToList();
            arrangementWarnings.Add("已从源谱第一处歌词开始，移除前奏。");
        }
        if (lyricVoice == null && sourceNotes.Values.Any(n => n.Lyrics.Count > 0))
            arrangementWarnings.Add("源谱有歌词，但缺少与演奏事件一致的音符标识，请使用“校准歌词”。");
        IReadOnlyList<TimedScoreEvent>? fullPerformance = null;
        Func<double, double>? playbackClock = null;
        if (performance is not null)
        {
            var beatAt = ImportedScoreFactory.PlaybackClock(tempoChanges, 1, bpm);
            playbackClock = beatAt;
            fullPerformance = performance.Select(note =>
            {
                double start = beatAt(note.StartBeats);
                return note with { StartBeats = start,
                    DurationBeats = beatAt(note.StartBeats + note.DurationBeats) - start };
            }).ToArray();
            pedals = pedals.OrderBy(p => p.Beat).Select(p => (beatAt(p.Beat), p.Down)).ToList();
        }
        else
        {
            double firstBeat = events[0].StartBeats;
            events = events.Select(note => note with { StartBeats = note.StartBeats - firstBeat }).ToList();
        }
        var imported = ImportedScoreFactory.Create(title, bpm, events, arrangementWarnings, preserveTiming,
            fullPerformance, filterShortOverlaps: melodyVoice == null, connectGapBeats: 1d / 24,
            centerMelody: true, playbackClock: playbackClock);
        int measureBeats = TicksPerMeasure(midi);
        if (!preserveTiming) imported = SplitAtMeasureBoundaries(imported, measureBeats);
        return imported with { Text = ScoreTextFormatter.FormatWithMeasures(imported.Notes, measureBeats),
            SustainPedal = pedals.Count == 0 ? null : pedals };
    }

    private static ImportedScore SplitAtMeasureBoundaries(ImportedScore imported, int measureBeats)
    {
        if (measureBeats <= 0) return imported;
        int measureUnits = checked(measureBeats * 4), elapsed = 0;
        var notes = new List<ScoreNote>();
        var boundaries = new int[imported.Notes.Count + 1];
        for (int index = 0; index < imported.Notes.Count; index++)
        {
            boundaries[index] = notes.Count;
            var note = imported.Notes[index];
            int remaining = (int)Math.Round(note.Beats * 4);
            while (remaining > 0)
            {
                int duration = Math.Min(remaining, measureUnits - elapsed);
                notes.Add(note with { Beats = duration / 4d, Position = notes.Count });
                remaining -= duration;
                elapsed = (elapsed + duration) % measureUnits;
                if (notes.Count > 20000) throw new FormatException("导入后的音符超过 20000 个，无法安全播放。");
            }
        }
        if (notes.Count == imported.Notes.Count) return imported;
        boundaries[^1] = notes.Count;
        BoundLyric Remap(BoundLyric word) => word with
        { StartNote = boundaries[word.StartNote], EndNote = boundaries[word.EndNote] };
        var binding = imported.Lyrics is { } original ? original with
        {
            ScoreSignature = ScoreLyrics.Signature(notes),
            Cues = original.Cues.Select(Remap).ToArray(),
            Syllables = original.Syllables.Select(Remap).ToArray()
        } : null;
        binding?.Validate(notes);
        return imported with { Notes = notes, Lyrics = binding,
            Warnings = imported.Warnings.Append("跨小节长音已按同音分段，保留总时值与逐字范围。").ToArray() };
    }

    private sealed record SourceLyric(int Verse, NoteLyric Lyric);
    private sealed record SourceNote(string Voice, IReadOnlyList<SourceLyric> Lyrics,
        bool IsGrace, bool IsArpeggiated, double? WrittenTick, double? WrittenTicks,
        double? WrittenBeats, bool IsArpeggioAccompaniment = false, long? WrittenPitch = null)
    {
        public double? WrittenDuration(int ticksPerBeat) => WrittenTicks / ticksPerBeat ?? WrittenBeats;
    }

    private static double? WrittenDuration(SourceNote first, JsonElement ids, string partPrefix,
        Dictionary<string, SourceNote> sourceNotes, int ticksPerBeat)
    {
        double? firstDuration = first.WrittenDuration(ticksPerBeat);
        if (firstDuration == null || first.WrittenPitch == null ||
            ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() < 2)
            return firstDuration;

        // The renderer links a written tie chain as a single MIDI event.
        // Recover its full score duration when every segment is identifiable.
        double total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in ids.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not string id || !seen.Add(id) ||
                !sourceNotes.TryGetValue(partPrefix + id, out var note) || note.Voice != first.Voice ||
                note.WrittenPitch != first.WrittenPitch || note.WrittenDuration(ticksPerBeat) is not double duration)
                return firstDuration;
            total += duration;
        }
        return total;
    }

    private static double? WrittenDuration(JsonElement note)
    {
        if (!note.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out int denominator) ||
            denominator is not (1 or 2 or 4 or 8 or 16 or 32 or 64 or 128))
            return null;
        int dots = 0;
        if (note.TryGetProperty("dots", out var dotsValue) &&
            (dotsValue.ValueKind != JsonValueKind.Number || !dotsValue.TryGetInt32(out dots) || dots is < 0 or > 3))
            return null;
        return 4d / denominator * (2 - 1d / (1 << dots));
    }

    private static long? NotatedPitch(JsonElement element)
    {
        if (!element.TryGetProperty("step", out var stepValue) || stepValue.ValueKind != JsonValueKind.Number || !stepValue.TryGetInt32(out int step) || step is < 1 or > 7 ||
            !element.TryGetProperty("octave", out var octaveValue) || octaveValue.ValueKind != JsonValueKind.Number || !octaveValue.TryGetInt32(out int octave)) return null;
        int alter = 0;
        if (element.TryGetProperty("alter", out var alterValue) && (alterValue.ValueKind != JsonValueKind.Number || !alterValue.TryGetInt32(out alter))) return null;
        return 12L * (octave + 1L) + new[] { 0, 2, 4, 5, 7, 9, 11 }[step - 1] + alter;
    }

    private static Dictionary<string, SourceNote> ReadSourceNotes(JsonElement score)
    {
        var result = new Dictionary<string, SourceNote>(StringComparer.Ordinal);
        if (!score.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return result;
        int partIndex = 0;
        foreach (var part in parts.EnumerateArray())
        {
            if (!part.TryGetProperty("measures", out var measures) || measures.ValueKind != JsonValueKind.Array) { partIndex++; continue; }
            foreach (var measure in measures.EnumerateArray())
            {
                if (!measure.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.Array) continue;
                foreach (var note in notes.EnumerateArray())
                {
                    var lyrics = new List<SourceLyric>();
                    if (note.TryGetProperty("lyrics", out var rawLyrics) && rawLyrics.ValueKind == JsonValueKind.Array)
                        foreach (var lyric in rawLyrics.EnumerateArray())
                        {
                            if (!lyric.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString())) continue;
                            string label = text.GetString()!;
                            // Some CCMZ piano scores store the simile direction in a lyric slot.
                            if (string.Equals(label.Trim(), "simile", StringComparison.OrdinalIgnoreCase)) continue;
                            int verse = lyric.TryGetProperty("num", out var num) && num.TryGetInt32(out int n) ? n : 0;
                            lyrics.Add(new SourceLyric(verse, new NoteLyric(label,
                                lyric.TryGetProperty("endLine", out var endLine) && endLine.ValueKind == JsonValueKind.True)
                            {
                                AlignmentIndex = lyric.TryGetProperty("alignmentIndex", out var ai) && ai.TryGetInt32(out int a) ? a : -1,
                                ExplicitSpan = lyric.TryGetProperty("explicitSpan", out var span) && span.ValueKind == JsonValueKind.True,
                                NeedsReview = lyric.TryGetProperty("needsReview", out var review) && review.ValueKind == JsonValueKind.True
                            }));
                        }
                    string voice = partIndex + ":" + (note.TryGetProperty("staff", out var staff) ? staff.ToString() : "1") +
                        ":" + (note.TryGetProperty("v", out var v) ? v.ToString() : "1");
                    bool isGrace = note.TryGetProperty("grace", out var grace) &&
                        grace.ValueKind is not (JsonValueKind.False or JsonValueKind.Null) &&
                        (grace.ValueKind != JsonValueKind.Number || grace.GetDouble() != 0);
                    var articulations = note.TryGetProperty("arts", out var arts) && arts.ValueKind == JsonValueKind.Array
                        ? arts.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.Object &&
                            a.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                            ? type.GetString() : null).ToArray() : Array.Empty<string?>();
                    bool isArpeggiated = articulations.Contains("arpeggiate");
                    bool canUseWrittenDuration = articulations.All(a => a == "arpeggiate");
                    double? writtenTick = note.TryGetProperty("tick", out var tickValue) && TryGetFiniteDouble(tickValue, out double tick) && tick >= 0
                        ? tick : null;
                    double? writtenTicks = canUseWrittenDuration && note.TryGetProperty("ticks", out var ticksValue) &&
                        TryGetFiniteDouble(ticksValue, out double ticks) && ticks > 0 ? ticks : null;
                    var sourceNote = new SourceNote(voice, lyrics, isGrace, isArpeggiated, writtenTick, writtenTicks,
                        canUseWrittenDuration ? WrittenDuration(note) : null);
                    if (note.TryGetProperty("elems", out var elements) && elements.ValueKind == JsonValueKind.Array)
                    {
                        long? highest = null;
                        if (elements.GetArrayLength() > 1 && isArpeggiated)
                        {
                            var pitches = elements.EnumerateArray().Select(NotatedPitch).ToArray();
                            if (pitches.All(p => p.HasValue)) highest = pitches.Max();
                        }
                        foreach (var element in elements.EnumerateArray())
                            if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                            {
                                long? pitch = NotatedPitch(element);
                                result[partIndex + ":" + id.GetString()] = sourceNote with
                                { IsArpeggioAccompaniment = highest.HasValue && pitch < highest, WrittenPitch = pitch };
                            }
                    }
                }
            }
            partIndex++;
        }
        return result;
    }

    private static int TicksPerBeat(JsonElement midi)
    {
        int beats = midi.TryGetProperty("beats", out var beatValue) && beatValue.TryGetInt32(out int parsedBeats) && parsedBeats > 0 ? parsedBeats : 4;
        if (midi.TryGetProperty("measures", out var measures) && measures.ValueKind == JsonValueKind.Object)
        {
            var measure = measures.EnumerateObject().Select(p => p.Value).FirstOrDefault();
            if (measure.ValueKind == JsonValueKind.Object && measure.TryGetProperty("duration", out var duration) && duration.TryGetInt32(out int ticks) && ticks > 0)
                return Math.Max(1, ticks / beats);
        }
        return 480;
    }

    private static int TicksPerMeasure(JsonElement midi)
    {
        return midi.TryGetProperty("beats", out var beatValue) && beatValue.TryGetInt32(out int beats) && beats > 0
            ? beats
            : 4;
    }

    private static string? ReadTitle(JsonElement score)
    {
        if (score.TryGetProperty("title", out var title))
        {
            if (title.ValueKind == JsonValueKind.String) return title.GetString();
            if (title.ValueKind == JsonValueKind.Object && title.TryGetProperty("title", out var nested) && nested.ValueKind == JsonValueKind.String)
                return nested.GetString();
        }
        return null;
    }

    private static bool TryGetFiniteDouble(JsonElement value, out double result)
    {
        result = 0;
        return value.TryGetDouble(out result) && double.IsFinite(result);
    }
}
