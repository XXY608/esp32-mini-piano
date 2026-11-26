using HarmonicaPlayer;
using HarmonicaPlayer.Mobile.Core;
using System.Net;

if (args.Length == 3 && args[0] == "--live-online")
{
    string directory = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(directory);
    using var http = OnlineScoreClient.CreateClient(new HttpClientHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.All });
    var client = new MobileScoreClient(http);
    var scores = await client.SearchAsync(args[1]);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { query = args[1], source = "虫虫钢琴", results = scores.Count, route = "host direct; no Windows proxy" }));
    foreach (var foundScore in scores.Take(5))
    {
        try
        {
            byte[] bytes = await client.DownloadAsync(foundScore);
            var imported = CcmzImporter.Import(bytes, foundScore.Title);
            var song = new StoredSong(SongPayloadFactory.Create(imported), imported.Text, DateTimeOffset.UtcNow);
            using var localStore = new JsonSongStore(Path.Combine(directory, "songs.json"));
            await localStore.SaveAsync(song);
            using var reopened = new JsonSongStore(Path.Combine(directory, "songs.json"));
            if ((await reopened.GetAllAsync()).Single(s => s.Song.Id == song.Song.Id).SourceText != imported.Text) throw new InvalidOperationException("Saved score mismatch.");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { success = true, foundScore.Title, foundScore.Url, bytes = bytes.Length, notes = imported.Notes.Count, bpm = imported.Bpm, savedAndReopened = true }));
            return;
        }
        catch (Exception error) { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { success = false, foundScore.Title, error = error.Message })); }
    }
    return;
}
if (args.Length == 2 && args[0] == "--live-lyrics")
{
    var live = await MobileLyricsLookup.FindAsync(args[1]);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        found = live is not null, source = live?.Source, title = live?.Lyrics.TrackName,
        artist = live?.Lyrics.ArtistName, characters = live?.Lyrics.Text.Length,
        timedLines = live is null ? 0 : MobileLyricsTimeline.Parse(live.Lyrics.Text).Count
    }));
    return;
}

var passed = 0;
void Expect(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}

var draft = TxtScoreImporter.Parse("春风_源谱 BPM：90.txt", "1 2_ #3 | 【4】 （5） 0");
Expect(draft.IsValid, "TXT 使用现有规则解析");
Expect(draft.Title == "春风" && draft.Bpm == 90 && draft.BpmSource == "文件名", "TXT 标题和源谱 BPM 从文件名读取");
Expect(draft.Notes.Count == 6 && draft.Notes[1].Beats == 0.5 && draft.Notes[2].Sharp, "TXT 时值、升半音和音区保留");
Expect(draft.Score!.Text.Contains("2_") && draft.Score.Text.Contains("#3"), "TXT 生成统一简谱文本");

var invalid = TxtScoreImporter.Parse("bad.txt", "1___");
Expect(!invalid.IsValid && invalid.ParseError is not null, "TXT 解析错误可读返回");

SongPayload payload = SongPayloadFactory.Create(draft.Score!, "test-song");
Expect(payload.SourceType == "txt" && payload.Notes.Count == draft.Notes.Count && payload.Notes[0].Pitch == 1,
    "统一歌曲 DTO 映射正确");
string json = System.Text.Json.JsonSerializer.Serialize(payload, DeviceJson.Options);
Expect(json.Contains("\"durationBeats\"") && json.Contains("\"sourceType\":\"txt\""), "设备 JSON 使用约定字段");

var phrasedNotes = ScoreParser.Parse("1 2 3 4 5", true);
var phrasedLyrics = ScoreLyrics.FromSyllables(phrasedNotes, new[] {
    (0, new NoteLyric("一")), (1, new NoteLyric("句", EndLine: true)),
    (3, new NoteLyric("二")), (4, new NoteLyric("句"))
});
var phrasedScore = draft.Score! with { Notes = phrasedNotes, Lyrics = phrasedLyrics };
var phrasedPayload = SongPayloadFactory.Create(phrasedScore, "phrased-test");
Expect(phrasedPayload.Notes[3].PhraseBefore && !phrasedPayload.Notes[2].PhraseBefore && !phrasedPayload.Notes[0].PhraseBefore,
    "源谱明确的歌词句尾在下一句首保留分句标记，休止及音符不变");
Expect(System.Text.Json.JsonSerializer.Serialize(phrasedPayload, DeviceJson.Options).Contains("\"phraseBefore\":true"), "分句标记写入设备 JSON");

using (var handler = new ContractHandler())
using (var httpDevice = new HttpEsp32Client("http://esp32.test", TimeSpan.FromSeconds(2), handler))
{
    DeviceStatusDto status = await httpDevice.GetStatusAsync();
    IReadOnlyList<DeviceSongSummary> songs = await httpDevice.GetSongsAsync();
    DeviceResponse uploadResponse = await httpDevice.UploadSongAsync(payload);
    DeviceResponse playResponse = await httpDevice.PlaySongAsync(payload.Id);
    DeviceResponse teachResponse = await httpDevice.TeachSongAsync(payload.Id);
    DeviceResponse stopResponse = await httpDevice.StopAsync();
    DeviceResponse volumeResponse = await httpDevice.SetVolumeAsync(65);
    DeviceResponse deleteResponse = await httpDevice.DeleteSongAsync(payload.Id);
    Expect(status.Ok && status.DeviceId == "real-test" && status.VolumePercent == 55 && songs.Count == 1, "HTTP 状态和歌曲列表响应解析");
    Expect(uploadResponse.Ok && playResponse.Ok && teachResponse.Ok && stopResponse.Ok && volumeResponse.Ok && deleteResponse.Ok,
        "HTTP 上传、播放、练习、停止、删除响应解析");
    Expect(handler.Paths.SequenceEqual(new[]
    {
        "GET /api/status", "GET /api/songs", "POST /api/songs/upload", "POST /api/song/play",
        "POST /api/song/teach", "POST /api/song/stop", "POST /api/device/volume", "DELETE /api/songs/test-song"
    }) && handler.LastUploadBody.Contains("\"durationBeats\""), "HTTP 使用约定路径和结构化 JSON");
}

var mock = new MockEsp32Client();
DeviceResponse upload = await mock.UploadSongAsync(payload);
Expect(upload.Ok && (await mock.GetSongsAsync()).Count == 1, "Mock 接收上传并列出歌曲");
Expect((await mock.PlaySongAsync(payload.Id)).Ok && (await mock.TeachSongAsync(payload.Id)).Ok, "Mock 支持播放和练习");
Expect((await mock.StopAsync()).Ok && (await mock.DeleteSongAsync(payload.Id)).Ok && (await mock.GetSongsAsync()).Count == 0,
    "Mock 支持停止和删除");

string tempDirectory = Path.Combine(Path.GetTempPath(), "electrickeyboard-mobile-tests-" + Guid.NewGuid().ToString("N"));
try
{
    using var store = new JsonSongStore(Path.Combine(tempDirectory, "songs.json"));
    await store.SaveAsync(new StoredSong(payload, "1 2_", DateTimeOffset.UtcNow));
    Expect((await store.GetAllAsync()).Single().Song.Id == "test-song", "本地歌曲 JSON 存储和读取");
    var quickImport = new StoredSong(payload with { Title = "直接导入", Id = "quick-import" }, "1 2_", DateTimeOffset.UtcNow);
    bool[] insertions = await Task.WhenAll(store.TrySaveNewAsync(quickImport), store.TrySaveNewAsync(quickImport with
    { Song = quickImport.Song with { Id = "other-import" }, SourceText = "3 4" }));
    Expect(insertions.SequenceEqual(new[] { true, false }) &&
        (await store.GetAllAsync()).Single(s => s.Song.Title == "直接导入").SourceText == "1 2_",
        "自动保存新曲谱时并发导入同名曲只保存一份，保留第一份谱面");
    Expect(!await store.TrySaveNewAsync(quickImport with { Song = quickImport.Song with { Title = "相同 ID" } }),
        "自动保存不会替换 ID 相同的曲谱");
    await store.DeleteAsync(quickImport.Song.Id);
    SongPayload replacement = payload with { Id = "replacement-song", Title = payload.Title };
    await store.SaveAsync(new StoredSong(replacement, "1 2 3", DateTimeOffset.UtcNow));
    IReadOnlyList<StoredSong> replacedSongs = await store.GetAllAsync();
    Expect(replacedSongs.Count == 1 && replacedSongs[0].Song.Id == "replacement-song", "同名歌曲导入覆盖旧版本");
    await store.DeleteAsync(payload.Id);
    await store.DeleteAsync(replacement.Id);
    Expect((await store.GetAllAsync()).Count == 0, "本地歌曲删除");
}
finally
{
    if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
}

using (var handler = new AudioContractHandler())
using (var client = new HttpEsp32Client("http://esp32.test", TimeSpan.FromSeconds(1), handler))
{
    Expect((await client.SetPhoneAudioAsync(4000000000u, true, default)).Ok, "实时发声携带完整 uint32 会话及固定 Content-Length");
    var packet = await client.GetAudioEventsAsync(4000000000u, 123, default);
    Expect(!packet.PhoneOutput && packet.Reset && packet.Events.Single().Midi == 72 && handler.ValidQuery,
        "实时音符解析输出路由、时间、触发和轮询游标");
}
DeviceAudioPacket ParseAudio(string text)
{
    using var document = System.Text.Json.JsonDocument.Parse(text);
    return DeviceAudioPacket.Parse(document.RootElement);
}
try { ParseAudio("{\"events\":[]}"); Expect(false, "旧固件拒绝"); }
catch (DeviceCommunicationException error) { Expect(error.Message.Contains("0.6.13"), "旧固件缺路由字段时给出升级提示"); }
try { ParseAudio("{\"phoneOutput\":true,\"events\":[[1,2,128,100,0,1,0]]}"); Expect(false, "非法音符拒绝"); }
catch (DeviceCommunicationException) { Expect(true, "非法 MIDI 不进入渲染器"); }
var audioBuffer = new DeviceAudioBuffer();
var tone = new DeviceAudioEvent(1, 100, 72, 50, 0, 1, false);
audioBuffer.Accept(new(true, true, new[] { tone }), 0);
var pcm = new short[2205]; audioBuffer.Render(pcm, 0);
Expect(pcm.Take(1323).All(x => x == 0) && pcm.Skip(1500).Any(x => x != 0), "实时音符按时间戳播放并保留短缓冲");
Expect(pcm.All(x => Math.Abs((int)x) < 2000), "纯音高音补偿与幅度保持有界");
audioBuffer.Accept(new(false, true, new[] { tone with { Sequence = 2, Milliseconds = 200, Midi = -1, Trigger = 2 } }), 100);
audioBuffer.Render(pcm, 160);
Expect(pcm.Skip(400).All(x => x == 0), "松键后淡出并保持静音，不残留长音");
audioBuffer.Accept(new(false, true, new[] { tone with { Sequence = 3, Milliseconds = 300, Trigger = 3 } }), 200);
audioBuffer.Render(pcm, 260);Expect(pcm.Any(x => x != 0), "相同音高的新触发再次发声");
audioBuffer.Accept(new(false, false, Array.Empty<DeviceAudioEvent>()), 300);
audioBuffer.Render(pcm, 300);Expect(pcm.All(x => x == 0), "琴端切本机立即清除手机声音和待播事件");
audioBuffer.Accept(new(true, true, new[] { tone }), 400);audioBuffer.Render(pcm, 460);
Expect(pcm.Any(x => x != 0), "切回手机可用当前快照恢复仍按住的音符");
audioBuffer.Reset();audioBuffer.Render(pcm, 500);Expect(pcm.All(x => x == 0), "连接取消时清空实时声音");
audioBuffer.Accept(new(true, true, new[] { tone with { Midi = -1, Click = true, Volume = 100 } }), 0);
audioBuffer.Render(pcm, 60);Expect(pcm.Take(550).Any(x => x != 0) && pcm.Skip(600).All(x => x == 0), "休止中仍可播放短节拍器，不产生持续声");
using (var cancel = new CancellationTokenSource())
{
    var fake = new AudioSessionFake(cancel, fail: false); var routes = new List<bool>();
    try { await DeviceAudioSession.RunAsync(fake, audioBuffer, routes.Add, cancel.Token); }
    catch (OperationCanceledException) { }
    Expect(routes.SequenceEqual(new[] { true, false, true }) && fake.Cursors[2] == 7 && fake.Cursors[4] == 0 && fake.Cursors[6] == 0,
        "会话保持轮询，切回手机无新音符时重取当前快照");
    Expect(fake.Released && fake.ReleasedSession == fake.AcquiredSession && fake.AcquiredSession != 0,
        "取消时释放同一会话，恢复琴端输出");
    audioBuffer.Render(pcm, 1000);Expect(pcm.All(x => x == 0), "会话取消后即使已排队也不再发声");
}
using (var cancel = new CancellationTokenSource())
{
    var fake = new AudioSessionFake(cancel, fail: true);
    try { await DeviceAudioSession.RunAsync(fake, audioBuffer, _ => { }, cancel.Token); }
    catch (DeviceCommunicationException) { }
    Expect(fake.Released && fake.ReleasedSession == fake.AcquiredSession, "轮询断线时释放会话，不让实体琴一直静音");
}

const string lyricJson = "[{\"trackName\":\"Imagine\",\"artistName\":\"Example\",\"syncedLyrics\":\"[00:01.00]test\"}]";
using (var primary = new LyricsHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(lyricJson) }))
using (var backup = new LyricsHandler(_ => throw new InvalidOperationException("unexpected fallback")))
using (var primaryClient = OnlineScoreClient.CreateClient(primary))
using (var backupClient = OnlineScoreClient.CreateClient(backup))
{
    var result = await MobileLyricsLookup.FindAsync("Imagine", primaryClient, backupClient);
    Expect(result?.IsSynced == true && result.Text.Contains("test") && backup.Calls == 0,
        "正常歌词响应不启用备用连接");
    Expect(primary.Url == "https://lrclib.net/api/search?q=Imagine", "歌词请求使用预期HTTPS接口");
}
using (var primary = new LyricsHandler(_ => throw new HttpRequestException("Connection failure", new IOException("native reset"))))
using (var backup = new LyricsHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(lyricJson) }))
using (var primaryClient = OnlineScoreClient.CreateClient(primary))
using (var backupClient = OnlineScoreClient.CreateClient(backup))
{
    var result = await MobileLyricsLookup.FindAsync("Imagine", primaryClient, backupClient);
    Expect(result?.TrackName == "Imagine" && primary.Calls == 1 && backup.Calls == 1,
        "系统连接失败后备用连接返回歌词");
}
foreach (var mode in new[] { "http", "empty", "cancel", "both-fail" })
{
    using var primary = new LyricsHandler(_ => mode switch
    {
        "http" => new(HttpStatusCode.Forbidden),
        "empty" => new(HttpStatusCode.OK) { Content = new StringContent("[]") },
        "cancel" => throw new OperationCanceledException(),
        _ => throw new HttpRequestException("Connection failure", new IOException("native reset"))
    });
    using var backup = new LyricsHandler(_ => throw new HttpRequestException("managed failed", new System.Security.Authentication.AuthenticationException("TLS detail")));
    using var primaryClient = OnlineScoreClient.CreateClient(primary);
    using var backupClient = OnlineScoreClient.CreateClient(backup);
    Exception? failure = null;
    OnlineLyrics? result = null;
    try { result = await MobileLyricsLookup.FindAsync("Imagine", primaryClient, backupClient); }
    catch (Exception error) { failure = error; }
    Expect(mode switch
    {
        "http" => failure is HttpRequestException { StatusCode: HttpStatusCode.Forbidden } && backup.Calls == 0,
        "empty" => failure is null && result is null && backup.Calls == 0,
        "cancel" => failure is OperationCanceledException && backup.Calls == 0,
        _ => failure is not null && backup.Calls == 1 && MobileLyricsLookup.DescribeError(failure).Contains("native reset") &&
            MobileLyricsLookup.DescribeError(failure).Contains("TLS detail")
    }, "歌词连接分支：" + mode);
}

const string neteaseSearch = "{\"code\":200,\"result\":{\"songs\":[{\"id\":1476431,\"name\":\"Imagine\",\"artists\":[{\"name\":\"John Lennon\"}]}]}}";
const string neteaseLyric = "{\"code\":200,\"lrc\":{\"lyric\":\"[00:01.00]fixture\"}}";
using (var provider = new LyricsHandler(request =>
{
    Expect(request.Headers.Referrer?.Host == "music.163.com", "网易请求附带本站Referer");
    return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("/lyric") ? neteaseLyric : neteaseSearch) };
}))
using (var client = OnlineScoreClient.CreateClient(provider))
using (var backup = new LyricsHandler(_ => throw new InvalidOperationException("unexpected LRCLIB")))
using (var backupClient = OnlineScoreClient.CreateClient(backup))
{
    var result = await MobileLyricsLookup.FindWithSourcesAsync("Imagine", client, backupClient);
    Expect(result?.Source == "网易云音乐" && result.Lyrics.IsSynced && result.Lyrics.ArtistName == "John Lennon" && provider.Calls == 2,
        "网易精确匹配成功，带来源返回LRC且不访问LRCLIB");
}
foreach (string mode in new[] { "unmatched", "nolyric", "invalid-code", "too-large", "lrclib", "cancel" })
{
    using var provider = new LyricsHandler(request =>
    {
        bool external = request.RequestUri!.Host == "lrclib.net";
        string body = external ? (mode == "lrclib" ? lyricJson : "[]") : mode switch
        {
            "unmatched" => neteaseSearch.Replace("Imagine", "Imagine (Live)"),
            "nolyric" => request.RequestUri.AbsolutePath.Contains("/lyric") ? "{\"code\":200,\"nolyric\":true}" : neteaseSearch,
            "invalid-code" => "{\"code\":500}",
            "too-large" => new string('x', OnlineScoreClient.MaxLyricsBytes + 1),
            "cancel" => throw new OperationCanceledException(),
            _ => "{\"code\":200,\"result\":{\"songs\":[]}}"
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(body) };
    });
    using var client = OnlineScoreClient.CreateClient(provider);
    Exception? failure = null;
    MobileLyricsMatch? result = null;
    try { result = await MobileLyricsLookup.FindWithSourcesAsync("Imagine", client, client); }
    catch (Exception error) { failure = error; }
    Expect(mode switch
    {
        "lrclib" => failure is null && result?.Source == "LRCLIB",
        "invalid-code" or "too-large" => failure is HttpRequestException && MobileLyricsLookup.DescribeError(failure).Contains("网易云音乐"),
        "cancel" => failure is OperationCanceledException && provider.Calls == 1,
        _ => failure is null && result is null
    }, "双歌词源分支：" + mode);
    if (mode == "unmatched") Expect(provider.Calls == 2, "相似曲名不查询或填入错误版本歌词");
}

const string ambiguousSearch = "{\"code\":200,\"result\":{\"songs\":[{\"id\":1,\"name\":\"Imagine\",\"artists\":[{\"name\":\"Singer A\"}]},{\"id\":2,\"name\":\"Imagine\",\"artists\":[{\"name\":\"Singer B\"}]}]}}";
foreach (string mode in new[] { "select", "cancel", "no-selector" })
{
    using var handler = new LyricsHandler(request => new(HttpStatusCode.OK)
    {
        Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("/lyric") ? neteaseLyric : ambiguousSearch)
    });
    using var client = OnlineScoreClient.CreateClient(handler);
    Func<IReadOnlyList<NeteaseSong>, Task<NeteaseSong?>>? select = mode switch
    {
        "select" => songs => Task.FromResult<NeteaseSong?>(songs[1]),
        "cancel" => _ => Task.FromResult<NeteaseSong?>(null),
        _ => null
    };
    Exception? failure = null;
    OnlineLyrics? result = null;
    try { result = await NeteaseLyrics.FindAsync("Imagine", client, choose: select); }
    catch (Exception error) { failure = error; }
    Expect(mode switch
    {
        "select" => failure is null && result?.ArtistName == "Singer B" && handler.Url!.Contains("id=2&"),
        "cancel" => failure is OperationCanceledException && handler.Calls == 1,
        _ => failure is InvalidOperationException && handler.Calls == 1
    }, "同名歌曲版本选择：" + mode);
}

var deviceScore = TxtScoreImporter.Parse("歌词传输_源谱 BPM：108.txt", string.Join(" ", Enumerable.Repeat("1", 500))).Score!;
const string deviceLrc = "[00:17.680]第一句歌词\n[00:26.230]第二句歌词\n[01:00.000]\n[01:05.000]第三句歌词";
var lyricPayload = SongPayloadFactory.Create(deviceScore, "mobile-lyrics-fixture", deviceLrc);
Expect(lyricPayload.LyricCues?.Count == 4 && Math.Abs(lyricPayload.LyricCues[0].StartBeat - 31.824) < 0.000001 &&
    lyricPayload.LyricCues[2].Text == "", "整句LRC携带准确拍位置及空白清除事件，不伪造逐音歌词");
Expect(lyricPayload.Notes.Count == 500 && lyricPayload.Notes.All(n => n.Lyric == ""), "整句歌词不会改写500音符或平均塞字");
var advanced = SongPayloadFactory.Create(deviceScore, "mobile-lyrics-fixture", deviceLrc, -17.68);
Expect(advanced.LyricCues![0].StartBeat == 0 && Math.Abs(advanced.LyricCues[1].StartBeat - 15.39) < 0.000001,
    "负歌词偏移提前显示且保持句间时间");
var delayed = SongPayloadFactory.Create(deviceScore, null, deviceLrc, 2);
Expect(Math.Abs(delayed.LyricCues![0].StartBeat - 35.424) < 0.000001, "正歌词偏移延后显示");
var bound = ScoreLyrics.FromSyllables(deviceScore.Notes, new[] { (0, new NoteLyric("词")) });
Expect(SongPayloadFactory.Create(deviceScore with { Lyrics = bound }).Notes[0].Lyric == "词", "保留源谱已有逐音绑定");
var fixturePath = Environment.GetEnvironmentVariable("PIANO_MOBILE_LYRICS_FIXTURE");
if (!string.IsNullOrWhiteSpace(fixturePath))
    await File.WriteAllTextAsync(fixturePath, System.Text.Json.JsonSerializer.Serialize(lyricPayload, DeviceJson.Options), new System.Text.UTF8Encoding(false));
string lyricStorePath = Path.Combine(Path.GetTempPath(), "mobile-lyrics-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    using var lyricStore = new JsonSongStore(lyricStorePath);
    await lyricStore.SaveAsync(new StoredSong(advanced, deviceScore.Text, DateTimeOffset.UtcNow, deviceLrc, true, -17.68));
    var reopened = (await lyricStore.GetAllAsync()).Single();
    var rebuilt = SongPayloadFactory.Create(deviceScore, reopened.Song.Id, reopened.LyricsText, reopened.LyricsOffsetSeconds);
    Expect(reopened.LyricsOffsetSeconds == -17.68 && rebuilt.LyricCues!.SequenceEqual(advanced.LyricCues!), "保存重开再生成上传数据仍保留歌词和偏移");
}
finally { if (File.Exists(lyricStorePath)) File.Delete(lyricStorePath); }
foreach (bool supported in new[] { false, true })
{
    string uploadBody = "";
    using var handler = new LyricsHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/status")
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"supportsLyricCues\":" + (supported ? "true" : "false") + "}") };
        uploadBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"message\":\"song uploaded\"}") };
    });
    using var client = new HttpEsp32Client("http://esp32.test", TimeSpan.FromSeconds(2), handler);
    Exception? failure = null;
    try { await client.UploadSongAsync(lyricPayload); } catch (Exception error) { failure = error; }
    Expect(supported ? failure is null && uploadBody.Contains("lyricCues") && uploadBody.Contains("31.824") :
        failure is DeviceCommunicationException && uploadBody.Length == 0, supported ? "实际HTTP上传正文带时间轴歌词" : "旧固件拒绝无声丢弃歌词的假成功");
}
foreach (string mode in new[] { "no-bpm", "oversize", "late", "invalid-offset" })
{
    bool rejected = false;
    try
    {
        SongPayloadFactory.Create(mode == "no-bpm" ? deviceScore with { Bpm = null } : deviceScore, null,
            mode == "oversize" ? "[00:01.00]" + new string('字', 200) : mode == "late" ? "[99:00.00]词" : deviceLrc,
            mode == "invalid-offset" ? double.NaN : 0);
    }
    catch (FormatException) { rejected = true; }
    Expect(rejected, "设备歌词校验：" + mode);
}

byte[] MidiFixture(bool tempo)
{
    var bytes = new List<byte>(new byte[] { 77,84,104,100, 0,0,0,6, 0,1, 0,2, 0,96 });
    void Track(byte[] content) { bytes.AddRange(new byte[] { 77,84,114,107,0,0,0,(byte)content.Length }); bytes.AddRange(content); }
    Track(tempo ? new byte[] { 0,255,81,3,7,161,32, 0,144,48,100,96,128,48,0, 0,255,47,0 } : new byte[] { 0,144,48,100,96,128,48,0,0,255,47,0 });
    Track(new byte[] { 0,144,72,100,96,128,72,0,0,255,47,0 });
    return bytes.ToArray();
}
byte[] midiFixture = MidiFixture(true);
var midiTracks = MidiImporter.GetTracks(midiFixture);
Expect(midiTracks.Count == 2 && midiTracks[1].Index == 1, "MIDI暴露可演奏音轨供用户选择");
var highTrack = MobileScoreFiles.Import(midiFixture, "旋律.mid", 1);
Expect(highTrack.Title == "旋律" && highTrack.Bpm == 120 && highTrack.Notes.Single().Octave == 1, "选择主旋律音轨且保留MIDI源速度和文件曲名");
Expect(MobileScoreFiles.Import(MidiFixture(false), "未标速度.midi", 1).Bpm is null, "无源速度MIDI不编造BPM");
Expect(MobileScoreFiles.Import(System.Text.Encoding.UTF8.GetBytes("\uFEFF1 2_"), "测试_源谱 BPM：90.txt").Bpm == 90, "多格式导入仍保留TXT编码/速度规则");
var xmlScore = MobileScoreFiles.Import(System.Text.Encoding.UTF8.GetBytes("""
<score-partwise><part id="P1"><measure number="1"><attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time></attributes><direction><sound tempo="90"/></direction><note><pitch><step>C</step><octave>4</octave></pitch><duration>1</duration><voice>1</voice><lyric><text>你</text></lyric></note></measure></part></score-partwise>
"""), "测试.musicxml");
Expect(xmlScore.Bpm == 90 && SongPayloadFactory.Create(xmlScore).Notes.Any(n => n.Lyric == "你"), "MusicXML源速度与已有逐音歌词可随转换保存");
var midiPerformance = new List<byte>(new byte[] { 77,84,104,100,0,0,0,6,0,1,0,3,0,96 });
void AddPerformanceTrack(params byte[] events)
{
    midiPerformance.AddRange(new byte[] { 77,84,114,107,0,0,0,(byte)events.Length });
    midiPerformance.AddRange(events);
}
AddPerformanceTrack(0,255,81,3,7,161,32,0,255,47,0);
AddPerformanceTrack(0,144,48,75,0x82,0x20,128,48,0,0,255,47,0);
AddPerformanceTrack(0,144,72,96,48,128,72,0,0,144,74,104,48,128,74,0,0,144,76,110,0,144,79,92,96,128,76,0,0,128,79,0,0,255,47,0);
byte[] polyMidi = midiPerformance.ToArray();
var polyScore = MobileScoreFiles.Import(polyMidi, "复音测试.mid", includePerformance: true);
Expect(polyScore.Performance is { Count: 5 } && polyScore.Performance.Any(n => n.MidiPitch == 48 && n.DurationBeats == 3 && n.Velocity == 75), "MIDI 合并左右手，保留跨音符低音与原力度");
Expect(polyScore.Performance!.Count(n => n.StartBeats == 1) == 2 && MobileScoreFiles.Import(polyMidi, "旧简谱.mid").Performance is null,
    "和弦共用起点，旧 MIDI 转简谱入口仍可用");
var polyPayload = SongPayloadFactory.Create(polyScore, "poly-test");
Expect(polyPayload.Polyphony is not null && Convert.FromBase64String(polyPayload.Polyphony).Length == 50 && payload.Polyphony is null,
    "五个独立时间区间编译进设备 JSON，旧 TXT JSON 兼容");
var jsonRoundTrip = MobileScoreFiles.ImportDeviceJson(SongPayloadFactory.SerializeForDevice(polyPayload with
{ TimeSignature = "4/4", Pedal = new uint[] { 1, 1920 } }));
Expect(jsonRoundTrip.Polyphony == polyPayload.Polyphony && jsonRoundTrip.Pedal!.SequenceEqual(new uint[] { 1, 1920 }) &&
    jsonRoundTrip.TimeSignature == "4/4" && jsonRoundTrip.SourceType == "device-json", "安卓导入已编译 JSON 时保留多音、踏板和拍号");
try
{
    MobileScoreFiles.ImportDeviceJson(SongPayloadFactory.SerializeForDevice(polyPayload with { Polyphony = "invalid" }));
    Expect(false, "拒绝错误的多音时间轴");
}
catch (FormatException) { Expect(true, "损坏的多音 JSON 不进入曲库"); }

if (Environment.GetEnvironmentVariable("PIANO_CCMZ_POLYPHONY_SOURCE") is { } actualCcmz)
{
    byte[] data = File.ReadAllBytes(actualCcmz);
    var importedCcmz = MobileScoreFiles.Import(data, Path.GetFileName(actualCcmz), includePerformance: true);
    Expect(importedCcmz.Performance is { Count: 693 } && importedCcmz.Bpm == 78 &&
        importedCcmz.Performance.Count(n => n.StartBeats == 0 && n.MidiPitch is 53 or 76) == 2 &&
        MobileScoreFiles.Import(data, Path.GetFileName(actualCcmz)).Performance is null,
        "实体琴所用的真实 CCMZ 从 App 导入 693 音，保留双手开头");
    var verified = SongPayloadFactory.Create(importedCcmz with { Title = "时间煮雨（多音）" }, "time-rain-poly-verified-78");
    byte[] output = SongPayloadFactory.SerializeForDevice(verified);
    var reloaded = MobileScoreFiles.ImportDeviceJson(output);
    using var handler = new ContractHandler { SupportsPolyphony = true };
    using var client = new HttpEsp32Client("http://esp32.test", TimeSpan.FromSeconds(2), handler);
    var sent = await client.UploadSongAsync(reloaded);
    var transmitted = MobileScoreFiles.ImportDeviceJson(System.Text.Encoding.UTF8.GetBytes(handler.LastUploadBody));
    Expect(sent.Ok && transmitted.Polyphony == verified.Polyphony && transmitted.Pedal is { Count: 21 } &&
        transmitted.Pedal.SequenceEqual(verified.Pedal!), "真实 CCMZ 经 Android 导入和发送仍保留 693 音与 21 次踏板");
    if (Environment.GetEnvironmentVariable("PIANO_REAL_SONG_OUTPUT") is { } outputPath) File.WriteAllBytes(outputPath, output);
}
if (Environment.GetEnvironmentVariable("PIANO_POLYPHONY_FIXTURE") is { } fixture)
{
    File.WriteAllBytes(fixture, SongPayloadFactory.SerializeForDevice(polyPayload));
    File.WriteAllBytes(Path.ChangeExtension(fixture, ".mid"), polyMidi);
}
midiPerformance.RemoveRange(14, midiPerformance.Count - 14);
midiPerformance[11] = 2;
AddPerformanceTrack(0,255,81,3,7,161,32,96,255,81,3,15,66,64,0,255,47,0);
AddPerformanceTrack(0,176,64,127,0,144,48,75,48,128,48,0,96,176,64,0,0,255,47,0);
var sustained = MobileScoreFiles.Import(midiPerformance.ToArray(), "踏板和变速.mid", includePerformance: true).Performance!.Single();
Expect(sustained.DurationBeats == 2, "MIDI 踏板延长音符并按经过的速度段计算结束时间");
using (var handler = new ContractHandler())
using (var client = new HttpEsp32Client("http://esp32.test", TimeSpan.FromSeconds(2), handler))
{
    try { await client.UploadSongAsync(polyPayload); Expect(false, "拒绝旧固件降级成单音"); }
    catch (DeviceCommunicationException) { Expect(handler.Paths.SequenceEqual(new[] { "GET /api/status" }), "旧固件只查询能力，未上传"); }
}
using (var handler = new ContractHandler { SupportsPolyphony = true })
using (var client = new HttpEsp32Client("http://esp32.test", TimeSpan.FromSeconds(2), handler))
{
    var sent = await client.UploadSongAsync(polyPayload);
    using var document = System.Text.Json.JsonDocument.Parse(handler.LastUploadBody);
    Expect(sent.Ok && document.RootElement.GetProperty("polyphony").GetString() == polyPayload.Polyphony, "HTTP 传谱保持完整多音数据");
}
var polyXml = MusicXmlImporter.Import("""
<score-partwise><part id="P1"><measure implicit="yes"><attributes><divisions>2</divisions></attributes>
<note><pitch><step>C</step><octave>5</octave></pitch><duration>2</duration><voice>1</voice></note>
<note><chord/><pitch><step>E</step><octave>5</octave></pitch><duration>2</duration><voice>1</voice></note>
<backup><duration>2</duration></backup>
<note><pitch><step>C</step><octave>3</octave></pitch><duration>2</duration><voice>2</voice><tie type="start"/><staff>2</staff></note>
</measure><measure implicit="yes"><note><pitch><step>C</step><octave>3</octave></pitch><duration>2</duration><voice>2</voice><tie type="stop"/><staff>2</staff></note></measure>
</part><part id="P2"><measure implicit="yes"><note><pitch><step>G</step><octave>4</octave></pitch><duration>1</duration></note></measure></part></score-partwise>
""", "两手谱", preserveTiming: true, includePerformance: true);
Expect(polyXml.Performance is { Count: 4 } && polyXml.Performance.All(n => n.StartBeats == 0) &&
    polyXml.Performance.Any(n => n.MidiPitch == 48 && n.DurationBeats == 2), "MusicXML 声部、和弦、跨小节连结与不同 part 保留");
try
{
    SongPayloadFactory.Create(polyScore with { Performance = Enumerable.Repeat(new TimedScoreEvent(0, 1, 60), 13).ToArray() });
    Expect(false, "13 个重叠音超过琴端上限");
}
catch (FormatException) { Expect(true, "超过复音上限时明确报错"); }
try { await MobileScoreFiles.ReadAsync(new MemoryStream(new byte[11]), 10); Expect(false, "文件上限"); }
catch (IOException) { Expect(true, "导入流超限停止读取"); }
try { MobileScoreFiles.Import(new byte[14], "假文件.mid"); Expect(false, "MIDI签名"); }
catch (FormatException) { Expect(true, "非MIDI内容不会当作成功导入"); }
try { MobileScoreFiles.Import(new byte[14], "图片.pdf"); Expect(false, "PDF拒绝"); }
catch (FormatException) { Expect(true, "PDF图片不冒充可播放曲谱"); }

var scoreRequests = new List<string>();
using (var scoreHandler = new LyricsHandler(request =>
{
    scoreRequests.Add(request.RequestUri!.AbsoluteUri);
    return new(HttpStatusCode.OK) { Content = new StringContent("<a href='/cchtml/123.htm'>Luv Letter</a>") };
}))
using (var scoreHttp = OnlineScoreClient.CreateClient(scoreHandler))
{
    var singleSource = new MobileScoreClient(scoreHttp);
    var foundScores = await singleSource.SearchAsync("Luv Letter");
    Expect(foundScores.Count == 1 && scoreRequests.Count == 1 && scoreRequests[0] == "https://www.gangqinpu.com/search/0_Luv+Letter_1.htm", "选曲仅发一次虫虫搜索请求且保留原站空格格式");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await singleSource.SearchAsync("曲名", canceled.Token); Expect(false, "查询取消"); }
    catch (OperationCanceledException) { Expect(scoreRequests.Count == 1, "取消查询不再发请求"); }
}
using (var failedScoreHandler = new LyricsHandler(_ => new(HttpStatusCode.ServiceUnavailable)))
using (var failedScoreHttp = OnlineScoreClient.CreateClient(failedScoreHandler))
{
    try { await new MobileScoreClient(failedScoreHttp).SearchAsync("曲名"); Expect(false, "搜索错误"); }
    catch (HttpRequestException) { Expect(true, "虫虫网络错误不冒充零条结果"); }
}
using (var ccmzHandler = new LyricsHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = request.RequestUri!.Host == "www.gangqinpu.com"
        ? new StringContent("https://s202.lzjoy.com/ccmz/public-test.ccmz") : new ByteArrayContent(new byte[] { 1, 2 })
}))
using (var ccmzHttp = OnlineScoreClient.CreateClient(ccmzHandler))
{
    var bytes = await new MobileScoreClient(ccmzHttp).DownloadAsync(new OnlineScore("test", "https://www.gangqinpu.com/cchtml/123.htm"));
    Expect(ccmzHandler.Calls == 2 && bytes.SequenceEqual(new byte[] { 1, 2 }), "虫虫详情和文件下载均使用同一HTTP客户端");
}
using (var slowHandler = new LyricsHandler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new SlowScoreStream()) }))
using (var slowHttp = new HttpClient(slowHandler) { Timeout = TimeSpan.FromMilliseconds(50) })
{
    try { await new MobileScoreClient(slowHttp).SearchAsync("曲名"); Expect(false, "搜索正文超时"); }
    catch (TimeoutException) { Expect(true, "搜索正文卡住会明确超时"); }
    try { await new MobileScoreClient(slowHttp).DownloadAsync(new OnlineScore("test", "https://www.gangqinpu.com/cchtml/123.htm")); Expect(false, "下载正文超时"); }
    catch (TimeoutException) { Expect(true, "下载正文卡住会明确超时"); }
}
using (var uiGuardHandler = new UiGuardHttpHandler(request =>
{
    if (request.RequestUri!.AbsolutePath.EndsWith(".ccmz")) return new byte[] { 1, 2 };
    if (request.RequestUri.AbsolutePath.StartsWith("/cchtml/")) return System.Text.Encoding.UTF8.GetBytes("https://s202.lzjoy.com/ccmz/ui-test.ccmz");
    return System.Text.Encoding.UTF8.GetBytes("<a href='/cchtml/123.htm'>茉莉花</a>");
}))
using (var uiGuardHttp = OnlineScoreClient.CreateClient(uiGuardHandler))
{
    var uiClient = new MobileScoreClient(uiGuardHttp);
    Task<T> CallFromUi<T>(Func<Task<T>> call)
    {
        var previousContext = SynchronizationContext.Current;
        try { SynchronizationContext.SetSynchronizationContext(new UiGuardContext()); return call(); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
    var foundScores = await CallFromUi(() => uiClient.SearchAsync("茉莉花"));
    Expect(foundScores.Count == 1, "从UI上下文搜索时网络流不在UI线程读取");
    var bytes = await CallFromUi(() => uiClient.DownloadAsync(foundScores[0]));
    Expect(bytes.Length == 2 && uiGuardHandler.UiRequests == 0 && uiGuardHandler.UiReads == 0 && uiGuardHandler.Reads > 0,
        "从UI上下文发起搜索和下载均保持网络线程修正");
}
Console.WriteLine($"PASS TOTAL: {passed} mobile core tests");

sealed class UiGuardContext : SynchronizationContext { }
sealed class UiGuardHttpHandler(Func<HttpRequestMessage, byte[]> content) : HttpMessageHandler
{
    public int UiRequests, UiReads, Reads;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (SynchronizationContext.Current is UiGuardContext) Interlocked.Increment(ref UiRequests);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UiGuardStream(content(request), this)) });
    }
}
sealed class UiGuardStream(byte[] bytes, UiGuardHttpHandler owner) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        Interlocked.Increment(ref owner.Reads);
        if (SynchronizationContext.Current is UiGuardContext)
        {
            Interlocked.Increment(ref owner.UiReads);
            throw new InvalidOperationException("NetworkOnMainThreadException: native-style synchronous work inside ReadAsync");
        }
        return base.ReadAsync(buffer, token);
    }
}

sealed class SlowScoreStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { await Task.Delay(Timeout.Infinite, token); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class LyricsHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public string? Url { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Url = request.RequestUri?.AbsoluteUri;
        return Task.FromResult(response(request));
    }
}

sealed class ContractHandler : HttpMessageHandler
{
    public bool SupportsPolyphony { get; init; }
    public List<string> Paths { get; } = new();
    public string LastUploadBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        Paths.Add(request.Method.Method.ToUpperInvariant() + " " + path);
        if (path == "/api/songs/upload") LastUploadBody = await (request.Content?.ReadAsStringAsync(cancellationToken) ?? Task.FromResult(string.Empty));
        string body = path switch
        {
            "/api/status" => "{\"ok\":true,\"deviceId\":\"real-test\",\"name\":\"ESP32\",\"firmwareVersion\":\"0.1\",\"apiVersion\":\"1\",\"mode\":\"sta\",\"volumePercent\":55" + (SupportsPolyphony ? ",\"supportsPolyphony\":true}" : "}"),
            "/api/songs" => "{\"songs\":[{\"id\":\"test-song\",\"title\":\"测试\",\"bpm\":90,\"noteCount\":3}]}",
            _ => "{\"ok\":true,\"message\":\"ok\",\"songId\":\"test-song\"}"
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

sealed class AudioContractHandler : HttpMessageHandler
{
    public bool ValidQuery { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string response = "{\"ok\":true,\"message\":\"ok\"}";
        if (request.Method == HttpMethod.Post)
        {
            byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            using var json = System.Text.Json.JsonDocument.Parse(body);
            if (request.RequestUri!.AbsolutePath != "/api/audio/output" || request.Content.Headers.ContentLength != body.Length ||
                json.RootElement.GetProperty("session").GetUInt32() != 4000000000u || !json.RootElement.GetProperty("enabled").GetBoolean())
                throw new InvalidOperationException("音频 POST 协议不符");
        }
        else
        {
            ValidQuery = request.RequestUri!.PathAndQuery == "/api/audio/events?after=123&session=4000000000";
            response = "{\"reset\":true,\"phoneOutput\":false,\"events\":[[124,100,72,50,0,1,0]]}";
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(response) };
    }
}
sealed class AudioSessionFake(CancellationTokenSource cancel, bool fail) : IDeviceAudioClient
{
    public uint AcquiredSession, ReleasedSession;
    public bool Released;
    public List<uint> Cursors { get; } = new();
    public Task<DeviceResponse> SetPhoneAudioAsync(uint session, bool enabled, CancellationToken cancellationToken)
    {
        if (enabled) AcquiredSession = session; else { Released = true; ReleasedSession = session; }
        return Task.FromResult(new DeviceResponse(true, "ok"));
    }
    public Task<DeviceAudioPacket> GetAudioEventsAsync(uint session, uint after, CancellationToken cancellationToken)
    {
        if (fail) throw new DeviceCommunicationException("模拟断线");
        Cursors.Add(after); int n = Cursors.Count;
        if (n == 8) cancel.Cancel();
        bool phone = n is not (4 or 5);
        DeviceAudioEvent[] events = n is 1 or 2 or 7 ? new[] { new DeviceAudioEvent(7, 100, 72, 50, 0, 1, false) } : Array.Empty<DeviceAudioEvent>();
        return Task.FromResult(new DeviceAudioPacket(n == 1, phone, events));
    }
}
