using System.Text;
using HarmonicaPlayer;

try
{
int passed = 0;
void Expect(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL: " + name);
    passed++;
}
var notes = ScoreParser.Parse("6 5 4 【1 21】64 565 456 434 6 #6654 565 434");
Expect(notes.Count == 28, "示例音符总数");
Expect(notes[3].Octave == 1 && notes[5].Octave == 1 && notes[6].Octave == 0, "高音范围");
var sharp = ScoreParser.Parse("#6654");
Expect(sharp[0].Sharp && !sharp[1].Sharp, "#只影响一个音");
Expect(ScoreParser.Parse("（1）(2)【#6】0").Select(n => n.Octave).SequenceEqual(new[] {-1,-1,1,0}), "音区");
Expect(ScoreParser.Parse("565").Select(n=>n.Degree).SequenceEqual(ScoreParser.Parse("5 6 5").Select(n=>n.Degree)), "空格不改变音符");
foreach (string invalid in new[] {"", "【1", "1】", "（1)", "#", "##1", "#0", "8", "标题1", "【（1）】", "#【1】"})
{
    bool rejected = false;
    try { ScoreParser.Parse(invalid); } catch (FormatException) { rejected = true; }
    Expect(rejected, "拒绝非法谱: " + invalid);
}
int[] Pauses(string text) => ScoreParser.Parse(text).Select(n => n.PauseBefore(100, 300)).ToArray();
Expect(Pauses("12").SequenceEqual(new[] {0, 0}), "连写不加停顿");
Expect(Pauses("1 2").SequenceEqual(new[] {0, 100}), "单空格停顿");
Expect(Pauses("1   \t　2").SequenceEqual(new[] {0, 100}), "连续空白合并");
Expect(Pauses("1 \r\n 2").SequenceEqual(new[] {0, 300}), "CRLF与空格不叠加");
Expect(Pauses("1\n\n \n2").SequenceEqual(new[] {0, 300}), "连续空行合并");
Expect(Pauses(" \n1 \n").SequenceEqual(new[] {0}), "忽略首尾空白");
Expect(Pauses("【1 21】").SequenceEqual(new[] {0, 100, 0}), "括号内部空格");
Expect(Pauses("1 【2】 3").SequenceEqual(new[] {0, 100, 100}), "跨括号分组");
Expect(Pauses("1\n【 2】").SequenceEqual(new[] {0, 300}), "跨括号换行优先");
Expect(Pauses("1 # 2").SequenceEqual(new[] {0, 100}), "跨半音标记空格合并");
Expect(ScoreParser.Parse("1 2\n3").All(n => n.PauseBefore(0, 0) == 0), "关闭两种停顿");
Expect(Pauses("1 0\n2").SequenceEqual(new[] {0, 100, 300}), "显式休止保留分隔停顿");
var timeline = new List<int>(); int time = 0;
foreach (var n in ScoreParser.Parse("12 3\n4"))
{ time += n.PauseBefore(100, 300); timeline.Add(time); time += 300; }
Expect(timeline.SequenceEqual(new[] {0, 300, 700, 1300}) && time == 1600, "额外停顿计入绝对时间线");
var rhythm = ScoreParser.Parse("1 2_ 3__ 4. 5_. 6 — — 0_ 0 -", true);
Expect(rhythm.Select(n => n.Beats).SequenceEqual(new[] {1.0, .5, .25, 1.5, .75, 3.0, .5, 2.0}), "完整时值解析");
Expect(rhythm.Count == 8 && rhythm[5].Degree == 6, "延长不生成重复音");
Expect(rhythm[^1].Degree == 0 && rhythm[^1].Beats == 2, "休止延长");
Expect(ScoreParser.Parse("【#1_.】 （2）-", true).Select(n => n.Beats).SequenceEqual(new[] {.75, 2.0}), "音区半音与时值组合");
Expect(ScoreParser.Parse("1 2_ 3_ 5 — | 0 6. 5_ 1 |", true).Sum(n => n.Beats) * 500 == 4000, "120速度八拍为四秒");
var audioNotes = ScoreParser.Parse("1 #1 【2】 0 （7）", true);
Expect(Math.Abs(LocalAudioRenderer.FrequencyOf(audioNotes[0]) - 261.6255653) < 0.001, "本机发声中音1频率");
Expect(LocalAudioRenderer.FrequencyOf(audioNotes[1]) > LocalAudioRenderer.FrequencyOf(audioNotes[0]), "本机发声升半音频率");
Expect(LocalAudioRenderer.FrequencyOf(audioNotes[2]) > LocalAudioRenderer.FrequencyOf(audioNotes[1]), "本机发声高音频率");
string audioTestPath = Path.Combine(Path.GetTempPath(), "HarmonicaPlayer-audio-test-" + Guid.NewGuid().ToString("N") + ".wav");
string audioBpmPath = Path.Combine(Path.GetTempPath(), "HarmonicaPlayer-audio-bpm-test-" + Guid.NewGuid().ToString("N") + ".wav");
try
{
    var audioInfo = LocalAudioRenderer.RenderToWaveFile(audioTestPath, audioNotes, 120, 20);
    byte[] audioHeader = File.ReadAllBytes(audioTestPath).Take(12).ToArray();
    Expect(Encoding.ASCII.GetString(audioHeader, 0, 4) == "RIFF" &&
           Encoding.ASCII.GetString(audioHeader, 8, 4) == "WAVE", "本机发声生成标准 WAV 文件");
    Expect(audioInfo.SampleRate == LocalAudioRenderer.SampleRate && audioInfo.SampleCount > 0, "本机发声生成 PCM 样本");
    var slowerAudioInfo = LocalAudioRenderer.RenderToWaveFile(audioBpmPath, audioNotes, 60, 20);
    var instrumentPaths = new List<string>();
    var instrumentSignatures = new HashSet<string>(StringComparer.Ordinal);
    try
    {
        foreach (AudioInstrumentOption option in AudioInstrumentCatalog.WindowsOptions)
        {
            string instrumentPath = Path.Combine(Path.GetTempPath(),
                "HarmonicaPlayer-instrument-" + option.Value + "-" + Guid.NewGuid().ToString("N") + ".wav");
            instrumentPaths.Add(instrumentPath);
            LocalAudioRenderer.RenderToWaveFile(
                instrumentPath, new[] { audioNotes[0] }, 120, 20, instrument: option.Value);
            instrumentSignatures.Add(Convert.ToBase64String(File.ReadAllBytes(instrumentPath).Skip(44).Take(2048).ToArray()));
        }
        Expect(AudioInstrumentCatalog.WindowsOptions.Count >= 7 &&
               instrumentSignatures.Count == AudioInstrumentCatalog.WindowsOptions.Count,
            "多种本机音色生成不同的 PCM 波形");
    }
    finally
    {
        foreach (string instrumentPath in instrumentPaths)
            if (File.Exists(instrumentPath)) File.Delete(instrumentPath);
    }
    LocalAudioRenderer.RenderToWaveFile(audioTestPath, ScoreParser.Parse("1 0 #1 【1】 （1）", true), 120, 20, instrument: AudioInstrument.SalamanderPiano);
    byte[] sampledBytes = File.ReadAllBytes(audioTestPath);
    Expect(sampledBytes.Length == 44 + 5 * 22050 * 2, "采样音源保持原节拍总时长");
    short[] sampledPcm = Enumerable.Range(0, (sampledBytes.Length - 44) / 2)
        .Select(i => BitConverter.ToInt16(sampledBytes, 44 + i * 2)).ToArray();
    Expect(sampledPcm.Any(value => Math.Abs((int)value) > 100), "Salamander采样实际发声");
    Expect(sampledPcm.Count(value => Math.Abs((int)value) >= 32760) < sampledPcm.Length / 1000,
        "Salamander输出无持续削波");
    foreach (string ending in new[] { "1 0__", "1" })
    {
        var endingNotes = ScoreParser.Parse(ending, true);
        var endingInfo = LocalAudioRenderer.RenderToWaveFile(audioTestPath, endingNotes, 300, 10,
            instrument: AudioInstrument.Organ);
        byte[] endingWave = File.ReadAllBytes(audioTestPath);
        Expect(BitConverter.ToInt16(endingWave, endingWave.Length - 2) == 0,
            "采样音源的末音和末休止均平滑归零: " + ending);
        Expect(endingInfo.SampleCount == (long)Math.Round(endingNotes.Sum(note => note.Beats) * 0.2 * 44100),
            "结尾淡出不改变节拍总长度: " + ending);
    }
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel(); bool cancelledRender = false;
        try { LocalAudioRenderer.RenderToWaveFile(audioBpmPath, audioNotes, 120, 20, cancelled.Token, AudioInstrument.Piano); }
        catch (OperationCanceledException) { cancelledRender = true; }
        Expect(cancelledRender, "社区采样播放可取消");
    }
    Expect(Math.Abs(slowerAudioInfo.DurationSeconds - audioInfo.DurationSeconds * 2) < 0.01,
        "本机发声 BPM 与总时长同步");
}
finally
{
    if (File.Exists(audioTestPath)) File.Delete(audioTestPath);
    if (File.Exists(audioBpmPath)) File.Delete(audioBpmPath);
}
var lyricNotes = ScoreParser.Parse("1 0\n2\n3", true);
var timedLyricText = "[00:00.00]第一行歌词\n[00:01.50]第二行歌词";
var lyricTimeline = LyricsTimeline.Create(timedLyricText, lyricNotes, 500);
Expect(lyricTimeline.IsTimed && lyricTimeline.TokenCount == 2 && lyricTimeline.MappedCount == 2, "读取带时间轴的整行歌词");
Expect(lyricTimeline.TimedCues[0].Text == "第一行歌词" && lyricTimeline.TimedCues[1].StartMilliseconds == 1500,
    "歌词时间点按原始 LRC 保留");
Expect(lyricTimeline.ForNote(0) == "第一行歌词" && lyricTimeline.ForNote(1) == null &&
       lyricTimeline.ForNote(2) == null && lyricTimeline.ForNote(3) == "第二行歌词",
    "歌词按简谱分行锚定到对应旋律起点");
Expect(lyricTimeline.NextAfterNote(0) == "第二行歌词" && lyricTimeline.NextAfterNote(3) == null,
    "双行歌词显示自动跳过无歌词的过渡简谱行");
var driftTimeline = LyricsTimeline.Create(
    "[00:00.00]A\n[00:01.00]B\n[00:07.00]C",
    ScoreParser.Parse("1 2\n3 4\n5 6\n7 1\n2 3\n4 5", true), 500);
Expect(!driftTimeline.Alignment.Applied && driftTimeline.MappedCount == 2,
    "原唱 LRC 不自动认定为已校准，超出曲谱的歌词不强塞到结尾");
Expect(driftTimeline.ForNote(0) == "A" && driftTimeline.ForNote(2) == "B" &&
       driftTimeline.ForNote(10) == null,
    "LRC 按音符时间匹配，超出谱长的句子保留为未对应");
var instrumentalGapTimeline = LyricsTimeline.Create(
    "[00:00.00]第一句\n[00:02.00]第二句\n[00:20.00]第三句",
    ScoreParser.Parse(string.Join(' ', Enumerable.Repeat("1", 48)), true), 500);
Expect(instrumentalGapTimeline.TextAt(1000) == "第一句" &&
       instrumentalGapTimeline.TextAt(4000) == "第二句" &&
       instrumentalGapTimeline.TextAt(6000) == "第二句" &&
       instrumentalGapTimeline.TextAt(20000) == "第三句",
    "未到下一句歌词时保持前一句显示");
var mismatchedTimeline = LyricsTimeline.Create(
    "[00:00.00]A\n[00:20.00]B",
    ScoreParser.Parse("1\n2", true), 500);
Expect(!mismatchedTimeline.Alignment.Applied && mismatchedTimeline.MappedCount == 1,
    "版本不匹配的 LRC 不伪报自动校准成功");
Expect(!LyricsTimeline.Create("第一行歌词\n第二行歌词", lyricNotes, 500).HasLyrics,
    "没有时间轴时不自动猜测歌词对应旋律");
Expect(LyricsTimeline.CreateOptional("第一行歌词\n第二行歌词", lyricNotes, 500) == null,
    "没有同步歌词时仍可创建可播放的无歌词流程");
Expect(LyricsTimeline.CreateOptional(timedLyricText, lyricNotes, 500)?.HasLyrics == true,
    "有同步歌词时保留歌词显示流程");
Expect(LyricsTimeline.Tokenize("春\n日 | 影").SequenceEqual(new[] { "春", "日", "影" }), "歌词空格换行和小节分隔");
Expect(LyricsTimeline.Tokenize("总是向你索取\n却不曾说谢谢你").SequenceEqual(
    new[] { "总是向你索取", "却不曾说谢谢你" }), "中文歌词保留整行显示");
Expect(LyricsTimeline.Tokenize("Hello world\n(Hmm-hmm)").SequenceEqual(
    new[] { "Hello world", "(Hmm-hmm)" }), "英文歌词保留整行显示");
Expect(LyricsTimeline.Tokenize("[ti:测试]\n[ar:歌手]\n[00:01.20]春\n[00:02.30]风").SequenceEqual(new[] { "春", "风" }), "清洗 LRC 时间和元数据标签");
var normalizedLyrics = LyricsTimeline.NormalizeText("""
    [ti:繁體中文]
    [00:52.50]總是向你索取
    01. 學習 Hello
    00:03.20 - français
    日本語 かな 한국어
    """);
Expect(normalizedLyrics == "总是向你索取\n学习 Hello\nfrançais\n日本語 かな 한국어" &&
       !normalizedLyrics.Contains("00:52.50") && !normalizedLyrics.Contains("01."), "歌词去除时间数字并转换繁体中文");
new LyricsLayout().Validate();
var coloredLayout = new LyricsLayout { CurrentColor = "#ff8899", NextColor = "#001122" }.Normalize();
Expect(coloredLayout.CurrentColor == "#FF8899" && coloredLayout.NextColor == "#001122", "歌词双行颜色规范化保存");
bool invalidLyricsLayout = false;
try { new LyricsLayout { Left = 0.9, Width = 0.5 }.Validate(); } catch (FormatException) { invalidLyricsLayout = true; }
Expect(invalidLyricsLayout, "拒绝越出屏幕的歌词布局");
bool invalidLyricsColor = false;
try { new LyricsLayout { CurrentColor = "red" }.Validate(); } catch (FormatException) { invalidLyricsColor = true; }
Expect(invalidLyricsColor, "拒绝无效的歌词颜色");
Expect(BpmFileName.TryRead("老男孩_源谱 BPM：57.txt", out var bpm57, out _) && bpm57 == 57, "读取中文冒号 BPM");
Expect(BpmFileName.TryRead("红昭愿_源谱BPM115.txt", out var bpm115, out _) && bpm115 == 115, "读取无空格 BPM");
Expect(BpmFileName.TryRead("恋爱告急_源谱 BPM:126.txt", out var bpm126, out _) && bpm126 == 126, "读取英文冒号 BPM");
Expect(!BpmFileName.TryRead("没有速度标记.txt", out _, out _), "无 BPM 文件名不误读");
Expect(!BpmFileName.TryRead("速度BPM350.txt", out _, out _), "拒绝超范围 BPM");
var searchHtml = "<a data-id='974936' title=\"Flower Dance&amp;钢琴谱\" href='/cchtml/974936.htm'>C调 Flower Dance</a>" +
                 "<a href=\"https://www.gangqinpu.com/cchtml/974936.htm\" title=\"重复\">重复</a>";
var onlineScores = OnlineScoreClient.ParseSearchResults(searchHtml);
Expect(onlineScores.Count == 1 && onlineScores[0].Title == "C调 Flower Dance", "解析在线曲谱搜索结果并去重");
Expect(onlineScores[0].Url == "https://www.gangqinpu.com/cchtml/974936.htm", "规范化在线曲谱相对地址");
Expect(OnlineScoreClient.TryFindCcmzUrl("<script>bad='https://evil.example/ccmz/no.ccmz'; good='https://s201.lzjoy.com/res/statics/fileupload/ccmz/202212/97/974936-638bdd4c7290a.ccmz'</script>")?.EndsWith("974936-638bdd4c7290a.ccmz") == true, "校验并解析公开 CCMZ 地址");
Expect(OnlineScoreClient.TryFindCcmzUrl("https://example.com/ccmz/no.ccmz") == null, "拒绝非公开 CCMZ 地址");
Expect(OnlineScoreClient.TryFindCcmzUrl("http://s201.lzjoy.com/ccmz/no.ccmz") == null, "拒绝非 HTTPS CCMZ 地址");
Expect(OnlineScoreClient.SafeFileName("Flower Dance:/测试") == "Flower Dance__测试", "清理 CCMZ 文件名");
var syncedLyrics = OnlineScoreClient.ParseLyricsSearchResults("""[{"trackName":"Imagine","artistName":"John Lennon","syncedLyrics":"[00:01.00]Imagine","plainLyrics":"Imagine"}]""");
Expect(syncedLyrics.Count == 1 && syncedLyrics[0].IsSynced && syncedLyrics[0].TrackName == "Imagine", "解析 LRCLIB 同步歌词并优先使用时间轴");
var plainLyrics = OnlineScoreClient.ParseLyricsSearchResults("""[{"trackName":"Yesterday","plainLyrics":"Yesterday"}]""");
Expect(plainLyrics.Count == 1 && !plainLyrics[0].IsSynced && plainLyrics[0].Text == "Yesterday", "解析 LRCLIB 普通歌词回退");
Expect(OnlineScoreClient.LyricsFilePath(@"C:\music\song.ccmz").EndsWith("song.lyrics.txt", StringComparison.OrdinalIgnoreCase), "生成 CCMZ 伴随歌词文件名");

Expect(BpmFileName.ReadTitle("\u7236\u4eb2_\u6e90\u8c31 BPM\uFF1A72.txt") == "\u7236\u4eb2", "Read title without the source BPM suffix");
Expect(BpmFileName.ReadTitle("Flower Dance.mid") == "Flower Dance", "Read title from a file name without BPM");
Expect(BpmFileName.ReadTitle("父亲_源谱 BPM：72 (13).txt") == "父亲", "搜词曲名去除源 BPM 与历史副本编号");
Expect(BpmFileName.ReadTitle("Father (Live).mid") == "Father (Live)", "曲名中真实版本说明不会被文件名提取误删");

byte[] BuildCcmz(byte type, string midiName, string midiJson, string scoreName, string scoreJson)
{
    using var ccmzStream = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(ccmzStream, System.IO.Compression.ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(zip.CreateEntry(midiName).Open(), new UTF8Encoding(false))) writer.Write(midiJson);
        using (var writer = new StreamWriter(zip.CreateEntry(scoreName).Open(), new UTF8Encoding(false))) writer.Write(scoreJson);
    }
    byte[] zipBytes = ccmzStream.ToArray();
    byte[] encoded = new byte[zipBytes.Length + 1]; encoded[0] = 2;
    encoded[0] = type;
    for (int i = 0; i < zipBytes.Length; i++)
        encoded[i + 1] = type == 1 ? zipBytes[i] : zipBytes[i] % 2 == 0 ? (byte)(zipBytes[i] + 1) : (byte)(zipBytes[i] - 1);
    return encoded;
}

void ExpectFormat(Func<object?> action, string name)
{
    bool rejected = false;
    try { action(); } catch (FormatException) { rejected = true; }
    Expect(rejected, name);
}
ExpectFormat(() => OnlineScoreClient.ParseLyricsSearchResults("{bad"), "拒绝损坏 LRCLIB JSON");

const string ccmzMelodyMidi = "{\"beats\":4,\"rightHandTrack\":0,\"measures\":{\"0\":{\"duration\":1920}},\"events\":[{\"tick\":0,\"duration\":240,\"track\":0,\"event\":[144,72,80]},{\"tick\":0,\"duration\":480,\"track\":0,\"event\":[144,64,80]},{\"tick\":480,\"duration\":240,\"track\":0,\"event\":[144,74,80]},{\"tick\":720,\"duration\":480,\"track\":0,\"event\":[144,76,80]},{\"tick\":960,\"duration\":960,\"track\":0,\"event\":[144,77,80]}]}";
var arrangedCcmz = CcmzImporter.Import(BuildCcmz(2, "midi.json", ccmzMelodyMidi, "score.json", "{}"), "arranged");
const string tripletMidi = "{\"beats\":4,\"rightHandTrack\":0,\"measures\":{\"0\":{\"duration\":1920}},\"events\":[{\"tick\":0,\"duration\":72,\"track\":0,\"event\":[144,71,80]},{\"tick\":80,\"duration\":72,\"track\":0,\"event\":[144,78,80]},{\"tick\":160,\"duration\":72,\"track\":0,\"event\":[144,79,80]},{\"tick\":240,\"duration\":480,\"track\":0,\"event\":[144,76,80]}]}";
var exactTriplet = CcmzImporter.Import(BuildCcmz(2, "midi.json", tripletMidi, "score.json", "{}"), "triplet", preserveTiming: true);
Expect(exactTriplet.Notes.Count == 4 && exactTriplet.Notes.All(n => n.Degree != 0) &&
    exactTriplet.Notes.Take(3).All(n => Math.Abs(n.Beats - 1d / 6) < 0.00001), "CCMZ 精确路径保留三连音和起音间隔");
var tripletTiming = ScoreTiming.Create(exactTriplet.Text, exactTriplet.Notes).ToJson();
Expect(ScoreTiming.Apply(exactTriplet.Text, tripletTiming).Take(3).All(n => Math.Abs(n.Beats - 1d / 6) < 0.00001), "精确时值往返保留三连音");
Expect(PracticeScoreDocument.Create(exactTriplet.Text, tripletTiming).Notes.Take(3).All(n => Math.Abs(n.Beats - 1d / 6) < 0.00001), "练习模式使用精确时值");
ExpectFormat(() => ScoreTiming.Apply(exactTriplet.Text + " ", tripletTiming), "修改谱面后拒绝旧时值");
const string timingFixture = "{\"beats\":4,\"rightHandTrack\":0,\"measures\":{\"0\":{\"duration\":1920}},\"tempos\":[{\"tempo\":750000,\"tick\":0}],\"events\":[{\"tick\":0,\"duration\":480,\"track\":0,\"event\":[144,60,80]},{\"tick\":0,\"duration\":480,\"track\":0,\"event\":[144,64,80]},{\"tick\":960,\"duration\":480,\"track\":0,\"event\":[144,67,80]}]}";
var timedFixture = CcmzImporter.Import(BuildCcmz(2, "midi.json", timingFixture, "score.json", "{}"), "timing", preserveTiming: true);
Expect(timedFixture.Bpm == 80 && timedFixture.Notes.Count == 3 && timedFixture.Notes[0].Degree == 3 &&
    timedFixture.Notes[1].Degree == 0 && Math.Abs(timedFixture.Notes[1].Beats - 1) < 1e-8 && timedFixture.Notes[2].Degree == 5,
    "CCMZ 不同速度下同时音选高音并保留一拍休止");
ExpectFormat(() => ScoreTiming.Apply(exactTriplet.Text, "{broken"), "拒绝损坏精确时值文件");
Expect(arrangedCcmz.Notes.Select(note => note.Degree).SequenceEqual(new[] { 1, 0, 2, 3, 4 }) &&
    arrangedCcmz.Notes.All(note => note.Octave == 0), "CCMZ 提取单音并保留半拍换气");
var arrangedNotes = ScoreParser.Parse(arrangedCcmz.Text, true);
Expect(arrangedCcmz.Text.EndsWith("|") && arrangedNotes.Count == 5 && arrangedNotes[1].Beats == .5 &&
    arrangedNotes[3].Beats == .5, "CCMZ 按原起音截断重叠旋律并保留小节线");
const string practiceMelodyMidi = """
    {"events":[
      {"tick":0,"duration":432,"event":[144,67,90],"id":"long","elem_ids":["long","continuation"]},
      {"tick":60,"duration":48,"event":[144,69,70],"id":"grace"},
      {"tick":240,"duration":96,"event":[144,60,60],"id":"inner"},
      {"tick":480,"duration":108,"event":[144,67,90],"id":"short"},
      {"tick":560,"duration":216,"event":[144,69,90],"id":"next","meas_start_tick":0},
      {"tick":960,"duration":480,"event":[144,67,90],"id":"last"}
    ]}
    """;
const string practiceMelodyScore = """
    {"parts":[{"measures":[{"notes":[
      {"staff":1,"v":1,"type":8,"elems":[{"id":"long","step":5,"octave":4}]},
      {"staff":1,"v":1,"type":8,"elems":[{"id":"continuation","step":5,"octave":4}]},
      {"staff":1,"v":1,"type":16,"grace":true,"elems":[{"id":"grace"}]},
      {"staff":1,"v":2,"type":16,"elems":[{"id":"inner"}]},
      {"staff":1,"v":1,"type":8,"ticks":120,"elems":[{"id":"short"}]},
      {"staff":1,"v":1,"type":8,"tick":600,"arts":[{"type":"arpeggiate"}],"elems":[{"id":"next"}]},
      {"staff":1,"v":1,"type":4,"elems":[{"id":"last"}]}
    ]}]}]}
    """;
var study = CcmzImporter.Import(BuildCcmz(2, "midi.json", practiceMelodyMidi, "score.json", practiceMelodyScore),
    "主旋律", preserveTiming: true, includePerformance: true);
Expect(study.Notes.Select(n => (n.Degree, n.Beats)).SequenceEqual(new[]
    { (5, 1d), (5, .25), (6, .5), (0, .25), (5, 1d) }),
    "CCMZ 按上方主声部、谱面时值和琶音拍点，保留有意的重弹、短音与休止");
Expect(study.Performance is { Count: 6 } && Math.Abs(study.Performance[0].DurationBeats - .9) < 1e-8 &&
    study.Performance[2].MidiPitch == 60 && Math.Abs(study.Performance[4].StartBeats - 560d / 480) < 1e-8,
    "CCMZ 简化不修改原始多音音符、拍点或时值");
var overlappingMelody = ImportedScoreFactory.Create("无声部标签", 120, new[]
{
    new TimedScoreEvent(0, 2, 67), new TimedScoreEvent(0, .25, 67),
    new TimedScoreEvent(.5, .125, 67), new TimedScoreEvent(.75, .25, 60),
    new TimedScoreEvent(1.5, .25, 65) { Lyric = new NoteLyric("唱", false) },
    new TimedScoreEvent(2, .125, 65), new TimedScoreEvent(2.125, .375, 67)
}, preserveTiming: true);
Expect(overlappingMelody.Notes.Select(n => (n.Degree, n.Beats)).SequenceEqual(new[]
    { (5, 1.5), (4, .25), (0, .25), (4, .125), (5, .375) }) &&
    overlappingMelody.Warnings.Any(w => w.Contains("下方短音")),
    "无声部标签时只抑制旋律长音内部的短重叠，保留歌词音、独立同音与原起音");
var arrangedText = ScoreTextFormatter.FormatWithMeasures(new[]
{
    new ScoreNote(1, 0, false, 0), new ScoreNote(2, 0, false, 1), new ScoreNote(3, 0, false, 2), new ScoreNote(4, 0, false, 3),
    new ScoreNote(5, 0, false, 4), new ScoreNote(6, 0, false, 5), new ScoreNote(7, 0, false, 6), new ScoreNote(1, 1, false, 7)
}, 4);
var formattedNotes = ScoreParser.Parse(arrangedText, true);
Expect(arrangedText.Contains(Environment.NewLine) && formattedNotes.Count == 8 && formattedNotes[4].SeparatorBefore == ScoreSeparator.LineBreak, "CCMZ measure formatting places each measure on a new line");
var crossedMeasureText = ScoreTextFormatter.FormatWithMeasures(new[]
{
    new ScoreNote(1, 0, false, 0, Beats: 3), new ScoreNote(2, 0, false, 1, Beats: 2), new ScoreNote(3, 0, false, 2)
}, 4);
var crossedMeasureNotes = ScoreParser.Parse(crossedMeasureText, true);
Expect(crossedMeasureText.Contains(Environment.NewLine) && crossedMeasureNotes.Count == 3 && crossedMeasureNotes[2].SeparatorBefore == ScoreSeparator.LineBreak, "CCMZ measure formatting wraps after a note crosses the measure boundary");

const string ccmzMidi = "{\"beats\":4,\"rightHandTrack\":0,\"tempos\":[{\"tempo\":500000,\"tick\":0}],\"measures\":{\"0\":{\"duration\":1920}},\"events\":[{\"tick\":0,\"duration\":480,\"track\":0,\"event\":[144,60,80]},{\"tick\":480,\"duration\":240,\"track\":0,\"event\":[144,62,80]},{\"tick\":0,\"duration\":480,\"track\":1,\"event\":[144,40,80]},{\"tick\":720,\"duration\":480,\"track\":0,\"event\":[153,70,80]},{\"tick\":720,\"duration\":480,\"track\":0,\"event\":[144,65,0]}]}";
var imported = CcmzImporter.Import(BuildCcmz(2, "midi.json", ccmzMidi, "score.json", "{\"title\":{\"title\":\"在线测试曲谱\"}}"), "fallback");
Expect(imported.Title == "在线测试曲谱" && imported.Bpm == 120 && imported.PlaybackBpm == 120 && imported.Text == "1 2_", "导入 CCMZ 主旋律并读取 BPM");
var importedType1 = CcmzImporter.Import(BuildCcmz(1, "data.ccmid", ccmzMidi, "data.ccxml", "{\"title\":\"类型一曲谱\"}"), "fallback");
Expect(importedType1.Title == "类型一曲谱" && importedType1.Notes.Count == 2, "导入 CCMZ 类型一");
string ccmzTempoChange = ccmzMidi.Replace("{\"tempo\":500000,\"tick\":0}",
    "{\"tempo\":500000,\"tick\":0},{\"tempo\":1000000,\"tick\":240}");
foreach (byte type in new byte[] { 1, 2 })
{
    var fullCcmz = CcmzImporter.Import(BuildCcmz(type, type == 1 ? "data.ccmid" : "midi.json", ccmzTempoChange,
        type == 1 ? "data.ccxml" : "score.json", "{}"), "双手演奏", preserveTiming: true, includePerformance: true);
    Expect(fullCcmz.Performance is { Count: 3 } && fullCcmz.Performance.Count(n => n.StartBeats == 0) == 2 &&
        fullCcmz.Performance.Any(n => n.MidiPitch == 40 && n.DurationBeats == 1.5 && n.Velocity == 80) &&
        fullCcmz.Performance.Any(n => n.MidiPitch == 62 && n.StartBeats == 1.5), $"CCMZ 类型 {type} 保留双手、重叠、力度和变速");
}
ExpectFormat(() => CcmzImporter.Import(new byte[] { 3, 1, 2 }, "fallback"), "拒绝未知 CCMZ 类型");
ExpectFormat(() => CcmzImporter.Import(BuildCcmz(2, "midi.json", "{bad", "score.json", "{}"), "fallback"), "拒绝损坏 CCMZ JSON");
var clamped = ImportedScoreFactory.Create("速度测试", 600, new[] { new TimedScoreEvent(0, 1, 60) });
Expect(clamped.Bpm == 600 && clamped.PlaybackBpm == 300 && clamped.Warnings.Any(w => w.Contains("播放器播放时使用")), "保留源 BPM 并单独限制播放 BPM");
var conversionWarnings = ImportedScoreFactory.Create("警告测试", null, new[]
{
    new TimedScoreEvent(0, .333, 60), new TimedScoreEvent(0, .333, 64)
});
Expect(conversionWarnings.Warnings.Any(w => w.Contains("和弦")) && conversionWarnings.Warnings.Any(w => w.Contains("量化")), "报告和弦与时值量化");
foreach (string invalid in new[] {"-1", "_1", "1___", "1..", "1-_", "1._", "1 | -", "#-1", "1-.", "0" + new string('-', 64)})
{
    bool rejected = false;
    try { ScoreParser.Parse(invalid, true); } catch (FormatException) { rejected = true; }
    Expect(rejected, "拒绝非法时值: " + invalid);
}
bool legacyRejects = false;
try { ScoreParser.Parse("1-", false); } catch (FormatException) { legacyRejects = true; }
Expect(legacyRejects, "旧模式不静默忽略节奏标记");

var practiceDocument = PracticeScoreDocument.Create("1 2 3\n4 5 6\n【1】 #2");
Expect(practiceDocument.Lines.Count == 3 && practiceDocument.Lines[1].StartNoteIndex == 3 &&
       practiceDocument.Lines[2].Notes[0].Octave == 1 && practiceDocument.Lines[2].Notes[1].Sharp,
    "练习模式按换行构建三行曲谱");
Expect(practiceDocument.Lines[1].Notes[0].Position != practiceDocument.Lines[1].StartNoteIndex &&
       practiceDocument.Lines[1].IsCurrentNote(0, practiceDocument.Lines[1].StartNoteIndex) &&
       !practiceDocument.Lines[1].IsCurrentNote(0, practiceDocument.Lines[1].StartNoteIndex + 1),
    "练习模式高亮使用全局音符索引而不是字符位置");
var wrappedPractice = PracticeScoreDocument.Create(string.Join(' ', Enumerable.Repeat("1", 17)));
Expect(wrappedPractice.Lines.Count == 2 && wrappedPractice.Lines[0].Notes.Count == PracticeScoreDocument.MaxNotesPerLine,
    "练习模式长行自动分段");
var measuredPractice = PracticeScoreDocument.Create("1 2 | 3 4");
Expect(measuredPractice.Lines.Count == 2 && measuredPractice.Lines[0].MeasureBreakAfter && measuredPractice.Lines[0].Text.EndsWith("|"),
    "练习模式保留小节分隔");
Expect(PracticeInputMapper.TryMapVirtualKey(0x5A, false, false, false, out var middleOne) &&
       middleOne.Degree == 1 && middleOne.Octave == 0 && !middleOne.Sharp,
    "练习模式映射 Z 为中音1");
Expect(PracticeInputMapper.TryMapVirtualKey(0xBC, false, false, false, out var highOne) &&
       highOne.Degree == 1 && highOne.Octave == 1,
    "练习模式映射高音1");
Expect(PracticeInputMapper.TryMapSimpleVirtualKey(0x5A, out int simpleOne) && simpleOne == 1 &&
       PracticeInputMapper.TryMapSimpleVirtualKey(0x4D, out int simpleSeven) && simpleSeven == 7 &&
       !PracticeInputMapper.TryMapSimpleVirtualKey(0xBC, out _),
    "简单演奏模式只映射 Z 到 M 的自然音级");
Expect(PracticeTiming.NoteDurationMilliseconds(new ScoreNote(1, 0, false, 0, Beats: 0.5), 75) == 400 &&
       PracticeTiming.NoteDurationMilliseconds(new ScoreNote(1, 0, false, 0, Beats: 1), 75) == 800 &&
       PracticeTiming.NoteDurationMilliseconds(new ScoreNote(1, 0, false, 0, Beats: 2), 75) == 1600,
    "练习模式按半拍一拍两拍计算不同音长");
Expect(PracticePerformanceModes.Default == PracticePerformanceMode.Simple &&
       PracticePerformanceModes.Options.Count == 3 &&
       PracticePerformanceModes.OptionOf(PracticePerformanceMode.Simple).Name == "简单练习" &&
       PracticePerformanceModes.OptionOf(PracticePerformanceMode.Rhythm).Name == "节奏练习" &&
       PracticePerformanceModes.OptionOf(PracticePerformanceMode.Normal).Name == "正常演奏模式",
    "演奏选择保留正常演奏并提供简单练习和节奏练习，默认简单练习");
Expect(PracticeInputMapper.TryMapVirtualKey(0x58, true, false, false, out var lowTwo) &&
       lowTwo.Degree == 2 && lowTwo.Octave == -1 &&
       PracticeInputMapper.TryMapVirtualKey(0x58, false, true, true, out var sharpHighTwo) &&
       sharpHighTwo.Degree == 2 && sharpHighTwo.Octave == 1 && sharpHighTwo.Sharp &&
       !PracticeInputMapper.TryMapVirtualKey(0x58, true, true, false, out _),
    "练习模式映射键盘低音高音和升半音修饰");
Expect(PracticeInputMapper.DisplayKey(new ScoreNote(2, 0, false, 0)) == "X" &&
       PracticeInputMapper.DisplayKey(new ScoreNote(1, 1, true, 0)) == "Alt + ," &&
       PracticeInputMapper.DisplayKey(new ScoreNote(2, -1, false, 0)) == "Ctrl + X" &&
       PracticeInputMapper.DisplayKey(new ScoreNote(2, 1, true, 0)) == "Shift + Alt + X",
    "练习模式显示准确的按键提示");
var practiceProgress = new PracticeProgress(practiceDocument);
Expect(practiceProgress.CurrentLineIndex == 0 && practiceProgress.Expected?.Degree == 1,
    "练习模式从第一行第一音开始");
Expect(practiceProgress.Submit(new ScoreNote(2, 0, false, 0)) == PracticeAdvanceResult.Wrong &&
       practiceProgress.CurrentNoteIndex == 0 && practiceProgress.WrongCount == 1,
    "练习模式错误音符不推进");
Expect(practiceProgress.Submit(new ScoreNote(1, 0, false, 0)) == PracticeAdvanceResult.Correct &&
       practiceProgress.CurrentNoteIndex == 1 && practiceProgress.CorrectCount == 1,
    "练习模式正确音符推进游标");
Expect(practiceProgress.SkipRest() == 0 && practiceProgress.CurrentLineIndex == 0,
    "练习模式非休止音不能被空格跳过");
var simplePitchProgress = new PracticeProgress(PracticeScoreDocument.Create("【1】 #2"));
Expect(simplePitchProgress.SubmitDegree(1) == PracticeAdvanceResult.Correct &&
       simplePitchProgress.Expected?.Degree == 2 &&
       simplePitchProgress.SubmitDegree(2) == PracticeAdvanceResult.Complete,
    "简单演奏模式按音级匹配并自动通过高音和升半音目标");
var restProgress = new PracticeProgress(PracticeScoreDocument.Create("0 1"));
Expect(restProgress.SkipRest() == 1 && restProgress.Expected?.Degree == 1 && restProgress.CurrentNoteIndex == 1,
    "练习模式空格跳过休止符");
var timedRestProgress = new PracticeProgress(PracticeScoreDocument.Create("0 0_ 1"));
Expect(timedRestProgress.SkipNextRest() && timedRestProgress.CurrentNoteIndex == 1 && timedRestProgress.Expected?.Degree == 0 &&
       timedRestProgress.SkipNextRest() && timedRestProgress.Expected?.Degree == 1,
    "练习模式自动休止逐个按时值推进");
Console.WriteLine($"PASS TOTAL: {passed} tests including practice model");
Console.WriteLine($"PASS: {passed} tests");

void RejectHotkey(Action action, string name)
{
    bool rejected = false;
    try { action(); } catch (FormatException) { rejected = true; }
    Expect(rejected, name);
}
HotkeyBinding.ValidatePair(new(0x75, 2), new(0x77));
Expect(new HotkeyBinding(0x75, 7).Label == "Ctrl + Alt + Shift + F6", "组合键显示及MOD位对应");
RejectHotkey(() => HotkeyBinding.ValidatePair(new(0x75), new(0x75)), "拒绝重复开始停止键");
foreach (var binding in new[] {new HotkeyBinding(0x7B), new HotkeyBinding(0x75, 8),
    new HotkeyBinding(0x5A, 2), new HotkeyBinding(0x41), new HotkeyBinding(0x73, 1),
    new HotkeyBinding(0x2E, 3), new HotkeyBinding(0), new HotkeyBinding(0x11),
    new HotkeyBinding(0x1B, 2), new HotkeyBinding(0x20, 1)})
    RejectHotkey(() => HotkeyBinding.ValidatePair(binding, new(0x77)), "拒绝无效/保留键 " + binding.Label);
Expect(new HotkeyBinding(0x41, 2).Error() == null && new HotkeyBinding(0x24).Error() == null, "接受Ctrl+A和Home");

var backend = new FakeHotkeys();
var manager = new HotkeyController(backend);
backend.Occupied.Add(HotkeyBinding.DefaultStart);
manager.Apply(HotkeyBinding.DefaultStart, HotkeyBinding.DefaultStop);
Expect(!manager.StartReady && manager.StopReady && manager.StartError == 1409, "开始冲突仍保留停止键");
Expect(manager.Describe(HotkeyBinding.DefaultStart, HotkeyBinding.DefaultStop).Contains("点击“开始”"), "开始冲突可用按钮提示");
backend.Occupied.Clear(); backend.Occupied.Add(HotkeyBinding.DefaultStop);
manager.Apply(HotkeyBinding.DefaultStart, HotkeyBinding.DefaultStop);
Expect(manager.StartReady && !manager.StopReady, "停止冲突独立标记");
Expect(manager.Describe(HotkeyBinding.DefaultStart, HotkeyBinding.DefaultStop).Contains("禁止真实演奏"), "停止冲突阻断提示");
backend.Occupied.Clear(); manager.Apply(HotkeyBinding.DefaultStart, HotkeyBinding.DefaultStop);
Expect(manager.StartReady && manager.StopReady, "解除占用后重试成功");
manager.Apply(HotkeyBinding.DefaultStop, HotkeyBinding.DefaultStart);
Expect(backend.Registered[1] == HotkeyBinding.DefaultStop && backend.Registered[2] == HotkeyBinding.DefaultStart, "互换快捷键先释放旧注册");
RejectHotkey(() => manager.Apply(new(0x75), new(0x75)), "无效配置拒绝应用");
Expect(manager.StartReady && manager.StopReady && backend.Registered.Count == 2, "无效配置保留已有注册");
manager.Suspend(); manager.Suspend();
Expect(backend.Registered.Count == 0 && !manager.StartReady && !manager.StopReady, "多次暂停幂等");
manager.Apply(new(0x75, 2), new(0x77, 5));
Expect(backend.Registered[1].Modifiers == 2 && backend.Registered[2].Modifiers == 5, "组合键正确传递至注册层");

string settingsDir = Path.Combine(Path.GetTempPath(), "HarmonicaPlayerTests-" + Guid.NewGuid().ToString("N"));
string settingsPath = Path.Combine(settingsDir, "settings.json");
try
{
    var defaults = SettingsStore.Load(settingsPath, out var warning);
    Expect(warning == null && defaults.Start == HotkeyBinding.DefaultStart, "缺少设置使用默认值");
    var custom = new AppSettings { Start = new(0x75, 2), Stop = new(0x77, 4), Rhythm = true, Bpm = 90, Gap = 10, Duration = 450, SpaceGap = 80, LineGap = 150,
        LyricsEnabled = true, LyricsText = "春 风 | 吹 过", LyricsScorePath = @"C:\scores\春风_源谱 BPM：90.txt", LyricsFilePath = @"C:\scores\春风_源谱 BPM：90.lyrics.txt",
        LyricsLayout = new LyricsLayout { Left = 0.2, Top = 0.7, Width = 0.6, Height = 0.16, FontSize = 42 } };
    custom = custom with { Instrument = AudioInstrument.Violin };
    SettingsStore.Save(settingsPath, custom);
    Expect(SettingsStore.Load(settingsPath, out warning) == custom && warning == null, "快捷键、时间和歌词设置往返保存");
    var changed = custom with { Bpm = 100 };
    SettingsStore.Save(settingsPath, changed);
    Expect(SettingsStore.Load(settingsPath, out warning).Bpm == 100 && Directory.GetFiles(settingsDir).Length == 1, "原子覆盖无临时文件残留");
    RejectHotkey(() => SettingsStore.Save(settingsPath, custom with { Bpm = 0 }), "不保存无效数值");
    Expect(SettingsStore.Load(settingsPath, out warning).Bpm == 100, "无效写入保留旧设置");
    File.WriteAllText(settingsPath, "{bad json");
    Expect(SettingsStore.Load(settingsPath, out warning) == new AppSettings() && warning != null, "损坏设置回退并告知");
    File.WriteAllText(settingsPath, "{\"Start\":null}");
    Expect(SettingsStore.Load(settingsPath, out warning).Start == HotkeyBinding.DefaultStart && warning != null, "空快捷键安全回退");
}
finally { if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true); }
Console.WriteLine($"PASS TOTAL: {passed} tests (parser, hotkeys, settings)");

// The settings writer must not perform a blocking disk action on the caller.
var initial = new AppSettings();
int unchangedWrites = 0;
var unchangedWriter = new SettingsWriter(_ => Interlocked.Increment(ref unchangedWrites), initial);
Expect(await unchangedWriter.SaveAsync(initial) == null && unchangedWrites == 0, "已保存且未更改时不重复写盘");
using var entered = new ManualResetEventSlim();
using var releaseWrite = new ManualResetEventSlim();
var writeOrder = new List<int>();
var queuedWriter = new SettingsWriter(value =>
{
    if (value.Bpm == 90)
    {
        entered.Set();
        if (!releaseWrite.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test gate timed out.");
    }
    lock (writeOrder) writeOrder.Add(value.Bpm);
}, initial);
try
{
    var firstSave = queuedWriter.SaveAsync(initial with { Bpm = 90 });
    Expect(entered.Wait(TimeSpan.FromSeconds(2)) && !firstSave.IsCompleted, "慢写盘在后台等待，调用方已返回");
    Expect(ReferenceEquals(firstSave, queuedWriter.SaveAsync(initial with { Bpm = 90 })), "重复待保存值共用同一任务");
    var finalSave = queuedWriter.SaveAsync(initial);
    Expect(!finalSave.IsCompleted && ReferenceEquals(finalSave, queuedWriter.FlushAsync()), "关闭等待最后一次保存而非首个保存");
    releaseWrite.Set();
    Expect(await finalSave.WaitAsync(TimeSpan.FromSeconds(3)) == null, "后台队列可完整结束");
    Expect(writeOrder.SequenceEqual(new[] {90, 120}), "快速修改再还原仍按顺序写入最终值");
    await queuedWriter.SaveAsync(initial);
    Expect(writeOrder.Count == 2, "关闭时已保存快照不重复写入");
}
finally { releaseWrite.Set(); }
int attempts = 0;
var retryWriter = new SettingsWriter(_ =>
{
    if (Interlocked.Increment(ref attempts) == 1) throw new IOException("Simulated disk failure");
});
Expect((await retryWriter.SaveAsync(initial))?.Contains("Simulated") == true, "后台保存错误返回给界面");
Expect(await retryWriter.SaveAsync(initial) == null && attempts == 2, "相同值写入失败后可重试");
Console.WriteLine($"PASS TOTAL: {passed} tests including async settings writer");

var musicXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <score-partwise version="4.0">
      <work><work-title>导入测试</work-title></work>
      <part-list><score-part id="P1"><part-name>旋律</part-name></score-part></part-list>
      <part id="P1">
        <measure number="1">
          <attributes><divisions>4</divisions><time><beats>4</beats><beat-type>4</beat-type></time></attributes>
          <direction><sound tempo="90" /></direction>
          <note><pitch><step>C</step><octave>4</octave></pitch><duration>4</duration><voice>1</voice><type>quarter</type></note>
          <note><pitch><step>E</step><octave>4</octave></pitch><duration>4</duration><voice>1</voice><type>quarter</type></note>
        </measure>
      </part>
    </score-partwise>
    """;
var importedXml = MusicXmlImporter.Import(musicXml, "fallback");
var tiedMelodyXml = """
    <score-partwise><part id="P1"><measure number="1"><attributes><divisions>4</divisions></attributes>
      <note><pitch><step>G</step><octave>4</octave></pitch><duration>4</duration><tie type="start"/><voice>1</voice><staff>1</staff></note>
      <note><pitch><step>G</step><octave>4</octave></pitch><duration>4</duration><tie type="stop"/><voice>1</voice><staff>1</staff></note>
      <note><pitch><step>G</step><octave>4</octave></pitch><duration>1</duration><voice>1</voice><staff>1</staff></note>
      <note><rest/><duration>1</duration><voice>1</voice><staff>1</staff></note>
      <backup><duration>10</duration></backup>
      <note><pitch><step>C</step><octave>3</octave></pitch><duration>2</duration><voice>1</voice><staff>2</staff></note>
      <note><pitch><step>E</step><octave>3</octave></pitch><duration>2</duration><voice>1</voice><staff>2</staff></note>
      <note><pitch><step>G</step><octave>3</octave></pitch><duration>2</duration><voice>1</voice><staff>2</staff></note>
      <note><pitch><step>E</step><octave>3</octave></pitch><duration>2</duration><voice>1</voice><staff>2</staff></note>
    </measure></part></score-partwise>
    """;
var tiedMelody = MusicXmlImporter.Import(tiedMelodyXml, "延音线", preserveTiming: true, includePerformance: true);
Expect(tiedMelody.Notes.Select(n => (n.Degree, n.Beats)).SequenceEqual(new[] { (5, 2d), (5, .25), (0, .25) }) &&
    tiedMelody.Performance is { Count: 6 }, "MusicXML 优先上方主声部，合并延音并保留实际重弹");
var tripletXml = "<score-partwise><part id='P1'><measure number='1'><attributes><divisions>6</divisions></attributes>" +
    string.Concat(new[] { "C", "D", "E" }.Select(step => $"<note><pitch><step>{step}</step><octave>4</octave></pitch><duration>1</duration><voice>1</voice></note>")) +
    "</measure></part></score-partwise>";
var xmlExact = MusicXmlImporter.Import(tripletXml, "triplet", preserveTiming: true);
Expect(xmlExact.Notes.Count == 3 && xmlExact.Notes.All(n => Math.Abs(n.Beats - 1d / 6) < 1e-8) &&
    ScoreTiming.Apply(xmlExact.Text, ScoreTiming.Create(xmlExact.Text, xmlExact.Notes).ToJson()).Count == 3,
    "MusicXML 三连音精确时值往返");
LyricsBindingTests.Run(Expect);
Expect(importedXml.Title == "导入测试" && importedXml.Bpm == 90, "MusicXML 读取标题和 BPM");
Expect(importedXml.Notes.Select(n => n.Degree).SequenceEqual(new[] { 1, 3 }), "MusicXML 提取主旋律音符");
Expect(ScoreParser.Parse(importedXml.Text, true).All(n => n.Beats == 1), "MusicXML 时值转换");

var midiTrack = new byte[] { 0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,
    0x00, 0x90, 0x3C, 0x64, 0x60, 0x80, 0x3C, 0x00,
    0x00, 0x90, 0x3E, 0x64, 0x60, 0x80, 0x3E, 0x00,
    0x00, 0xFF, 0x2F, 0x00 };
var midiBytes = new List<byte>();
midiBytes.AddRange(Encoding.ASCII.GetBytes("MThd"));
midiBytes.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 0, 96 });
midiBytes.AddRange(Encoding.ASCII.GetBytes("MTrk"));
midiBytes.AddRange(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(midiTrack.Length)));
midiBytes.AddRange(midiTrack);
var importedMidi = MidiImporter.Import(midiBytes.ToArray(), "midi fallback");
var midiTripletTrack = new byte[] { 0,0x90,60,80,16,0x80,60,0, 0,0x90,62,80,16,0x80,62,0, 0,0x90,64,80,16,0x80,64,0, 0,0xff,0x2f,0 };
var midiTriplet = new List<byte>(midiBytes.Take(14));
midiTriplet.AddRange(Encoding.ASCII.GetBytes("MTrk"));
midiTriplet.AddRange(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(midiTripletTrack.Length)));
midiTriplet.AddRange(midiTripletTrack);
var midiExact = MidiImporter.Import(midiTriplet.ToArray(), "triplet", preserveTiming: true);
Expect(midiExact.Notes.Count == 3 && midiExact.Notes.All(n => Math.Abs(n.Beats - 1d / 6) < 1e-8) &&
    ScoreTiming.Apply(midiExact.Text, ScoreTiming.Create(midiExact.Text, midiExact.Notes).ToJson()).Count == 3,
    "MIDI 三连音精确时值往返");
Expect(importedMidi.Bpm == 120 && importedMidi.Notes.Count == 2, "MIDI 读取速度和音符");
Expect(importedMidi.Notes.Select(n => n.Degree).SequenceEqual(new[] { 1, 2 }), "MIDI 音高转换");
var namedMidi = new List<byte>(midiBytes.Take(14));
namedMidi[9] = 1;
namedMidi[11] = 2;
var accompaniment = midiTripletTrack;
var lead = new byte[] { 0, 0xff, 3, 6 }.Concat(Encoding.ASCII.GetBytes("Melody"))
    .Concat(new byte[] { 0, 0x90, 67, 90, 96, 0x80, 67, 0, 0, 0xff, 0x2f, 0 }).ToArray();
foreach (var track in new[] { accompaniment, lead })
{
    namedMidi.AddRange(Encoding.ASCII.GetBytes("MTrk"));
    namedMidi.AddRange(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(track.Length)));
    namedMidi.AddRange(track);
}
var autoMelody = MidiImporter.ImportTrack(namedMidi.ToArray(), "命名旋律", null, preserveTiming: true, includePerformance: true);
var explicitMidiTrack = MidiImporter.ImportTrack(namedMidi.ToArray(), "手动选轨", 0, preserveTiming: true);
Expect(autoMelody.Notes is { Count: 1 } && autoMelody.Notes[0].Degree == 5 &&
    autoMelody.Performance is { Count: 4 } && explicitMidiTrack.Notes.Count == 3,
    "MIDI 优先明确命名的旋律轨，保留原谱和手动选轨");
Console.WriteLine($"PASS FINAL: {passed} tests");
}
catch (Exception error)
{
    Console.Error.WriteLine("TEST FAILURE: " + error);
    Environment.ExitCode = 1;
}

sealed class FakeHotkeys : IHotkeyBackend
{
    public HashSet<HotkeyBinding> Occupied { get; } = new();
    public Dictionary<int, HotkeyBinding> Registered { get; } = new();
    public int Register(int id, HotkeyBinding binding)
    {
        if (Occupied.Contains(binding) || Registered.Values.Contains(binding) || Registered.ContainsKey(id)) return 1409;
        Registered[id] = binding; return 0;
    }
    public void Unregister(int id) => Registered.Remove(id);
}
