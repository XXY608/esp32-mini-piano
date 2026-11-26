using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonicaPlayer;

namespace HarmonicaPlayer.Mobile.Core;

public sealed record NeteaseSong(long Id, string Name, string Artist);

public static class NeteaseLyrics
{
    public static async Task<OnlineLyrics?> FindAsync(string title, HttpClient client, CancellationToken token = default,
        Func<IReadOnlyList<NeteaseSong>, Task<NeteaseSong?>>? choose = null)
    {
        foreach (string query in OnlineScoreClient.LyricsSearchQueries(title))
        {
            string json = await GetAsync(client, "https://music.163.com/api/search/get?s=" +
                Uri.EscapeDataString(query) + "&type=1&limit=10", token);
            using var search = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            CheckCode(search.RootElement);
            if (!search.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) continue;
            var matches = new List<NeteaseSong>();
            foreach (var song in songs.EnumerateArray())
            {
                if (!song.TryGetProperty("id", out var id) || !id.TryGetInt64(out long songId) || songId <= 0 ||
                    !song.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                string track = name.GetString() ?? "";
                string artist = song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                    ? string.Join(" / ", artists.EnumerateArray().Select(a => a.TryGetProperty("name", out var n) &&
                        n.ValueKind == JsonValueKind.String ? n.GetString() : "").Where(a => !string.IsNullOrWhiteSpace(a))) : "";
                string q = Normalize(query), t = Normalize(track), a = Normalize(artist);
                // Do not silently substitute a similarly named song or a live/remix version.
                if (t.Length > 0 && (q == t || q == t + a || q == a + t)) matches.Add(new(songId, track, artist));
            }
            var versions = matches.GroupBy(m => (Normalize(m.Name), Normalize(m.Artist))).Select(g => g.First()).ToArray();
            if (versions.Length > 1)
            {
                if (choose is null) throw new InvalidOperationException("找到多位歌手的同名歌曲，请补充歌手名或选择版本。");
                var selected = await choose(versions);
                token.ThrowIfCancellationRequested();
                if (selected is null) throw new OperationCanceledException("已取消歌词版本选择。");
                versions = versions.Where(v => v.Id == selected.Id).ToArray();
            }
            foreach (var song in versions.Take(1))
            {
                await Task.Delay(250, token);
                string body = await GetAsync(client, $"https://music.163.com/api/song/lyric?id={song.Id}&lv=-1&kv=-1&tv=-1", token);
                using var lyrics = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
                CheckCode(lyrics.RootElement);
                if (IsTrue(lyrics.RootElement, "nolyric") || IsTrue(lyrics.RootElement, "uncollected")) continue;
                if (!lyrics.RootElement.TryGetProperty("lrc", out var lrc) ||
                    !lrc.TryGetProperty("lyric", out var text) || text.ValueKind != JsonValueKind.String) continue;
                string value = text.GetString() ?? "";
                if (value.Length > 200000 || MobileLyricsTimeline.Parse(value).Count == 0) continue;
                return new OnlineLyrics(value, true, song.Name, song.Artist);
            }
        }
        return null;
    }

    private static bool IsTrue(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

    private static void CheckCode(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out int number) || number != 200)
            throw new FormatException("网易云歌词接口返回失败或无效数据。");
    }

    private static async Task<string> GetAsync(HttpClient client, string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > OnlineScoreClient.MaxLyricsBytes)
            throw new IOException("歌词接口返回数据超过1MB。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > OnlineScoreClient.MaxLyricsBytes)
                throw new IOException("歌词接口返回数据超过1MB。");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
