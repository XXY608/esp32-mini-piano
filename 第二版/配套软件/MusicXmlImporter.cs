using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace HarmonicaPlayer;

public static class MusicXmlImporter
{
    private readonly record struct VoiceKey(string Voice, string Staff);

    public static ImportedScore ImportFile(string path, string fallbackTitle, bool preserveTiming = false, bool includePerformance = false)
    {
        if (!Path.GetExtension(path).Equals(".mxl", StringComparison.OrdinalIgnoreCase))
            return Import(File.ReadAllText(path, new System.Text.UTF8Encoding(false, true)), fallbackTitle, preserveTiming, includePerformance);
        using var file = File.OpenRead(path);
        return ImportCompressed(file, fallbackTitle, preserveTiming, includePerformance);
    }

    public static ImportedScore ImportCompressed(Stream file, string fallbackTitle, bool preserveTiming = false, bool includePerformance = false)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > 1000) throw new FormatException("MXL 包含过多文件。");
        var container = zip.GetEntry("META-INF/container.xml") ?? throw new FormatException("MXL 缺少 container.xml。");
        using var stream = container.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 50 * 1024 * 1024 };
        using var reader = XmlReader.Create(stream, settings);
        var manifest = XDocument.Load(reader);
        string? name = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        var entry = name == null ? null : zip.GetEntry(name);
        if (entry == null || entry.Length > CcmzImporter.MaxFileBytes) throw new FormatException("MXL 主谱面不存在或超过 50MB。");
        using var source = new StreamReader(entry.Open());
        return Import(source.ReadToEnd(), fallbackTitle, preserveTiming, includePerformance);
    }

    public static ImportedScore Import(string xml, string fallbackTitle, bool preserveTiming = false, bool includePerformance = false)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new FormatException("MusicXML 文件为空。");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 50 * 1024 * 1024 };
        using var textReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(textReader, settings);
        var document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);

        var parts = document.Descendants().Where(e => e.Name.LocalName == "part").ToList();
        var part = parts.OrderByDescending(p => p.Descendants().Count(HasLyric))
            .ThenByDescending(p => p.Descendants().Count(IsPitchedNote)).FirstOrDefault();
        if (part == null) throw new FormatException("MusicXML 中没有可识别的乐谱声部。");

        var selectedKey = part.Descendants().Where(IsPitchedNote)
            .GroupBy(VoiceOf)
            .OrderByDescending(g => g.Count(HasLyric))
            .ThenBy(g => g.Key.Staff == "1" ? 0 : 1)
            .ThenBy(g => g.Key.Voice == "1" ? 0 : 1)
            .ThenByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var events = ReadPart(part, selectedKey);
        var warnings = new List<string>();
        string title = FirstText(document, "work-title", "movement-title") ?? fallbackTitle;
        int? bpm = ReadBpm(document);
        double trim = events.FirstOrDefault(e => e.Lyric != null)?.StartBeats ?? 0;
        if (trim > 0)
        {
            events = events.Where(e => e.StartBeats >= trim)
                .Select(e => e with { StartBeats = e.StartBeats - trim }).ToList();
            warnings.Add("已从源谱第一处歌词开始，移除前奏。");
        }
        if (document.Descendants().Any(e => e.Name.LocalName == "sound" &&
            (e.Attribute("dacapo") != null || e.Attribute("dalsegno") != null)))
            warnings.Add("谱面包含 D.C./D.S. 跳转，请在制谱软件中展开后导入；当前按小节反复顺序读取。");
        IReadOnlyList<TimedScoreEvent>? performance = null;
        if (includePerformance)
        {
            performance = parts.SelectMany(p => ReadPart(p, null))
                .Where(n => !n.IsRest && n.StartBeats + n.DurationBeats > trim)
                .Select(n => n with { StartBeats = Math.Max(0, n.StartBeats - trim),
                    DurationBeats = n.DurationBeats - Math.Max(0, trim - n.StartBeats) }).ToArray();
            if (document.Descendants().Where(e => e.Name.LocalName == "sound")
                .Select(e => (string?)e.Attribute("tempo")).Where(t => t != null).Distinct().Skip(1).Any())
                warnings.Add("MusicXML 含速度变化，当前按源谱标注的首个 BPM 演奏。可导出 MIDI 以保留变速。");
        }
        return ImportedScoreFactory.Create(title, bpm, events, warnings, preserveTiming, performance,
            filterShortOverlaps: false);
    }

    private static List<TimedScoreEvent> ReadPart(XElement part, VoiceKey? selectedKey)
    {
        int divisions = 1;
        double measureBeats = 4;
        double measureStart = 0;
        int order = 0;
        var events = new List<TimedScoreEvent>();
        var ties = new Dictionary<(VoiceKey Voice, int Midi), int>();
        var measures = part.Elements().Where(e => e.Name.LocalName == "measure").ToArray();
        var attributesAt = new Dictionary<XElement, (int Divisions, double Beats)>();
        foreach (var measure in measures)
        {
            var attributes = measure.Elements().FirstOrDefault(e => e.Name.LocalName == "attributes");
            if (int.TryParse(attributes == null ? null : TextOf(attributes, "divisions"), out int d) && d > 0) divisions = d;
            var time = attributes?.Elements().FirstOrDefault(e => e.Name.LocalName == "time");
            if (time != null && double.TryParse(TextOf(time, "beats"), out double b) &&
                double.TryParse(TextOf(time, "beat-type"), out double t) && t > 0) measureBeats = b * 4 / t;
            attributesAt[measure] = (divisions, measureBeats);
        }

        foreach (var (measure, pass) in PlaybackMeasures(measures))
        {
            (divisions, measureBeats) = attributesAt[measure];
            var attributes = measure.Elements().FirstOrDefault(e => e.Name.LocalName == "attributes");
            if (attributes != null)
            {
                var divisionText = TextOf(attributes, "divisions");
                if (int.TryParse(divisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDivisions) && parsedDivisions > 0)
                    divisions = parsedDivisions;

                var time = attributes.Elements().FirstOrDefault(e => e.Name.LocalName == "time");
                if (time != null &&
                    double.TryParse(TextOf(time, "beats"), NumberStyles.Float, CultureInfo.InvariantCulture, out var beats) &&
                    double.TryParse(TextOf(time, "beat-type"), NumberStyles.Float, CultureInfo.InvariantCulture, out var beatType) && beatType > 0)
                    measureBeats = beats * 4 / beatType;
            }

            double cursor = 0;
            double previousStart = 0;
            double maxCursor = 0;
            bool hasPrevious = false;

            foreach (var element in measure.Elements())
            {
                if (element.Name.LocalName is "backup" or "forward")
                {
                    double amount = DurationOf(element, divisions);
                    cursor += element.Name.LocalName == "backup" ? -amount : amount;
                    if (cursor < 0) cursor = 0;
                    maxCursor = Math.Max(maxCursor, cursor);
                    continue;
                }

                if (element.Name.LocalName != "note") continue;
                double duration = DurationOf(element, divisions);
                bool chord = element.Elements().Any(e => e.Name.LocalName == "chord");
                double start = chord && hasPrevious ? previousStart : cursor;
                if (!chord) cursor += duration;
                maxCursor = Math.Max(maxCursor, cursor);
                previousStart = start;
                hasPrevious = true;

                var voice = VoiceOf(element);
                if (element.Elements().Any(e => e.Name.LocalName == "grace") || duration <= 0 ||
                    (selectedKey.HasValue && voice != selectedKey.Value))
                    continue;

                if (element.Elements().Any(e => e.Name.LocalName == "rest"))
                {
                    events.Add(new TimedScoreEvent(measureStart + start, duration, IsRest: true, Order: order++));
                }
                else if (TryGetMidiPitch(element, out int midiPitch))
                {
                    double onset = measureStart + start;
                    var key = (voice, midiPitch);
                    string[] tieTypes = element.Descendants()
                        .Where(e => e.Name.LocalName is "tie" or "tied")
                        .Select(e => (string?)e.Attribute("type") ?? "").ToArray();
                    int index = events.Count;
                    if (ties.Remove(key, out int previous) && (tieTypes.Contains("stop") || tieTypes.Contains("continue")) &&
                        Math.Abs(events[previous].StartBeats + events[previous].DurationBeats - onset) < 0.000001)
                    {
                        var held = events[previous];
                        events[previous] = held with { DurationBeats = onset + duration - held.StartBeats };
                        index = previous;
                    }
                    else events.Add(new TimedScoreEvent(onset, duration, midiPitch, false, order++)
                        { Lyric = ReadLyric(element, pass) });
                    if (tieTypes.Contains("start") || tieTypes.Contains("continue")) ties[key] = index;
                }
            }

            bool implicitMeasure = (string?)measure.Attribute("implicit") == "yes";
            measureStart += implicitMeasure ? maxCursor : Math.Max(measureBeats, maxCursor);
            if (events.Count > 20000) throw new FormatException("反复展开后的音符超过 20000 个。");
        }

        return events;
    }

    private static bool HasLyric(XElement note) => IsPitchedNote(note) &&
        note.Elements().Any(e => e.Name.LocalName == "lyric" && !string.IsNullOrWhiteSpace(TextOf(e, "text")));

    private static NoteLyric? ReadLyric(XElement note, int pass)
    {
        var lyrics = note.Elements().Where(e => e.Name.LocalName == "lyric" &&
            !string.IsNullOrWhiteSpace(TextOf(e, "text"))).ToArray();
        var lyric = lyrics.FirstOrDefault(e => (string?)e.Attribute("number") == pass.ToString(CultureInfo.InvariantCulture))
            ?? lyrics.FirstOrDefault();
        if (lyric == null) return null;
        string? only = (string?)lyric.Attribute("time-only");
        if (only != null && !only.Split(',').Select(s => s.Trim()).Contains(pass.ToString(CultureInfo.InvariantCulture))) return null;
        string text = string.Concat(lyric.Elements().Where(e => e.Name.LocalName == "text").Select(e => e.Value));
        return new NoteLyric(text, lyric.Elements().Any(e => e.Name.LocalName is "end-line" or "end-paragraph"),
            TextOf(lyric, "syllabic") is "begin" or "middle");
    }

    // Expand ordinary bar repeats and numbered endings before attaching lyric verses.
    private static IEnumerable<(XElement Measure, int Pass)> PlaybackMeasures(XElement[] measures)
    {
        int start = 0, pass = 1, emitted = 0;
        var completed = new Dictionary<int, int>();
        HashSet<int>? ending = null;
        for (int i = 0; i < measures.Length; i++)
        {
            var measure = measures[i];
            var bars = measure.Elements().Where(e => e.Name.LocalName == "barline").ToArray();
            if (bars.SelectMany(e => e.Elements()).Any(e => e.Name.LocalName == "repeat" && (string?)e.Attribute("direction") == "forward"))
            { start = i; pass = completed.GetValueOrDefault(i) + 1; }
            var endings = bars.SelectMany(e => e.Elements()).Where(e => e.Name.LocalName == "ending").ToArray();
            foreach (var marker in endings.Where(e => (string?)e.Attribute("type") == "start"))
                ending = ((string?)marker.Attribute("number") ?? "").Split(',')
                    .Where(s => int.TryParse(s.Trim(), out _)).Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToHashSet();
            bool play = ending == null || ending.Contains(pass);
            if (play)
            {
                if (++emitted > 20000) throw new FormatException("MusicXML 反复次数过多。");
                yield return (measure, pass);
            }
            var backward = bars.SelectMany(e => e.Elements()).FirstOrDefault(e =>
                e.Name.LocalName == "repeat" && (string?)e.Attribute("direction") == "backward");
            if (backward != null && play)
            {
                int times = int.TryParse((string?)backward.Attribute("times"), out int count) ? count : 2;
                if (times is < 1 or > 8) throw new FormatException("MusicXML 反复次数须为 1～8。");
                if (pass < times)
                { completed[start] = pass; pass++; i = start - 1; ending = null; continue; }
                start = i + 1;
            }
            if (endings.Any(e => (string?)e.Attribute("type") is "stop" or "discontinue")) ending = null;
        }
    }

    private static bool IsPitchedNote(XElement element) => element.Name.LocalName == "note" && TryGetMidiPitch(element, out _);

    private static VoiceKey VoiceOf(XElement note)
    {
        string voice = TextOf(note, "voice") ?? "1";
        string staff = TextOf(note, "staff") ?? "1";
        return new VoiceKey(voice.Trim(), staff.Trim());
    }

    private static double DurationOf(XElement element, int divisions)
    {
        if (!long.TryParse(TextOf(element, "duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return 0;
        return value / (double)Math.Max(1, divisions);
    }

    private static bool TryGetMidiPitch(XElement note, out int midiPitch)
    {
        midiPitch = 0;
        var pitch = note.Elements().FirstOrDefault(e => e.Name.LocalName == "pitch");
        if (pitch == null || !int.TryParse(TextOf(pitch, "octave"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int octave))
            return false;

        int basePitch = (TextOf(pitch, "step") ?? "").Trim().ToUpperInvariant() switch
        {
            "C" => 0, "D" => 2, "E" => 4, "F" => 5,
            "G" => 7, "A" => 9, "B" => 11, _ => -1
        };
        if (basePitch < 0) return false;
        int alter = 0;
        int.TryParse(TextOf(pitch, "alter"), NumberStyles.Integer, CultureInfo.InvariantCulture, out alter);
        midiPitch = (octave + 1) * 12 + basePitch + alter;
        return midiPitch is >= 0 and <= 127;
    }

    private static int? ReadBpm(XDocument document)
    {
        foreach (var sound in document.Descendants().Where(e => e.Name.LocalName == "sound"))
        {
            if (double.TryParse((string?)sound.Attribute("tempo"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tempo))
                return (int)Math.Round(tempo, MidpointRounding.AwayFromZero);
        }
        foreach (var perMinute in document.Descendants().Where(e => e.Name.LocalName == "per-minute"))
        {
            if (double.TryParse(perMinute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var tempo))
                return (int)Math.Round(tempo, MidpointRounding.AwayFromZero);
        }
        return null;
    }

    private static string? FirstText(XDocument document, params string[] names) => names
        .Select(name => document.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim())
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? TextOf(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
}
