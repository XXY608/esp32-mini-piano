using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HarmonicaPlayer;

public sealed record OnlineScore(string Title, string Url);
public sealed record OnlineLyrics(string Text, bool IsSynced, string TrackName, string ArtistName);

public static class OnlineScoreClient
{
    public const string SiteUrl = "https://www.gangqinpu.com/pulist";
    public const int MaxCcmzBytes = 50 * 1024 * 1024;
    public const int MaxLyricsBytes = 1024 * 1024;
    private const int MaxPageBytes = 10 * 1024 * 1024;
    private const string SearchBaseUrl = "https://www.gangqinpu.com/search/0_";
    private const string LyricsSearchUrl = "https://lrclib.net/api/search";
    private static readonly Uri SiteRoot = new("https://www.gangqinpu.com");
    private static readonly HttpClient Http = CreateClient();

    public static async Task<IReadOnlyList<OnlineScore>> SearchAsync(string keyword, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            throw new ArgumentException("请输入歌曲名、演唱者或制谱者。", nameof(keyword));
        string url = SearchBaseUrl + Uri.EscapeDataString(keyword.Trim()) + "_1.htm";
        string html = await GetTextAsync(url, token);
        return ParseSearchResults(html);
    }

    public static Task<byte[]> DownloadCcmzAsync(string scoreUrl, CancellationToken token = default) =>
        DownloadCcmzAsync(scoreUrl, Http, token);

    public static async Task<byte[]> DownloadCcmzAsync(string scoreUrl, HttpClient client, CancellationToken token = default)
    {
        if (!TryNormalizeScoreUrl(scoreUrl, out var pageUri))
            throw new ArgumentException("曲谱详情页地址无效。", nameof(scoreUrl));

        string html = await GetTextAsync(pageUri.AbsoluteUri, token, client);
        string? ccmzUrl = TryFindCcmzUrl(html);
        if (ccmzUrl == null)
            throw new InvalidOperationException("这个曲谱详情页没有找到公开的 CCMZ 文件，可能需要登录、购买或该曲谱不提供 CCMZ。");
        if (!TryCreateAllowedCcmzUri(ccmzUrl, out var fileUri))
            throw new InvalidOperationException("曲谱页面提供的 CCMZ 地址不在允许的公开资源范围内。");

        using var response = await client.GetAsync(fileUri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxCcmzBytes)
            throw new IOException("CCMZ 文件超过 50MB，已拒绝下载。");
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载地址返回的是网页而不是 CCMZ 文件。");

        byte[] data = await ReadLimitedAsync(response.Content, MaxCcmzBytes, token);
        if (data.Length < 2 || data[0] is not (1 or 2))
            throw new InvalidDataException("下载内容不是受支持的 CCMZ 文件。");
        return data;
    }

    public static Task<OnlineLyrics?> FindLyricsAsync(string scoreTitle, CancellationToken token = default) =>
        FindLyricsAsync(scoreTitle, Http, token);

    public static async Task<OnlineLyrics?> FindLyricsAsync(string scoreTitle, HttpClient client, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(scoreTitle)) return null;
        var queries = LyricsSearchQueries(scoreTitle).ToArray();
        for (int index = 0; index < queries.Length; index++)
        {
            if (index > 0) await Task.Delay(250, token);
            string url = LyricsSearchUrl + "?q=" + Uri.EscapeDataString(queries[index]);
            var candidates = ParseLyricsSearchResults(await GetTextAsync(url, token, client));
            OnlineLyrics? best = PickBestLyrics(candidates, queries[index]);
            if (best != null) return best;
        }
        return null;
    }

    public static IReadOnlyList<OnlineLyrics> ParseLyricsSearchResults(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<OnlineLyrics>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<OnlineLyrics>();
            var results = new List<OnlineLyrics>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string track = ReadString(item, "trackName") ?? ReadString(item, "name") ?? "";
                string artist = ReadString(item, "artistName") ?? "";
                string? synced = ReadString(item, "syncedLyrics");
                string? plain = ReadString(item, "plainLyrics");
                string? text = !string.IsNullOrWhiteSpace(synced) ? synced : plain;
                if (string.IsNullOrWhiteSpace(text) || LyricsTooLarge(text)) continue;
                results.Add(new OnlineLyrics(text, !string.IsNullOrWhiteSpace(synced), track, artist));
            }
            return results;
        }
        catch (JsonException error)
        {
            throw new FormatException("在线歌词返回的数据无法解析。", error);
        }
    }

    public static IReadOnlyList<OnlineScore> ParseSearchResults(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<OnlineScore>();
        var results = new List<OnlineScore>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string pattern = "<a\\b(?<attributes>[^>]*)>(?<text>.*?)</a>";
        foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string attributes = match.Groups["attributes"].Value;
            if (!TryNormalizeScoreUrl(GetAttribute(attributes, "href"), out var url)) continue;
            string title = CleanHtmlText(match.Groups["text"].Value);
            if (title.Length == 0) title = CleanHtmlText(GetAttribute(attributes, "title") ?? "");
            if (title.Length == 0 || !seen.Add(url.AbsoluteUri)) continue;
            results.Add(new OnlineScore(title, url.AbsoluteUri));
        }
        return results;
    }

    public static string? TryFindCcmzUrl(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        const string pattern = "https?://[^\\\"'<>\\s]+/ccmz/[^\\\"'<>\\s]+\\.ccmz(?:\\?[^\\\"'<>\\s]*)?";
        foreach (Match match in Regex.Matches(html, pattern, RegexOptions.IgnoreCase))
        {
            string candidate = WebUtility.HtmlDecode(match.Value).Trim().Replace("\\/", "/");
            if (TryCreateAllowedCcmzUri(candidate, out var uri)) return uri.AbsoluteUri;
        }
        return null;
    }

    public static string SafeFileName(string title)
    {
        string invalid = new string(Path.GetInvalidFileNameChars());
        string clean = string.Concat(title.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
        if (clean.Length == 0) clean = "score";
        return clean.Length <= 120 ? clean : clean[..120].TrimEnd();
    }

    public static string LyricsFilePath(string scorePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scorePath)) ?? AppContext.BaseDirectory,
            Path.GetFileNameWithoutExtension(scorePath) + ".lyrics.txt");

    internal static IEnumerable<string> LyricsSearchQueries(string title)
    {
        title = BpmFileName.ReadTitle(title + ".txt");
        string cleaned = Regex.Replace(title, @"[（(][^）)]*[）)]", " ");
        cleaned = Regex.Replace(cleaned, "(?i)(钢琴谱|钢琴|简谱|五线谱|原调|精编|简易版|完整版|带歌词|配歌词|弹唱|独奏谱|伴奏)", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Length == 0) yield break;
        yield return cleaned;
        foreach (char separator in new[] { '-', '—', '–', '|', '｜' })
        {
            int split = cleaned.IndexOf(separator);
            if (split > 0)
            {
                string first = cleaned[..split].Trim();
                if (first.Length >= 2) yield return first;
                break;
            }
        }
    }

    private static OnlineLyrics? PickBestLyrics(IReadOnlyList<OnlineLyrics> candidates, string query)
    {
        if (candidates.Count == 0) return null;
        string normalizedQuery = NormalizeMatchText(query);
        return candidates
            .Select(candidate => (candidate, score: LyricsMatchScore(candidate, normalizedQuery)))
            .Where(item => item.score >= 500)
            .OrderByDescending(item => item.score)
            .ThenByDescending(item => item.candidate.IsSynced)
            .Select(item => item.candidate)
            .FirstOrDefault();
    }

    private static int LyricsMatchScore(OnlineLyrics candidate, string normalizedQuery)
    {
        string track = NormalizeMatchText(candidate.TrackName);
        if (track.Length == 0) return 0;
        int score = track == normalizedQuery ? 1000 :
            track.Contains(normalizedQuery, StringComparison.Ordinal) ? 700 :
            normalizedQuery.Contains(track, StringComparison.Ordinal) ? 500 : 0;
        string artist = NormalizeMatchText(candidate.ArtistName);
        if (score > 0 && artist.Length > 0 && normalizedQuery.Contains(artist, StringComparison.Ordinal)) score += 200;
        return score;
    }

    private static string NormalizeMatchText(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

    private static bool LyricsTooLarge(string text) =>
        text.Length > 200000 || Encoding.UTF8.GetByteCount(text) > MaxLyricsBytes;

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
            : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ElectronicKeyboard/2.0 (https://github.com/lanselanlanxi-wq/HarmonicaPlayer)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.5");
        return client;
    }

    private static async Task<string> GetTextAsync(string url, CancellationToken token, HttpClient? client = null)
    {
        using var response = await (client ?? Http).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        byte[] data = await ReadLimitedAsync(response.Content, MaxPageBytes, token);
        return System.Text.Encoding.UTF8.GetString(data);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        await using Stream input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new IOException($"下载内容超过 {maxBytes / (1024 * 1024)}MB，已拒绝保存。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool TryNormalizeScoreUrl(string? raw, out Uri uri)
    {
        uri = SiteRoot;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string value = WebUtility.HtmlDecode(raw).Trim().Replace("\\/", "/");
        if (!Uri.TryCreate(SiteRoot, value, out var candidate) ||
            !string.Equals(candidate.Host, "www.gangqinpu.com", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(candidate.AbsolutePath, @"^/cchtml/\d+\.htm$", RegexOptions.IgnoreCase))
            return false;
        uri = new Uri(candidate.GetLeftPart(UriPartial.Path));
        return true;
    }

    private static bool TryCreateAllowedCcmzUri(string? raw, out Uri uri)
    {
        uri = SiteRoot;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string value = WebUtility.HtmlDecode(raw).Trim().Replace("\\/", "/");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsAllowedCcmzHost(candidate.Host) ||
            !candidate.AbsolutePath.Contains("/ccmz/", StringComparison.OrdinalIgnoreCase) ||
            !candidate.AbsolutePath.EndsWith(".ccmz", StringComparison.OrdinalIgnoreCase))
            return false;
        uri = candidate;
        return true;
    }

    private static bool IsAllowedCcmzHost(string host) =>
        string.Equals(host, "www.gangqinpu.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".lzjoy.com", StringComparison.OrdinalIgnoreCase);

    private static string? GetAttribute(string attributes, string name)
    {
        string prefix = $@"(?<![\w:-]){Regex.Escape(name)}\s*=\s*";
        foreach (string pattern in new[]
        {
            prefix + @"""(?<value>[^""]*)""",
            prefix + @"'(?<value>[^']*)'",
            prefix + @"(?<value>[^\s""'=<>`]+)"
        })
        {
            var match = Regex.Match(attributes, pattern, RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups["value"].Value;
        }
        return null;
    }

    private static string CleanHtmlText(string value)
    {
        string text = Regex.Replace(value, "<[^>]+>", " ", RegexOptions.Singleline);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, "\\s+", " ").Trim();
    }
}
