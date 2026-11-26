using HarmonicaPlayer.Mobile.Core;
#if ANDROID
using Android.Content.PM;
using Microsoft.Maui.ApplicationModel;
#endif

namespace HarmonicaPlayer.Mobile;

public sealed class HomePage : ContentPage
{
    private readonly MobileAppState state;
    private readonly LocalSongStore songStore;
    private readonly DeviceClientFactory deviceFactory;
    private readonly SearchBar filter = new() { Placeholder = "搜索曲库", IsVisible = false };
    private readonly VerticalStackLayout songList = new() { Spacing = 6 };
    private readonly Button importButton = Ui.PrimaryButton("导入文件");
    private readonly Button onlineButton = Ui.SecondaryButton("在线选曲");
    private IReadOnlyList<StoredSong> songs = Array.Empty<StoredSong>();
    private bool navigating;

    public HomePage(MobileAppState state, LocalSongStore songStore, DeviceClientFactory deviceFactory)
    {
        this.state = state;
        this.songStore = songStore;
        this.deviceFactory = deviceFactory;
        Title = "曲库";
        BackgroundColor = Ui.PageBackground;

        var create = new ToolbarItem { Text = "新建简谱" };
        create.Clicked += async (_, _) => await Navigation.PushAsync(new ImportPage(state, songStore, deviceFactory));
        ToolbarItems.Add(create);
        importButton.Clicked += async (_, _) => await ImportAsync();
        onlineButton.Clicked += async (_, _) => await Shell.Current.GoToAsync("//online");
        filter.TextChanged += (_, _) => RenderSongs();
        Content = new ScrollView
        {
            Content = Ui.PageLayout(Ui.Row(importButton, onlineButton, equalWidth: true), filter, songList)
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        if (Platform.CurrentActivity is { } activity)
            activity.RequestedOrientation = ScreenOrientation.Portrait;
#endif
        try
        {
            songs = await songStore.GetAllAsync();
            RenderSongs();
        }
        catch (Exception error)
        {
            songList.Children.Clear();
            songList.Children.Add(Ui.Caption("读取曲库失败：" + error.Message));
        }
    }

    private async Task ImportAsync()
    {
        if (navigating) return;
        navigating = true;
        importButton.IsEnabled = onlineButton.IsEnabled = false;
        try
        {
            if (await MobileFileImport.PickAsync(this) is not { } import) return;
            bool saved = await songStore.TrySaveNewAsync(import.Song);
            await Navigation.PushAsync(new ImportPage(state, songStore, deviceFactory, import.Song, saved, import.Warnings));
        }
        catch (Exception error) { await DisplayAlertAsync("导入失败", error.Message, "好"); }
        finally
        {
            importButton.IsEnabled = onlineButton.IsEnabled = true;
            navigating = false;
        }
    }

    private void RenderSongs()
    {
        filter.IsVisible = songs.Count > 0;
        songList.Children.Clear();
        string query = (filter.Text ?? "").Trim();
        if (songs.Count == 0)
        {
            songList.Children.Add(Ui.Caption("曲库为空"));
            return;
        }
        foreach (var stored in songs.Where(s => s.Song.Title.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var title = Ui.TextButton(stored.Song.Title);
            title.HorizontalOptions = LayoutOptions.Fill;
            title.LineBreakMode = LineBreakMode.TailTruncation;
            title.FontAttributes = FontAttributes.Bold;
            title.Clicked += async (_, _) => await OpenSongAsync(stored, preview: false);
            var play = Ui.TextButton("试听");
            play.Clicked += async (_, _) => await OpenSongAsync(stored, preview: true);
            songList.Children.Add(Ui.Card(Ui.Row(title, play), padding: 4));
        }
        if (songList.Children.Count == 0) songList.Children.Add(Ui.Caption("没有匹配曲谱"));
    }

    private async Task OpenSongAsync(StoredSong song, bool preview)
    {
        if (navigating) return;
        navigating = true;
        try
        {
            await Navigation.PushAsync(preview
                ? new PracticePage(state, PracticeAudioFactory.Create(), song, autoPlay: true)
                : new ImportPage(state, songStore, deviceFactory, song));
        }
        finally { navigating = false; }
    }
}
