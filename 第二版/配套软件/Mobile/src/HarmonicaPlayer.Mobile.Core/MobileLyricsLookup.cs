using System.Net.Http;
using HarmonicaPlayer;

namespace HarmonicaPlayer.Mobile.Core;

public sealed record MobileLyricsMatch(OnlineLyrics Lyrics, string Source);

public static class MobileLyricsLookup
{
    private static readonly HttpClient Native = OnlineScoreClient.CreateClient();
    private static readonly HttpClient Managed = OnlineScoreClient.CreateClient(new SocketsHttpHandler());

    public static Task<MobileLyricsMatch?> FindAsync(string title, CancellationToken token = default,
        Func<IReadOnlyList<NeteaseSong>, Task<NeteaseSong?>>? choose = null) =>
        FindWithSourcesAsync(title, Native, Managed, token, choose);

    public static async Task<MobileLyricsMatch?> FindWithSourcesAsync(string title, HttpClient native,
        HttpClient managed, CancellationToken token = default,
        Func<IReadOnlyList<NeteaseSong>, Task<NeteaseSong?>>? choose = null)
    {
        Exception? sourceError = null;
        try
        {
            var lyrics = await NeteaseLyrics.FindAsync(title, native, token, choose);
            if (lyrics is not null) return new(lyrics, "网易云音乐");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or FormatException or System.Text.Json.JsonException)
        { sourceError = error; }
        try
        {
            var lyrics = await FindAsync(title, native, managed, token);
            if (lyrics is not null) return new(lyrics, "LRCLIB");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or FormatException or System.Text.Json.JsonException)
        {
            throw new HttpRequestException("歌词查询未完成。网易云音乐：" +
                (sourceError is null ? "未找到可用歌词" : DescribeError(sourceError)) + "\nLRCLIB：", error);
        }
        if (sourceError is not null)
            throw new HttpRequestException("网易云音乐查询失败，LRCLIB未找到匹配歌词：", sourceError);
        return null;
    }

    // Android's native HTTP implementation can fail even when the browser works.
    // Try the managed transport only on connection errors, keeping normal TLS validation.
    public static async Task<OnlineLyrics?> FindAsync(string title, HttpClient native, HttpClient managed,
        CancellationToken token = default)
    {
        try
        {
            return await OnlineScoreClient.FindLyricsAsync(title, native, token);
        }
        catch (HttpRequestException first) when (first.StatusCode is null && !token.IsCancellationRequested)
        {
            try
            {
                return await OnlineScoreClient.FindLyricsAsync(title, managed, token);
            }
            catch (Exception second) when (second is not OperationCanceledException && !token.IsCancellationRequested)
            {
                throw new HttpRequestException("无法连接 lrclib.net。\n系统连接：" + DescribeError(first) +
                    "\n备用连接：", second);
            }
        }
    }

    public static string DescribeError(Exception error)
    {
        var parts = new List<string>();
        for (Exception? current = error; current is not null; current = current.InnerException)
            parts.Add(current.GetType().Name + ": " + current.Message);
        return string.Join(" → ", parts);
    }
}
