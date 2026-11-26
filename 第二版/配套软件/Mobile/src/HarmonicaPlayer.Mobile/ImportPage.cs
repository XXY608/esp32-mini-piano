using System.Globalization;
using HarmonicaPlayer;
using HarmonicaPlayer.Mobile.Core;

namespace HarmonicaPlayer.Mobile;

public sealed class ImportPage : ContentPage
{
    private readonly MobileAppState state;
    private readonly LocalSongStore songStore;
    private readonly DeviceClientFactory deviceFactory;
    private readonly Entry titleEntry = new() { Placeholder = "曲名" };
    private readonly Entry bpmEntry = new() { Placeholder = "BPM", Keyboard = Keyboard.Numeric, WidthRequest = 92 };
    private readonly Editor sourceEditor = new() { HeightRequest = 158, Placeholder = "例如 1 2 3 1 | 1 2 3 1" };
    private readonly Editor lyricsEditor = new() { HeightRequest = 126, Placeholder = "歌词或 LRC" };
    private readonly Entry lyricsOffsetEntry = new() { Text = "0", Keyboard = Keyboard.Numeric, WidthRequest = 92 };
    private readonly Label titleLabel = Ui.Heading("");
    private readonly Label metadata = Ui.Caption("");
    private readonly Label messageLabel = Ui.Caption("");
    private readonly Label lyricsMessage = Ui.Caption("");
    private readonly Button sendButton = Ui.PrimaryButton("传到琴");
    private readonly Button lyricsButton = Ui.SecondaryButton("同步歌词");
    private readonly ToolbarItem saveAction = new() { Text = "保存" };
    private readonly VerticalStackLayout scoreFields;
    private readonly VerticalStackLayout lyricFields;
    private StoredSong? currentStored;
    private bool saved;
    private bool dirty;
    private bool busy;
    private bool lyricsIsSynced;

    public ImportPage(MobileAppState state, LocalSongStore songStore, DeviceClientFactory deviceFactory,
        StoredSong? stored = null, bool saved = true, IReadOnlyList<string>? warnings = null)
    {
        this.state = state;
        this.songStore = songStore;
        this.deviceFactory = deviceFactory;
        currentStored = stored;
        this.saved = stored is not null && saved;
        Title = stored is null ? "新建简谱" : "曲谱";
        BackgroundColor = Ui.PageBackground;
        ShowStatus(messageLabel, "");

        var play = Ui.SecondaryButton("试听");
        play.Clicked += async (_, _) => await RunAsync("试听", () => OpenPracticeAsync(autoPlay: true));
        var practice = Ui.SecondaryButton("练习");
        practice.Clicked += async (_, _) => await RunAsync("练习", () => OpenPracticeAsync(autoPlay: false));
        sendButton.Clicked += async (_, _) => await RunAsync("传琴", SendAsync);
        saveAction.Clicked += async (_, _) => await RunAsync("保存", SaveAsync);
        lyricsButton.Clicked += async (_, _) => await RunAsync("同步歌词", SyncLyricsAsync, lyricsMessage);

        sourceEditor.IsReadOnly = HasExactNotes;
        scoreFields = new VerticalStackLayout
        {
            Spacing = 6,
            Children = { Ui.Row(titleEntry, bpmEntry), sourceEditor }
        };
        SemanticProperties.SetDescription(titleEntry, "曲名");
        SemanticProperties.SetDescription(bpmEntry, "每分钟节拍数");
        if (warnings is { Count: > 0 })
            scoreFields.Children.Add(Ui.Caption(string.Join(Environment.NewLine, warnings.Distinct())));

        lyricFields = new VerticalStackLayout
        {
            Spacing = 6,
            Children = { lyricsEditor, Ui.Row(Ui.Caption("歌词偏移（秒）"), lyricsOffsetEntry), lyricsButton, lyricsMessage }
        };
        SemanticProperties.SetDescription(lyricsOffsetEntry, "歌词偏移秒数，正数延后");
        var scorePanel = Ui.Card(scoreFields);
        var lyricPanel = Ui.Card(lyricFields);
        scorePanel.IsVisible = stored is null;
        lyricPanel.IsVisible = false;
        var scoreToggle = Ui.SecondaryButton(scorePanel.IsVisible ? "收起谱面" : "谱面");
        var lyricToggle = Ui.SecondaryButton("歌词");
        scoreToggle.Clicked += (_, _) =>
        {
            scorePanel.IsVisible = !scorePanel.IsVisible;
            if (scorePanel.IsVisible) lyricPanel.IsVisible = false;
            scoreToggle.Text = scorePanel.IsVisible ? "收起谱面" : "谱面";
            lyricToggle.Text = lyricPanel.IsVisible ? "收起歌词" : "歌词";
        };
        lyricToggle.Clicked += (_, _) =>
        {
            lyricPanel.IsVisible = !lyricPanel.IsVisible;
            if (lyricPanel.IsVisible) scorePanel.IsVisible = false;
            scoreToggle.Text = scorePanel.IsVisible ? "收起谱面" : "谱面";
            lyricToggle.Text = lyricPanel.IsVisible ? "收起歌词" : "歌词";
        };

        if (stored is not null)
        {
            titleEntry.Text = stored.Song.Title;
            bpmEntry.Text = stored.Song.Bpm?.ToString(CultureInfo.InvariantCulture);
            sourceEditor.Text = stored.SourceText;
            lyricsEditor.Text = stored.LyricsText;
            lyricsIsSynced = stored.LyricsIsSynced;
            lyricsOffsetEntry.Text = stored.LyricsOffsetSeconds.ToString(CultureInfo.InvariantCulture);
            if (!saved) ShowStatus(messageLabel, "曲库已有同名曲谱");
        }
        titleEntry.TextChanged += (_, _) => MarkEdited();
        bpmEntry.TextChanged += (_, _) => MarkEdited();
        sourceEditor.TextChanged += (_, _) => MarkEdited();
        lyricsEditor.TextChanged += (_, _) => MarkEdited();
        lyricsOffsetEntry.TextChanged += (_, _) => MarkEdited();

        Content = new ScrollView
        {
            Content = Ui.PageLayout(titleLabel, metadata, sendButton, Ui.Row(play, practice, equalWidth: true),
                messageLabel, Ui.Row(scoreToggle, lyricToggle, equalWidth: true), scorePanel, lyricPanel)
        };
        ShowStatus(lyricsMessage, "");
        UpdateSummary();
    }

    private bool HasExactNotes => currentStored?.Song is { Polyphony: not null } or { SourceType: "device-json" };

    private void MarkEdited()
    {
        dirty = true;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        titleLabel.Text = titleEntry.Text?.Trim();
        titleLabel.IsVisible = !string.IsNullOrWhiteSpace(titleLabel.Text);
        string tempo = int.TryParse(bpmEntry.Text, out int bpm) ? $"{bpm} BPM" : "";
        metadata.Text = currentStored?.Song.Polyphony is null ? tempo : string.IsNullOrEmpty(tempo) ? "多音原谱" : tempo + " · 多音原谱";
        metadata.IsVisible = metadata.Text.Length > 0;
        bool needsSave = dirty || !saved;
        if (needsSave && !ToolbarItems.Contains(saveAction)) ToolbarItems.Add(saveAction);
        if (!needsSave && ToolbarItems.Contains(saveAction)) ToolbarItems.Remove(saveAction);
    }

    private static void ShowStatus(Label label, string message, Color? color = null)
    {
        label.Text = message;
        label.TextColor = color ?? Ui.Muted;
        label.IsVisible = message.Length > 0;
    }

    private async Task RunAsync(string name, Func<Task> action, Label? feedback = null)
    {
        if (busy) return;
        busy = true;
        sendButton.IsEnabled = saveAction.IsEnabled = scoreFields.IsEnabled = lyricFields.IsEnabled = false;
        feedback ??= messageLabel;
        ShowStatus(feedback, "");
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowStatus(feedback, $"{name}失败：{error.Message}", Colors.DarkRed); }
        finally
        {
            sendButton.IsEnabled = saveAction.IsEnabled = scoreFields.IsEnabled = lyricFields.IsEnabled = true;
            busy = false;
        }
    }

    private StoredSong ReadSong()
    {
        int maximumBpm = HasExactNotes ? 400 : 300;
        string bpmText = bpmEntry.Text?.Trim() ?? "";
        int? bpm = null;
        if (bpmText.Length > 0)
        {
            if (!int.TryParse(bpmText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < 20 || parsed > maximumBpm)
                throw new FormatException($"BPM 填写 20～{maximumBpm} 之间的整数。");
            bpm = parsed;
        }
        if (!double.TryParse(lyricsOffsetEntry.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double offset) || !double.IsFinite(offset))
            throw new FormatException("歌词偏移须为有效秒数。");

        string title = titleEntry.Text?.Trim() ?? "";
        string lyrics = lyricsEditor.Text ?? "";
        string source = sourceEditor.Text ?? "";
        SongPayload payload;
        if (currentStored is { } original && (HasExactNotes || source == original.SourceText))
        {
            var notes = original.Song.Notes.Select((n, i) => new ScoreNote(n.Pitch, n.Octave, n.Sharp, i, Beats: n.DurationBeats)).ToArray();
            var score = new ImportedScore(title.Length == 0 ? original.Song.Title : title, bpm, source, notes, Array.Empty<string>());
            var cues = original.Song.Notes.Any(n => n.Lyric.Length > 0) ? null :
                string.IsNullOrWhiteSpace(lyrics) ? original.Song.LyricCues : MobileDeviceLyrics.CreateCues(score, lyrics, offset);
            payload = original.Song with { Title = score.Title, Bpm = bpm, LyricCues = cues };
        }
        else
        {
            var draft = TxtScoreImporter.Parse("未命名.txt", source, title, bpm);
            if (draft.Score is not { } score) throw new FormatException(draft.ParseError);
            payload = SongPayloadFactory.Create(score, currentStored?.Song.Id, lyrics, offset);
            if (currentStored?.Song.Notes is { } prior && prior.Any(n => n.Lyric.Length > 0) && prior.Count == payload.Notes.Count &&
                prior.Zip(payload.Notes).All(pair => pair.First.Pitch == pair.Second.Pitch && pair.First.Octave == pair.Second.Octave &&
                    pair.First.Sharp == pair.Second.Sharp && pair.First.DurationBeats == pair.Second.DurationBeats))
                payload = payload with
                {
                    Notes = payload.Notes.Select((n, i) => n with { Lyric = prior[i].Lyric, PhraseBefore = prior[i].PhraseBefore }).ToArray(),
                    LyricCues = null
                };
        }
        return new StoredSong(payload, source, DateTimeOffset.UtcNow, lyrics, lyricsIsSynced, offset);
    }

    private Task OpenPracticeAsync(bool autoPlay)
    {
        var song = ReadSong();
        return Navigation.PushAsync(new PracticePage(state, PracticeAudioFactory.Create(), song, autoPlay));
    }

    private async Task SaveAsync()
    {
        var song = ReadSong();
        if (!saved && await songStore.TrySaveNewAsync(song))
        {
            MarkSaved(song);
            return;
        }

        var existing = await songStore.GetAllAsync();
        if (existing.Any(item =>
                (!saved || !string.Equals(item.Song.Id, song.Song.Id, StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(item.Song.Title.Trim(), song.Song.Title.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.Song.Id, song.Song.Id, StringComparison.OrdinalIgnoreCase))) &&
            !await DisplayAlertAsync("替换曲谱？", song.Song.Title, "替换", "取消"))
            return;
        await songStore.SaveAsync(song);
        MarkSaved(song);
    }

    private void MarkSaved(StoredSong song)
    {
        currentStored = song;
        titleEntry.Text = song.Song.Title;
        saved = true;
        dirty = false;
        UpdateSummary();
        ShowStatus(messageLabel, "已保存", Colors.DarkGreen);
    }

    private async Task SendAsync()
    {
        var song = ReadSong();
        if (!string.IsNullOrWhiteSpace(song.LyricsText) && song.Song.LyricCues is not { Count: > 0 } && !song.Song.Notes.Any(n => n.Lyric.Length > 0))
            throw new FormatException("纯文本歌词没有时间点。传琴时请使用 LRC 或逐音歌词。");
        ShowStatus(messageLabel, "正在传琴…");
        using IDeviceClient client = deviceFactory.Create();
        DeviceResponse response = await client.UploadSongAsync(song.Song);
        ShowStatus(messageLabel, response.Ok ? "已传到琴" : "传琴失败：" + response.Message, response.Ok ? Colors.DarkGreen : Colors.DarkRed);
    }

    private async Task SyncLyricsAsync()
    {
        string title = titleEntry.Text?.Trim() ?? "";
        if (title.Length == 0) throw new FormatException("请先填写曲名。");
        ShowStatus(lyricsMessage, "正在找歌词…");
        MobileLyricsMatch? match;
        try { match = await MobileLyricsLookup.FindAsync(title, choose: ChooseLyricsVersionAsync); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ShowStatus(lyricsMessage, "找歌词失败：" + MobileLyricsLookup.DescribeError(error), Colors.DarkRed);
            return;
        }
        if (match is null)
        {
            ShowStatus(lyricsMessage, "没有匹配歌词");
            return;
        }
        lyricsEditor.Text = match.Lyrics.Text;
        lyricsIsSynced = match.Lyrics.IsSynced;
        MarkEdited();
        ShowStatus(lyricsMessage, match.Lyrics.IsSynced ? "已同步歌词" : "已找到纯文本歌词", Colors.DarkGreen);
    }

    private async Task<NeteaseSong?> ChooseLyricsVersionAsync(IReadOnlyList<NeteaseSong> songs)
    {
        string[] labels = songs.Select((song, index) => $"{index + 1}. {song.Artist}").ToArray();
        string? selected = await DisplayActionSheetAsync("选择歌手", "取消", null, labels);
        int index = Array.IndexOf(labels, selected);
        return index < 0 ? null : songs[index];
    }
}
