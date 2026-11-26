using HarmonicaPlayer;
using HarmonicaPlayer.Mobile.Core;
#if ANDROID
using Android.Content.PM;
using Microsoft.Maui.ApplicationModel;
#endif

namespace HarmonicaPlayer.Mobile;

public sealed class PracticePage : ContentPage
{
    private static readonly Color PanelColor = Color.FromArgb("#1D252C");
    private static readonly Color KeyColor = Color.FromArgb("#111111");
    private static readonly Color SelectedColor = Color.FromArgb("#8C9399");
    private static readonly Color ScoreColor = Color.FromArgb("#F4F7FA");
    private static readonly Color CurrentScoreColor = Color.FromArgb("#FFE08A");

    private readonly MobileAppState state;
    private readonly IPracticeAudio audio;
    private readonly StoredSong? stored;
    private readonly PracticeScoreDocument? scoreDocument;
    private readonly IReadOnlyList<MobileLyricCue> lyricCues;
    private readonly Label progressLabel = new() { FontSize = 13, TextColor = Color.FromArgb("#B7C4D0") };
    private readonly Label statusLabel = new() { FontSize = 13, TextColor = Color.FromArgb("#B7C4D0") };
    private readonly Label lyricLabel = new() { FontSize = 15, TextColor = ScoreColor, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label currentScoreLabel = new()
    {
        FontSize = 20,
        TextColor = ScoreColor,
        LineBreakMode = LineBreakMode.WordWrap
    };
    private readonly Label nextScoreLabel = new()
    {
        FontSize = 16,
        TextColor = Color.FromArgb("#9AA8B5"),
        LineBreakMode = LineBreakMode.WordWrap
    };
    private readonly Picker performanceModePicker = new()
    {
        Title = "演奏方式",
        FontSize = 12,
        TextColor = Colors.White,
        BackgroundColor = PanelColor,
        WidthRequest = 130,
        HeightRequest = 42,
        HorizontalOptions = LayoutOptions.End
    };
    private readonly Button[] noteButtons = new Button[7];
    private readonly Button[] modifierButtons = new Button[4];
    private readonly Button playButton;
    private readonly bool autoPlay;
    private CancellationTokenSource? playbackCancellation;
    private int octave;
    private bool sharp;
    private int normalActiveDegree;
    private PracticePerformanceMode selectedPerformanceMode = PracticePerformanceModes.Default;
    private int modifierFlashGeneration;
    private CancellationTokenSource? noteFlashCancellation;
    private int expectedIndex;
    private bool playing;
#if ANDROID
    private ScreenOrientation? originalOrientation;
    private bool orientationChanged;
#endif

    public PracticePage(MobileAppState state, IPracticeAudio audio, StoredSong? stored = null, bool autoPlay = false)
    {
        this.state = state;
        this.audio = audio;
        this.stored = stored;
        this.autoPlay = autoPlay;
        scoreDocument = TryCreateDocument(stored?.SourceText);
        lyricCues = MobileLyricsTimeline.Parse(stored?.LyricsText, includeEmpty: true)
            .Select(c => c with { StartMilliseconds = c.StartMilliseconds + (stored?.LyricsOffsetSeconds ?? 0) * 1000 }).ToArray();
        Title = stored?.Song.Title ?? "自由练习";
        BackgroundColor = Color.FromArgb("#10151B");
        Shell.SetTabBarIsVisible(this, false);
        Shell.SetNavBarIsVisible(this, false);

        performanceModePicker.ItemsSource = PracticePerformanceModes.Options.ToList();
        performanceModePicker.SelectedIndex = (int)selectedPerformanceMode;
        performanceModePicker.SelectedIndexChanged += (_, _) => OnPerformanceModeChanged();

        string[] modifiers = { "半音", "升调", "自然", "降调" };
        for (int index = 0; index < modifiers.Length; index++)
        {
            int modifierIndex = index;
            modifierButtons[index] = new Button
            {
                Text = modifiers[index],
                FontSize = 13,
                CornerRadius = 28,
                HeightRequest = 52,
                BackgroundColor = PanelColor,
                TextColor = Colors.White,
                BorderColor = Color.FromArgb("#53606A"),
                BorderWidth = 1,
                HorizontalOptions = LayoutOptions.Fill
            };
            modifierButtons[index].Clicked += (_, _) => SelectModifier(modifierIndex);
        }

        for (int index = 0; index < noteButtons.Length; index++)
        {
            int degree = index + 1;
            noteButtons[index] = new Button
            {
                Text = degree.ToString(),
                FontSize = 27,
                FontAttributes = FontAttributes.Bold,
                CornerRadius = 30,
                HeightRequest = 64,
                BackgroundColor = KeyColor,
                TextColor = Colors.White,
                HorizontalOptions = LayoutOptions.Fill
            };
            noteButtons[index].Clicked += (_, _) => OnNoteClicked(degree);
            noteButtons[index].Pressed += (_, _) => OnNotePressed(degree);
            noteButtons[index].Released += (_, _) => OnNoteReleased(degree);
        }

        var practiceKeys = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };
        for (int index = 0; index < noteButtons.Length; index++)
        {
            practiceKeys.Children.Add(noteButtons[index]);
            Grid.SetColumn(noteButtons[index], index);
        }

        var modifierKeys = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)
            }
        };
        for (int index = 0; index < modifierButtons.Length; index++)
        {
            modifierKeys.Children.Add(modifierButtons[index]);
            Grid.SetColumn(modifierButtons[index], index);
        }

        playButton = new Button
        {
            Text = "试听",
            FontSize = 12,
            BackgroundColor = Color.FromArgb("#2F80ED"),
            TextColor = Colors.White,
            CornerRadius = 9,
            HeightRequest = 38,
            WidthRequest = 66,
            Padding = new Thickness(8, 0)
        };
        playButton.Clicked += async (_, _) =>
        {
            if (playing) playbackCancellation?.Cancel();
            else await PlaySongAsync();
        };
        var resetButton = new Button
        {
            Text = "重置",
            FontSize = 12,
            BackgroundColor = Color.FromArgb("#29343D"),
            TextColor = Colors.White,
            CornerRadius = 9,
            HeightRequest = 38,
            WidthRequest = 66,
            Padding = new Thickness(8, 0)
        };
        resetButton.Clicked += (_, _) => ResetPractice();

        var scoreMeta = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            Children = { progressLabel, lyricLabel }
        };
        Grid.SetColumn(lyricLabel, 1);

        var scoreCard = new Frame
        {
            Padding = new Thickness(14, 9),
            HeightRequest = 110,
            CornerRadius = 16,
            BorderColor = Color.FromArgb("#33404A"),
            BackgroundColor = PanelColor,
            HasShadow = false,
            VerticalOptions = LayoutOptions.Fill,
            Content = new VerticalStackLayout
            {
                Spacing = 5,
                Children =
                {
                    scoreMeta,
                    currentScoreLabel,
                    nextScoreLabel
                }
            }
        };

        var backButton = new Button
        {
            Text = "‹ 返回",
            FontSize = 15,
            BackgroundColor = Color.FromArgb("#29343D"),
            TextColor = Colors.White,
            CornerRadius = 10,
            HeightRequest = 42,
            Padding = new Thickness(12, 0)
        };
        backButton.Clicked += async (_, _) => await Navigation.PopAsync();

        var headerDetails = new Label
        {
            Text = Title, FontSize = 21, FontAttributes = FontAttributes.Bold, TextColor = Colors.White,
            LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center
        };
        var headerActions = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children = { playButton, resetButton }
        };
        var header = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            Children =
            {
                backButton,
                headerDetails,
                headerActions,
                performanceModePicker
            }
        };
        Grid.SetColumn(headerDetails, 1);
        Grid.SetColumn(headerActions, 2);
        Grid.SetColumn(performanceModePicker, 3);

        var practiceInfo = new Grid
        {
            Children = { scoreCard }
        };

        var footer = new VerticalStackLayout
        {
            Spacing = 5,
            Children = { statusLabel
            }
        };

        var root = new Grid
        {
            Padding = new Thickness(16, 10, 16, 12),
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            }
        };
        root.Children.Add(header);
        root.Children.Add(practiceInfo);
        root.Children.Add(modifierKeys);
        root.Children.Add(practiceKeys);
        root.Children.Add(footer);
        Grid.SetRow(practiceInfo, 1);
        Grid.SetRow(modifierKeys, 2);
        Grid.SetRow(practiceKeys, 3);
        Grid.SetRow(footer, 4);
        Content = root;

        UpdateDisplay();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        state.LocalPracticeActive = true;
#if ANDROID
        var activity = Platform.CurrentActivity;
        if (activity is not null)
        {
            originalOrientation = activity.RequestedOrientation;
            activity.RequestedOrientation = ScreenOrientation.Landscape;
            orientationChanged = true;
        }
#endif
        if (autoPlay && !playing)
            _ = PlaySongAsync();
    }

    private async void SelectModifier(int index)
    {
        switch (index)
        {
            case 0: sharp = !sharp; break;
            case 1: octave = octave == 1 ? 0 : 1; break;
            case 2: sharp = false; break;
            case 3: octave = octave == -1 ? 0 : -1; break;
        }
        UpdateDisplay();
        int generation = ++modifierFlashGeneration;
        modifierButtons[index].BackgroundColor = SelectedColor;
        await Task.Delay(160);
        if (generation == modifierFlashGeneration) UpdateModifierButtonStates();
    }

    private void OnPerformanceModeChanged()
    {
        PracticePerformanceMode mode = performanceModePicker.SelectedIndex == (int)PracticePerformanceMode.Normal
            ? PracticePerformanceMode.Normal
            : PracticePerformanceMode.Simple;
        if (selectedPerformanceMode == mode)
        {
            UpdateDisplay();
            return;
        }

        normalActiveDegree = 0;
        audio.Stop();
        selectedPerformanceMode = mode;
        if (mode == PracticePerformanceMode.Simple)
        {
            octave = 0;
            sharp = false;
        }
        statusLabel.Text = "";
        UpdateDisplay();
    }

    private void OnNoteClicked(int degree)
    {
        if (selectedPerformanceMode == PracticePerformanceMode.Simple)
            PlaySimpleDegree(degree);
    }

    private void OnNotePressed(int degree)
    {
        if (selectedPerformanceMode != PracticePerformanceMode.Normal || playing || normalActiveDegree != 0)
            return;

        SkipExpectedRests();
        if (stored is not null && expectedIndex >= stored.Song.Notes.Count)
        {
            statusLabel.Text = "练习完成！";
            UpdateDisplay();
            return;
        }

        var note = new ScoreNote(degree, octave, sharp, expectedIndex);
        try
        {
            audio.Play(note, state.Instrument, 0.9, PracticeTiming.MaxDurationMilliseconds);
        }
        catch (InvalidOperationException error)
        {
            statusLabel.Text = "本机发声失败：" + error.Message;
            return;
        }

        normalActiveDegree = degree;
        if (stored is null)
        {
            statusLabel.Text = "";
        }
        else
        {
            ScoreNote expected = ToScoreNote(stored.Song.Notes[expectedIndex], expectedIndex);
            if (PracticeInputMapper.SamePitch(expected, note))
            {
                expectedIndex++;
                statusLabel.Text = expectedIndex >= stored.Song.Notes.Count ? "练习完成" : "";
            }
            else
            {
                statusLabel.Text = $"目标：{expected.Label}，当前：{note.Label}。";
            }
        }
        UpdateDisplay();
        _ = FlashNoteAsync(degree);
    }

    private void OnNoteReleased(int degree)
    {
        if (selectedPerformanceMode != PracticePerformanceMode.Normal || normalActiveDegree != degree)
            return;

        normalActiveDegree = 0;
        audio.Stop();
    }

    private void PlaySimpleDegree(int degree)
    {
        if (playing) return;
        SkipExpectedRests();

        if (stored is not null && expectedIndex >= stored.Song.Notes.Count)
        {
            statusLabel.Text = "练习完成！";
            UpdateDisplay();
            return;
        }

        ScoreNote? expected = stored is null ? null : ToScoreNote(stored.Song.Notes[expectedIndex], expectedIndex);
        bool correct = expected is not null && expected.Degree == degree;
        if (stored is not null && !correct)
        {
            statusLabel.Text = $"请按 {expected!.Label}";
            UpdateDisplay();
            _ = FlashNoteAsync(degree);
            return;
        }

        ScoreNote note = expected ?? new ScoreNote(degree, 0, false, expectedIndex, Beats: 1);
        try
        {
            // Simple mode follows the Windows behavior: valid notes are queued
            // at their score duration; an invalid touch never creates audio.
            audio.Enqueue(note, state.Instrument, 0.9,
                PracticeTiming.NoteDurationMilliseconds(note, CurrentBpm));
        }
        catch (InvalidOperationException error)
        {
            statusLabel.Text = "本机发声失败：" + error.Message;
            return;
        }

        if (stored is null)
        {
            statusLabel.Text = "";
        }
        else
        {
            expectedIndex++;
            statusLabel.Text = expectedIndex >= stored.Song.Notes.Count ? "练习完成" : "";
        }

        UpdateDisplay();
        _ = FlashNoteAsync(degree);
    }

    private void SkipExpectedRests()
    {
        if (stored is null) return;
        while (expectedIndex < stored.Song.Notes.Count && stored.Song.Notes[expectedIndex].Pitch == 0)
            expectedIndex++;
    }

    private async Task FlashNoteAsync(int degree)
    {
        noteFlashCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        noteFlashCancellation = cancellation;
        UpdateButtonStates();
        noteButtons[degree - 1].BackgroundColor = SelectedColor;
        try
        {
            await Task.Delay(70, cancellation.Token);
            noteButtons[degree - 1].BackgroundColor = KeyColor;
        }
        catch (OperationCanceledException)
        {
            // A newer key press immediately owns the feedback state.
        }
        finally
        {
            if (ReferenceEquals(noteFlashCancellation, cancellation)) noteFlashCancellation = null;
        }
    }

    private async Task PlaySongAsync()
    {
        if (stored is null || playing) return;
        playing = true;
        using var cancellation = new CancellationTokenSource();
        playbackCancellation = cancellation;
        playButton.Text = "停止";
        statusLabel.Text = "";
        try
        {
            Task audioTask = audio.PlaySequenceAsync(
                stored.Song.Notes.Select((note, index) => ToScoreNote(note, index)).ToArray(),
                state.Instrument,
                CurrentBpm,
                0.9,
                cancellation.Token);
            double elapsedMilliseconds = 0;
            for (int index = 0; index < stored.Song.Notes.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                ScoreNote note = ToScoreNote(stored.Song.Notes[index], index);
                
                UpdateScore(index, elapsedMilliseconds);
                int duration = PracticeTiming.NoteDurationMilliseconds(note, CurrentBpm);
                await Task.Delay(duration, cancellation.Token);
                elapsedMilliseconds += duration;
            }
            await audioTask;
            statusLabel.Text = "试听结束";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            statusLabel.Text = "";
        }
        catch (Exception error) { statusLabel.Text = "试听失败：" + error.Message; }
        finally
        {
            if (ReferenceEquals(playbackCancellation, cancellation)) playbackCancellation = null;
            playing = false;
            playButton.Text = "试听";
            audio.Stop();
        }
    }

    private void ResetPractice()
    {
        playbackCancellation?.Cancel();
        expectedIndex = 0;
        normalActiveDegree = 0;
        audio.Stop();
        octave = 0;
        sharp = false;
        modifierFlashGeneration++;
        noteFlashCancellation?.Cancel();
        lyricLabel.Text = string.Empty;
        statusLabel.Text = "";
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (stored is null)
        {
            progressLabel.Text = AudioInstrumentCatalog.NameOf(state.Instrument);
            currentScoreLabel.Text = "";
            nextScoreLabel.Text = "";
        }
        else
        {
            UpdateScore(Math.Min(expectedIndex, Math.Max(0, stored.Song.Notes.Count - 1)), 0);
            if (lyricCues.Count == 0 && !string.IsNullOrWhiteSpace(stored.LyricsText))
                lyricLabel.Text = stored.LyricsText.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        UpdateButtonStates();
    }

    private void UpdateScore(int noteIndex, double elapsedMilliseconds)
    {
        if (stored is null || stored.Song.Notes.Count == 0) return;
        IReadOnlyList<PracticeScoreLine> lines = scoreDocument?.Lines ?? BuildFallbackLines();
        if (lines.Count == 0) return;
        int safeIndex = Math.Clamp(noteIndex, 0, stored.Song.Notes.Count - 1);
        int lineIndex = scoreDocument?.LineIndexForNote(safeIndex) ?? safeIndex / PracticeScoreDocument.MaxNotesPerLine;
        lineIndex = Math.Clamp(lineIndex, 0, lines.Count - 1);
        PracticeScoreLine current = lines[lineIndex];
        RenderScoreLine(currentScoreLabel, current.Notes, current.StartNoteIndex, safeIndex, CurrentScoreColor);
        if (lineIndex + 1 < lines.Count)
        {
            PracticeScoreLine next = lines[lineIndex + 1];
            RenderScoreLine(nextScoreLabel, next.Notes, next.StartNoteIndex, -1, nextScoreLabel.TextColor);
        }
        else nextScoreLabel.FormattedText = null;
        progressLabel.Text = $"{safeIndex + 1}/{stored.Song.Notes.Count}  ·  {CurrentBpm} BPM";
        UpdateLyric(elapsedMilliseconds);
    }

    private IReadOnlyList<PracticeScoreLine> BuildFallbackLines()
    {
        var notes = stored!.Song.Notes.Select((note, index) => ToScoreNote(note, index)).ToArray();
        var lines = new List<PracticeScoreLine>();
        for (int start = 0; start < notes.Length; start += PracticeScoreDocument.MaxNotesPerLine)
        {
            int count = Math.Min(PracticeScoreDocument.MaxNotesPerLine, notes.Length - start);
            lines.Add(new PracticeScoreLine(lines.Count + 1, start, start + count,
                notes.Skip(start).Take(count).ToArray()));
        }
        return lines;
    }

    private static void RenderScoreLine(Label target, IReadOnlyList<ScoreNote> notes, int startIndex,
        int currentIndex, Color highlightColor)
    {
        var formatted = new FormattedString();
        for (int index = 0; index < notes.Count; index++)
        {
            int globalIndex = startIndex + index;
            formatted.Spans.Add(new Span
            {
                Text = (index == 0 ? "" : " ") + ScoreTextFormatter.FormatNote(notes[index]),
                TextColor = globalIndex == currentIndex ? highlightColor : target.TextColor,
                FontAttributes = globalIndex == currentIndex ? FontAttributes.Bold : FontAttributes.None
            });
        }
        target.FormattedText = formatted;
    }

    private void UpdateLyric(double elapsedMilliseconds)
    {
        if (lyricCues.Count == 0) return;
        string? current = MobileLyricsTimeline.At(lyricCues, elapsedMilliseconds);
        lyricLabel.Text = current ?? "";
    }

    private void UpdateButtonStates()
    {
        for (int index = 0; index < noteButtons.Length; index++)
            noteButtons[index].BackgroundColor = KeyColor;
        UpdateModifierButtonStates();
    }

    private void UpdateModifierButtonStates()
    {
        bool enabled = selectedPerformanceMode == PracticePerformanceMode.Normal;
        for (int index = 0; index < modifierButtons.Length; index++)
        {
            modifierButtons[index].IsEnabled = enabled;
            modifierButtons[index].Opacity = enabled ? 1 : 0.55;
        }
        modifierButtons[0].BackgroundColor = enabled && sharp ? SelectedColor : PanelColor;
        modifierButtons[1].BackgroundColor = enabled && octave == 1 ? SelectedColor : PanelColor;
        modifierButtons[2].BackgroundColor = PanelColor;
        modifierButtons[3].BackgroundColor = enabled && octave == -1 ? SelectedColor : PanelColor;
    }

    private int CurrentBpm => Math.Clamp(stored?.Song.Bpm ?? 80, 20, 300);

    private static PracticeScoreDocument? TryCreateDocument(string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return null;
        try { return PracticeScoreDocument.Create(sourceText); }
        catch (FormatException) { return null; }
    }

    protected override void OnDisappearing()
    {
        playbackCancellation?.Cancel();
        playing = false;
        audio.Stop();
        audio.Dispose();
        state.LocalPracticeActive = false;
#if ANDROID
        if (orientationChanged && Platform.CurrentActivity is { } activity)
            activity.RequestedOrientation = ScreenOrientation.Portrait;
        orientationChanged = false;
#endif
        base.OnDisappearing();
    }

    private static ScoreNote ToScoreNote(SongNoteDto note, int position) =>
        new(note.Pitch, note.Octave, note.Sharp, position, Beats: note.DurationBeats);
}
