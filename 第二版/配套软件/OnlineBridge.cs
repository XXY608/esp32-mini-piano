using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using HarmonicaPlayer;
using HarmonicaPlayer.Mobile.Core;

using System.IO;
using System.Net.Http;
using System.Windows;

namespace HarmonicaPlayer;

internal static class OnlineBridge
{
    public static async Task RunAsync()
    {
const string prefix = "http://127.0.0.1:43177/";
using var listener = new HttpListener();
listener.Prefixes.Add(prefix);
try { listener.Start(); }
catch (HttpListenerException e)
{
    try
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var health = JsonDocument.Parse(await client.GetStringAsync(prefix + "health"));
        if (health.RootElement.GetProperty("service").GetString() == "harmonica-online-bridge" &&
            health.RootElement.GetProperty("version").GetInt32() == 1) return;
    }
    catch (Exception) { }
    MessageBox.Show("无法启动联网选曲服务：" + e.Message, "电子琴");
    return;
}
Console.WriteLine("电子琴联网选曲服务：" + prefix);
var results = new ConcurrentDictionary<string, OnlineScore>();
while (listener.IsListening)
{
    HttpListenerContext context;
    try { context = await listener.GetContextAsync(); }
    catch (HttpListenerException) { break; }
    _ = HandleAsync(context);
}

async Task HandleAsync(HttpListenerContext context)
{
    HttpListenerRequest request = context.Request;
    HttpListenerResponse response = context.Response;
    try
    {
        string? origin = request.Headers["Origin"];
        if (origin is not null && origin != "null") { response.StatusCode = 403; return; }
        response.Headers["Access-Control-Allow-Origin"] = "null";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
        response.Headers["Cache-Control"] = "no-store";
        if (request.HttpMethod == "OPTIONS") { response.StatusCode = 204; return; }
        if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/health")
        { await WriteAsync(response, new { ok = true, service = "harmonica-online-bridge", version = 1 }); return; }
        if (request.HttpMethod != "POST" || request.ContentLength64 is < 0 or > 8192)
        { response.StatusCode = 400; await WriteAsync(response, new { ok = false, message = "请求无效" }); return; }
        using var document = await JsonDocument.ParseAsync(request.InputStream);
        var root = document.RootElement;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        if (request.Url?.AbsolutePath == "/search")
        {
            string keyword = root.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String ? query.GetString()?.Trim() ?? "" : "";
            if (keyword.Length is 0 or > 200) throw new ArgumentException("请输入不超过 200 个字符的歌曲名、演唱者或制谱者。");
            var found = await OnlineScoreClient.SearchAsync(keyword, timeout.Token);
            results.Clear();
            var visible = found.Take(40).Select(score => { string id = Guid.NewGuid().ToString("N"); results[id] = score; return new { id, title = score.Title }; }).ToArray();
            await WriteAsync(response, new { ok = true, results = visible }); return;
        }
        if (request.Url?.AbsolutePath == "/download")
        {
            string id = root.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            if (!results.TryGetValue(id, out var score)) throw new ArgumentException("搜索结果已失效，请重新搜索。");
            byte[] data = await OnlineScoreClient.DownloadCcmzAsync(score.Url, timeout.Token);
            ImportedScore imported = CcmzImporter.Import(data, score.Title, preserveTiming: true);
            SongPayload song = SongPayloadFactory.Create(imported);
            if (song.Notes.Count > 1024) throw new InvalidOperationException($"此曲转换后有 {song.Notes.Count} 个音符，超过模拟琴单曲 1024 音的上限，未导入。");
            await WriteAsync(response, new { ok = true, song, sourceText = imported.Text, warnings = imported.Warnings }); return;
        }
        response.StatusCode = 404; await WriteAsync(response, new { ok = false, message = "接口不存在" });
    }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or HttpRequestException or JsonException or OperationCanceledException or FormatException)
    {
        response.StatusCode = 400;
        try { await WriteAsync(response, new { ok = false, message = e is OperationCanceledException ? "联网请求超时，请稍后重试。" : e.Message }); }
        catch (HttpListenerException) { }
    }
    finally { response.Close(); }
}

static async Task WriteAsync(HttpListenerResponse response, object value)
{
    response.ContentType = "application/json; charset=utf-8";
    await JsonSerializer.SerializeAsync(response.OutputStream, value, DeviceJson.Options);
}

    }
}
