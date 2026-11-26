using System.Net;
using System.Text;
using HarmonicaPlayer;

namespace HarmonicaPlayer.Mobile.Core;

public sealed class MobileScoreClient
{
    public static MobileScoreClient Shared { get; } = new();
    private readonly HttpClient http;

    public MobileScoreClient(HttpClient? http = null) => this.http = http ??
        OnlineScoreClient.CreateClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });

    // Native Android streams may perform synchronous I/O inside ReadAsync.
    public Task<IReadOnlyList<OnlineScore>> SearchAsync(string keyword, CancellationToken token = default) =>
        Task.Run(() => SearchCoreAsync(keyword, token), token);

    private async Task<IReadOnlyList<OnlineScore>> SearchCoreAsync(string keyword, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(keyword)) throw new ArgumentException("请输入歌曲名、演唱者或制谱者。");
        string url = "https://www.gangqinpu.com/search/0_" + Uri.EscapeDataString(keyword.Trim()).Replace("%20", "+") + "_1.htm";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(http.Timeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            const int pageLimit = 10 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > pageLimit) throw new IOException("曲谱页面超过大小限制。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[] bytes = await MobileScoreFiles.ReadAsync(stream, pageLimit, deadline.Token);
            return OnlineScoreClient.ParseSearchResults(Encoding.UTF8.GetString(bytes)).Take(40).ToArray();
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("虫虫钢琴请求超时，请稍后重试。", error); }
    }

    public Task<byte[]> DownloadAsync(OnlineScore score, CancellationToken token = default) =>
        Task.Run(() => DownloadCoreAsync(score, token), token);

    private async Task<byte[]> DownloadCoreAsync(OnlineScore score, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(http.Timeout);
        try { return await OnlineScoreClient.DownloadCcmzAsync(score.Url, http, deadline.Token); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("虫虫钢琴下载超时，请稍后重试。", error); }
    }
}
