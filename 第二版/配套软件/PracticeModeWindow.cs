using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HarmonicaPlayer;

/// <summary>
/// A focused local practice surface. Keyboard and mouse events are consumed
/// only by this window; no event is translated into SendInput.
/// </summary>
public sealed class PracticeModeWindow : Window
{
    private const double TopPosition = 16;
    private const double CenterPosition = 154;
    private const double BottomPosition = 292;
    private const double OffTopPosition = -132;
    private const double OffBottomPosition = 430;
    private const double SmallScale = 0.82;
    private const double CenterScale = 1.0;
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(260);

    private readonly TextBlock songTitle = new();
    private readonly TextBlock songMeta = new();
    private readonly TextBlock progressText = new();
    private readonly TextBlock status = new();
    private readonly Canvas displayCanvas = new();
    private readonly Button reset = new() { Content = "重置进度" };
    private readonly Button sync = new() { Content = "同步主窗口" };
    private readonly Button close = new() { Content = "关闭" };
    private readonly ComboBox performanceMode = new() { Width = 164 };
    private readonly TextBlock modeDescription = new();
    private readonly TextBlock inputLegend = new();
    private readonly DispatcherTimer restTimer = new();
    private readonly DispatcherTimer rhythmTimer = new();
    private int rhythmDegree;
    private readonly PracticeAudioEngine audio = new();
    private readonly Func<PracticeModeState>? hostStateProvider;
    private readonly List<PracticeCard> cards = new();
    private PracticeCard previousCard = null!;
    private PracticeCard currentCard = null!;
    private PracticeCard nextCard = null!;
    private PracticeCard bufferCard = null!;
    private PracticeScoreDocument? document;
    private PracticeProgress? progress;
    private string currentSongTitle = "练习曲谱";
    private int currentBpm = 120;
    private int displayedLine;
    private bool animating;
    private bool closing;
    private bool restAutoWaiting;
    private PracticePerformanceMode selectedPerformanceMode = PracticePerformanceModes.Default;
    private int? activeVirtualKey;

    public PracticeModeWindow(PracticeModeState initialState, Func<PracticeModeState>? hostStateProvider = null)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        this.hostStateProvider = hostStateProvider;
        Title = "电子琴 2.0 · 练习模式";
        Width = 1060;
        Height = 740;
        MinWidth = 780;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = BrushOf("#F5F7FA");
        FontSize = 14;
        restTimer.Tick += OnRestTimerTick;
        rhythmTimer.Tick += OnRhythmTimerTick;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += (_, _) => StopActiveNote();
        Closed += (_, _) => DisposePracticeWindow();

        Content = BuildContent();
        ApplyHostState(initialState, false);
        Loaded += (_, _) =>
        {
            FocusDisplay();
            Keyboard.Focus(displayCanvas);
        };
    }

    public bool IsSessionStarted => progress != null && (progress.CurrentNoteIndex > 0 || progress.CorrectCount > 0 || progress.WrongCount > 0);

    public void ApplyHostState(PracticeModeState state, bool showStatus = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (closing) return;
        if (IsSessionStarted)
        {
            status.Text = "练习已经开始，请先点击“重置进度”后再同步谱面。";
            return;
        }

        try
        {
            StopRestTimer();
            StopActiveNote();
            document = PracticeScoreDocument.Create(state.ScoreText, state.TimingJson);
            progress = new PracticeProgress(document);
            currentSongTitle = string.IsNullOrWhiteSpace(state.SongTitle) ? "未命名练习曲谱" : state.SongTitle.Trim();
            currentBpm = state.Bpm is >= 20 and <= 300 ? state.Bpm : 120;
            audio.SetInstrument(state.Instrument);
            songTitle.Text = currentSongTitle;
            songMeta.Text = $"BPM {currentBpm} · 共 {document.Notes.Count} 个音符/休止 · 练习时不会发送外部键鼠输入";
            displayedLine = 0;
            RenderCardsStatic();
            if (showStatus) status.Text = "已同步主窗口谱面；点击此窗口后即可开始练习。";
            ScheduleAutomaticRest();
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            document = null;
            progress = null;
            status.Text = "练习模式无法载入谱面：" + error.Message;
        }
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock
        {
            Text = "模拟键鼠演奏 · 练习模式",
            FontSize = 25,
            FontWeight = FontWeights.Bold,
            Foreground = BrushOf("#172B4D")
        });
        songTitle.FontSize = 17;
        songTitle.FontWeight = FontWeights.SemiBold;
        songTitle.Foreground = BrushOf("#3D4E6B");
        songTitle.Margin = new Thickness(0, 6, 0, 2);
        header.Children.Add(songTitle);
        songMeta.Foreground = BrushOf("#667085");
        songMeta.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(songMeta);
        modeDescription.Foreground = BrushOf("#2E6F95");
        modeDescription.TextWrapping = TextWrapping.Wrap;
        modeDescription.Margin = new Thickness(0, 4, 0, 0);
        header.Children.Add(modeDescription);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        StyleButton(reset, "#E9EEF8", "#25416C", 112);
        StyleButton(sync, "#E9EEF8", "#25416C", 124);
        StyleButton(close, "#B64242", "#FFFFFF", 86);
        toolbar.Children.Add(reset);
        toolbar.Children.Add(sync);
        toolbar.Children.Add(close);
        toolbar.Children.Add(new Border { Width = 14 });
        toolbar.Children.Add(new TextBlock
        {
            Text = "演奏方式",
            Foreground = BrushOf("#475467"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        performanceMode.ItemsSource = PracticePerformanceModes.Options;
        performanceMode.SelectedItem = PracticePerformanceModes.OptionOf(selectedPerformanceMode);
        performanceMode.MinHeight = 38;
        performanceMode.Padding = new Thickness(8, 4, 8, 4);
        performanceMode.FontWeight = FontWeights.SemiBold;
        performanceMode.Foreground = BrushOf("#25416C");
        performanceMode.Background = BrushOf("#E9EEF8");
        performanceMode.BorderBrush = BrushOf("#C7D2E5");
        performanceMode.SelectionChanged += OnPerformanceModeChanged;
        toolbar.Children.Add(performanceMode);
        UpdateModeDescription();
        reset.Click += (_, _) => ResetPractice();
        sync.Click += (_, _) => SyncFromHost();
        close.Click += (_, _) => Close();
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        var viewport = new Border
        {
            Background = BrushOf("#E9EDF4"),
            BorderBrush = BrushOf("#D8DEE9"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16),
            ClipToBounds = true,
            Child = displayCanvas
        };
        displayCanvas.Height = 430;
        displayCanvas.Background = BrushOf("#F8FAFC");
        displayCanvas.Focusable = true;
        displayCanvas.ClipToBounds = true;
        displayCanvas.SizeChanged += (_, _) => UpdateCardWidths();
        for (int index = 0; index < 4; index++)
        {
            var card = new PracticeCard();
            cards.Add(card);
            displayCanvas.Children.Add(card.Border);
        }
        previousCard = cards[0];
        currentCard = cards[1];
        nextCard = cards[2];
        bufferCard = cards[3];
        Grid.SetRow(viewport, 2);
        root.Children.Add(viewport);

        inputLegend.Foreground = BrushOf("#667085");
        inputLegend.TextWrapping = TextWrapping.Wrap;
        inputLegend.Margin = new Thickness(4, 12, 4, 8);
        Grid.SetRow(inputLegend, 3);
        root.Children.Add(inputLegend);

        var footer = new StackPanel();
        progressText.Foreground = BrushOf("#475467");
        progressText.Margin = new Thickness(4, 0, 4, 4);
        footer.Children.Add(progressText);
        status.Foreground = BrushOf("#344054");
        status.TextWrapping = TextWrapping.Wrap;
        status.Margin = new Thickness(4, 0, 4, 0);
        footer.Children.Add(status);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
        return root;
    }

    private static SolidColorBrush BrushOf(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static void StyleButton(Button button, string background, string foreground, double width)
    {
        button.MinWidth = width;
        button.MinHeight = 38;
        button.Margin = new Thickness(0, 0, 8, 0);
        button.Padding = new Thickness(12, 6, 12, 6);
        button.FontWeight = FontWeights.SemiBold;
        button.Background = BrushOf(background);
        button.Foreground = BrushOf(foreground);
        button.BorderBrush = button.Background;
    }

    private void ResetPractice()
    {
        if (closing || progress == null) return;
        StopRestTimer();
        StopActiveNote();
        CancelAnimations();
        progress.Reset();
        displayedLine = 0;
        RenderCardsStatic();
        FocusDisplay();
        status.Text = "已重置到第一行；请按图例演奏。";
        ScheduleAutomaticRest();
    }

    private void SyncFromHost()
    {
        if (hostStateProvider == null)
        {
            status.Text = "当前没有可同步的主窗口谱面。";
            return;
        }
        ApplyHostState(hostStateProvider());
        FocusDisplay();
    }

    private void FocusDisplay()
    {
        if (!closing)
        {
            displayCanvas.Focusable = true;
            displayCanvas.Focus();
            Keyboard.Focus(displayCanvas);
        }
    }

    private void OnPerformanceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (performanceMode.SelectedItem is not PracticePerformanceModeOption option)
            return;

        if (selectedPerformanceMode == option.Value)
        {
            UpdateModeDescription();
            return;
        }

        StopActiveNote();
        selectedPerformanceMode = option.Value;
        UpdateModeDescription();
        if (!closing) UpdateStatus(option.Description);
        FocusDisplay();
    }

    private void UpdateModeDescription()
    {
        PracticePerformanceModeOption option = PracticePerformanceModes.OptionOf(selectedPerformanceMode);
        modeDescription.Text = $"当前：{option.Name}　{option.Description}";
        inputLegend.Text = selectedPerformanceMode != PracticePerformanceMode.Normal
            ? "只按 Z X C V B N M，自动处理高低音和升号；简单练习允许提前输入，节奏练习需前音结束后再按。空格跳过休止，Esc 退出。"
            : "正常模式：Z X C V B N M 为中音，Shift 高音、Ctrl 低音、Alt 升半音，可组合使用；按住发声，松开停止。空格跳过休止，Esc 退出。";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (closing || progress == null) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            StopActiveNote();
            Close();
            return;
        }
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            StopActiveNote();
            SkipRest();
            return;
        }
        if (e.IsRepeat)
        {
            e.Handled = true;
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(e.Key);
        if (selectedPerformanceMode != PracticePerformanceMode.Normal)
        {
            if (!PracticeInputMapper.TryMapSimpleVirtualKey(virtualKey, out int degree))
                return;
            e.Handled = true;
            SubmitSimpleDegree(degree);
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        bool low = (modifiers & ModifierKeys.Control) != 0;
        bool high = (modifiers & ModifierKeys.Shift) != 0;
        bool sharp = (modifiers & ModifierKeys.Alt) != 0;
        if (!PracticeInputMapper.TryMapVirtualKey(virtualKey, low, high, sharp, out ScoreNote? note))
            return;
        e.Handled = true;
        StopActiveNote();
        activeVirtualKey = virtualKey;
        SubmitNote(note);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (closing || selectedPerformanceMode != PracticePerformanceMode.Normal) return;
        int virtualKey = KeyInterop.VirtualKeyFromKey(e.Key);
        if (activeVirtualKey != virtualKey) return;
        e.Handled = true;
        StopActiveNote();
    }

    private void StopActiveNote()
    {
        rhythmTimer.Stop();
        activeVirtualKey = null;
        audio.Stop();
    }

    private void SubmitSimpleDegree(int degree)
    {
        if (progress == null || animating || progress.IsComplete) return;
        if (rhythmTimer.IsEnabled)
        {
            UpdateStatus("按早了：请等当前音符结束后，松开并重新按下一个音符。 ");
            return;
        }

        ScoreNote expected = progress.Expected!;
        bool correct = expected.Degree == degree;
        int oldLine = progress.CurrentLineIndex;
        if (!correct)
        {
            progress.SubmitDegree(degree);
            RefreshCurrentCard();
            UpdateStatus($"刚才按下了 {PracticeInputMapper.DisplayDegreeKey(degree)}，按键错误，不发声；请继续尝试当前目标。 ");
            return;
        }

        try
        {
            if (selectedPerformanceMode == PracticePerformanceMode.Rhythm)
            {
                int duration = NoteDurationMs();
                audio.Play(expected, duration);
                rhythmDegree = degree;
                rhythmTimer.Interval = TimeSpan.FromMilliseconds(duration);
                rhythmTimer.Start();
                UpdateStatus("正确，当前音符按谱面时值发声；结束后再按下一个音符。 ");
                return;
            }
            // Correct simple-mode notes are queued so an early key press cannot
            // cut off the note that is already sounding.
            audio.Enqueue(expected, NoteDurationMs());
        }
        catch (InvalidOperationException error)
        {
            status.Text = "本机发声失败：" + error.Message;
            return;
        }

        PracticeAdvanceResult result = progress.SubmitDegree(degree);
        MoveAfterProgress(oldLine, result == PracticeAdvanceResult.Complete
            ? "练习完成！可以点击“重置进度”再来一遍。"
            : "正确，已加入谱面播放队列，继续下一个音符。 ");
    }

    private void OnRhythmTimerTick(object? sender, EventArgs e)
    {
        rhythmTimer.Stop();
        if (closing || progress == null) return;
        int oldLine = progress.CurrentLineIndex;
        PracticeAdvanceResult result = progress.SubmitDegree(rhythmDegree);
        MoveAfterProgress(oldLine, result == PracticeAdvanceResult.Complete
            ? "练习完成！可以点击“重置进度”再来一遍。"
            : "当前音符结束，请按下一个音符。 ");
    }

    private void SubmitNote(ScoreNote note)
    {
        if (progress == null || animating) return;
        try
        {
            audio.Play(note, PracticeTiming.MaxDurationMilliseconds);
        }
        catch (InvalidOperationException error)
        {
            status.Text = "本机发声失败：" + error.Message;
            return;
        }

        int oldLine = progress.CurrentLineIndex;
        PracticeAdvanceResult result = progress.Submit(note);
        if (result == PracticeAdvanceResult.Wrong)
        {
            RefreshCurrentCard();
            UpdateStatus($"刚才演奏了 {ScoreTextFormatter.FormatNote(note)}，请继续尝试当前目标。 ");
            return;
        }

        MoveAfterProgress(oldLine, result == PracticeAdvanceResult.Complete
            ? "练习完成！可以点击“重置进度”再来一遍。"
            : "正确，继续下一个音符。 ");
    }

    private void ScheduleAutomaticRest()
    {
        if (closing || restTimer.IsEnabled || restAutoWaiting || progress == null || animating || progress.IsComplete || progress.Expected?.Degree != 0)
            return;

        restTimer.Interval = TimeSpan.FromMilliseconds(NoteDurationMs());
        restAutoWaiting = true;
        restTimer.Start();
        UpdateStatus();
    }

    private void StopRestTimer()
    {
        restTimer.Stop();
        restAutoWaiting = false;
    }

    private void OnRestTimerTick(object? sender, EventArgs e)
    {
        StopRestTimer();
        if (closing || progress == null || animating) return;
        int oldLine = progress.CurrentLineIndex;
        if (!progress.SkipNextRest()) return;
        MoveAfterProgress(oldLine, "休止结束，继续下一个音符。 ");
    }

    private void SkipRest()
    {
        if (progress == null || animating) return;
        StopRestTimer();
        int oldLine = progress.CurrentLineIndex;
        int skipped = progress.SkipRest();
        if (skipped == 0)
        {
            UpdateStatus("当前不是休止符，请按图中目标音符演奏。 ");
            return;
        }
        MoveAfterProgress(oldLine, $"已跳过 {skipped} 个休止符。 ");
    }

    private void MoveAfterProgress(int oldLine, string message)
    {
        if (progress == null) return;
        int targetLine = progress.CurrentLineIndex;
        if (targetLine != displayedLine && targetLine == displayedLine + 1)
        {
            status.Text = message + "正在平滑进入下一行…";
            TransitionForward(targetLine);
        }
        else
        {
            RenderCardsStatic();
            UpdateStatus(message);
            ScheduleAutomaticRest();
        }
    }

    private int NoteDurationMs() => PracticeTiming.NoteDurationMilliseconds(
        progress?.Expected ?? new ScoreNote(1, 0, false, 0), currentBpm);

    private void UpdateStatus(string? message = null)
    {
        if (progress == null || document == null) return;
        int shownIndex = Math.Min(progress.CurrentNoteIndex, document.Notes.Count);
        progressText.Text = $"进度 {shownIndex}/{document.Notes.Count} · 正确 {progress.CorrectCount} · 需要重试 {progress.WrongCount} · 当前第 {progress.CurrentLineIndex + 1}/{document.Lines.Count} 行";
        if (message != null)
        {
            status.Text = message;
            return;
        }
        if (progress.IsComplete)
        {
            status.Text = "练习完成！可以点击“重置进度”再来一遍。";
            return;
        }
        if (progress.Expected?.Degree == 0)
        {
            status.Text = restAutoWaiting
                ? $"当前是休止符 {ScoreTextFormatter.FormatNote(progress.Expected!)}，按谱面时值自动等待；空格可立即跳过。"
                : "当前是休止符，将按谱面时值自动等待；空格可立即跳过。";
            return;
        }
        ScoreNote expected = progress.Expected!;
        string keyHint = selectedPerformanceMode != PracticePerformanceMode.Normal
            ? PracticeInputMapper.DisplayDegreeKey(expected.Degree)
            : PracticeInputMapper.DisplayKey(expected);
        status.Text = $"当前目标：{ScoreTextFormatter.FormatNote(expected)} · 按键提示：{keyHint}";
    }

    private void RenderCardsStatic()
    {
        if (document == null || progress == null) return;
        displayedLine = progress.CurrentLineIndex;
        CancelAnimations();
        SetCardContent(previousCard, displayedLine - 1);
        SetCardContent(currentCard, displayedLine);
        SetCardContent(nextCard, displayedLine + 1);
        SetCardContent(bufferCard, -1);
        SetCardVisual(previousCard, TopPosition, SmallScale, displayedLine > 0 ? 0.62 : 0, displayedLine > 0);
        SetCardVisual(currentCard, CenterPosition, CenterScale, 1, true);
        SetCardVisual(nextCard, BottomPosition, SmallScale, displayedLine + 1 < document.Lines.Count ? 0.62 : 0, displayedLine + 1 < document.Lines.Count);
        SetCardVisual(bufferCard, OffBottomPosition, SmallScale, 0, false);
        UpdateCardWidths();
        UpdateStatus();
    }

    private void RefreshCurrentCard()
    {
        if (progress == null) return;
        SetCardContent(currentCard, displayedLine);
        UpdateStatus();
    }

    private void TransitionForward(int targetLine)
    {
        if (document == null || progress == null || targetLine != displayedLine + 1)
        {
            RenderCardsStatic();
            return;
        }

        PracticeCard outgoing = previousCard;
        PracticeCard oldCurrent = currentCard;
        PracticeCard oldNext = nextCard;
        PracticeCard incoming = bufferCard;
        SetCardContent(oldCurrent, displayedLine);
        SetCardContent(oldNext, targetLine);
        SetCardContent(incoming, targetLine + 1);
        SetCardVisual(outgoing, OffTopPosition, SmallScale, 0, true);
        SetCardVisual(incoming, OffBottomPosition, SmallScale, 0, true);
        animating = true;
        int completed = 0;
        void CompleteOne()
        {
            completed++;
            if (completed < 4 || closing) return;
            previousCard = oldCurrent;
            currentCard = oldNext;
            nextCard = incoming;
            bufferCard = outgoing;
            displayedLine = targetLine;
            SetCardVisual(bufferCard, OffBottomPosition, SmallScale, 0, false);
            animating = false;
            UpdateStatus();
            ScheduleAutomaticRest();
        }

        AnimateCard(outgoing, OffTopPosition, SmallScale, 0, CompleteOne);
        AnimateCard(oldCurrent, TopPosition, SmallScale, 0.62, CompleteOne);
        AnimateCard(oldNext, CenterPosition, CenterScale, 1, CompleteOne);
        AnimateCard(incoming, BottomPosition, SmallScale, targetLine + 1 < document.Lines.Count ? 0.62 : 0, CompleteOne);
    }

    private void AnimateCard(PracticeCard card, double top, double scale, double opacity, Action completed)
    {
        card.Border.Visibility = Visibility.Visible;
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var storyboard = new Storyboard();
        var topAnimation = new DoubleAnimation(top, TransitionDuration) { EasingFunction = easing };
        Storyboard.SetTarget(topAnimation, card.Border);
        Storyboard.SetTargetProperty(topAnimation, new PropertyPath("(Canvas.Top)"));
        storyboard.Children.Add(topAnimation);
        var scaleX = new DoubleAnimation(scale, TransitionDuration) { EasingFunction = easing };
        Storyboard.SetTarget(scaleX, card.Border);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath("RenderTransform.ScaleX"));
        storyboard.Children.Add(scaleX);
        var scaleY = new DoubleAnimation(scale, TransitionDuration) { EasingFunction = easing };
        Storyboard.SetTarget(scaleY, card.Border);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath("RenderTransform.ScaleY"));
        storyboard.Children.Add(scaleY);
        var opacityAnimation = new DoubleAnimation(opacity, TransitionDuration) { EasingFunction = easing };
        Storyboard.SetTarget(opacityAnimation, card.Border);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("Opacity"));
        storyboard.Children.Add(opacityAnimation);
        storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
    }

    private void SetCardVisual(PracticeCard card, double top, double scale, double opacity, bool visible)
    {
        card.Border.BeginAnimation(Canvas.TopProperty, null);
        card.Border.BeginAnimation(UIElement.OpacityProperty, null);
        if (card.Border.RenderTransform is ScaleTransform transform)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }
        Canvas.SetTop(card.Border, top);
        card.Border.Opacity = opacity;
        card.Border.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    }

    private void SetCardContent(PracticeCard card, int lineIndex)
    {
        card.Text.Inlines.Clear();
        card.LineIndex = lineIndex;
        if (document == null || lineIndex < 0 || lineIndex >= document.Lines.Count)
            return;

        PracticeScoreLine line = document.Lines[lineIndex];
        bool isCurrentLine = progress != null && lineIndex == progress.CurrentLineIndex;
        card.Text.FontSize = Math.Clamp(38d - line.Text.Length * 0.24d, 19d, 36d);
        for (int localNoteIndex = 0; localNoteIndex < line.Notes.Count; localNoteIndex++)
        {
            ScoreNote note = line.Notes[localNoteIndex];
            bool isCurrentNote = isCurrentLine && progress?.Expected != null && line.IsCurrentNote(localNoteIndex, progress.CurrentNoteIndex);
            var run = new Run(ScoreTextFormatter.FormatNote(note))
            {
                Foreground = isCurrentNote ? BrushOf("#9A6700") : BrushOf("#172B4D"),
                FontWeight = isCurrentNote ? FontWeights.Bold : FontWeights.Normal,
                Background = isCurrentNote ? BrushOf("#FFE08A") : Brushes.Transparent
            };
            card.Text.Inlines.Add(run);
            if (localNoteIndex + 1 < line.Notes.Count) card.Text.Inlines.Add(new Run(" "));
        }
        if (line.MeasureBreakAfter) card.Text.Inlines.Add(new Run("  |"));
    }

    private void UpdateCardWidths()
    {
        double width = Math.Max(260, displayCanvas.ActualWidth - 32);
        foreach (PracticeCard card in cards)
        {
            card.Border.Width = width;
            Canvas.SetLeft(card.Border, 16);
        }
    }

    private void CancelAnimations()
    {
        foreach (PracticeCard card in cards)
        {
            card.Border.BeginAnimation(Canvas.TopProperty, null);
            card.Border.BeginAnimation(UIElement.OpacityProperty, null);
            if (card.Border.RenderTransform is ScaleTransform transform)
            {
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
        }
        animating = false;
    }

    private void DisposePracticeWindow()
    {
        if (closing) return;
        closing = true;
        StopRestTimer();
        rhythmTimer.Stop();
        audio.Dispose();
    }

    private sealed class PracticeCard
    {
        public PracticeCard()
        {
            Text = new TextBlock
            {
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 30,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BrushOf("#172B4D")
            };
            Border = new Border
            {
                Height = 120,
                Padding = new Thickness(18, 8, 18, 8),
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1),
                Background = BrushOf("#FFFFFF"),
                BorderBrush = BrushOf("#D0D5DD"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.82, 0.82),
                Child = Text
            };
        }

        public Border Border { get; }
        public TextBlock Text { get; }
        public int LineIndex { get; set; }
    }
}
