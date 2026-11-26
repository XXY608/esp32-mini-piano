using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace HarmonicaPlayer;

public static class LyricsBindingTests
{
    public static void Run(Action<bool, string> expect)
    {
        var notes = ScoreParser.Parse("1_ 2 3 4 |\n0 — 5 6", true);
        var binding = ScoreLyrics.Create(notes, new[] { new BoundLyric(1, 4, "第一句"), new BoundLyric(5, 7, "第二句") }, "手动校准");
        var fast = LyricsTimeline.Create("第一句\n第二句", notes, 500, bindingJson: binding.ToJson());
        var slow = LyricsTimeline.Create("第一句\n第二句", notes, 1000, bindingJson: binding.ToJson());
        expect(fast.ForNote(0) == null && fast.ForNote(1) == "第一句" && fast.ForNote(4) == "" && fast.ForNote(5) == "第二句", "歌词可在小节中间开始，并在间奏休止清空");
        expect(fast.TextAt(249) == null && fast.TextAt(250) == "第一句" && slow.TextAt(499) == null && slow.TextAt(500) == "第一句", "音符绑定歌词随 BPM 等比例变速");
        expect(ScoreLyrics.Parse(binding.ToJson(), ScoreParser.Parse("1_ 2 3 4 | 0 — 5 6", true)).Cues.Count == 2, "仅调整排版不破坏歌词绑定");
        var edited = ScoreParser.Parse("1 2 3 4 | 0 — 5 6", true);
        expect(!LyricsTimeline.Create("第一句", edited, 500, bindingJson: binding.ToJson()).HasLyrics, "修改时值后禁用旧绑定，避免静默错配");
        bool overlapRejected = false;
        try { ScoreLyrics.Create(notes, new[] { new BoundLyric(0, 3, "A"), new BoundLyric(2, 4, "B") }, "test"); }
        catch (FormatException) { overlapRejected = true; }
        expect(overlapRejected, "拒绝重叠歌词区间");
        bool wordsRejected = false;
        try
        {
            var invalid = binding with { Syllables = new[] { new BoundLyric(0, 2, "错位") } };
            invalid.Validate(notes);
        }
        catch (FormatException) { wordsRejected = true; }
        expect(wordsRejected, "拒绝超出所属句子范围的逐字锚点，避免显示错位");
        var spanNotes = ScoreParser.Parse("1 2 0 3 4 5", true);
        var explicitBinding = ScoreLyrics.FromSyllables(spanNotes, new[] {
            (0, new NoteLyric("风") { AlignmentIndex = 0, ExplicitSpan = true }),
            (1, new NoteLyric("风") { AlignmentIndex = 0, ExplicitSpan = true }),
            (4, new NoteLyric("来", true) { AlignmentIndex = 1, ExplicitSpan = true }) })!;
        expect(explicitBinding.Syllables.Count == 2 && explicitBinding.Syllables[0].EndNote == 2 &&
            explicitBinding.Syllables[1].StartNote == 4 && explicitBinding.Syllables[1].EndNote == 5,
            "显式逐字范围合并拖腔，间奏及尾奏不被吞进字音范围");
        var verified = explicitBinding with { Syllables = explicitBinding.Syllables.Select(w => w with
            { ReferenceScoreUrl = "https://example.invalid/reference", ReferenceScoreSha256 = new string('a', 64),
                AcousticDiagnostics = "vowel-pitch-mismatch" }).ToArray() };
        expect(ScoreLyrics.Parse(verified.ToJson(), spanNotes).Syllables.SequenceEqual(verified.Syllables),
            "带词谱核实来源、校验值和原唱诊断随逐字绑定保存重载");
        var positioned = explicitBinding with { Syllables = explicitBinding.Syllables.Select(w => w with
            { PositionEvidence = "{\"status\":\"corroborated-local-position\",\"anchorIndices\":[1,2,4,5],\"occupiedSingleUnitNotes\":{\"2\":2}}",
                AcousticDiagnostics = "vowel-pitch-mismatch" }).ToArray() };
        expect(ScoreLyrics.Parse(positioned.ToJson(), spanNotes).Syllables.SequenceEqual(positioned.Syllables),
            "局部字音定位的锚点、单格占用依据与原唱诊断随逐字绑定保存重载");
        var anchoredWords = Enumerable.Range(0, 8).Select(i => new BoundLyric(i, i + 1, "字" + i)
            { AlignmentIndex = i, PositionEvidence = i == 4 ? "{\"anchorIndices\":[1,2,5,6],\"occupiedSingleUnitNotes\":{\"2\":2}}" : "",
                AcousticDiagnostics = i == 4 ? "vowel-pitch-mismatch" : "" }).ToArray();
        expect(ScoreLyrics.InvalidateChangedPositionAnchors(anchoredWords, anchoredWords).SequenceEqual(anchoredWords),
            "未修改锚点时保留局部位置判断依据");
        var changedAnchor = anchoredWords.Select(w => w.AlignmentIndex == 2 ? w with { Text = "改" } : w).ToArray();
        var invalidated = ScoreLyrics.InvalidateChangedPositionAnchors(anchoredWords, changedAnchor)[4];
        expect(invalidated.PositionEvidence == "" && invalidated.NeedsReview && invalidated.AcousticDiagnostics == "vowel-pitch-mismatch",
            "修改单格占用锚点字时撤销依赖它的位置依据并恢复疑点，保留原唱诊断");
        var sixAnchors = anchoredWords.Select(w => w.AlignmentIndex == 4 ? w with
            { PositionEvidence = "{\"anchorIndices\":[0,1,2,5,6,7]}" } : w).ToArray();
        expect(ScoreLyrics.InvalidateChangedPositionAnchors(sixAnchors, sixAnchors).SequenceEqual(sixAnchors),
            "六锚点发音时序依据保持保存兼容");
        var changedOuterAnchor = sixAnchors.Select(w => w.AlignmentIndex == 7 ? w with { Text = "改" } : w).ToArray();
        expect(ScoreLyrics.InvalidateChangedPositionAnchors(sixAnchors, changedOuterAnchor)[4].NeedsReview,
            "修改六锚点模型的外侧锚点也撤销依赖依据");
        bool badReferenceRejected = false;
        try { (verified with { Syllables = new[] { verified.Syllables[0] with { ReferenceScoreSha256 = "bad" } } }).Validate(spanNotes); }
        catch (FormatException) { badReferenceRejected = true; }
        expect(badReferenceRejected, "拒绝无效的带词谱核实校验值");
        var longWord = ImportedScoreFactory.Create("long", 120, new[] {
            new TimedScoreEvent(0, 70, 60, false, 0) { Lyric = new NoteLyric("长", true) { AlignmentIndex = 0, ExplicitSpan = true } }
        });
        expect(longWord.Notes.Count == 2 && longWord.Lyrics!.Syllables.Single().EndNote == 2 && longWord.Notes.Sum(n => n.Beats) == 70,
            "显式逐字范围保留超过 64 拍音符的全部播放分片");
        string directory = Path.Combine(Path.GetTempPath(), "piano-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = ScoreLyrics.SidecarPath(Path.Combine(directory, "test.txt"));
            binding.Save(path, notes);
            var reopened = ScoreLyrics.Parse(File.ReadAllText(path, new UTF8Encoding(false, true)), notes);
            expect(reopened.Cues.SequenceEqual(binding.Cues) && Directory.GetFiles(directory).Length == 1, "绑定文件 UTF-8 保存重载，不留下临时副本");

            const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE score-partwise PUBLIC "test" "https://invalid.example/never-fetch.dtd">
            <score-partwise><work><work-title>带词测试</work-title></work><part-list/>
            <part id="accompaniment"><measure><attributes><divisions>1</divisions></attributes>
              <note><pitch><step>G</step><octave>3</octave></pitch><duration>4</duration></note>
            </measure></part>
            <part id="vocal"><measure implicit="yes"><attributes><divisions>1</divisions></attributes><direction><sound tempo="96"/></direction>
              <note><pitch><step>C</step><octave>4</octave></pitch><duration>1</duration></note>
              <note><pitch><step>D</step><octave>4</octave></pitch><duration>1</duration><lyric><text>春</text></lyric></note>
              <note><pitch><step>E</step><octave>4</octave></pitch><duration>1</duration><lyric><text>风</text><end-line/></lyric></note>
              <note><pitch><step>F</step><octave>4</octave></pitch><duration>1</duration><lyric><extend type="stop"/></lyric></note>
            </measure><measure implicit="yes"><note><rest/><duration>2</duration></note>
              <note><pitch><step>G</step><octave>4</octave></pitch><duration>1</duration><lyric><text>来</text></lyric></note>
            </measure></part></score-partwise>
            """;
            var score = MusicXmlImporter.Import(xml, "fallback");
            expect(score.Bpm == 96 && score.Notes[0].Degree == 2 && score.Lyrics?.Cues[0].Text == "春风", "MusicXML 选带词旋律，移除前奏，保留歌词");
            expect(score.Lyrics?.Cues[0].EndNote == 3 && score.Lyrics.Cues[1].StartNote == 4, "MusicXML 拖腔跨多个音符，间奏后歌词位置保留");
            expect(score.Lyrics!.Syllables.Count == 3 && score.Lyrics.Syllables[0].Text == "春" &&
                score.Lyrics.Syllables[1].StartNote == 1 && score.Lyrics.Syllables[1].EndNote == 3,
                "源谱保留独立逐字锚点，一字跨两个音符，不因整句显示丢失");
            var wordsReloaded = ScoreLyrics.Parse(score.Lyrics.ToJson(), score.Notes);
            expect(wordsReloaded.Syllables.SequenceEqual(score.Lyrics.Syllables), "逐字音符区间保存重载保持完整");
            var xmlTimeline = LyricsTimeline.Create("", ScoreParser.Parse(score.Text, true), 625, bindingJson: score.Lyrics!.ToJson());
            expect(xmlTimeline.ForNote(0) == "春风" && xmlTimeline.ForNote(3) == "" && xmlTimeline.ForNote(4) == "来", "转换后 TXT 重载仍保留词音关系");
            expect(xmlTimeline.HighlightForNote(0) == new LyricHighlight(0, 1) &&
                xmlTimeline.HighlightForNote(1) == new LyricHighlight(1, 1) &&
                xmlTimeline.HighlightForNote(2) == new LyricHighlight(1, 1) && xmlTimeline.HighlightForNote(3) == null,
                "逐字高亮跟随音符，拖腔保持同一字，休止不高亮");
            var wordBinding = ScoreLyrics.FromWordBindings(score.Notes, wordsReloaded.Syllables, "逐字校准");
            expect(wordBinding.Cues.Select(c => c.Text).SequenceEqual(new[] { "春风", "来" }) &&
                wordBinding.Syllables[1].EndNote == 3, "逐字校准重建显示句子并保留一字多音");
            string mxl = Path.Combine(directory, "score.mxl");
            using (var zip = ZipFile.Open(mxl, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("META-INF/container.xml").Open()))
                    writer.Write("<container><rootfiles><rootfile full-path=\"score.xml\"/></rootfiles></container>");
                using (var writer = new StreamWriter(zip.CreateEntry("score.xml").Open())) writer.Write(xml);
            }
            expect(MusicXmlImporter.ImportFile(mxl, "fallback").Lyrics?.Cues[0].Text == "春风", "压缩 MXL 一体导入曲谱与歌词");
            string verseNote = "<note><pitch><step>C</step><octave>4</octave></pitch><duration>1</duration><lyric number=\"1\"><text>甲</text><end-line/></lyric><lyric number=\"2\"><text>乙</text><end-line/></lyric></note>";
            var repeated = MusicXmlImporter.Import("<score-partwise><part><measure implicit=\"yes\"><barline><repeat direction=\"forward\"/></barline>" + verseNote + "<barline><repeat direction=\"backward\"/></barline></measure></part></score-partwise>", "repeat");
            expect(repeated.Notes.Count == 2 && repeated.Lyrics?.Cues.Select(c => c.Text).SequenceEqual(new[] { "甲", "乙" }) == true, "MusicXML 反复展开并选择对应段歌词");
            string Note(string step, string word) => "<note><pitch><step>" + step + "</step><octave>4</octave></pitch><duration>1</duration><lyric><text>" + word + "</text><end-line/></lyric></note>";
            var endings = MusicXmlImporter.Import("<score-partwise><part>" +
                "<measure implicit=\"yes\"><barline><repeat direction=\"forward\"/></barline>" + Note("C", "主") + "</measure>" +
                "<measure implicit=\"yes\"><barline><ending number=\"1\" type=\"start\"/></barline>" + Note("D", "一") + "<barline><ending number=\"1\" type=\"stop\"/><repeat direction=\"backward\"/></barline></measure>" +
                "<measure implicit=\"yes\"><barline><ending number=\"2\" type=\"start\"/></barline>" + Note("E", "二") + "<barline><ending number=\"2\" type=\"stop\"/></barline></measure>" +
                "</part></score-partwise>", "endings");
            expect(endings.Notes.Select(n => n.Degree).SequenceEqual(new[] { 1, 2, 1, 3 }) &&
                endings.Lyrics!.Cues.Select(c => c.Text).SequenceEqual(new[] { "主", "一", "主", "二" }), "MusicXML 一二房子按真实播放顺序保留词音位置");
            var quantized = ImportedScoreFactory.Create("量化", 120, new[]
            {
                new TimedScoreEvent(0, .2, 60),
                new TimedScoreEvent(.2, 1, 62) { Lyric = new NoteLyric("词") }
            });
            expect(quantized.Notes.Select(n => n.Degree).SequenceEqual(new[] { 1, 2 }) &&
                quantized.Lyrics?.Syllables.SingleOrDefault() is { Text: "词", StartNote: 1 } &&
                quantized.Warnings.Count > 0, "量化不足四分之一拍的音符时保留后继音和正确词音绑定");
            const string midi = """
            {"beats":4,"rightHandTrack":0,"measures":{"0":{"duration":1920}},"events":[
              {"tick":0,"duration":480,"track":0,"id":"intro","event":[144,72,80]},
              {"tick":480,"duration":480,"track":1,"id":"word-a","repeatIndex":0,"event":[144,60,80]},
              {"tick":960,"duration":480,"track":1,"id":"word-b","repeatIndex":0,"event":[144,62,80]},
              {"tick":1440,"duration":480,"track":1,"id":"word-a","repeatIndex":1,"event":[144,60,80]},
              {"tick":1920,"duration":480,"track":1,"id":"word-b","repeatIndex":1,"event":[144,62,80]}]}
            """;
            const string ccxml = """
            {"parts":[{"measures":[{"notes":[
              {"staff":1,"v":1,"elems":[{"id":"intro"}]},
              {"staff":1,"v":2,"elems":[{"id":"word-a"}],"lyrics":[{"num":0,"text":"春"},{"num":1,"text":"秋"}]},
              {"staff":1,"v":2,"elems":[{"id":"word-b"}],"lyrics":[{"num":0,"text":"风"},{"num":1,"text":"雨"}]}]}]}]}
            """;
            foreach (byte type in new byte[] { 1, 2 })
            {
                var imported = CcmzImporter.Import(BuildCcmz(type, midi, ccxml), "ccmz");
                expect(imported.Notes.Count == 4 && string.Concat(imported.Lyrics!.Cues.Select(c => c.Text)) == "春风秋雨", $"CCMZ 类型 {type} 按源音符标识绑定歌词，选带词声部，反复取对应段");
            }
            const string rolledMidi = """
            {"beats":4,"rightHandTrack":0,"measures":{"0":{"duration":1920}},"events":[
              {"tick":0,"duration":480,"track":0,"id":"a","event":[144,72,80]},
              {"tick":360,"duration":600,"track":0,"id":"lower","event":[144,74,80]},
              {"tick":480,"duration":480,"track":0,"id":"upper","event":[144,76,80]},
              {"tick":960,"duration":480,"track":0,"id":"c","event":[144,77,80]}]}
            """;
            const string rolledScore = """
            {"parts":[{"measures":[{"notes":[
              {"elems":[{"id":"a","step":1,"octave":5}],"lyrics":[{"text":"春"}]},
              {"arts":[{"type":"arpeggiate"}],"elems":[{"id":"lower","step":2,"octave":5},{"id":"upper","step":3,"octave":5}],"lyrics":[{"text":"风"}]},
              {"elems":[{"id":"c","step":4,"octave":5}],"lyrics":[{"text":"来"}]}]}]}]}
            """;
            var rolled = CcmzImporter.Import(BuildCcmz(1, rolledMidi, rolledScore), "rolled");
            expect(rolled.Notes.Select(n => n.Degree).SequenceEqual(new[] { 1, 3, 4 }) && rolled.Notes.Sum(n => n.Beats) == 3 &&
                string.Concat(rolled.Lyrics!.Cues.Select(c => c.Text)) == "春风来",
                "CCMZ 按明确琶音和弦保留上声部，不把提前奏响的下音混入旋律或重复歌词");
            var unmarked = CcmzImporter.Import(BuildCcmz(1, rolledMidi, rolledScore.Replace("arpeggiate", "other")), "unmarked");
            expect(unmarked.Notes.Select(n => n.Degree).SequenceEqual(new[] { 1, 2, 3, 4 }),
                "无明确琶音标注时不凭靠近的起点删除真实音符");
            var fallback = CcmzImporter.Import(BuildCcmz(1, rolledMidi, rolledScore.Replace("\"step\":3,\"octave\":5", "\"step\":3")), "incomplete");
            expect(fallback.Notes.Select(n => n.Degree).SequenceEqual(new[] { 1, 2, 3, 4 }),
                "琶音和弦音高信息不完整时保留原有导入行为");
            const string crossingMidi = """
            {"beats":4,"rightHandTrack":0,"measures":{"0":{"duration":1920}},"events":[
              {"tick":0,"duration":600,"track":0,"id":"a","event":[144,60,80]},
              {"tick":600,"duration":3120,"track":0,"id":"b","event":[144,62,80]},
              {"tick":3720,"duration":120,"track":0,"id":"c","event":[144,64,80]}]}
            """;
            const string crossingScore = """
            {"parts":[{"measures":[{"notes":[
              {"v":1,"elems":[{"id":"a"}],"lyrics":[{"text":"春","explicitSpan":true,"alignmentIndex":0}]},
              {"v":1,"elems":[{"id":"b"}],"lyrics":[{"text":"风","explicitSpan":true,"alignmentIndex":1}]},
              {"v":1,"elems":[{"id":"c"}],"lyrics":[{"text":"来","explicitSpan":true,"alignmentIndex":2,"endLine":true}]}]}]}]}
            """;
            var crossing = CcmzImporter.Import(BuildCcmz(1, crossingMidi, crossingScore), "crossing");
            expect(crossing.Notes.Select(n => n.Beats).SequenceEqual(new[] { 1.25, 2.75, 3.75, .25 }) &&
                crossing.Text.Split('\n').All(line => ScoreParser.Parse(line, true).Sum(n => n.Beats) == 4),
                "CCMZ 跨小节长音按小节分段，每行四拍且总时值不变");
            var crossingBinding = ScoreLyrics.Parse(crossing.Lyrics!.ToJson(), ScoreParser.Parse(crossing.Text, true));
            expect(crossingBinding.Syllables.Count == 3 && crossingBinding.Syllables[1].StartNote == 1 &&
                crossingBinding.Syllables[1].EndNote == 3 && crossingBinding.Syllables[2].StartNote == 3,
                "小节分段同步重映射字音范围与签名，跨线拖腔仍为同一字");
            var raw = LyricsTimeline.Create("[00:00.50]中间开始", ScoreParser.Parse("1 2 3 4", true), 500);
            expect(raw.ForNote(1) == "中间开始" && raw.ForNote(0) == null, "LRC 句首不再被强制吸附到 TXT 行首");
            expect(LyricsTimeline.CreateOptional("[99:00.00]超出谱长", notes, 500) == null,
                "全曲范围外的 LRC 不创建空歌词播放层");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static byte[] BuildCcmz(byte type, string midi, string score)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(type == 1 ? "data.ccmid" : "midi.json").Open())) writer.Write(midi);
            using (var writer = new StreamWriter(zip.CreateEntry(type == 1 ? "data.ccxml" : "score.json").Open())) writer.Write(score);
        }
        byte[] data = stream.ToArray();
        return new[] { type }.Concat(data.Select(b => type == 1 ? b : b % 2 == 0 ? (byte)(b + 1) : (byte)(b - 1))).ToArray();
    }
}
