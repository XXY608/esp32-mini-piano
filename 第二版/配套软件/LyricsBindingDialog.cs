using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace HarmonicaPlayer;

public sealed class LyricsBindingRow
{
    public string Text { get; set; } = "";
    public int? Start { get; set; }
    public int? End { get; set; }
    public bool EndLine { get; set; }
    public bool NeedsReview { get; set; }
    public int AlignmentIndex { get; set; } = -1;
    public double? ReferenceStartSeconds { get; set; }
    public double? ReferenceEndSeconds { get; set; }
    public string ReferenceScoreUrl { get; set; } = "";
    public string ReferenceScoreSha256 { get; set; } = "";
    public string AcousticDiagnostics { get; set; } = "";
    public string PositionEvidence { get; set; } = "";
    public string? EvidenceSummary => !string.IsNullOrWhiteSpace(ReferenceScoreUrl)
        ? "字音位置已由带词谱核实；原唱音高／时间诊断仍保留。\n来源：" + ReferenceScoreUrl
        : !string.IsNullOrWhiteSpace(PositionEvidence)
            ? "字音位置得到前后发音与节奏定位支持；原唱音高／时间诊断仍保留。" : null;
}

public sealed class LyricsBindingDialog : Window
{
    private readonly IReadOnlyList<ScoreNote> notes;
    private readonly int bpm;
    private readonly ObservableCollection<LyricsBindingRow> rows = new();
    private readonly DataGrid lyrics = new() { AutoGenerateColumns = false, CanUserAddRows = true, CanUserDeleteRows = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly ListBox noteList = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly MediaPlayer player = new();
    private readonly List<string> audioFiles = new();
    private readonly bool wordMode;
    private readonly MediaPlayer referencePlayer = new();
    private readonly System.Windows.Threading.DispatcherTimer referenceTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly string referenceAudioPath;
    private readonly string referenceUrl;
    private readonly string sourceScoreSha256;
    private readonly IReadOnlyList<BoundLyric> originalWords;
    private double referenceStart, referenceEnd;
    public ScoreLyrics? Result { get; private set; }

    public LyricsBindingDialog(IReadOnlyList<ScoreNote> notes, int bpm, string text, string bindingJson,
        IReadOnlyList<LyricsBindingRow>? draftRows = null)
    {
        this.notes = notes;
        this.bpm = Math.Clamp(bpm, 20, 300);
        ScoreLyrics? restoredBinding = null;
        if (!string.IsNullOrWhiteSpace(bindingJson))
            try { restoredBinding = ScoreLyrics.Parse(bindingJson, notes); }
            catch (FormatException error) { status.Text = error.Message; }
        wordMode = draftRows != null || restoredBinding?.Syllables.Count > 0;
        referenceAudioPath = restoredBinding?.ReferenceAudioPath ?? "";
        referenceUrl = restoredBinding?.ReferenceUrl ?? "";
        sourceScoreSha256 = restoredBinding?.SourceScoreSha256 ?? "";
        originalWords = restoredBinding?.Syllables ?? Array.Empty<BoundLyric>();
        Title = "校准歌词 · 按音符绑定";
        Width = 1050; Height = 700; MinWidth = 800; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(18) };
        Content = panel;
        var help = new TextBlock { Text = wordMode
            ? "每行一个字／音节，对应右侧具体音符；起止序号包含结束音符，一个字可以跨多个音。“待核对”表示自动候选存在疑点；空序号表示尚未匹配。悬停可查看带词谱来源或位置判断依据。保存会保留逐字位置与诊断。"
            : "左侧选择一句歌词，右侧选择它开始的音符，再点“绑定句首”。可直接填写音符序号；一句可跨小节。结束音符为包含的最后一个音符，留空则延续到下一句或长休止。先试听核对，再保存。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        footer.Children.Add(status);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        AddButton(actions, "载入歌词文件", LoadLyrics);
        AddButton(actions, wordMode ? "绑定字音起点" : "绑定句首", BindSelected);
        AddButton(actions, "试听选中音符起 4 拍", PreviewSelected);
        if (wordMode) AddButton(actions, "试听原唱该字", PreviewReference);
        AddButton(actions, "停止试听", () => { player.Stop(); referencePlayer.Stop(); referenceTimer.Stop(); });
        AddButton(actions, "保存对应关系", Save);
        AddButton(actions, "取消", () => DialogResult = false);
        var body = new Grid(); panel.Children.Add(body);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lyrics.Columns.Add(new DataGridTextColumn { Header = wordMode ? "字／音节" : "歌词（整句）", Binding = new Binding(nameof(LyricsBindingRow.Text)), Width = wordMode ? new DataGridLength(110) : new DataGridLength(1, DataGridLengthUnitType.Star) });
        lyrics.Columns.Add(new DataGridTextColumn { Header = "起始音符", Binding = new Binding(nameof(LyricsBindingRow.Start)), Width = 85 });
        lyrics.Columns.Add(new DataGridTextColumn { Header = "结束音符", Binding = new Binding(nameof(LyricsBindingRow.End)), Width = 85 });
        if (wordMode)
        {
            lyrics.Columns.Add(new DataGridCheckBoxColumn { Header = "句末", Binding = new Binding(nameof(LyricsBindingRow.EndLine)), Width = 55 });
            lyrics.Columns.Add(new DataGridCheckBoxColumn { Header = "待核对", Binding = new Binding(nameof(LyricsBindingRow.NeedsReview)), Width = 65 });
            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(LyricsBindingRow.EvidenceSummary))));
            lyrics.RowStyle = rowStyle;
        }
        lyrics.ItemsSource = rows; lyrics.Margin = new Thickness(0, 0, 12, 0); body.Children.Add(lyrics);
        Grid.SetColumn(noteList, 1); body.Children.Add(noteList);
        double beat = 0;
        for (int i = 0; i < notes.Count; i++)
        {
            noteList.Items.Add($"{i + 1,4}  · 第 {beat + 1:0.##} 拍 · {ScoreTextFormatter.FormatNote(notes[i])}");
            beat += notes[i].Beats;
        }
        bool restored = false;
        if (draftRows != null)
        {
            foreach (var row in draftRows) rows.Add(row);
            restored = true;
            status.Text = $"逐字候选 {rows.Count} 项；未匹配 {rows.Count(r => r.Start == null)} 项，待核对 {rows.Count(r => r.NeedsReview)} 项。";
        }
        else if (restoredBinding != null)
        {
            foreach (var cue in wordMode ? restoredBinding.Syllables : restoredBinding.Cues)
                rows.Add(new LyricsBindingRow { Text = cue.Text, Start = cue.StartNote + 1, End = cue.EndNote,
                    EndLine = cue.EndLine, NeedsReview = cue.NeedsReview, AlignmentIndex = cue.AlignmentIndex,
                    ReferenceStartSeconds = cue.ReferenceStartSeconds, ReferenceEndSeconds = cue.ReferenceEndSeconds,
                    ReferenceScoreUrl = cue.ReferenceScoreUrl, ReferenceScoreSha256 = cue.ReferenceScoreSha256,
                    AcousticDiagnostics = cue.AcousticDiagnostics, PositionEvidence = cue.PositionEvidence });
            if (wordMode) status.Text = $"逐字 {rows.Count} 项；带词谱核实 {rows.Count(r => !string.IsNullOrWhiteSpace(r.ReferenceScoreUrl))} 项，位置定位支持 {rows.Count(r => !string.IsNullOrWhiteSpace(r.PositionEvidence))} 项，待核对 {rows.Count(r => r.NeedsReview)} 项。";
            restored = true;
        }
        if (!restored) SetText(text);
        lyrics.SelectionChanged += (_, _) =>
        {
            if (lyrics.SelectedItem is LyricsBindingRow { Start: int start } && start >= 1 && start <= notes.Count)
            { noteList.SelectedIndex = start - 1; noteList.ScrollIntoView(noteList.SelectedItem); }
        };
        player.MediaEnded += (_, _) => player.Stop();
        referencePlayer.MediaOpened += (_, _) =>
        { referencePlayer.Position = TimeSpan.FromSeconds(referenceStart); referencePlayer.Play(); referenceTimer.Start(); };
        referencePlayer.MediaFailed += (_, e) => { referenceTimer.Stop(); status.Text = "原唱试听失败：" + e.ErrorException.Message; };
        referenceTimer.Tick += (_, _) =>
        { if (referencePlayer.Position.TotalSeconds >= referenceEnd) { referencePlayer.Stop(); referenceTimer.Stop(); } };
        Closed += (_, _) =>
        {
            player.Close();
            referenceTimer.Stop(); referencePlayer.Close();
            foreach (string path in audioFiles)
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }

    private static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }

    private void SetText(string text)
    {
        rows.Clear();
        foreach (string line in LyricsTimeline.Tokenize(text))
        {
            if (wordMode)
            {
                var chars = System.Globalization.StringInfo.GetTextElementEnumerator(line);
                while (chars.MoveNext())
                    if (!string.IsNullOrWhiteSpace(chars.GetTextElement())) rows.Add(new LyricsBindingRow { Text = chars.GetTextElement(), NeedsReview = true });
                if (rows.Count > 0) rows[^1].EndLine = true;
            }
            else rows.Add(new LyricsBindingRow { Text = line });
        }
        if (rows.Count > 0) lyrics.SelectedIndex = 0;
        status.Text = $"已载入 {rows.Count} 句；未填写起始音符的句子不会被猜测绑定。";
    }

    private void LoadLyrics()
    {
        try
        {
            var dialog = new OpenFileDialog { Filter = "歌词文件|*.lrc;*.txt;*.lyrics", Title = "载入需要校准的歌词" };
            if (dialog.ShowDialog(this) != true) return;
            if (new FileInfo(dialog.FileName).Length > 1024 * 1024) throw new FormatException("歌词文件不得超过 1MB。");
            SetText(File.ReadAllText(dialog.FileName, new UTF8Encoding(false, true)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or DecoderFallbackException)
        { status.Text = error.Message; }
    }

    private void BindSelected()
    {
        lyrics.CommitEdit(DataGridEditingUnit.Cell, true); lyrics.CommitEdit(DataGridEditingUnit.Row, true);
        if (lyrics.SelectedItem is not LyricsBindingRow row || noteList.SelectedIndex < 0)
        { status.Text = "请先选择歌词句子与起始音符。"; return; }
        if (notes[noteList.SelectedIndex].Degree == 0) { status.Text = "句首请选择可发声的音符。"; return; }
        row.Start = noteList.SelectedIndex + 1;
        row.End = null;
        lyrics.Items.Refresh();
        status.Text = $"已将“{row.Text}”绑定到第 {row.Start} 个音符；可试听后继续绑定下一句。";
    }

    private void PreviewSelected()
    {
        try
        {
            referenceTimer.Stop(); referencePlayer.Stop();
            int index = noteList.SelectedIndex;
            if (index < 0) { status.Text = "请先选择右侧音符。"; return; }
            var snippet = new List<ScoreNote>();
            double beats = 0;
            for (int i = index; i < notes.Count && beats < 4; i++)
            { snippet.Add(notes[i]); beats += notes[i].Beats; }
            string path = Path.Combine(Path.GetTempPath(), "piano-lyrics-" + Guid.NewGuid().ToString("N") + ".wav");
            audioFiles.Add(path);
            LocalAudioRenderer.RenderToWaveFile(path, snippet, bpm, 10);
            player.Stop(); player.Open(new Uri(path)); player.Play();
            status.Text = $"正在试听第 {index + 1} 个音符开始的片段（BPM {bpm}）。";
        }
        catch (Exception error) { status.Text = "试听失败：" + error.Message; }
    }

    private void PreviewReference()
    {
        if (lyrics.SelectedItem is not LyricsBindingRow row || row.ReferenceStartSeconds is not double start ||
            row.ReferenceEndSeconds is not double end || string.IsNullOrWhiteSpace(referenceAudioPath) || !File.Exists(referenceAudioPath))
        { status.Text = "当前行没有可用原唱片段；请保留对应的 work/lyrics-alignment/jobs 处理记录。"; return; }
        player.Stop(); referenceTimer.Stop(); referencePlayer.Close();
        referenceStart = Math.Max(0, start - .12); referenceEnd = end + .12;
        referencePlayer.Open(new Uri(Path.GetFullPath(referenceAudioPath)));
        status.Text = $"试听原唱“{row.Text}”：{start:0.00}–{end:0.00} 秒。来源：{referenceUrl}";
    }

    private void Save()
    {
        if (!lyrics.CommitEdit(DataGridEditingUnit.Cell, true) || !lyrics.CommitEdit(DataGridEditingUnit.Row, true))
        { status.Text = "音符序号必须是整数。"; return; }
        try
        {
            var active = rows.Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToArray();
            if (active.Length == 0) throw new FormatException("请先载入或输入歌词。");
            if (active.Any(r => r.Start == null)) throw new FormatException("还有未填写起始音符的歌词；请绑定，或删除不在本曲中的句子。");
            var cues = new List<BoundLyric>();
            for (int i = 0; i < active.Length; i++)
            {
                int start = active[i].Start!.Value - 1;
                int end = active[i].End ?? (i + 1 < active.Length ? active[i + 1].Start!.Value - 1 : notes.Count);
                if (active[i].End == null)
                    for (int n = Math.Max(0, start + 1); n < Math.Min(end, notes.Count); n++)
                        if (notes[n].Degree == 0 && notes[n].Beats >= 1) { end = n; break; }
                string wordText = LyricsTimeline.NormalizeText(active[i].Text);
                var original = originalWords.FirstOrDefault(w => w.StartNote == start && w.EndNote == end &&
                    LyricsTimeline.NormalizeText(w.Text) == wordText && w.ReferenceStartSeconds == active[i].ReferenceStartSeconds &&
                    w.ReferenceEndSeconds == active[i].ReferenceEndSeconds);
                cues.Add(new BoundLyric(start, end, wordText)
                { EndLine = active[i].EndLine, NeedsReview = active[i].NeedsReview, AlignmentIndex = active[i].AlignmentIndex,
                    ReferenceStartSeconds = active[i].ReferenceStartSeconds, ReferenceEndSeconds = active[i].ReferenceEndSeconds,
                    ReferenceScoreUrl = original?.ReferenceScoreUrl ?? "", ReferenceScoreSha256 = original?.ReferenceScoreSha256 ?? "",
                    AcousticDiagnostics = original?.AcousticDiagnostics ?? "", PositionEvidence = original?.PositionEvidence ?? "" });
            }
            Result = wordMode ? ScoreLyrics.FromWordBindings(notes,
                ScoreLyrics.InvalidateChangedPositionAnchors(originalWords, cues), "逐字音符绑定")
                : ScoreLyrics.Create(notes, cues, "手动校准");
            if (wordMode) Result = Result with { ReferenceAudioPath = referenceAudioPath, ReferenceUrl = referenceUrl, SourceScoreSha256 = sourceScoreSha256 };
            DialogResult = true;
        }
        catch (FormatException error) { status.Text = error.Message; }
    }
}
