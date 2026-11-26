using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace HarmonicaPlayer;

public sealed record LocalAudioState(
    string ScoreText,
    string SongTitle,
    int Bpm,
    int GapMs,
    string LyricsText,
    string LyricsTimingText,
    LyricsLayout LyricsLayout,
    bool LyricsEnabled,
    AudioInstrument Instrument,
    string LyricsBindingJson = "",
    string TimingJson = "",
    string? PerformanceSourcePath = null);

/// <summary>
/// Independent local-audio module. It uses the host window's current score,
/// BPM and lyrics when opened, but plays only through the local audio device.
/// It never calls NativeInput or sends keyboard/mouse input to another window.
/// </summary>
public sealed class LocalAudioWindow : Window
{
    private string timingJson = "";
    private string? performanceSourcePath, performanceProjection;
    private readonly TextBox score = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 180,
        Text = "1234567 【1234567】 （1234567） #1 #2 #4 #5 #6 0"
    };
    private readonly TextBox bpm = new() { Text = "120", Width = 80 };
    private readonly TextBox gap = new() { Text = "20", Width = 80 };
    private readonly Button import = new() { Content = "导入 TXT", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4) };
    private readonly Button sync = new() { Content = "同步主窗口", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4) };
    private readonly Button play = new() { Content = "播放声音", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4) };
    private readonly Button stop = new() { Content = "停止", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(4) };
    private readonly TextBlock lyricsInfo = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { Text = "本模块直接通过扬声器发声，不发送键盘或鼠标输入。", TextWrapping = TextWrapping.Wrap };
    private readonly MediaPlayer player = new();
    private readonly Func<LocalAudioState>? hostStateProvider;
    private readonly Action<LyricsLayout>? lyricsLayoutChanged;
    private CancellationTokenSource? playbackCancellation;
    private string? wavePath;
    private LyricsWindow? lyricsWindow;
    private LyricsTimeline? activeLyrics;
    private IReadOnlyList<ScoreNote>? activeNotes;
    private int activeBpm;
    private bool activePerformance;
    private string lyricsText = "";
    private string songTitle = "";
    private bool lyricsEnabled;
    private string lyricsTimingText = "";
    private string lyricsBindingJson = "";
    private LyricsLayout lyricsLayout = new();
    private bool rendering;
    private bool closing;
    private AudioInstrument currentInstrument = AudioInstrument.Harmonica;

    public LocalAudioWindow(LocalAudioState initialState, Func<LocalAudioState>? hostStateProvider = null,
        Action<LyricsLayout>? lyricsLayoutChanged = null)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        this.hostStateProvider = hostStateProvider;
        this.lyricsLayoutChanged = lyricsLayoutChanged;
        Title = "电子琴 2.0 · 本机发声模块";
        Width = 860;
        Height = 760;
        MinWidth = 680;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 250));
        FontSize = 14;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "本机发声模块",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(23, 43, 77)),
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        StyleButton(import, "#E9EEF8", "#25416C");
        StyleButton(sync, "#E9EEF8", "#25416C");
        StyleButton(play, "#197A62", "#FFFFFF");
        StyleButton(stop, "#B64242", "#FFFFFF");
        controls.Children.Add(import);
        controls.Children.Add(sync);
        controls.Children.Add(play);
        controls.Children.Add(stop);
        controls.Children.Add(new TextBlock { Text = "BPM", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
        controls.Children.Add(bpm);
        controls.Children.Add(new TextBlock { Text = "留白(ms)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
        controls.Children.Add(gap);
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        score.FontFamily = new FontFamily("Consolas");
        score.FontSize = 15;
        score.Padding = new Thickness(10);
        score.BorderBrush = new SolidColorBrush(Color.FromRgb(208, 213, 221));
        score.BorderThickness = new Thickness(1);
        score.Background = Brushes.White;
        Grid.SetRow(score, 2);
        root.Children.Add(score);

        lyricsInfo.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 133));
        lyricsInfo.Margin = new Thickness(4, 12, 4, 0);
        Grid.SetRow(lyricsInfo, 3);
        root.Children.Add(lyricsInfo);

        status.Foreground = new SolidColorBrush(Color.FromRgb(52, 64, 84));
        status.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(status, 4);
        root.Children.Add(status);
        Content = root;

        import.Click += async (_, _) => await ImportScoreAsync();
        sync.Click += (_, _) => SyncFromHost();
        play.Click += async (_, _) => await PlayAsync();
        stop.Click += (_, _) => StopPlayback("已停止。");
        player.MediaOpened += (_, _) => OnMediaOpened();
        player.MediaEnded += (_, _) => Dispatcher.BeginInvoke(new Action(() => StopPlayback("播放完成。")));
        player.MediaFailed += (_, e) => Dispatcher.BeginInvoke(new Action(() => StopPlayback("音频播放失败：" + e.ErrorException?.Message)));
        Closing += (_, _) =>
        {
            closing = true;
            StopPlayback("已关闭本机发声模块。");
        };

        ApplyHostState(initialState, false);
        SetBusy(false);
    }

    public bool IsPlaying => playbackCancellation != null;

    public void ApplyHostState(LocalAudioState state, bool showStatus = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (closing || IsPlaying) return;
        score.Text = state.ScoreText;
        performanceSourcePath = state.PerformanceSourcePath;
        performanceProjection = state.ScoreText;
        timingJson = state.TimingJson;
        songTitle = state.SongTitle;
        bpm.Text = state.Bpm.ToString();
        gap.Text = state.GapMs.ToString();
        currentInstrument = AudioInstrumentCatalog.Normalize(state.Instrument);
        lyricsText = LyricsTimeline.NormalizeText(state.LyricsText);
        lyricsTimingText = state.LyricsTimingText;
        lyricsBindingJson = state.LyricsBindingJson;
        lyricsEnabled = state.LyricsEnabled;
        lyricsLayout = state.LyricsLayout.Normalize();
        lyricsWindow?.ApplyLayout(lyricsLayout);
        UpdateLyricsStatus();
        if (showStatus) status.Text = $"已同步主窗口：BPM {state.Bpm}，谱面和歌词已更新。";
        if (performanceSourcePath != null && SoundFontAudio.UsesSamples(currentInstrument))
            status.Text = "播放声音将使用原谱多音演奏。编辑简谱后将播放编辑内容。";
    }

    private static void StyleButton(Button button, string background, string foreground)
    {
        button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
        button.BorderBrush = button.Background;
    }

    private void SyncFromHost()
    {
        if (hostStateProvider == null)
        {
            status.Text = "当前没有可同步的主窗口谱面。";
            return;
        }
        if (IsPlaying)
        {
            status.Text = "播放期间不能同步，请先停止。";
            return;
        }
        ApplyHostState(hostStateProvider(), true);
    }

    private async Task ImportScoreAsync()
    {
        if (rendering || closing) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入用于本机发声的 TXT 简谱",
            Filter = "UTF-8 TXT 简谱|*.txt",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true || closing) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 1024 * 1024)
                throw new IOException("文件不得超过 1MB。");
            score.Text = await File.ReadAllTextAsync(dialog.FileName, new UTF8Encoding(false, true));
            performanceSourcePath = performanceProjection = null;
            timingJson = "";
            if (File.Exists(ScoreTiming.SidecarPath(dialog.FileName)))
            {
                string candidate = File.ReadAllText(ScoreTiming.SidecarPath(dialog.FileName), new UTF8Encoding(false, true));
                try { ScoreTiming.Apply(score.Text, candidate); timingJson = candidate; }
                catch (FormatException) { }
            }
            if (BpmFileName.TryRead(dialog.FileName, out int sourceBpm, out _)) bpm.Text = sourceBpm.ToString();
            lyricsText = lyricsTimingText = lyricsBindingJson = "";
            songTitle = BpmFileName.ReadTitle(dialog.FileName);
            string boundPath = ScoreLyrics.SidecarPath(dialog.FileName);
            if (File.Exists(boundPath))
            {
                if (new FileInfo(boundPath).Length > ScoreLyrics.MaxJsonLength) throw new FormatException("歌词绑定文件不得超过 1MB。");
                var binding = ScoreLyrics.Parse(File.ReadAllText(boundPath, new UTF8Encoding(false, true)), TimingNotes());
                lyricsBindingJson = binding.ToJson();
                lyricsText = string.Join(Environment.NewLine, binding.Cues.Select(c => c.Text));
            }
            else
            {
                string? path = new[] { Path.ChangeExtension(dialog.FileName, ".lyrics.txt"), Path.ChangeExtension(dialog.FileName, ".lrc") }.FirstOrDefault(File.Exists);
                if (path != null)
                {
                    if (new FileInfo(path).Length > 1024 * 1024) throw new FormatException("歌词文件不得超过 1MB。");
                    string raw = File.ReadAllText(path, new UTF8Encoding(false, true));
                    lyricsText = LyricsTimeline.NormalizeText(raw);
                    lyricsTimingText = LyricsTimeline.HasTiming(raw) ? raw : "";
                }
            }
            UpdateLyricsStatus();
            status.Text = "已导入本机发声谱面与同名歌词：" + Path.GetFileName(dialog.FileName);
        }
        catch (Exception e)
        {
            status.Text = "导入失败：" + e.Message;
        }
    }

    private async Task PlayAsync()
    {
        if (rendering || IsPlaying || closing) return;
        CancellationTokenSource cancellation = new();
        playbackCancellation = cancellation;
        bool handedToPlayer = false;
        string? path = null;
        try
        {
            List<ScoreNote> notes = TimingNotes();
            if (!int.TryParse(bpm.Text, out int tempo)) throw new FormatException("BPM 必须是整数。");
            string? original = score.Text == performanceProjection && SoundFontAudio.UsesSamples(currentInstrument) ? performanceSourcePath : null;
            int silence = 0;
            if (original == null && !int.TryParse(gap.Text, out silence)) throw new FormatException("留白必须是整数。");
            LyricsTimeline? timeline = lyricsEnabled
                ? LyricsTimeline.CreateOptional(lyricsText, notes, 60000d / tempo, timingText: lyricsTimingText, bindingJson: lyricsBindingJson)
                : null;

            if (timeline?.HasLyrics == true && !string.IsNullOrWhiteSpace(songTitle))
            {
                ShowLyricsWindow();
                lyricsWindow?.ShowTitle(songTitle);
            }

            path = Path.Combine(Path.GetTempPath(), "HarmonicaPlayer-audio-" + Guid.NewGuid().ToString("N") + ".wav");
            rendering = true;
            SetBusy(true);
            status.Text = $"正在生成本机音频（BPM {tempo}），请稍候…";
            LocalAudioRenderInfo info = await Task.Run(
                () => original == null
                    ? LocalAudioRenderer.RenderToWaveFile(path, notes, tempo, silence, cancellation.Token, currentInstrument)
                    : LocalAudioRenderer.RenderPerformanceToWaveFile(path, PlayerWindow.ReadPerformanceFile(original), tempo,
                        cancellation.Token, currentInstrument), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            activeNotes = notes;
            activeBpm = tempo;
            activePerformance = original != null;
            activeLyrics = timeline;
            wavePath = path;
            player.Open(new Uri(path, UriKind.Absolute));
            handedToPlayer = true;
            status.Text = $"音频已生成：{info.DurationSeconds:0.##} 秒（BPM {tempo}），等待扬声器播放…";
        }
        catch (OperationCanceledException)
        {
            if (!closing) status.Text = "已取消音频生成。";
        }
        catch (Exception e)
        {
            if (!closing) status.Text = "本机发声失败：" + e.Message;
        }
        finally
        {
            rendering = false;
            if (!handedToPlayer)
            {
                if (path != null) TryDelete(path);
                if (ReferenceEquals(playbackCancellation, cancellation))
                {
                    playbackCancellation = null;
                    cancellation.Dispose();
                }
            }
            SetBusy(false);
        }
    }

    private List<ScoreNote> TimingNotes()
    {
        if (timingJson.Length > 0)
        {
            try { return ScoreTiming.Apply(score.Text, timingJson); }
            catch (FormatException) { timingJson = ""; status.Text = "谱面已修改，精确时值已失效；现在使用 TXT 近似时值。"; }
        }
        return ScoreParser.Parse(score.Text, rhythm: true);
    }

    private void OnMediaOpened()
    {
        if (closing || playbackCancellation == null) return;
        player.Play();
        string lyricsMessage = lyricsEnabled && activeLyrics?.HasLyrics != true
            ? "；未找到同步歌词，已跳过歌词显示。"
            : "";
        status.Text = $"正在通过默认扬声器播放{(activePerformance ? "原谱多音" : "简谱")}（BPM {activeBpm}）。{lyricsMessage}";
        if (activeLyrics?.HasLyrics == true && activeNotes != null)
        {
            ShowLyricsWindow();
            _ = RunLyricsAsync(activeNotes, activeLyrics, activeBpm, playbackCancellation.Token);
        }
    }

    private async Task RunLyricsAsync(
        IReadOnlyList<ScoreNote> notes,
        LyricsTimeline timeline,
        int tempo,
        CancellationToken token)
    {
        double beatMs = 60000d / tempo;
        double targetMs = 0;
        string? activeCue = null;
        try
        {
            for (int index = 0; index < notes.Count; index++)
            {
                await WaitUntilMediaPositionAsync(targetMs, token);
                if (timeline != null)
                {
                    string? cue = timeline.ForNote(index);
                    if (cue != null && !string.Equals(cue, activeCue, StringComparison.Ordinal))
                    {
                        activeCue = cue;
                        string? nextCue = timeline.NextAfterNote(index);
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!closing && !token.IsCancellationRequested)
                            {
                                lyricsWindow?.ShowLyrics(cue, nextCue);
                            }
                        }));
                    }
                }
                targetMs += notes[index].Beats * beatMs;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WaitUntilMediaPositionAsync(double targetMs, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            double remaining = targetMs - player.Position.TotalMilliseconds;
            if (remaining <= 0) return;
            await Task.Delay((int)Math.Min(20, Math.Max(1, remaining - 1)), token);
        }
    }

    private static async Task WaitUntilAsync(Stopwatch clock, double targetMs, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            double remaining = targetMs - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;
            await Task.Delay((int)Math.Min(20, Math.Max(1, remaining - 1)), token);
        }
    }

    private void ShowLyricsWindow()
    {
        if (lyricsWindow != null) return;
        lyricsWindow = new LyricsWindow(lyricsLayout);
        lyricsWindow.SettingsRequested += ConfigureLyrics;
        lyricsWindow.LayoutChanged += UpdateLyricsLayout;
    }

    private void ConfigureLyrics()
    {
        if (closing) return;
        var samples = LyricsTimeline.Tokenize(lyricsText);
        string sample = samples.Count == 0 ? "歌词预览" : string.Join("  ", samples.Take(2));
        var dialog = new LyricsLayoutDialog(lyricsLayout, sample) { Owner = this };
        if (dialog.ShowDialog() == true && !closing) UpdateLyricsLayout(dialog.Layout);
    }

    private void UpdateLyricsLayout(LyricsLayout layout)
    {
        if (closing) return;
        lyricsLayout = layout.Normalize();
        lyricsWindow?.ApplyLayout(lyricsLayout);
        lyricsLayoutChanged?.Invoke(lyricsLayout);
    }

    private void HideLyricsWindow()
    {
        if (lyricsWindow != null && lyricsWindow.IsVisible) lyricsWindow.Close();
        lyricsWindow = null;
    }

    private void StopPlayback(string? message)
    {
        playbackCancellation?.Cancel();
        player.Stop();
        player.Close();
        HideLyricsWindow();
        string? oldPath = wavePath;
        wavePath = null;
        activeLyrics = null;
        activeNotes = null;
        if (oldPath != null) TryDelete(oldPath);
        if (playbackCancellation != null)
        {
            playbackCancellation.Dispose();
            playbackCancellation = null;
        }
        rendering = false;
        if (message != null && !closing) status.Text = message;
        SetBusy(false);
    }

    private void SetBusy(bool busy)
    {
        bool active = busy || rendering || IsPlaying;
        import.IsEnabled = sync.IsEnabled = play.IsEnabled = !active && !closing;
        stop.IsEnabled = active;
        score.IsReadOnly = active;
        bpm.IsEnabled = gap.IsEnabled = !active;
    }

    private void UpdateLyricsStatus()
    {
        int count = LyricsTimeline.Tokenize(lyricsText).Count;
        string enabled = lyricsEnabled ? "已开启置顶歌词" : "未开启置顶歌词";
        string text = count == 0
            ? "无歌词文本"
            : string.IsNullOrWhiteSpace(lyricsTimingText) && string.IsNullOrWhiteSpace(lyricsBindingJson)
                ? $"{count} 行歌词，请在主窗口校准对应音符"
                : GetLyricsAlignmentMessage();
        lyricsInfo.Text = $"歌词设置沿用主窗口：{enabled}，{text}；播放时使用主窗口歌词布局。";
    }

    private string GetLyricsAlignmentMessage()
    {
        try
        {
            List<ScoreNote> notes = TimingNotes();
            if (!int.TryParse(bpm.Text, out int tempo) || tempo <= 0)
                return "等待有效 BPM";
            LyricsTimeline timeline = LyricsTimeline.Create(
                lyricsText, notes, 60000d / tempo, timingText: lyricsTimingText, bindingJson: lyricsBindingJson);
            return timeline.Alignment.Message;
        }
        catch (FormatException)
        {
            return "等待有效简谱后读取歌词对应关系";
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
