using System.Net.Http.Headers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarmonicaPlayer.Mobile.Core;

public static class DeviceJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false
    };
}

public sealed record SongNoteDto(
    [property: JsonPropertyName("pitch")] int Pitch,
    [property: JsonPropertyName("octave")] int Octave,
    [property: JsonPropertyName("sharp")] bool Sharp,
    [property: JsonPropertyName("durationBeats")] double DurationBeats,
    [property: JsonPropertyName("lyric")] string Lyric = "")
{
    [JsonPropertyName("phraseBefore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PhraseBefore { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BarBefore { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LineBefore { get; init; }
}

public sealed record SongPayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("bpm")] int? Bpm,
    [property: JsonPropertyName("sourceType")] string SourceType,
    [property: JsonPropertyName("notes")] IReadOnlyList<SongNoteDto> Notes)
{
    [JsonPropertyName("lyricCues")]
    public IReadOnlyList<SongLyricCueDto>? LyricCues { get; init; }
    [JsonPropertyName("polyphony")]
    public string? Polyphony { get; init; }
    [JsonPropertyName("pedal")]
    public IReadOnlyList<uint>? Pedal { get; init; }
    public string? TimeSignature { get; init; }
}

public sealed record SongLyricCueDto(
    [property: JsonPropertyName("startBeat")] double StartBeat,
    [property: JsonPropertyName("text")] string Text);

public sealed record DeviceStatusDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("message")] string Message = "",
    [property: JsonPropertyName("volumePercent")] int VolumePercent = 80,
    [property: JsonPropertyName("supportsLyricCues")] bool SupportsLyricCues = false,
    [property: JsonPropertyName("supportsPolyphony")] bool SupportsPolyphony = false);

public sealed record DeviceSongSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("bpm")] int? Bpm,
    [property: JsonPropertyName("noteCount")] int NoteCount);

public sealed record DeviceResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("songId")] string? SongId = null);

public interface IDeviceClient : IDisposable
{
    Task<DeviceStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceSongSummary>> GetSongsAsync(CancellationToken cancellationToken = default);
    Task<DeviceResponse> UploadSongAsync(SongPayload song, CancellationToken cancellationToken = default);
    Task<DeviceResponse> PlaySongAsync(string songId, CancellationToken cancellationToken = default);
    Task<DeviceResponse> TeachSongAsync(string songId, CancellationToken cancellationToken = default);
    Task<DeviceResponse> StopAsync(CancellationToken cancellationToken = default);
    Task<DeviceResponse> SetVolumeAsync(int volumePercent, CancellationToken cancellationToken = default);
    Task<DeviceResponse> DeleteSongAsync(string songId, CancellationToken cancellationToken = default);
}

public sealed class DeviceCommunicationException : Exception
{
    public DeviceCommunicationException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public static class SongPayloadFactory
{
    private const int TicksPerBeat = 960, MaximumTicks = 65536 * TicksPerBeat, MaximumVoices = 12;

    public static SongPayload Create(ImportedScore score, string? id = null, string? lyricsText = null, double lyricsOffsetSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(score);
        score.Lyrics?.Validate(score.Notes);
        var syllables = score.Lyrics?.Syllables;
        var words = syllables?.ToDictionary(w => w.StartNote, w => w.Text);
        var phraseStarts = new HashSet<int>();
        if (syllables is not null)
            for (int i = 1; i < syllables.Count; i++)
            {
                var previous = syllables[i - 1];
                if (previous.EndLine || (!string.IsNullOrEmpty(previous.Text) && "。！？!?；;".Contains(previous.Text[^1])))
                    phraseStarts.Add(syllables[i].StartNote);
            }
        var notes = score.Notes.Select((note, index) => new SongNoteDto(note.Degree, note.Octave,
            note.Sharp, note.Beats, words?.GetValueOrDefault(index) ?? string.Empty)
            { PhraseBefore = phraseStarts.Contains(index) }).ToArray();
        if (notes.Any(n => Encoding.UTF8.GetByteCount(n.Lyric) > 24))
            throw new FormatException("设备逐音歌词每音最多24字节，请使用整句歌词时间轴。");
        IReadOnlyList<SongLyricCueDto>? cues = null;
        if (words is null || words.Count == 0)
            cues = MobileDeviceLyrics.CreateCues(score, lyricsText, lyricsOffsetSeconds);
        if (score.Performance is not null && notes.Length > 1024)
            throw new FormatException("练习用主旋律超过琴端 1024 音限制，请将曲谱分段。");
        return new SongPayload(
            string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("D") : id,
            score.Title,
            score.Performance is null ? score.Bpm : score.PlaybackBpm,
            score.Performance is null ? "txt" : "polyphonic",
            notes) { LyricCues = cues, Polyphony = score.Performance is { } performance ? EncodePerformance(performance) : null,
                Pedal = score.Performance != null && score.SustainPedal is { Count: > 0 } pedals ? EncodePedal(pedals) : null };
    }

    private static IReadOnlyList<uint> EncodePedal(IReadOnlyList<(double Beat, bool Down)> pedals)
    {
        var result = new List<uint>();
        bool down = false;
        foreach (var pedal in pedals.OrderBy(p => p.Beat))
        {
            if (down == pedal.Down) continue;
            if (!double.IsFinite(pedal.Beat) || pedal.Beat < 0 || pedal.Beat * TicksPerBeat > MaximumTicks || result.Count >= 512)
                throw new FormatException("延音踏板时间轴无效或超过 512 次切换。");
            uint tick = (uint)Math.Round(pedal.Beat * TicksPerBeat);
            result.Add(tick * 2 + (pedal.Down ? 1u : 0u));
            down = pedal.Down;
        }
        return result;
    }

    // Each event contains two little-endian tick positions, MIDI pitch and velocity.
    // Encoding the timeline as one JSON string keeps the ESP32 JSON tree small.
    private static string EncodePerformance(IReadOnlyList<TimedScoreEvent> performance)
    {
        var notes = performance.Where(n => !n.IsRest).OrderBy(n => n.StartBeats).ToArray();
        if (notes.Length is < 1 or > 4096) throw new FormatException("多音曲谱须包含 1～4096 个演奏音符。");
        var bytes = new byte[notes.Length * 10];
        var active = new PriorityQueue<int, int>();
        for (int i = 0; i < notes.Length; i++)
        {
            var note = notes[i];
            if (!double.IsFinite(note.StartBeats) || !double.IsFinite(note.DurationBeats) ||
                note.StartBeats < 0 || note.DurationBeats <= 0 || (note.StartBeats + note.DurationBeats) * TicksPerBeat > MaximumTicks)
                throw new FormatException("多音谱含有无效时值，或超过琴端最长时长。");
            if (note.MidiPitch is < 21 or > 108 || note.Velocity is < 1 or > 127)
                throw new FormatException("多音谱音高须在钢琴 88 键范围内，力度须为 1～127。");
            int start = (int)Math.Round(note.StartBeats * TicksPerBeat);
            int end = Math.Max(start + 1, (int)Math.Round((note.StartBeats + note.DurationBeats) * TicksPerBeat));
            if (end > MaximumTicks) throw new FormatException("多音谱超过琴端最长时长。");
            while (active.TryPeek(out _, out int due) && due <= start) active.Dequeue();
            if (active.Count == MaximumVoices) throw new FormatException("曲谱超过琴端 12 音同时发声上限，请先精简伴奏。");
            active.Enqueue(end, end);
            var destination = bytes.AsSpan(i * 10, 10);
            BinaryPrimitives.WriteInt32LittleEndian(destination, start);
            BinaryPrimitives.WriteInt32LittleEndian(destination[4..], end);
            destination[8] = (byte)note.MidiPitch;
            destination[9] = (byte)note.Velocity;
        }
        return Convert.ToBase64String(bytes);
    }

    public static byte[] SerializeForDevice(SongPayload song)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(song, DeviceJson.Options);
        if (data.Length > 120 * 1024)
            throw new FormatException("曲谱超过琴端 120 KiB 限制，请将曲谱分段或缩短歌词。");
        return data;
    }
}

public sealed record StoredSong(
    [property: JsonPropertyName("song")] SongPayload Song,
    [property: JsonPropertyName("sourceText")] string SourceText,
    [property: JsonPropertyName("importedAtUtc")] DateTimeOffset ImportedAtUtc,
    [property: JsonPropertyName("lyricsText")] string LyricsText = "",
    [property: JsonPropertyName("lyricsIsSynced")] bool LyricsIsSynced = false,
    [property: JsonPropertyName("lyricsOffsetSeconds")] double LyricsOffsetSeconds = 0);

public interface ISongStore
{
    Task<IReadOnlyList<StoredSong>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(StoredSong song, CancellationToken cancellationToken = default);
    Task DeleteAsync(string songId, CancellationToken cancellationToken = default);
}

public class JsonSongStore : ISongStore, IDisposable
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    public JsonSongStore(string path)
    {
        this.path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    }

    public async Task<IReadOnlyList<StoredSong>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var songs = CollapseDuplicates(await LoadNoLockAsync(cancellationToken));
            return songs;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(StoredSong song, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var songs = CollapseDuplicates(await LoadNoLockAsync(cancellationToken));
            string title = song.Song.Title.Trim();
            songs.RemoveAll(item =>
                string.Equals(item.Song.Id, song.Song.Id, StringComparison.OrdinalIgnoreCase) ||
                (title.Length > 0 && string.Equals(item.Song.Title.Trim(), title, StringComparison.OrdinalIgnoreCase)));
            songs.Insert(0, song);
            await WriteNoLockAsync(songs, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> TrySaveNewAsync(StoredSong song, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var songs = CollapseDuplicates(await LoadNoLockAsync(cancellationToken)).ToList();
            if (songs.Any(item => string.Equals(item.Song.Id, song.Song.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Song.Title.Trim(), song.Song.Title.Trim(), StringComparison.OrdinalIgnoreCase)))
                return false;
            songs.Insert(0, song);
            await WriteNoLockAsync(songs, cancellationToken);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task DeleteAsync(string songId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(songId)) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var songs = (await LoadNoLockAsync(cancellationToken)).ToList();
            songs.RemoveAll(item => string.Equals(item.Song.Id, songId, StringComparison.OrdinalIgnoreCase));
            await WriteNoLockAsync(songs, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<StoredSong>> LoadNoLockAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return Array.Empty<StoredSong>();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<StoredSong>>(stream, DeviceJson.Options, cancellationToken)
            ?? new List<StoredSong>();
    }

    private async Task WriteNoLockAsync(IReadOnlyList<StoredSong> songs, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, songs, DeviceJson.Options, cancellationToken);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static List<StoredSong> CollapseDuplicates(IEnumerable<StoredSong> source)
    {
        var result = new List<StoredSong>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StoredSong song in source)
        {
            string title = song.Song.Title.Trim();
            if (!ids.Add(song.Song.Id) || (title.Length > 0 && !titles.Add(title))) continue;
            result.Add(song);
        }
        return result;
    }

    public void Dispose() => gate.Dispose();
}

public sealed class HttpEsp32Client : IDeviceClient, IDeviceAudioClient
{
    private readonly HttpClient http;

    public HttpEsp32Client(string baseAddress, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(baseAddress?.Trim(), UriKind.Absolute, out Uri? baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("设备地址必须是 http:// 或 https:// 地址。", nameof(baseAddress));

        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        http.Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(8) : timeout;
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<DeviceStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await SendDocumentAsync(HttpMethod.Get, "api/status", null, cancellationToken);
        EnsureOk(document.RootElement);
        var root = document.RootElement;
        return new DeviceStatusDto(
            ReadBool(root, "ok", true),
            ReadString(root, "deviceId") ?? "unknown",
            ReadString(root, "name") ?? "ESP32-S3 电子乐器",
            ReadString(root, "firmwareVersion") ?? "unknown",
            ReadString(root, "apiVersion") ?? "1",
            ReadString(root, "mode") ?? "unknown",
            ReadString(root, "message") ?? "",
            ReadInt(root, "volumePercent", 80),
            ReadBool(root, "supportsLyricCues", false), ReadBool(root, "supportsPolyphony", false));
    }

    public async Task<IReadOnlyList<DeviceSongSummary>> GetSongsAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await SendDocumentAsync(HttpMethod.Get, "api/songs", null, cancellationToken);
        EnsureOk(document.RootElement);
        JsonElement root = document.RootElement;
        JsonElement songs = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("songs", out JsonElement wrapped) ? wrapped : default;
        if (songs.ValueKind != JsonValueKind.Array)
            throw new DeviceCommunicationException("设备歌曲列表格式无法识别。");

        return songs.Deserialize<List<DeviceSongSummary>>(DeviceJson.Options) ?? new List<DeviceSongSummary>();
    }

    public async Task<DeviceResponse> UploadSongAsync(SongPayload song, CancellationToken cancellationToken = default)
    {
        if (song.LyricCues is { Count: > 0 } || song.Polyphony is not null)
        {
            var status = await GetStatusAsync(cancellationToken);
            if (song.Polyphony is not null && (!status.Ok || !status.SupportsPolyphony))
                throw new DeviceCommunicationException("琴端固件尚不支持多音自动播放，请先更新实体琴固件。");
            if (song.LyricCues is { Count: > 0 } && (!status.Ok || !status.SupportsLyricCues))
                throw new DeviceCommunicationException("琴端固件尚不支持LRC整句歌词，请先更新至0.6.14或以上版本后重新发送。");
        }
        try { SongPayloadFactory.SerializeForDevice(song); }
        catch (FormatException error) { throw new DeviceCommunicationException(error.Message, error); }
        return await SendResponseAsync(HttpMethod.Post, "api/songs/upload", song, cancellationToken);
    }

    public Task<DeviceResponse> PlaySongAsync(string songId, CancellationToken cancellationToken = default) =>
        SendResponseAsync(HttpMethod.Post, "api/song/play", new { id = songId }, cancellationToken);

    public Task<DeviceResponse> TeachSongAsync(string songId, CancellationToken cancellationToken = default) =>
        SendResponseAsync(HttpMethod.Post, "api/song/teach", new { id = songId }, cancellationToken);

    public Task<DeviceResponse> StopAsync(CancellationToken cancellationToken = default) =>
        SendResponseAsync(HttpMethod.Post, "api/song/stop", null, cancellationToken);

    public Task<DeviceResponse> SetPhoneAudioAsync(uint session, bool enabled, CancellationToken cancellationToken) =>
        SendResponseAsync(HttpMethod.Post, "api/audio/output", new { session, enabled }, cancellationToken);

    public async Task<DeviceAudioPacket> GetAudioEventsAsync(uint session, uint after, CancellationToken cancellationToken)
    {
        using var document = await SendDocumentAsync(HttpMethod.Get, $"api/audio/events?after={after}&session={session}", null, cancellationToken);
        EnsureOk(document.RootElement);
        return DeviceAudioPacket.Parse(document.RootElement);
    }

    public Task<DeviceResponse> SetVolumeAsync(int volumePercent, CancellationToken cancellationToken = default) =>
        SendResponseAsync(HttpMethod.Post, "api/device/volume",
            new { volumePercent = Math.Clamp(volumePercent, 0, 100) }, cancellationToken);

    public Task<DeviceResponse> DeleteSongAsync(string songId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(songId)) throw new ArgumentException("歌曲 ID 不能为空。", nameof(songId));
        return SendResponseAsync(HttpMethod.Delete, "api/songs/" + Uri.EscapeDataString(songId), null, cancellationToken);
    }

    private async Task<DeviceResponse> SendResponseAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendDocumentAsync(method, path, body, cancellationToken);
        JsonElement root = document.RootElement;
        return new DeviceResponse(
            ReadBool(root, "ok", true),
            ReadString(root, "message") ?? (ReadBool(root, "ok", true) ? "设备已完成请求。" : "设备拒绝了请求。"),
            ReadString(root, "songId"));
    }

    private async Task<JsonDocument> SendDocumentAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            // Android may use chunked transfer for JsonContent. The ESP32
            // Arduino WebServer 2.0.x parser expects a concrete Content-Length
            // for a raw JSON POST, especially for larger song payloads.
            byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, DeviceJson.Options));
            var content = new ByteArrayContent(jsonBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
            request.Content = content;
        }

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string detail = TryReadMessage(text) ?? response.ReasonPhrase ?? "设备返回错误";
                throw new DeviceCommunicationException($"设备返回 HTTP {(int)response.StatusCode}：{detail}");
            }
            if (string.IsNullOrWhiteSpace(text))
                return JsonDocument.Parse("{\"ok\":true,\"message\":\"设备已完成请求。\"}");
            try { return JsonDocument.Parse(text); }
            catch (JsonException error) { throw new DeviceCommunicationException("设备返回的 JSON 无法解析。", error); }
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeviceCommunicationException("连接设备超时，请确认手机与 ESP32 在同一局域网。", error);
        }
        catch (HttpRequestException error)
        {
            throw new DeviceCommunicationException("无法连接设备，请检查地址、热点/AP 和设备电源。", error);
        }
    }

    private static void EnsureOk(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out JsonElement ok) &&
            ok.ValueKind == JsonValueKind.False)
            throw new DeviceCommunicationException(ReadString(root, "message") ?? "设备返回失败。");
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) &&
        value.TryGetInt32(out int parsed) ? parsed : fallback;

    private static string? TryReadMessage(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return ReadString(document.RootElement, "message");
        }
        catch (JsonException) { return null; }
    }

    public void Dispose() => http.Dispose();
}

public sealed class MockEsp32Client : IDeviceClient
{
    private readonly Dictionary<string, SongPayload> songs = new(StringComparer.OrdinalIgnoreCase);
    private string lastMessage = "Mock 设备已启动。";
    private int volumePercent = 80;

    public Task<DeviceStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceStatusDto(true, "mock-esp32-s3", "Mock ESP32-S3", "mock-1.0", "1", "mock", lastMessage, volumePercent, true));

    public Task<IReadOnlyList<DeviceSongSummary>> GetSongsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DeviceSongSummary> result = songs.Values
            .Select(song => new DeviceSongSummary(song.Id, song.Title, song.Bpm, song.Notes.Count))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<DeviceResponse> UploadSongAsync(SongPayload song, CancellationToken cancellationToken = default)
    {
        if (song.Notes.Count == 0) return Task.FromResult(new DeviceResponse(false, "歌曲没有音符，Mock 设备拒绝上传。"));
        songs[song.Id] = song;
        lastMessage = $"Mock 已收到《{song.Title}》的 {song.Notes.Count} 个音符。";
        return Task.FromResult(new DeviceResponse(true, lastMessage, song.Id));
    }

    public Task<DeviceResponse> PlaySongAsync(string songId, CancellationToken cancellationToken = default) => Command(songId, "播放");
    public Task<DeviceResponse> TeachSongAsync(string songId, CancellationToken cancellationToken = default) => Command(songId, "练习");

    public Task<DeviceResponse> StopAsync(CancellationToken cancellationToken = default)
    {
        lastMessage = "Mock 已停止播放/练习。";
        return Task.FromResult(new DeviceResponse(true, lastMessage));
    }

    public Task<DeviceResponse> SetVolumeAsync(int volumePercent, CancellationToken cancellationToken = default)
    {
        volumePercent = Math.Clamp(volumePercent, 0, 100);
        this.volumePercent = volumePercent;
        lastMessage = $"Mock 音量已设置为 {volumePercent}%。";
        return Task.FromResult(new DeviceResponse(true, lastMessage));
    }

    public Task<DeviceResponse> DeleteSongAsync(string songId, CancellationToken cancellationToken = default)
    {
        if (!songs.Remove(songId)) return Task.FromResult(new DeviceResponse(false, "Mock 中未找到这首歌曲。"));
        lastMessage = "Mock 已删除歌曲。";
        return Task.FromResult(new DeviceResponse(true, lastMessage, songId));
    }

    private Task<DeviceResponse> Command(string songId, string action)
    {
        if (!songs.ContainsKey(songId)) return Task.FromResult(new DeviceResponse(false, "Mock 中未找到这首歌曲。"));
        lastMessage = $"Mock 已执行：{action}《{songs[songId].Title}》。";
        return Task.FromResult(new DeviceResponse(true, lastMessage, songId));
    }

    public void Dispose() { }
}
