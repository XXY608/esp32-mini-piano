using HarmonicaPlayer.Mobile.Core;

namespace HarmonicaPlayer.Mobile;

public sealed class DevicePage : ContentPage
{
    private readonly MobileAppState state;
    private readonly DeviceClientFactory deviceFactory;
    private readonly DeviceAudioService deviceAudio;
    private readonly Switch audioSwitch = new();
    private readonly Label audioStatus = Ui.Caption("");
    private readonly Slider volumeSlider = new() { Minimum = 0, Maximum = 100, MinimumTrackColor = Ui.Ink };
    private readonly Label volumeLabel = Ui.Caption("");
    private readonly Label statusLabel = Ui.Heading("未连接");
    private readonly Label messageLabel = Ui.Caption("");
    private readonly Button stopButton = Ui.SecondaryButton("停止");
    private readonly Button applyVolumeButton = Ui.TextButton("设置");
    private readonly Button reconnectButton = Ui.TextButton("重连");
    private readonly VerticalStackLayout songList = new() { Spacing = 6 };
    private readonly RefreshView refreshView;
    private DeviceStatusDto? currentStatus;
    private string? connectionError;
    private bool loading;
    private bool sending;

    public DevicePage(MobileAppState state, DeviceClientFactory deviceFactory, DeviceAudioService deviceAudio)
    {
        this.state = state;
        this.deviceFactory = deviceFactory;
        this.deviceAudio = deviceAudio;
        Title = "电子琴";
        BackgroundColor = Ui.PageBackground;

        var menu = Ui.MoreButton("连接操作");
        menu.Clicked += async (_, _) => await OpenConnectionMenuAsync();
        stopButton.Clicked += async (_, _) => await SendCommandAsync("停止", client => client.StopAsync());
        applyVolumeButton.Clicked += async (_, _) => await ApplyVolumeAsync();
        reconnectButton.Clicked += (_, _) => deviceAudio.Refresh();
        SemanticProperties.SetDescription(volumeSlider, "琴端音量");
        volumeSlider.ValueChanged += (_, args) => volumeLabel.Text = $"琴端音量 {Math.Round(args.NewValue):0}%";
        audioSwitch.Toggled += (_, args) =>
        {
            state.AutoDeviceAudio = args.Value;
            UpdateAudioStatus();
        };
        SemanticProperties.SetDescription(audioSwitch, "通过手机发声");
        deviceAudio.Changed += UpdateAudioStatus;
        volumeSlider.Value = state.DeviceVolumePercent;
        volumeLabel.Text = $"琴端音量 {state.DeviceVolumePercent}%";
        SetConnected(false);
        ShowMessage("");

        refreshView = new RefreshView
        {
            Content = new ScrollView
            {
                Content = Ui.PageLayout(
                    Ui.Row(statusLabel, menu),
                    Ui.Card(new VerticalStackLayout
                    {
                        Spacing = 3,
                        Children = { volumeLabel, Ui.Row(volumeSlider, applyVolumeButton) }
                    }),
                    Ui.Card(new VerticalStackLayout
                    {
                        Spacing = 3,
                        Children = { Ui.Row(Ui.Caption("用手机发声"), audioSwitch), Ui.Row(audioStatus, reconnectButton) }
                    }),
                    Ui.Row(new Label { Text = "琴内曲库", FontAttributes = FontAttributes.Bold, TextColor = Ui.Ink,
                        VerticalOptions = LayoutOptions.Center }, stopButton),
                    messageLabel, songList)
            }
        };
        refreshView.Refreshing += async (_, _) => await RefreshAsync();
        Content = refreshView;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        audioSwitch.IsToggled = state.AutoDeviceAudio;
        UpdateAudioStatus();
        await RefreshAsync();
    }

    private void UpdateAudioStatus()
    {
        audioStatus.Text = deviceAudio.Status;
        audioStatus.IsVisible = reconnectButton.IsVisible = state.AutoDeviceAudio;
    }

    private void SetConnected(bool connected)
    {
        stopButton.IsEnabled = applyVolumeButton.IsEnabled = volumeSlider.IsEnabled = connected;
        statusLabel.Text = connected ? (state.UseMock ? "模拟电子琴" : "已连接") : "未连接";
        statusLabel.TextColor = connected ? Colors.DarkGreen : Ui.Ink;
    }

    private async Task OpenConnectionMenuAsync()
    {
        string? choice = await DisplayActionSheetAsync(null, "取消", null, "刷新", "连接详情", "连接设置");
        if (choice == "刷新") await RefreshAsync();
        else if (choice == "连接设置") await Shell.Current.GoToAsync("//settings");
        else if (choice == "连接详情")
        {
            string details = currentStatus is { } status
                ? $"{status.Name}\n{state.DeviceAddress}\n设备 ID {status.DeviceId}\n固件 {status.FirmwareVersion} · API {status.ApiVersion}\n{status.Mode}"
                : $"{state.DeviceAddress}\n{connectionError ?? "未连接"}";
            if (state.AutoDeviceAudio && deviceAudio.LastError is { } audioError)
                details += "\n手机发声：" + audioError;
            if (currentStatus is not null && connectionError is not null)
                details += "\n曲库：" + connectionError;
            await DisplayAlertAsync("连接详情", details, "关闭");
        }
    }

    private async Task RefreshAsync()
    {
        if (loading) return;
        loading = true;
        ShowMessage("");
        try
        {
            using IDeviceClient client = deviceFactory.Create();
            DeviceStatusDto status = await client.GetStatusAsync();
            if (!status.Ok) throw new InvalidOperationException(status.Message);
            currentStatus = status;
            connectionError = null;
            SetConnected(true);
            volumeSlider.Value = Math.Clamp(status.VolumePercent, 0, 100);
            state.DeviceVolumePercent = (int)Math.Round(volumeSlider.Value);
            try { await RefreshSongsAsync(client); }
            catch (Exception error)
            {
                connectionError = error.Message;
                songList.Children.Clear();
                ShowMessage("读取琴内曲库失败，可下拉重试");
            }
        }
        catch (Exception error)
        {
            connectionError = error.Message;
            currentStatus = null;
            SetConnected(false);
            songList.Children.Clear();
            ShowMessage("连接电子琴 Wi-Fi 后下拉刷新");
        }
        finally
        {
            loading = false;
            if (refreshView.IsRefreshing) refreshView.IsRefreshing = false;
        }
    }

    private async Task RefreshSongsAsync(IDeviceClient client)
    {
        var songs = await client.GetSongsAsync();
        songList.Children.Clear();
        foreach (DeviceSongSummary song in songs) AddSongRow(song);
        if (songs.Count == 0) songList.Children.Add(Ui.Caption("琴内曲库为空"));
    }

    private void AddSongRow(DeviceSongSummary song)
    {
        var title = new Label
        {
            Text = song.Title, TextColor = Ui.Ink, FontSize = 15, LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center
        };
        var play = Ui.TextButton("播放");
        play.Clicked += async (_, _) => await SendCommandAsync("播放", client => client.PlaySongAsync(song.Id));
        var more = Ui.MoreButton(song.Title + "的其他操作");
        more.Clicked += async (_, _) =>
        {
            string? selected = await DisplayActionSheetAsync(song.Title, "取消", "删除", "跟弹");
            if (selected == "跟弹")
                await SendCommandAsync("跟弹", client => client.TeachSongAsync(song.Id));
            else if (selected == "删除" && await DisplayAlertAsync("从琴内删除？", song.Title, "删除", "取消"))
                await SendCommandAsync("删除", client => client.DeleteSongAsync(song.Id), refreshSongs: true);
        };
        songList.Children.Add(Ui.Card(Ui.Row(title, Ui.Row(play, more)), padding: 6));
    }

    private Task ApplyVolumeAsync()
    {
        int volume = (int)Math.Round(volumeSlider.Value);
        return SendCommandAsync("设置音量", async client =>
        {
            var result = await client.SetVolumeAsync(volume);
            if (result.Ok) state.DeviceVolumePercent = volume;
            return result;
        });
    }

    private async Task SendCommandAsync(string name, Func<IDeviceClient, Task<DeviceResponse>> command, bool refreshSongs = false)
    {
        if (sending || loading) return;
        sending = true;
        ShowMessage(name + "中…");
        try
        {
            using IDeviceClient client = deviceFactory.Create();
            DeviceResponse response = await command(client);
            if (response.Ok && refreshSongs) await RefreshSongsAsync(client);
            ShowMessage(response.Ok ? "" : $"{name}失败：{response.Message}", !response.Ok);
        }
        catch (Exception error) { ShowMessage($"{name}失败：{error.Message}", true); }
        finally { sending = false; }
    }

    private void ShowMessage(string message, bool error = false)
    {
        messageLabel.Text = message;
        messageLabel.TextColor = error ? Colors.DarkRed : Ui.Muted;
        messageLabel.IsVisible = message.Length > 0;
    }
}
