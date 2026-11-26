using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using HarmonicaPlayer.Mobile.Core;

namespace HarmonicaPlayer;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--online-bridge") OnlineBridge.RunAsync().GetAwaiter().GetResult();
        else SingleInstance.Run();
    }
}

public sealed class PlayerWindow : Window
{
    private readonly TextBox score = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 230, Text = "1234567 【1234567】 （1234567） #1 #2 #4 #5 #6 111 000" };
    private readonly TextBox duration = new() { Text = "300", Width = 75 };
    private readonly TextBox gap = new() { Text = "20", Width = 75 };
    private readonly TextBox spaceGap = new() { Text = "100", Width = 75 };
    private readonly TextBox lineGap = new() { Text = "300", Width = 75 };
    private readonly TextBlock status = new() { Text = "就绪：先导入谱面，快捷键状态见上方。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox log = new() { IsReadOnly = true, Height = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button start = new() { Content = "开始 F6", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };
    private readonly Button import = new() { Content = "导入 TXT/XML/MIDI", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };
    private readonly Button importCcmz = new() { Content = "导入 CCMZ", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button bindLyrics = new() { Content = "绑定歌词", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button mobileVersion = new() { Content = "手机版", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button practiceMode = new() { Content = "练习模式", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button online = new() { Content = "在线曲谱 / CCMZ", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };
    private readonly Button localAudio = new() { Content = "本机发声模块", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
    private readonly TextBox bpm = new() { Text = "120", Width = 75 };
    private readonly ComboBox instrumentSelector = new() { Width = 196, ToolTip = "本机发声和练习模式使用的音色，选择后自动保存。" };
    private readonly TextBox preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button refresh = new() { Content = "预览 / 安全检查", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
    private readonly TextBlock fileInfo = new() { Text = "尚未导入谱面" };
    private readonly TextBlock bpmStatus = new() { Text = "导入后自动读取文件名 BPM" };
    private readonly TextBlock previewSummary = new() { Text = "等待解析" };
    private readonly CheckBox showLyrics = new() { Content = "播放时显示置顶歌词", VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox lyricsInput = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button configureLyrics = new() { Content = "预览并调整位置/大小", Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button alignLyrics = new() { Content = "校准歌词", Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button searchLyrics = new() { Content = "搜索并导入歌词", Tag = "search-lyrics", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button audioAlignLyrics = new() { Content = "一键配词并导入", Tag = "audio-align-lyrics", Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
    private readonly TextBox lyricsSearchTitle = new() { Tag = "lyrics-search-title", ToolTip = "默认使用当前曲名；搜错时可修改曲名或补充歌手后重新搜索。" };
    private readonly TextBlock lyricsStatus = new() { TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? cancellation;
    private Task? running;
    public bool IsClosing => closing;
    private SettingsWriter settingsWriter = null!;
    private readonly string settingsPath;
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly NativeInput output = new();
    private HwndSource? source;
    private IntPtr hwnd;
    private bool allowClose, closing, editingHotkeys, beginning;
    private HotkeyController? hotkeys;
    private AppSettings settings = new();
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBlock settingsStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock hotkeyStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button configure = new() { Content = "自定义快捷键", Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button retry = new() { Content = "重试注册", Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button stop = new() { Content = "停止 F8", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };
    private LyricsWindow? lyricsWindow;
    private LocalAudioWindow? localAudioWindow;
    private PracticeModeWindow? practiceModeWindow;
    private MobileSimulatorWindow? mobileSimulatorWindow;
    private readonly bool dryRun;
    private string? currentScorePath;
    private string currentTimingJson = "";
    private string? currentCcmzPath;
    private string? currentSourceScorePath;
    private string? currentSourcePreviewText;
    private string lyricsTimingText = "";
    private string lyricsBindingJson = "";
    private bool applyingLyricsSource;
    private string currentScoreTitle = "";
    private readonly Func<string, CancellationToken, Task<OnlineLyrics?>> lyricsLookup;
    private CancellationTokenSource? lyricsSearchCancellation;
    private CancellationTokenSource? audioAlignmentCancellation;
    private readonly Func<string, string, IProgress<string>, CancellationToken, Task<AudioLyricResult>> audioAligner;

    public PlayerWindow(string? settingsFile = null, bool dryRun = false,
        Func<string, CancellationToken, Task<OnlineLyrics?>>? lyricsLookup = null,
        Func<string, string, IProgress<string>, CancellationToken, Task<AudioLyricResult>>? audioAligner = null)
    {
        settingsPath = settingsFile ?? SettingsStore.DefaultPath;
        this.dryRun = dryRun;
        this.lyricsLookup = lyricsLookup ?? OnlineScoreClient.FindLyricsAsync;
        this.audioAligner = audioAligner ?? ((path, title, progress, token) => AudioLyricsAligner.RunAsync(AppContext.BaseDirectory, path, title, progress, token));
        Title = "电子琴 2.1 · 电脑版"; Width = 940; Height = 860; MinWidth = 760; MinHeight = 650;
        Background = BrushOf("#F5F7FA"); FontSize = 14;
        AllowDrop = true;

        StyleTextBox(score, multiLine: true);
        score.Height = 190; score.FontSize = 15; score.TextWrapping = TextWrapping.Wrap;
        StyleTextBox(bpm); StyleTextBox(duration); StyleTextBox(gap); StyleTextBox(spaceGap); StyleTextBox(lineGap);
        StyleComboBox(instrumentSelector);
        instrumentSelector.ItemsSource = AudioInstrumentCatalog.WindowsOptions;
        instrumentSelector.DisplayMemberPath = nameof(AudioInstrumentOption.Name);
        bpm.Tag = "bpm"; duration.Tag = "duration"; gap.Tag = "gap"; spaceGap.Tag = "space-gap"; lineGap.Tag = "line-gap";
        StyleButton(import, "#2F6FED", "#FFFFFF", 150);
        StyleButton(importCcmz, "#5B4BBA", "#FFFFFF", 112);
        StyleButton(bindLyrics, "#3C8D76", "#FFFFFF", 110);
        StyleButton(practiceMode, "#7C3AED", "#FFFFFF", 112);
        StyleButton(online, "#6B5DD3", "#FFFFFF", 145);
        StyleButton(refresh, "#E9EEF8", "#25416C", 154);
        StyleButton(configure, "#E9EEF8", "#25416C", 128);
        StyleButton(retry, "#E9EEF8", "#25416C", 96);
        StyleButton(configureLyrics, "#E9EEF8", "#25416C", 180);
        StyleButton(localAudio, "#6B5DD3", "#FFFFFF", 126);
        online.Tag = "primary-online";
        localAudio.Tag = "primary-local-audio";
        StyleButton(start, "#197A62", "#FFFFFF", 150);
        StyleButton(stop, "#B64242", "#FFFFFF", 130);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new Border { Background = BrushOf("#FFFFFF"), BorderBrush = BrushOf("#E4E8EF"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(24, 18, 24, 16) };
        var headerPanel = new StackPanel();
        headerPanel.Children.Add(new TextBlock { Text = "电子琴 · 电脑版", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = BrushOf("#172B4D") });
        headerPanel.Children.Add(new TextBlock { Text = "TXT → 单音演奏 · 导入、检查，再开始演奏", Margin = new Thickness(0, 5, 0, 0), Foreground = BrushOf("#667085") });
        header.Child = headerPanel;
        Grid.SetRow(header, 0); root.Children.Add(header);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(24, 18, 24, 22) };
        var panel = new StackPanel(); scroll.Content = panel;
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        var importBody = new StackPanel();
        var fileRow = new Grid { Margin = new Thickness(0, 0, 0, 10), Tag = "primary-actions" };
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileInfo.Foreground = BrushOf("#344054"); fileInfo.VerticalAlignment = VerticalAlignment.Center; fileInfo.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(online, 0); Grid.SetColumn(fileInfo, 1); Grid.SetColumn(localAudio, 2);
        fileRow.Children.Add(online); fileRow.Children.Add(fileInfo); fileRow.Children.Add(localAudio);
        importBody.Children.Add(fileRow);
        var secondaryTools = new Expander
        {
            Tag = "secondary-tools",
            Margin = new Thickness(0, 0, 0, 8),
            Header = new TextBlock
            {
                Text = "\u5176\u4ed6\u5bfc\u5165\u4e0e\u5de5\u5177\uff08TXT / MIDI / \u6b4c\u8bcd / \u7ec3\u4e60 / \u68c0\u67e5\uff09",
                Foreground = BrushOf("#475467"),
                FontWeight = FontWeights.SemiBold
            }
        };
        var secondaryRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 2) };
        secondaryRow.Children.Add(import);
        var compilePerformance = new Button { Content = "编译实体琴多音谱", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
        StyleButton(compilePerformance, "#2F6FED", "#FFFFFF", 144);
        compilePerformance.Click += async (_, _) => await CompilePerformanceAsync();
        secondaryRow.Children.Add(compilePerformance);
        secondaryRow.Children.Add(importCcmz);
        secondaryRow.Children.Add(practiceMode);
        StyleButton(mobileVersion, "#2F6FED", "#FFFFFF", 112);
        secondaryRow.Children.Add(mobileVersion);
        mobileVersion.Click += (_, _) => {
            if (mobileSimulatorWindow is { IsVisible: true }) { mobileSimulatorWindow.Activate(); return; }
            mobileSimulatorWindow = new MobileSimulatorWindow(settings.Instrument) { Owner = this };
            mobileSimulatorWindow.Closed += (_, _) => mobileSimulatorWindow = null;
            mobileSimulatorWindow.Show();
        };
        secondaryRow.Children.Add(refresh);
        secondaryRow.Children.Add(bindLyrics);
        secondaryTools.Content = secondaryRow;
        importBody.Children.Add(secondaryTools);
        importBody.Children.Add(new TextBlock { Text = "支持 TXT、MusicXML、MIDI 和 CCMZ；本机发声模块独立运行，不发送游戏键鼠输入", Foreground = BrushOf("#667085"), Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        importBody.Children.Add(score);
        panel.Children.Add(MakeCard("① 导入谱面", "文件名中的 BPM 会在导入时自动读取。", importBody));

        var modeBody = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        modeBody.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modeBody.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modeBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bpmGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var bpmLabel = new TextBlock { Text = "BPM", FontWeight = FontWeights.SemiBold, Foreground = BrushOf("#344054"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        bpmGroup.Children.Add(bpmLabel);
        bpm.Width = 82; bpm.HorizontalContentAlignment = HorizontalAlignment.Center; bpmGroup.Children.Add(bpm);
        Grid.SetColumn(bpmGroup, 0); modeBody.Children.Add(bpmGroup);
        var instrumentGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        instrumentGroup.Children.Add(new TextBlock { Text = "音色", FontWeight = FontWeights.SemiBold, Foreground = BrushOf("#344054"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        instrumentGroup.Children.Add(instrumentSelector);
        Grid.SetColumn(instrumentGroup, 1); modeBody.Children.Add(instrumentGroup);
        bpmStatus.Foreground = BrushOf("#667085"); bpmStatus.VerticalAlignment = VerticalAlignment.Center; bpmStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(bpmStatus, 2); modeBody.Children.Add(bpmStatus);
        panel.Children.Add(MakeCard("② 演奏设置", "节奏模式固定开启；BPM 控制速度，音色仅影响本机发声和练习模式。", modeBody));

        var previewBody = new StackPanel();
        previewSummary.Foreground = BrushOf("#197A62"); previewSummary.FontWeight = FontWeights.SemiBold; previewSummary.Margin = new Thickness(0, 0, 0, 10); previewBody.Children.Add(previewSummary);
        preview.FontFamily = new FontFamily("Consolas"); preview.Background = BrushOf("#FBFCFE"); preview.BorderBrush = BrushOf("#D0D5DD"); preview.BorderThickness = new Thickness(1); preview.Height = 230; preview.Padding = new Thickness(10); previewBody.Children.Add(preview);
        var lyricsBody = new StackPanel();
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var searchLabel = new TextBlock { Text = "搜词曲名", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(searchLabel, Dock.Left); searchRow.Children.Add(searchLabel);
        StyleButton(searchLyrics, "#197A62", "#FFFFFF", 155);
        searchLyrics.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(searchLyrics, Dock.Right); searchRow.Children.Add(searchLyrics);
        StyleTextBox(lyricsSearchTitle); searchRow.Children.Add(lyricsSearchTitle); lyricsBody.Children.Add(searchRow);
        var lyricsActionRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        showLyrics.Foreground = BrushOf("#172B4D"); showLyrics.FontWeight = FontWeights.SemiBold; showLyrics.Margin = new Thickness(0, 0, 14, 0);
        lyricsActionRow.Children.Add(showLyrics); lyricsActionRow.Children.Add(configureLyrics); lyricsActionRow.Children.Add(alignLyrics); lyricsActionRow.Children.Add(audioAlignLyrics); lyricsBody.Children.Add(lyricsActionRow);
        lyricsBody.Children.Add(new TextBlock { Text = "点击“一键配词并导入”，选择无词 CCMZ 后自动找词、配到音符并保存；已导入 CCMZ 时处理当前曲目。支持一字多音及同音长音拆分。疑点保留标记，可选用“校准歌词”试听；自动对应仍可能有误。", Foreground = BrushOf("#667085"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) });
        StyleTextBox(lyricsInput, multiLine: true); lyricsInput.MinHeight = 0; lyricsInput.Height = 105; lyricsInput.FontSize = 15; lyricsBody.Children.Add(lyricsInput);
        lyricsStatus.Foreground = BrushOf("#667085"); lyricsStatus.Margin = new Thickness(0, 8, 0, 0); lyricsBody.Children.Add(lyricsStatus);
        panel.Children.Add(MakeCard("③ 置顶歌词", "先勾选是否显示，再点击“预览并调整位置/大小”保存歌词窗口布局。", lyricsBody));
        panel.Children.Add(MakeCard("④ 解析预览", "检查音符、时值和预计播放时间；这里不是声音试听。", previewBody));

        var advancedBody = new StackPanel { Orientation = Orientation.Horizontal };
        advancedBody.Children.Add(new TextBlock { Text = "留白（ms）", Foreground = BrushOf("#344054"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        gap.Width = 82; advancedBody.Children.Add(gap);
        panel.Children.Add(MakeCard("⑤ 高级设置", "留白控制每个音结束前提前松开的时间，通常保持 10～20ms。", advancedBody));

        var hotkeyBody = new StackPanel();
        hotkeyStatus.Foreground = BrushOf("#344054"); hotkeyBody.Children.Add(hotkeyStatus);
        var shortcutRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        shortcutRow.Children.Add(configure); shortcutRow.Children.Add(retry); hotkeyBody.Children.Add(shortcutRow);
        settingsStatus.Foreground = BrushOf("#667085"); hotkeyBody.Children.Add(settingsStatus);
        hotkeyBody.Children.Add(new TextBlock { Text = "实际演奏时请保持游戏口琴界面在前台，除停止键外不要操作鼠标键盘。", TextWrapping = TextWrapping.Wrap, Foreground = BrushOf("#667085"), Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(MakeCard("⑥ 快捷键与安全提示", "切换窗口会自动停止真实演奏。", hotkeyBody));

        var footer = new Border { Background = BrushOf("#FFFFFF"), BorderBrush = BrushOf("#E4E8EF"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 12, 24, 12) };
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.Foreground = BrushOf("#344054"); status.VerticalAlignment = VerticalAlignment.Center; status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(status, 0); footerGrid.Children.Add(status);
        Grid.SetColumn(start, 1); Grid.SetColumn(stop, 2); footerGrid.Children.Add(start); footerGrid.Children.Add(stop);
        footer.Child = footerGrid; Grid.SetRow(footer, 2); root.Children.Add(footer);

        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) _ = OpenScoreFileAsync(files[0]); };
        settings = SettingsStore.Load(settingsPath, out var loadWarning);
        settingsWriter = new SettingsWriter(value => SettingsStore.Save(settingsPath, value),
            loadWarning == null && File.Exists(settingsPath) ? settings : null);
        bpm.Text = settings.Bpm.ToString();
        duration.Text = settings.Duration.ToString(); gap.Text = settings.Gap.ToString();
        spaceGap.Text = settings.SpaceGap.ToString(); lineGap.Text = settings.LineGap.ToString();
        instrumentSelector.SelectedItem = AudioInstrumentCatalog.WindowsOptions.First(option =>
            option.Value == AudioInstrumentCatalog.Normalize(settings.Instrument));
        showLyrics.IsChecked = settings.LyricsEnabled;
        lyricsInput.Text = settings.LyricsText;
        lyricsTimingText = settings.LyricsTimingText;
        lyricsBindingJson = settings.LyricsBindingJson;
        UpdateLyricsStatus();
        settingsStatus.Text = loadWarning ?? "设置会自动保存；节奏模式固定开启，预览 / 安全检查不会发送键鼠。";
        saveTimer.Tick += async (_, _) => { saveTimer.Stop(); await SaveCurrentSettingsAsync(); };
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); Preview(); };
        configure.Click += (_, _) => ConfigureHotkeys();
        retry.Click += (_, _) => ApplyHotkeys();
        configureLyrics.Click += (_, _) => ConfigureLyrics();
        alignLyrics.Click += (_, _) => AlignLyrics();
        searchLyrics.Click += async (_, _) => await SearchLyricsAsync();
        audioAlignLyrics.Click += async (_, _) => await AlignFromAudioAsync();
        import.Click += (_, _) => ImportAll(); importCcmz.Click += (_, _) => ImportCcmz(); practiceMode.Click += (_, _) => OpenPracticeMode(); bindLyrics.Click += (_, _) => ImportLyricsFile(); online.Click += (_, _) => OpenOnlineScores(); localAudio.Click += (_, _) => OpenLocalAudio(); start.Click += async (_, _) => await Begin();
        stop.Click += (_, _) => Stop();
        refresh.Click += (_, _) => Preview();
        score.TextChanged += (_, _) => { QueuePreview(); UpdateLyricsStatus(); SyncLocalAudioFromHost(); SyncPracticeModeFromHost(); };
        bpm.TextChanged += (_, _) => { QueuePreview(); SyncLocalAudioFromHost(); SyncPracticeModeFromHost(); };
        instrumentSelector.SelectionChanged += (_, _) =>
        {
            if (instrumentSelector.SelectedItem is AudioInstrumentOption option)
                OnAudioInstrumentChanged(option.Value);
        };
        gap.TextChanged += (_, _) => SyncLocalAudioFromHost();
        duration.TextChanged += (_, _) => QueuePreview();
        spaceGap.TextChanged += (_, _) => QueuePreview();
        lineGap.TextChanged += (_, _) => QueuePreview();
        lyricsInput.TextChanged += (_, _) =>
        {
            if (!applyingLyricsSource) { lyricsTimingText = ""; lyricsBindingJson = ""; }
            UpdateLyricsStatus(); QueueSave(); SyncLocalAudioFromHost();
        };
        showLyrics.Checked += (_, _) => { UpdateLyricsStatus(); QueueSave(); SyncLocalAudioFromHost(); };
        showLyrics.Unchecked += (_, _) => { UpdateLyricsStatus(); QueueSave(); SyncLocalAudioFromHost(); };
        foreach (var box in new[] { bpm, duration, gap, spaceGap, lineGap })
            box.TextChanged += (_, _) => QueueSave();
        SetBusy(false); Preview();
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(hwnd); source.AddHook(Hook);
            hotkeys = new HotkeyController(new NativeHotkeys(hwnd));
            ApplyHotkeys();
        };
        Closing += (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            saveTimer.Stop(); previewTimer.Stop();
            cancellation?.Cancel(); // Cancel first; do not put disk I/O before it.
            lyricsSearchCancellation?.Cancel();
            audioAlignmentCancellation?.Cancel();
            localAudioWindow?.Close();
            practiceModeWindow?.Close();
            mobileSimulatorWindow?.Close();
            HideLyricsWindow();
            SetBusy(true); stop.IsEnabled = false;
            status.Text = "正在停止演奏并关闭…";
            // Always leave the initial Closing event before calling Close again,
            // even when there is no playback task or every await completes inline.
            Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => _ = CloseAfterStopAsync()));
        };
    }

    private static SolidColorBrush BrushOf(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static void StyleTextBox(TextBox box, bool multiLine = false)
    {
        box.MinHeight = multiLine ? 180 : 34;
        box.Padding = new Thickness(8, 5, 8, 5);
        box.BorderBrush = BrushOf("#D0D5DD"); box.BorderThickness = new Thickness(1);
        box.Background = BrushOf("#FFFFFF"); box.Foreground = BrushOf("#172B4D");
        box.VerticalContentAlignment = multiLine ? VerticalAlignment.Top : VerticalAlignment.Center;
    }

    private static void StyleComboBox(ComboBox box)
    {
        box.MinHeight = 34;
        box.Padding = new Thickness(8, 4, 8, 4);
        box.BorderBrush = BrushOf("#D0D5DD");
        box.BorderThickness = new Thickness(1);
        box.Background = BrushOf("#FFFFFF");
        box.Foreground = BrushOf("#172B4D");
        box.VerticalContentAlignment = VerticalAlignment.Center;
    }

    private static void StyleButton(Button button, string background, string foreground, double minWidth)
    {
        button.MinWidth = minWidth; button.MinHeight = 40; button.Margin = new Thickness(0, 0, 8, 0);
        button.Padding = new Thickness(14, 7, 14, 7); button.FontWeight = FontWeights.SemiBold;
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, BrushOf(background)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, BrushOf(foreground)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, BrushOf(background)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.OpacityProperty, 0.86)); style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.RenderTransformProperty, new ScaleTransform(0.98, 0.98))); style.Triggers.Add(pressed);
        button.Style = style;
    }

    private static Border MakeCard(string title, string subtitle, UIElement content)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = BrushOf("#172B4D") });
        body.Children.Add(new TextBlock { Text = subtitle, Foreground = BrushOf("#667085"), Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(content);
        return new Border { Background = BrushOf("#FFFFFF"), BorderBrush = BrushOf("#E4E8EF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), Child = body };
    }

    private static void AddTimingField(Grid grid, string label, TextBox box, int column, int row)
    {
        var panel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 14, row == 0 ? 0 : 12, 14, 0) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = BrushOf("#344054"), Margin = new Thickness(0, 0, 0, 5) });
        box.HorizontalContentAlignment = HorizontalAlignment.Left; panel.Children.Add(box);
        Grid.SetColumn(panel, column); Grid.SetRow(panel, row); grid.Children.Add(panel);
    }
    private async Task CloseAfterStopAsync()
    {
        try
        {
            Task? playback = running;
            if (playback != null)
            {
                if (await Task.WhenAny(playback, Task.Delay(1500)) != playback)
                    status.Text = "正在等待演奏任务停止，窗口仍可响应…";
                try { await playback; }
                catch (Exception e) { status.Text = "演奏结束异常：" + e.Message; }
            }
            // No background note sender remains before final cleanup begins.
            string? releaseError = await Task.Run(output.Release);
            if (releaseError != null)
            {
                // Do not silently exit with keys still held. A second close retries.
                status.Text = "按键释放失败，请手动按下并松开相关键后再关闭：" + releaseError;
                return;
            }
            Task save = SaveCurrentSettingsAsync(true);
            if (await Task.WhenAny(save, Task.Delay(1500)) != save)
                status.Text = "正在保存最后的设置，请稍候（窗口仍可响应）…";
            await save;
            hotkeys?.Suspend(); source?.RemoveHook(Hook);
            allowClose = true; Close();
        }
        catch (Exception e) { status.Text = "关闭未完成，请重试：" + e.Message; }
        finally
        {
            if (!allowClose)
            {
                closing = false; stop.IsEnabled = true; SetBusy(false);
            }
        }
    }
    private void QueuePreview()
    {
        if (closing) return;
        previewTimer.Stop(); previewTimer.Start();
    }

    private IntPtr Hook(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x0312)
        {
            // Discard queued messages from old registrations after editing.
            uint packed = unchecked((uint)l.ToInt64());
            uint key = packed >> 16, modifiers = packed & 0xFFFF;
            if (!editingHotkeys && w.ToInt32() == 1 && hotkeys?.StartReady == true &&
                key == settings.Start.Key && modifiers == settings.Start.Modifiers) _ = Begin(key);
            if (!editingHotkeys && w.ToInt32() == 2 && hotkeys?.StopReady == true &&
                key == settings.Stop.Key && modifiers == settings.Stop.Modifiers) Stop();
            handled = true;
        }
        return IntPtr.Zero;
    }
    private void QueueSave()
    {
        if (closing) return;
        saveTimer.Stop(); saveTimer.Start();
    }
    private async Task SaveCurrentSettingsAsync(bool preserveValidTiming = false)
    {
        AppSettings next = settings;
        string? invalid = null;
        if (!int.TryParse(bpm.Text, out int tempo) || !int.TryParse(duration.Text, out int ms) ||
            !int.TryParse(gap.Text, out int silence) || !int.TryParse(spaceGap.Text, out int space) ||
            !int.TryParse(lineGap.Text, out int line))
            invalid = "部分数值尚未填写完整，保留上次有效设置。";
        else
        {
            var candidate = settings with { Rhythm = true, Bpm = tempo, Duration = ms,
                Gap = silence, SpaceGap = space, LineGap = line,
                LyricsEnabled = showLyrics.IsChecked == true, LyricsText = lyricsInput.Text,
                LyricsTimingText = lyricsTimingText, LyricsBindingJson = lyricsBindingJson };
            try { candidate.Validate(); next = candidate; }
            catch (FormatException e) { invalid = e.Message; }
        }
        if (invalid != null && !preserveValidTiming)
        { settingsStatus.Text = invalid; return; }
        settings = next; // Update the in-memory snapshot before asynchronous disk work.
        string? error = await settingsWriter.SaveAsync(next);
        if (settings != next) return; // Do not overwrite a newer save's status.
        settingsStatus.Text = error != null ? "设置未保存：" + error :
            invalid ?? "设置已保存（快捷键、节奏模式、速度和停顿）。";
    }
    private void ApplyHotkeys()
    {
        if (hotkeys == null || cancellation != null || closing || beginning || editingHotkeys) return;
        hotkeys.Apply(settings.Start, settings.Stop);
        start.Content = "开始 " + settings.Start.Label; stop.Content = "停止 " + settings.Stop.Label;
        hotkeyStatus.Text = hotkeys.Describe(settings.Start, settings.Stop);
    }
    private void ConfigureHotkeys()
    {
        if (hotkeys == null || cancellation != null || closing || beginning || editingHotkeys) return;
        editingHotkeys = true; hotkeys.Suspend();
        try
        {
            var dialog = new HotkeyDialog(settings.Start, settings.Stop) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                settings = settings with { Start = dialog.Start, Stop = dialog.Stop };
                _ = SaveCurrentSettingsAsync(true);
            }
        }
        finally { editingHotkeys = false; ApplyHotkeys(); }
    }
    private async Task WaitForRelease(uint triggerKey, CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        while (NativeHotkeys.Held(triggerKey))
        {
            token.ThrowIfCancellationRequested();
            if (timeout.Elapsed.TotalSeconds > 15)
                throw new InvalidOperationException("等待松开快捷键超时，已取消演奏。");
            status.Text = $"请松开启动键及 Ctrl / Alt / Shift / Win；{settings.Stop.Label} 可停止。";
            await Task.Delay(20, token);
        }
        token.ThrowIfCancellationRequested();
    }
    private void ImportAll()
    {
        if (cancellation != null || beginning || editingHotkeys || closing) return;
        try
        {
            var dialog = new OpenFileDialog { Filter = "支持的谱面|*.txt;*.musicxml;*.xml;*.mxl;*.mid;*.midi;*.ccmz|TXT 简谱|*.txt|MusicXML|*.musicxml;*.xml;*.mxl|MIDI|*.mid;*.midi|CCMZ|*.ccmz" };
            if (dialog.ShowDialog(this) == true && !closing)
            {
                _ = OpenScoreFileAsync(dialog.FileName);
            }
        }
        catch (Exception e) { status.Text = "导入失败：" + e.Message; }
    }

    private void ImportCcmz()
    {
        if (cancellation != null || beginning || editingHotkeys || closing) return;
        try
        {
            var dialog = new OpenFileDialog { Filter = "CCMZ 曲谱|*.ccmz" };
            if (dialog.ShowDialog(this) != true || closing) return;
            _ = OpenScoreFileAsync(dialog.FileName);
        }
        catch (Exception e) { status.Text = "CCMZ 导入失败：" + e.Message; }
    }

    private void OpenOnlineScores()
    {
        if (cancellation != null || beginning || editingHotkeys || closing) return;
        new OnlineScoreWindow(this, ImportDownloadedCcmzAsync).ShowDialog();
    }

    private void OpenLocalAudio()
    {
        if (localAudioWindow is { IsVisible: true })
        {
            localAudioWindow.Activate();
            return;
        }
        localAudioWindow = new LocalAudioWindow(CurrentLocalAudioState(), CurrentLocalAudioState, UpdateLyricsLayout) { Owner = this };
        localAudioWindow.Closed += (_, _) => localAudioWindow = null;
        localAudioWindow.Show();
    }

    private void OpenPracticeMode()
    {
        if (closing) return;
        if (practiceModeWindow is { IsVisible: true })
        {
            practiceModeWindow.Activate();
            return;
        }
        try
        {
            practiceModeWindow = new PracticeModeWindow(CurrentPracticeModeState(), CurrentPracticeModeState) { Owner = this };
            practiceModeWindow.Closed += (_, _) => practiceModeWindow = null;
            practiceModeWindow.Show();
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            status.Text = "无法打开练习模式：" + error.Message;
            practiceModeWindow = null;
        }
    }

    private LocalAudioState CurrentLocalAudioState()
    {
        int tempo = int.TryParse(bpm.Text, out int parsedBpm) ? parsedBpm : settings.Bpm;
        int silence = int.TryParse(gap.Text, out int parsedGap) ? parsedGap : settings.Gap;
        return new LocalAudioState(score.Text, currentScoreTitle, tempo, silence, lyricsInput.Text, lyricsTimingText,
            settings.LyricsLayout.Normalize(), showLyrics.IsChecked == true, settings.Instrument, lyricsBindingJson, currentTimingJson,
            score.Text == currentSourcePreviewText ? CurrentPerformanceSource() : null);
    }

    private PracticeModeState CurrentPracticeModeState()
    {
        int tempo = int.TryParse(bpm.Text, out int parsedBpm) ? parsedBpm : settings.Bpm;
        return new PracticeModeState(score.Text, currentScoreTitle, tempo, settings.Instrument, currentTimingJson);
    }

    private void SyncLocalAudioFromHost() => localAudioWindow?.ApplyHostState(CurrentLocalAudioState());

    private void OnAudioInstrumentChanged(AudioInstrument instrument)
    {
        settings = settings with { Instrument = AudioInstrumentCatalog.Normalize(instrument) };
        SyncLocalAudioFromHost();
        SyncPracticeModeFromHost();
        QueueSave();
    }

    private void SyncPracticeModeFromHost() => practiceModeWindow?.ApplyHostState(CurrentPracticeModeState(), false);

    private void ConfigureLyrics()
    {
        if (cancellation != null || beginning || editingHotkeys || closing) return;
        var tokens = LyricsTimeline.Tokenize(lyricsInput.Text);
        string sample = tokens.Count == 0 ? "歌词预览" : string.Join("  ", tokens.Take(12));
        var dialog = new LyricsLayoutDialog(settings.LyricsLayout, sample) { Owner = this };
        if (dialog.ShowDialog() != true || closing) return;
        settings = settings with { LyricsLayout = dialog.Layout };
        lyricsWindow?.ApplyLayout(settings.LyricsLayout);
        lyricsStatus.Text = DescribeLyricsLayout(settings.LyricsLayout);
        SyncLocalAudioFromHost();
        _ = SaveCurrentSettingsAsync(true);
    }

    private string BindLyricsToFinalScore(string finalScorePath, string? selectedLyricsPath)
    {
        string scorePath = Path.GetFullPath(finalScorePath);
        currentScorePath = scorePath;
        bool explicitlySelected = !string.IsNullOrWhiteSpace(selectedLyricsPath);
        if (explicitlySelected && SamePath(scorePath, selectedLyricsPath))
            throw new FormatException("歌词文件不能与最终 TXT 谱面使用同一个文件。");
        string boundPath = ScoreLyrics.SidecarPath(scorePath);
        if ((!explicitlySelected && File.Exists(boundPath)) ||
            (explicitlySelected && selectedLyricsPath!.EndsWith(".lyrics.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                string input = explicitlySelected ? selectedLyricsPath! : boundPath;
                if (new FileInfo(input).Length > ScoreLyrics.MaxJsonLength) throw new FormatException("歌词绑定文件不得超过 1MB。");
                var binding = ScoreLyrics.Parse(File.ReadAllText(input, new UTF8Encoding(false, true)), CurrentTimingNotes());
                ApplyScoreLyrics(binding, scorePath);
                return $"已恢复 {binding.Cues.Count} 句音符绑定歌词";
            }
            catch (Exception error) when (!explicitlySelected && error is IOException or UnauthorizedAccessException or FormatException)
            {
                ClearLyricsBinding(scorePath);
                return "歌词对应关系需要重新校准：" + error.Message;
            }
        }
        string? lyricsPath = explicitlySelected ? selectedLyricsPath : FindLyricsSidecar(scorePath);
        if (lyricsPath != null)
        {
            try
            {
                var source = ReadLyricsSource(lyricsPath);
                string sidecarPath = LyricsSidecarPath(scorePath);
                string storedPath = sidecarPath;
                string persistenceWarning = "";
                string storedText = string.IsNullOrWhiteSpace(source.TimingText) ? source.Text : source.TimingText;
                try { if (!SamePath(lyricsPath, sidecarPath)) File.WriteAllText(sidecarPath, storedText, new UTF8Encoding(false)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    storedPath = Path.GetFullPath(lyricsPath);
                    persistenceWarning = "；最终 TXT 伴随文件写入失败，已保留原歌词路径";
                }
                SetLyricsSource(source.Text, source.TimingText);
                if (explicitlySelected && File.Exists(boundPath)) File.Delete(boundPath);
                settings = settings with
                {
                    LyricsText = source.Text,
                    LyricsTimingText = source.TimingText,
                    LyricsBindingJson = "",
                    LyricsScorePath = scorePath,
                    LyricsFilePath = storedPath
                };
                UpdateLyricsStatus();
                _ = SaveCurrentSettingsAsync(true);
                string origin = explicitlySelected ? "已选择" : "已找到同名";
                return $"{origin}歌词：{Path.GetFileName(lyricsPath)}{persistenceWarning}";
            }
            catch (Exception e) when (!explicitlySelected &&
                (e is IOException or UnauthorizedAccessException or DecoderFallbackException or FormatException))
            {
                ClearLyricsBinding(scorePath);
                return "同名歌词读取失败，未绑定：" + e.Message;
            }
        }

        if (SamePath(settings.LyricsScorePath, scorePath) && !string.IsNullOrWhiteSpace(settings.LyricsText))
        {
            if (!string.IsNullOrWhiteSpace(settings.LyricsBindingJson))
            {
                try { ScoreLyrics.Parse(settings.LyricsBindingJson, CurrentTimingNotes()); }
                catch (FormatException)
                {
                    ClearLyricsBinding(scorePath);
                    return "谱面已改变，旧歌词绑定未沿用";
                }
            }
            SetLyricsSource(settings.LyricsText, settings.LyricsTimingText);
            lyricsBindingJson = settings.LyricsBindingJson;
            UpdateLyricsStatus();
            return "已恢复此 TXT 的已保存歌词";
        }

        ClearLyricsBinding(scorePath);
        return "未找到对应歌词";
    }

    private void ClearLyricsBinding(string scorePath)
    {
        SetLyricsSource("", "");
        settings = settings with
        {
            LyricsText = "",
            LyricsTimingText = "",
            LyricsBindingJson = "",
            LyricsScorePath = Path.GetFullPath(scorePath),
            LyricsFilePath = ""
        };
        UpdateLyricsStatus();
        _ = SaveCurrentSettingsAsync(true);
    }

    private void SetLyricsSource(string text, string timingText)
    {
        applyingLyricsSource = true;
        try { lyricsInput.Text = LyricsTimeline.NormalizeText(text); }
        finally { applyingLyricsSource = false; }
        lyricsTimingText = timingText;
        lyricsBindingJson = "";
        SyncLocalAudioFromHost();
    }

    private void ApplyScoreLyrics(ScoreLyrics binding, string scorePath)
    {
        var notes = CurrentTimingNotes();
        binding.Validate(notes);
        string bindingPath = ScoreLyrics.SidecarPath(scorePath);
        binding.Save(bindingPath, notes);
        SetLyricsSource(string.Join(Environment.NewLine, binding.Cues.Select(c => c.Text)), "");
        lyricsBindingJson = binding.ToJson();
        settings = settings with { LyricsText = lyricsInput.Text, LyricsTimingText = "", LyricsBindingJson = lyricsBindingJson,
            LyricsScorePath = Path.GetFullPath(scorePath), LyricsFilePath = bindingPath };
        currentScorePath = Path.GetFullPath(scorePath);
        UpdateLyricsStatus(); SyncLocalAudioFromHost();
        _ = SaveCurrentSettingsAsync(true);
    }

    private void AlignLyrics()
    {
        if (cancellation != null || beginning || editingHotkeys || closing) return;
        try
        {
            if (currentScorePath == null) throw new FormatException("请先导入一首曲谱，再保存歌词对应关系。");
            var notes = CurrentTimingNotes();
            int tempo = int.TryParse(bpm.Text, out int t) ? t : settings.Bpm;
            var dialog = new LyricsBindingDialog(notes, tempo, lyricsInput.Text, lyricsBindingJson) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result == null || closing) return;
            ApplyScoreLyrics(dialog.Result, currentScorePath);
            status.Text = $"已保存 {dialog.Result.Cues.Count} 句歌词对应关系；重新导入此曲谱会自动恢复。";
        }
        catch (Exception error) { status.Text = "歌词校准失败：" + error.Message; }
    }

    private async Task SearchLyricsAsync()
    {
        if (lyricsSearchCancellation != null) { lyricsSearchCancellation.Cancel(); return; }
        if (cancellation != null || beginning || editingHotkeys || closing) return;
        if (currentScorePath == null) { status.Text = "请先导入曲谱，再搜索歌词。"; return; }
        string query = lyricsSearchTitle.Text.Trim();
        if (query.Length == 0) { status.Text = "请填写搜词曲名。"; return; }
        if (!string.IsNullOrWhiteSpace(lyricsBindingJson))
        {
            try
            {
                ScoreLyrics.Parse(lyricsBindingJson, CurrentTimingNotes());
                status.Text = "当前已有按音符绑定的歌词，已保留；可在“校准歌词”中修改，无需重新下载。";
                return;
            }
            catch (FormatException) { /* A changed score may obtain a fresh lyric draft. */ }
        }
        string scorePath = currentScorePath;
        using var request = new CancellationTokenSource();
        lyricsSearchCancellation = request;
        beginning = true; SetBusy(true);
        searchLyrics.Content = "取消搜词";
        status.Text = $"正在搜索“{query}”的歌词…";
        string? temp = null;
        try
        {
            OnlineLyrics? found = await lyricsLookup(query, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (closing) return;
            if (found == null)
            {
                status.Text = "未找到匹配歌词；可修改搜词曲名或补充歌手后重试，当前曲谱和歌词已保留。";
                return;
            }
            string raw = found.IsSynced ? found.Text : LyricsTimeline.NormalizeText(found.Text);
            if (string.IsNullOrWhiteSpace(LyricsTimeline.NormalizeText(raw))) throw new FormatException("搜索结果没有可用歌词。");
            if (Encoding.UTF8.GetByteCount(raw) > OnlineScoreClient.MaxLyricsBytes || raw.Length > 200000)
                throw new FormatException("歌词超过允许大小。");
            string target = LyricsSidecarPath(scorePath);
            temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, raw, new UTF8Encoding(false), request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (closing) return;
            File.Move(temp, target, true); temp = null;
            BindLyricsToFinalScore(scorePath, target);
            string artist = string.IsNullOrWhiteSpace(found.ArtistName) ? "" : " · " + found.ArtistName;
            status.Text = $"已下载并导入《{found.TrackName}》{artist}，保存为 {Path.GetFileName(target)}。待校准：请确认与当前曲谱对应。";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        { if (!closing) status.Text = "已取消搜词，当前曲谱和歌词已保留。"; }
        catch (Exception error)
        { if (!closing) status.Text = "歌词搜索或导入失败，当前曲谱保留：" + error.Message; }
        finally
        {
            if (temp != null)
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            lyricsSearchCancellation = null; beginning = false;
            if (!closing) { searchLyrics.Content = "搜索并导入歌词"; SetBusy(false); }
        }
    }

    private async Task AlignFromAudioAsync()
    {
        if (audioAlignmentCancellation != null) { audioAlignmentCancellation.Cancel(); return; }
        if (beginning || running != null || closing) return;
        string? sourcePath = currentCcmzPath;
        string query = lyricsSearchTitle.Text.Trim();
        if (sourcePath == null || !File.Exists(sourcePath))
        {
            var picker = new OpenFileDialog { Title = "选择 CCMZ，自动配词并导入", Filter = "CCMZ 曲谱 (*.ccmz)|*.ccmz" };
            if (picker.ShowDialog(this) != true) return;
            sourcePath = picker.FileName;
            try
            {
                var metadata = CcmzImporter.Import(File.ReadAllBytes(sourcePath), Path.GetFileNameWithoutExtension(sourcePath));
                query = BpmFileName.ReadTitle(metadata.Title + ".txt");
            }
            catch (Exception error) { status.Text = "未能读取 CCMZ，当前曲谱保留：" + error.Message; return; }
        }
        if (query.Length == 0) { status.Text = "请填写曲名，可补充歌手。"; return; }
        using var request = new CancellationTokenSource();
        audioAlignmentCancellation = request; beginning = true; SetBusy(true);
        audioAlignLyrics.Content = "取消逐字对齐";
        try
        {
            var progress = new Progress<string>(message => { if (!closing) status.Text = message; });
            var result = await audioAligner(sourcePath, query, progress, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (closing) return;
            var binding = result.Score.Lyrics!;
            ValidateAutomaticBinding(result);
            LoadImportedScoreCore(sourcePath, result.Score, null, restoreExistingAlignment: false);
            ApplyScoreLyrics(binding, currentScorePath!);
            status.Text = $"已自动导入并保存 {binding.Syllables.Count} 个字／音节，疑点 {binding.Syllables.Count(s => s.NeedsReview)} 项（算法标记，并非准确率）。可直接播放；录音来源：{result.SourceUrl}；记录：{result.JobDirectory}";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        { if (!closing) status.Text = "已取消逐字对齐，当前曲谱和绑定已保留。"; }
        catch (Exception error)
        { if (!closing) status.Text = "逐字对齐未导入，当前曲谱保留：" + error.Message; }
        finally
        {
            audioAlignmentCancellation = null; beginning = false;
            if (!closing) { audioAlignLyrics.Content = "一键配词并导入"; SetBusy(false); }
        }
    }

    private static void ValidateAutomaticBinding(AudioLyricResult result)
    {
        var binding = result.Score.Lyrics ?? throw new FormatException("没有得到逐字音符对应。");
        binding.Validate(result.Score.Notes);
        var anchors = binding.Syllables.ToDictionary(s => s.AlignmentIndex);
        if (result.Words.Count == 0 || anchors.Count != result.Words.Count || result.Words.Select(w => w.Index).Distinct().Count() != result.Words.Count ||
            result.Words.Any(w => !anchors.TryGetValue(w.Index, out var anchor) || LyricsTimeline.NormalizeText(w.Text) != LyricsTimeline.NormalizeText(anchor.Text)))
            throw new FormatException("部分歌词没有对应音符，未导入不完整结果。");
    }

    private static (string Text, string TimingText) ReadLyricsSource(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new IOException("歌词文件不得超过 1MB。");
        string raw = File.ReadAllText(path, new UTF8Encoding(false, true));
        string text = LyricsTimeline.NormalizeText(raw);
        if (text.Length > 200000) throw new FormatException("歌词文本不得超过 200000 个字符。");
        if (LyricsTimeline.Tokenize(text).Count == 0) throw new FormatException("歌词文件中没有可用歌词片段。");
        return (text, LyricsTimeline.HasTiming(raw) ? raw : "");
    }

    private static string? FindLyricsSidecar(string scorePath)
    {
        string directory = Path.GetDirectoryName(scorePath) ?? AppContext.BaseDirectory;
        string stem = Path.GetFileNameWithoutExtension(scorePath);
        foreach (string suffix in new[] { ".lyrics.json", ".lyrics.txt", ".lrc", ".lyrics", ".lyric.txt" })
        {
            string candidate = Path.Combine(directory, stem + suffix);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string LyricsSidecarPath(string scorePath) =>
        Path.Combine(Path.GetDirectoryName(scorePath) ?? AppContext.BaseDirectory,
            Path.GetFileNameWithoutExtension(scorePath) + ".lyrics.txt");

    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private void UpdateLyricsStatus()
    {
        int count = LyricsTimeline.Tokenize(lyricsInput.Text).Count;
        string enabled = showLyrics.IsChecked == true ? "已开启" : "未开启";
        string mapping = "";
        try
        {
            var notes = CurrentTimingNotes();
            int tempo = int.TryParse(bpm.Text, out int parsedTempo) ? parsedTempo : settings.Bpm;
            var timeline = LyricsTimeline.Create(lyricsInput.Text, notes, 60000d / tempo, timingText: lyricsTimingText, bindingJson: lyricsBindingJson);
            mapping = timeline.HasLyrics
                ? $"{timeline.Alignment.Message}。"
                : "未绑定可播放歌词；" + (string.IsNullOrWhiteSpace(lyricsBindingJson) ? "请点击“校准歌词”指定每句起始音符。" : timeline.Alignment.Message);
        }
        catch (FormatException) { }
        lyricsStatus.Text = count == 0
            ? $"{enabled}；还没有歌词行。{mapping}"
            : $"{enabled}；已输入 {count} 行歌词。{mapping}{DescribeLyricsLayout(settings.LyricsLayout)}";
    }

    private static string DescribeLyricsLayout(LyricsLayout layout) =>
        $"位置 {layout.Left * 100:0}%/{layout.Top * 100:0}%，大小 {layout.Width * 100:0}%×{layout.Height * 100:0}%，字号 {layout.FontSize:0}px，当前/下一句颜色 {layout.CurrentColor} / {layout.NextColor}。";

    private void ShowLyricsWindow()
    {
        if (closing || lyricsWindow != null) return;
        lyricsWindow = new LyricsWindow(settings.LyricsLayout);
        lyricsWindow.SettingsRequested += ConfigureLyrics;
        lyricsWindow.LayoutChanged += UpdateLyricsLayout;
    }

    private void UpdateLyricsLayout(LyricsLayout layout)
    {
        if (closing) return;
        settings = settings with { LyricsLayout = layout.Normalize() };
        lyricsWindow?.ApplyLayout(settings.LyricsLayout);
        lyricsStatus.Text = DescribeLyricsLayout(settings.LyricsLayout);
        SyncLocalAudioFromHost();
        _ = SaveCurrentSettingsAsync(true);
    }

    private void SetLyricsCue(string text, string? next, CancellationToken token)
    {
        if (closing || token.IsCancellationRequested) return;
        lyricsWindow?.ShowLyrics(text, next);
    }

    private void HideLyricsWindow()
    {
        if (lyricsWindow != null && lyricsWindow.IsVisible)
            lyricsWindow.Close();
        lyricsWindow = null;
    }

    private void ImportLyricsFile()
    {
        if (cancellation != null || beginning || editingHotkeys || closing) return;
        if (currentScorePath == null)
        {
            status.Text = "请先导入或下载歌曲，生成最终 TXT 后再绑定歌词。";
            return;
        }
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择要绑定到当前 TXT 的歌词",
                Filter = "歌词与绑定文件|*.lrc;*.lyrics.txt;*.lyrics;*.txt;*.lyrics.json|音符绑定歌词|*.lyrics.json|LRC 歌词|*.lrc|纯文本歌词|*.lyrics.txt;*.lyrics;*.txt",
                CheckFileExists = true,
                InitialDirectory = Path.GetDirectoryName(currentScorePath)
            };
            if (dialog.ShowDialog(this) != true || closing) return;
            string message = BindLyricsToFinalScore(currentScorePath, dialog.FileName);
            status.Text = $"已绑定歌词到 {Path.GetFileName(currentScorePath)}：{message}";
        }
        catch (Exception e) { status.Text = "绑定歌词失败：" + e.Message; }
    }

    private async Task<bool> OpenScoreFileAsync(string path, string? selectedLyricsPath = null)
    {
        if (cancellation != null || beginning || editingHotkeys || closing) return false;
        beginning = true; SetBusy(true);
        try
        {
            string extension = Path.GetExtension(path);
            if (extension.Equals(".ccmz", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".musicxml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mxl", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mid", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".midi", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(path).Length > CcmzImporter.MaxFileBytes) throw new IOException("文件不得超过 50MB。");
                ImportedScore imported = await Task.Run(() => OpenConvertedScore(path, extension));
                if (closing) return false;
                LoadImportedScore(path, imported, selectedLyricsPath);
                return true;
            }
            if (!string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
                throw new IOException("请选择 TXT 简谱文件。");
            if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("文件不得超过1MB。");
            score.Text = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true));
            currentTimingJson = "";
            if (File.Exists(ScoreTiming.SidecarPath(path)))
            {
                string candidate = File.ReadAllText(ScoreTiming.SidecarPath(path), new UTF8Encoding(false, true));
                try { ScoreTiming.Apply(score.Text, candidate); currentTimingJson = candidate; }
                catch (FormatException) { status.Text = "精确时值与谱面不匹配，已使用 TXT 时值。"; }
            }
            currentCcmzPath = null;
            currentSourceScorePath = null;
            currentSourcePreviewText = null;
            fileInfo.ToolTip = null;
            currentScoreTitle = BpmFileName.ReadTitle(path);
            lyricsSearchTitle.Text = currentScoreTitle;
            Title = "电子琴 2.1 — " + Path.GetFileName(path);
            fileInfo.Text = "当前文件：" + Path.GetFileName(path);
            SyncPracticeModeFromHost();

            string bpmMessage = ApplyBpmFromFileName(path);
            string lyricMessage = BindLyricsToFinalScore(path, selectedLyricsPath);
            Preview();
            status.Text = "已导入：" + Path.GetFileName(path) + " · " + bpmMessage + " · " + lyricMessage;
            return true;
        }
        catch (Exception e) { status.Text = "导入失败：" + e.Message; }
        finally { beginning = false; SetBusy(false); }
        return false;
    }

    private async Task<bool> ImportDownloadedCcmzAsync(string path, byte[] source, ImportedScore preview, CancellationToken token)
    {
        if (beginning || cancellation != null || closing || editingHotkeys) return false;
        beginning = true;
        SetBusy(true);
        try
        {
            string? savedPath = null;
            string? compileError = null;
            int count = 0;
            try
            {
                var (arrangement, bytes) = await Task.Run(() => CompilePerformanceSource(path, source), token);
                count = arrangement.Performance!.Count;
                savedPath = await SaveDownloadedPerformanceAsync(path, bytes, token);
            }
            catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
            {
                compileError = error.Message;
            }
            token.ThrowIfCancellationRequested();
            if (closing) return false;
            LoadImportedScore(path, preview);
            if (savedPath != null)
            {
                fileInfo.Text = "当前文件：" + Path.GetFileName(path) + " · 多音谱已就绪";
                fileInfo.ToolTip = savedPath;
                status.Text = $"已自动导入 · 多音谱 {count} 音：{savedPath}";
            }
            else status.Text = $"已导入简谱，多音谱未生成：{compileError}";
            return true;
        }
        finally { beginning = false; SetBusy(false); }
    }

    private static async Task<string> SaveDownloadedPerformanceAsync(string sourcePath, byte[] bytes, CancellationToken token)
    {
        string stem = Path.Combine(Path.GetDirectoryName(sourcePath)!, Path.GetFileNameWithoutExtension(sourcePath) + "_多音");
        string temporary = Path.Combine(Path.GetDirectoryName(sourcePath)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            for (int attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                string candidate = stem + (attempt == 1 ? "" : $" ({attempt})") + ".json";
                try { File.Move(temporary, candidate); return candidate; }
                catch (IOException) when (File.Exists(candidate))
                {
                    if (new FileInfo(candidate).Length == bytes.Length &&
                        (await File.ReadAllBytesAsync(candidate, token)).SequenceEqual(bytes)) return candidate;
                }
            }
        }
        finally { File.Delete(temporary); }
    }

    private async Task CompilePerformanceAsync()
    {
        if (beginning || cancellation != null || closing) return;
        string? path = CurrentPerformanceSource();
        if (path is null)
        {
            var open = new OpenFileDialog { Title = "选择多声部源谱", Filter = "多声部源谱|*.ccmz;*.mid;*.midi;*.musicxml;*.xml;*.mxl" };
            if (open.ShowDialog(this) != true) return;
            path = open.FileName;
        }
        beginning = true;
        SetBusy(true);
        try
        {
            var (imported, bytes) = await Task.Run(() => CompilePerformanceFile(path));
            if (closing) return;
            var save = new SaveFileDialog
            {
                Title = "保存实体琴多音谱", Filter = "实体琴曲谱|*.json", DefaultExt = ".json",
                FileName = Path.GetFileNameWithoutExtension(path) + "_多音.json"
            };
            if (save.ShowDialog(this) != true) return;
            await File.WriteAllBytesAsync(save.FileName, bytes);
            var warnings = imported.Warnings.Where(w => !w.StartsWith("单音预览", StringComparison.Ordinal));
            MessageBox.Show(this, $"已编译 {imported.Performance!.Count} 个演奏音符。\n连接实体琴后，在琴的网页中选择此 JSON 并上传，再点击播放。\n" +
                string.Join("\n", warnings), "实体琴多音谱", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            if (!closing) MessageBox.Show(this, error.Message, "多音谱编译失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { beginning = false; SetBusy(false); }
    }

    private string? CurrentPerformanceSource() => File.Exists(currentSourceScorePath) ? currentSourceScorePath : null;

    private static (ImportedScore Score, byte[] Bytes) CompilePerformanceFile(string path)
    {
        if (new FileInfo(path).Length > CcmzImporter.MaxFileBytes) throw new IOException("文件不得超过 50MB。");
        return CompilePerformanceSource(path, File.ReadAllBytes(path));
    }

    private static (ImportedScore Score, byte[] Bytes) CompilePerformanceSource(string path, byte[] source)
    {
        var arrangement = ReadPerformanceSource(path, source);
        string id = "poly-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source))[..32].ToLowerInvariant();
        return (arrangement, SongPayloadFactory.SerializeForDevice(SongPayloadFactory.Create(arrangement, id)));
    }

    internal static ImportedScore ReadPerformanceFile(string path)
    {
        if (new FileInfo(path).Length > CcmzImporter.MaxFileBytes) throw new IOException("文件不得超过 50MB。");
        return ReadPerformanceSource(path, File.ReadAllBytes(path));
    }

    private static ImportedScore ReadPerformanceSource(string path, byte[] source)
    {
        string title = Path.GetFileNameWithoutExtension(path);
        using var stream = new MemoryStream(source, writable: false);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ccmz" => CcmzImporter.Import(source, title, preserveTiming: true, includePerformance: true),
            ".mid" or ".midi" => MidiImporter.ImportTrack(source, title, null, preserveTiming: true, includePerformance: true),
            ".mxl" => MusicXmlImporter.ImportCompressed(stream, title, preserveTiming: true, includePerformance: true),
            ".xml" or ".musicxml" => MusicXmlImporter.Import(new UTF8Encoding(false, true).GetString(source).TrimStart('\uFEFF'),
                title, preserveTiming: true, includePerformance: true),
            _ => throw new FormatException("请选择 CCMZ、MIDI、MusicXML 或 MXL 源谱。")
        };
    }

    private static ImportedScore OpenConvertedScore(string path, string extension)
    {
        string fallbackTitle = Path.GetFileNameWithoutExtension(path);
        ImportedScore imported;
        if (extension.Equals(".ccmz", StringComparison.OrdinalIgnoreCase))
        {
            imported = CcmzImporter.Import(File.ReadAllBytes(path), fallbackTitle, preserveTiming: true);
        }
        else if (extension.Equals(".musicxml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mxl", StringComparison.OrdinalIgnoreCase))
        {
            imported = MusicXmlImporter.ImportFile(path, fallbackTitle, preserveTiming: true);
        }
        else
        {
            imported = MidiImporter.Import(File.ReadAllBytes(path), fallbackTitle, preserveTiming: true);
        }
        return imported;
    }

    private void LoadImportedScore(string path, ImportedScore imported, string? selectedLyricsPath = null)
        => LoadImportedScoreCore(path, imported, selectedLyricsPath, restoreExistingAlignment: true);

    private void LoadImportedScoreCore(string path, ImportedScore imported, string? selectedLyricsPath, bool restoreExistingAlignment)
    {
        currentSourceScorePath = Path.GetFullPath(path);
        currentCcmzPath = Path.GetExtension(path).Equals(".ccmz", StringComparison.OrdinalIgnoreCase) ? currentSourceScorePath : null;
        if (restoreExistingAlignment && currentCcmzPath != null && imported.Bpm is int alignedBpm)
        {
            string restoredTitle = new string(imported.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(restoredTitle)) restoredTitle = fallbackFileTitle(path);
            string restoredPath = Path.Combine(AppContext.BaseDirectory, "简谱", $"{restoredTitle}_源谱 BPM：{alignedBpm}.txt");
            if (File.Exists(restoredPath) && File.Exists(ScoreLyrics.SidecarPath(restoredPath)))
            {
                try
                {
                    string restoredText = File.ReadAllText(restoredPath, new UTF8Encoding(false, true));
                    var restoredNotes = File.Exists(ScoreTiming.SidecarPath(restoredPath))
                        ? ScoreTiming.Apply(restoredText, File.ReadAllText(ScoreTiming.SidecarPath(restoredPath)))
                        : ScoreParser.Parse(restoredText, true);
                    var restoredBinding = ScoreLyrics.Parse(File.ReadAllText(ScoreLyrics.SidecarPath(restoredPath)), restoredNotes);
                    if (restoredBinding.SourceScoreSha256.Length == 64 && string.Equals(restoredBinding.SourceScoreSha256,
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), StringComparison.OrdinalIgnoreCase))
                        imported = imported with { Text = restoredText, Notes = restoredNotes, Lyrics = restoredBinding };
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or DecoderFallbackException)
                { imported = imported with { Warnings = imported.Warnings.Concat(new[] { "原有逐字绑定未能恢复：" + error.Message }).ToArray() }; }
            }
        }
        currentTimingJson = ScoreTiming.Create(imported.Text, imported.Notes).ToJson();
        currentSourcePreviewText = imported.Text;
        score.Text = imported.Text;
        currentScorePath = Path.GetFullPath(path);
        currentScoreTitle = imported.Title;
        lyricsSearchTitle.Text = BpmFileName.ReadTitle(imported.Title + ".txt");
        Title = "电子琴 2.1 — " + Path.GetFileName(path);
        fileInfo.Text = "当前文件：" + Path.GetFileName(path) + " · 单音预览，可编译多音谱";
        fileInfo.ToolTip = null;
        SyncPracticeModeFromHost();
        if (imported.Bpm is int sourceBpm && imported.PlaybackBpm is int playbackBpm)
        {
            bpm.Text = playbackBpm.ToString();
            bpmStatus.Text = sourceBpm == playbackBpm
                ? "✓ 已读取源谱 BPM：" + sourceBpm
                : $"✓ 源谱 BPM：{sourceBpm}；播放使用：{playbackBpm}";
            bpmStatus.Foreground = BrushOf("#197A62");
        }
        else
        {
            bpmStatus.Text = "源谱未标注 BPM，请手动确认速度";
            bpmStatus.Foreground = BrushOf("#B54708");
        }
        Preview();

        string saved = "未自动保存（源谱未提供 BPM）";
        string lyricMessage = "未绑定歌词（尚未生成最终 TXT）";
        if (imported.Bpm is int saveBpm)
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "简谱");
            Directory.CreateDirectory(directory);
            string safeTitle = new string(imported.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = fallbackFileTitle(path);
            string target = Path.Combine(directory, $"{safeTitle}_源谱 BPM：{saveBpm}.txt");
            SaveImportedText(target, imported.Text);
            if (currentTimingJson.Length > 0)
                File.WriteAllText(ScoreTiming.SidecarPath(target), currentTimingJson, new UTF8Encoding(false));
            saved = "已保存 TXT：" + Path.GetFileName(target);
            string? automaticLyricsPath = selectedLyricsPath ??
                (File.Exists(ScoreLyrics.SidecarPath(target)) ? ScoreLyrics.SidecarPath(target) : null) ??
                FindLyricsSidecar(path) ??
                FindLyricsSidecar(target) ??
                FindLyricsSidecar(Path.Combine(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory, safeTitle + ".txt"));
            lyricMessage = BindLyricsToFinalScore(target, automaticLyricsPath);
            if (imported.Lyrics != null && selectedLyricsPath == null &&
                automaticLyricsPath?.EndsWith(".lyrics.json", StringComparison.OrdinalIgnoreCase) != true)
            {
                ApplyScoreLyrics(imported.Lyrics, target);
                lyricMessage = $"已从源谱绑定 {imported.Lyrics.Cues.Count} 句歌词";
            }
            else if (imported.Lyrics != null && selectedLyricsPath == null && currentCcmzPath != null)
            {
                // A matching note signature keeps existing calibration. Link
                // that retained binding to this import, not an older ZIP hash.
                var retained = ScoreLyrics.Parse(lyricsBindingJson, imported.Notes);
                string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(currentCcmzPath))).ToLowerInvariant();
                ApplyScoreLyrics(retained with { SourceScoreSha256 = sourceHash }, target);
            }
        }
        else if (imported.Lyrics != null)
        {
            ApplyScoreLyrics(imported.Lyrics, path);
            lyricMessage = $"已从源谱绑定 {imported.Lyrics.Cues.Count} 句歌词（播放速度请手动确认）";
        }
        else ClearLyricsBinding(path);

        string warning = imported.Warnings.Count == 0 ? "" : " · " + string.Join("；", imported.Warnings);
        status.Text = $"已导入：{Path.GetFileName(path)} · {imported.Notes.Count} 个音符 · {saved} · {lyricMessage}{warning}";
        SyncLocalAudioFromHost();
    }

    private static string fallbackFileTitle(string path) => Path.GetFileNameWithoutExtension(path);

    private static void SaveImportedText(string path, string text)
    {
        if (File.Exists(path))
        {
            if (File.ReadAllText(path, Encoding.UTF8) == text) return;
            string history = Path.Combine(AppContext.BaseDirectory, "work", "score-history", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(history);
            File.Copy(path, Path.Combine(history, Path.GetFileName(path)));
            foreach (string previous in new[] { ScoreLyrics.SidecarPath(path), ScoreTiming.SidecarPath(path), LyricsSidecarPath(path) })
                if (File.Exists(previous)) File.Copy(previous, Path.Combine(history, Path.GetFileName(previous)));
        }
        File.WriteAllText(path, text, new UTF8Encoding(false));
        // A changed arrangement must not inherit anchors from the previous note sequence.
        if (File.Exists(ScoreLyrics.SidecarPath(path))) File.Delete(ScoreLyrics.SidecarPath(path));
    }

    private string ApplyBpmFromFileName(string path)
    {
        if (!BpmFileName.TryRead(path, out int tempo, out string message))
        {
            bpmStatus.Text = "未读取文件名 BPM，请手动确认";
            bpmStatus.Foreground = BrushOf("#B54708");
            return "未读取到 BPM，请手动确认速度";
        }
        bpm.Text = tempo.ToString();
        bpmStatus.Text = "✓ 自动读取 BPM：" + tempo;
        bpmStatus.Foreground = BrushOf("#197A62");
        return $"已自动设置 BPM {tempo}";
    }
    private void Stop()
    {
        if (closing) return; // Shutdown owns final input cleanup; do not race it.
        cancellation?.Cancel();
        HideLyricsWindow();
        if (cancellation != null) status.Text = "正在停止并释放输入…";
        else { var error = output.Release(); status.Text = error ?? "已停止。"; }
    }
    private async Task Begin(uint triggerKey = 0)
    {
        if (cancellation != null || closing || beginning || editingHotkeys) return;
        beginning = true; SetBusy(true);
        try
        {
            var notes = CurrentTimingNotes();
            var (ms, spaceMs, lineMs) = Timing();
            ValidatePlaybackTiming(notes, ms);
            var lyricTimeline = showLyrics.IsChecked == true
                ? LyricsTimeline.CreateOptional(lyricsInput.Text, notes, ms, spaceMs, lineMs, lyricsTimingText, lyricsBindingJson)
                : null;
            if (hotkeys != null && hotkeys.StopReady != true)
                throw new InvalidOperationException($"停止键 {settings.Stop.Label} 不可用，禁止真实演奏。请点击“自定义快捷键”或“重试注册”。");
            int silence = int.Parse(gap.Text);
            bool simulation = dryRun;
            cancellation = new CancellationTokenSource();
            if (closing || cancellation.IsCancellationRequested) return;
            _ = SaveCurrentSettingsAsync();
            SetBusy(true); log.Clear();
            running = Run(notes, lyricTimeline, currentScoreTitle, ms, silence, spaceMs, lineMs, simulation, triggerKey, cancellation.Token);
            await running;
        }
        catch (Exception e) { status.Text = e.Message; }
        finally
        {
            HideLyricsWindow();
            cancellation?.Dispose(); cancellation = null; running = null; beginning = false; SetBusy(false);
        }
    }
    private (double Ms, int Space, int Line) Timing()
    {
        if (!int.TryParse(bpm.Text, out int tempo) || tempo < 20 || tempo > 300)
            throw new FormatException("速度范围20～300（四分音符/分钟）。");
        return (60000.0 / tempo, 0, 0);
    }

    private List<ScoreNote> CurrentTimingNotes()
    {
        if (currentTimingJson.Length > 0)
        {
            try { return ScoreTiming.Apply(score.Text, currentTimingJson); }
            catch (FormatException) { currentTimingJson = ""; status.Text = "谱面已修改，精确时值已失效；现在使用 TXT 近似时值。"; }
        }
        return ScoreParser.Parse(score.Text, true);
    }

    private void ValidatePlaybackTiming(List<ScoreNote> notes, double ms)
    {
        if (!int.TryParse(gap.Text, out int silence) || silence < 10 || silence > 5000 ||
            notes.Any(n => n.Degree != 0 && n.Beats * ms - silence < 40))
            throw new FormatException("留白范围10～5000毫秒；最短音减去留白后须至少40毫秒。请降低速度或减少留白。");
    }

    private void Preview()
    {
        if (cancellation != null || closing) return;
        try
        {
            var notes = CurrentTimingNotes();
            var (ms, space, line) = Timing();
            ValidatePlaybackTiming(notes, ms);
            if (hotkeys != null && hotkeys.StopReady != true)
                throw new InvalidOperationException($"停止键 {settings.Stop.Label} 当前不可用，无法安全开始真实演奏。");
            double total = 0;
            var lines = new StringBuilder();
            foreach (var note in notes)
            {
                total += note.PauseBefore(space, line);
                if (lines.Length < 30000)
                    lines.AppendLine($"{total / 1000:0.###}s  {note.Label}  {note.Beats:0.###}拍  ({note.Beats * ms:0.##}ms)");
                total += note.Beats * ms;
            }
            previewSummary.Foreground = BrushOf("#197A62");
            previewSummary.Text = $"{notes.Count} 个音 / 休止 · {notes.Sum(n => n.Beats):0.###} 拍 · 预计 {total / 1000:0.###} 秒";
            preview.Text = lines.ToString();
            status.Text = $"检查通过：{notes.Count} 个音 / 休止，预计 {total / 1000:0.###} 秒；未发送键鼠。";
        }
        catch (Exception e)
        {
            previewSummary.Text = "检查未通过，请修正谱面或设置"; previewSummary.Foreground = BrushOf("#B42318");
            preview.Text = "谱面/设置错误：" + e.Message;
            status.Text = "检查未通过：" + e.Message;
        }
    }
    private void SetBusy(bool busy)
    {
        busy |= closing;
        alignLyrics.IsEnabled = !busy;
        searchLyrics.IsEnabled = !closing && (!busy || lyricsSearchCancellation != null);
        audioAlignLyrics.IsEnabled = !closing && (!busy || audioAlignmentCancellation != null);
        lyricsSearchTitle.IsReadOnly = busy;
        start.IsEnabled = import.IsEnabled = importCcmz.IsEnabled = practiceMode.IsEnabled = online.IsEnabled = refresh.IsEnabled = bindLyrics.IsEnabled = gap.IsEnabled = !busy;
        bpm.IsEnabled = instrumentSelector.IsEnabled = !busy;
        configure.IsEnabled = retry.IsEnabled = !busy;
        showLyrics.IsEnabled = configureLyrics.IsEnabled = !busy;
        lyricsInput.IsReadOnly = score.IsReadOnly = busy;
    }
    private async Task Run(List<ScoreNote> notes, LyricsTimeline? lyricTimeline, string songTitle, double ms, int silence, int spaceMs, int lineMs, bool simulation, uint triggerKey, CancellationToken token)
    {
        string result = "演奏完成。";
        try
        {
            await WaitForRelease(triggerKey, token);
            if (lyricTimeline?.HasLyrics == true && !string.IsNullOrWhiteSpace(songTitle))
                Dispatcher.Invoke(() =>
                {
                    ShowLyricsWindow();
                    lyricsWindow?.ShowTitle(songTitle);
                });
            for (int n = 3; n > 0; n--)
            { status.Text = $"{n}秒后开始，请切到目标窗口…"; await Task.Delay(1000, token); }
            // Check again after countdown: the user may have used Alt+Tab to switch windows.
            await WaitForRelease(triggerKey, token);
            var target = NativeInput.GetForegroundWindow();
            NativeInput.GetWindowThreadProcessId(target, out uint pid);
            if (!simulation && (target == IntPtr.Zero || pid == (uint)Environment.ProcessId))
                throw new InvalidOperationException("没有切到外部目标窗口，已取消。");
            if (lyricTimeline?.HasLyrics == true)
                Dispatcher.Invoke(ShowLyricsWindow);
            await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                string? activeLyric = null;
                void Check()
                {
                    token.ThrowIfCancellationRequested();
                    if (!simulation && NativeInput.GetForegroundWindow() != target)
                        throw new InvalidOperationException("目标窗口失去焦点，已停止。");
                }
                void Wait(double until)
                {
                    while (true)
                    {
                        Check(); double remaining = until - clock.Elapsed.TotalMilliseconds;
                        if (remaining <= 0) return;
                        if (remaining > 2) token.WaitHandle.WaitOne((int)Math.Min(5, Math.Max(1, remaining - 1)));
                        else Thread.SpinWait(100);
                    }
                }
                var initialError = simulation ? null : output.Release();
                if (initialError != null) throw new InvalidOperationException(initialError);
                double nextStart = 0;
                for (int i = 0; i < notes.Count; i++)
                {
                    var note = notes[i];
                    double noteMs = note.Beats * ms;
                    int pause = note.PauseBefore(spaceMs, lineMs);
                    double begin = nextStart + pause;
                    if (pause > 0)
                    {
                        string pauseLabel = note.SeparatorBefore == ScoreSeparator.LineBreak ? "换行" : "空格";
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                        {
                            if (!closing && !token.IsCancellationRequested)
                                status.Text = $"{pauseLabel}停顿：{pause}ms";
                        }));
                    }
                    Wait(begin); Check();
                    // 严重超时直接停止，避免恢复后瞬间补发积压音符。
                    if (clock.Elapsed.TotalMilliseconds - begin > Math.Min(50, noteMs / 4.0))
                        throw new InvalidOperationException("调度延迟过大，已停止；请降低后台负载后重试。");
                    int index = i;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (closing || token.IsCancellationRequested) return;
                        status.Text = $"{index + 1}/{notes.Count}：{note.Label} / {note.Beats:0.###}拍";
                        if (log.LineCount > 150) log.Clear();
                        log.AppendText($"{begin:0}ms  {note.Label}  {note.Beats:0.###}拍 ({noteMs:0.##}ms)\n"); log.ScrollToEnd();
                    }));
                    if (lyricTimeline != null)
                    {
                        string? lyric = lyricTimeline.ForNote(i);
                        if (lyric != null && !string.Equals(lyric, activeLyric, StringComparison.Ordinal))
                        {
                            activeLyric = lyric;
                            string? nextLyric = lyricTimeline.NextAfterNote(i);
                            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                                new Action(() => SetLyricsCue(lyric, nextLyric, token)));
                        }
                    }
                    if (note.Degree != 0)
                    {
                        if (!simulation) output.Modifiers(note);
                        Wait(begin + 12); Check();
                        if (!simulation) output.NoteOn(note);
                        Wait(begin + noteMs - silence);
                        var error = simulation ? null : output.Release();
                        if (error != null) throw new InvalidOperationException(error);
                    }
                    Wait(begin + noteMs);
                    nextStart = begin + noteMs;
                }
            }, token);
        }
        catch (OperationCanceledException) { result = "已停止。"; }
        catch (Exception e) { result = "已停止：" + e.Message; }
        finally
        {
            // 工作任务已退出，再做最终释放，避免释放之后仍有旧任务按键。
            var error = simulation ? null : await Task.Run(output.Release);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(HideLyricsWindow));
            if (!closing) status.Text = error == null ? result : result + " 释放失败，请手动按下并松开相关键：" + error;
        }
    }

}
