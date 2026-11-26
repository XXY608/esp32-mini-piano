using HarmonicaPlayer;
using HarmonicaPlayer.Mobile.Core;

namespace HarmonicaPlayer.Mobile;

public sealed class OnlineScorePage : ContentPage
{
    private readonly MobileAppState state;
    private readonly LocalSongStore songStore;
    private readonly DeviceClientFactory deviceFactory;
    private readonly Entry searchEntry = new() { Placeholder = "曲名或歌手", ReturnType = ReturnType.Search };
    private readonly Button searchButton = Ui.PrimaryButton("搜索");
    private readonly Button cancelButton = Ui.SecondaryButton("取消");
    private readonly VerticalStackLayout resultList = new() { Spacing = 10 };
    private readonly Label messageLabel = Ui.Caption("");
    private CancellationTokenSource? operation;

    public OnlineScorePage(MobileAppState state, LocalSongStore songStore, DeviceClientFactory deviceFactory)
    {
        this.state = state;
        this.songStore = songStore;
        this.deviceFactory = deviceFactory;
        Title = "选曲";
        BackgroundColor = Ui.PageBackground;
        searchButton.Clicked += async (_, _) => await SearchAsync();
        searchEntry.Completed += async (_, _) => await SearchAsync();
        cancelButton.IsVisible = false;
        cancelButton.Clicked += (_, _) => operation?.Cancel();
        Content = new ScrollView
        {
            Content = Ui.PageLayout(Ui.Row(searchEntry, searchButton), Ui.Row(messageLabel, cancelButton), resultList)
        };
    }

    protected override void OnDisappearing() { operation?.Cancel(); base.OnDisappearing(); }

    private CancellationToken BeginOperation()
    {
        operation = new CancellationTokenSource();
        searchButton.IsEnabled = false;
        cancelButton.IsVisible = true;
        messageLabel.TextColor = Ui.Ink;
        return operation.Token;
    }

    private void EndOperation()
    {
        operation?.Dispose(); operation = null;
        searchButton.IsEnabled = true;
        cancelButton.IsVisible = false;
    }

    private async Task SearchAsync()
    {
        if (operation is not null || string.IsNullOrWhiteSpace(searchEntry.Text)) return;
        searchEntry.Unfocus();
        CancellationToken token = BeginOperation();
        resultList.Children.Clear();
        messageLabel.Text = "搜索中…";
        try
        {
            var scores = await MobileScoreClient.Shared.SearchAsync(searchEntry.Text ?? "", token);
            token.ThrowIfCancellationRequested();
            messageLabel.Text = scores.Count > 0 ? "" : "没有找到曲谱";
            foreach (OnlineScore score in scores) AddResult(score);
        }
        catch (OperationCanceledException) { messageLabel.Text = ""; }
        catch (Exception error) { messageLabel.TextColor = Colors.DarkRed; messageLabel.Text = "搜索失败：" + error.Message; }
        finally { EndOperation(); }
    }

    private void AddResult(OnlineScore result)
    {
        var download = Ui.SecondaryButton("获取");
        download.Clicked += async (_, _) => await DownloadAsync(result, download);
        var title = new Label
        {
            Text = result.Title, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink,
            LineBreakMode = LineBreakMode.WordWrap, VerticalOptions = LayoutOptions.Center
        };
        resultList.Children.Add(Ui.Card(Ui.Row(title, download)));
    }

    private async Task DownloadAsync(OnlineScore result, Button button)
    {
        if (operation is not null) return;
        CancellationToken token = BeginOperation();
        button.IsEnabled = false;
        messageLabel.Text = "获取曲谱中…";
        try
        {
            byte[] data = await MobileScoreClient.Shared.DownloadAsync(result, token);
            token.ThrowIfCancellationRequested();
            ImportedScore score = await Task.Run(() => MobileScoreFiles.Import(data, result.Title + ".ccmz", includePerformance: true), token);
            token.ThrowIfCancellationRequested();
            score = score with { Title = result.Title };
            var stored = new StoredSong(SongPayloadFactory.Create(score), score.Text, DateTimeOffset.UtcNow, string.Empty, false);
            bool saved = await songStore.TrySaveNewAsync(stored, token);
            token.ThrowIfCancellationRequested();
            messageLabel.Text = "";
            await Navigation.PushAsync(new ImportPage(state, songStore, deviceFactory, stored, saved, score.Warnings));
        }
        catch (OperationCanceledException) { messageLabel.Text = ""; }
        catch (Exception error) { messageLabel.TextColor = Colors.DarkRed; messageLabel.Text = "下载失败：" + error.Message; }
        finally { button.IsEnabled = true; EndOperation(); }
    }
}
