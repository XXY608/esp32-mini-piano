using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace HarmonicaPlayer;

// Windows-only regression test using actual WPF windows. Only dry/log playback;
// no note input. Uses a fresh temporary settings file for each run.
public static class WindowsSmokeTests
{
    [STAThread]
    public static int Main()
    {
        using (var simulatorAudio = new SimulatorAudio(AudioInstrument.SalamanderPiano, startOutput: false))
        {
            var samples = new float[1024];
            var first = new ScoreNote(1, 0, false, 0);
            simulatorAudio.Play(first, 8000);
            simulatorAudio.Read(samples, 0, samples.Length);
            if (!samples.Any(x => Math.Abs(x) > 0.0001f)) throw new InvalidOperationException("Simulator note is silent.");
            simulatorAudio.Stop();
            simulatorAudio.Read(samples, 0, samples.Length);
            if (samples.Skip(441).Any(x => x != 0)) throw new InvalidOperationException("Simulator release did not reach silence.");
            simulatorAudio.Play(first, 8000);
            simulatorAudio.Read(samples, 0, 100);
            simulatorAudio.Play(new ScoreNote(2, 0, false, 0), 8000);
            simulatorAudio.Read(samples, 0, samples.Length);
            if (!samples.Any(x => Math.Abs(x) > 0.0001f)) throw new InvalidOperationException("Simulator replacement note is silent.");
            simulatorAudio.Stop();
            simulatorAudio.Read(samples, 0, samples.Length);
            if (samples.Skip(441).Any(x => x != 0)) throw new InvalidOperationException("Simulator replacement release did not reach silence.");
            Console.WriteLine("PASS simulator audio: release, rapid replacement and silence");
        }
        using (var poly = new SimulatorAudio(AudioInstrument.Flute, startOutput: false))
        {
            var samples = new float[4096];
            poly.SetManualNotes(new[] { 60, -1, 64, -1, -1, -1, -1 }, 80);
            poly.Read(samples, 0, samples.Length);
            if (!samples.Any(x => Math.Abs(x) > 0.0001f) || samples.Any(x => Math.Abs(x) > 1)) throw new InvalidOperationException("Manual chord missing or clipped.");
            poly.SetManualNotes(new[] { -1, -1, 64, -1, -1, -1, -1 }, 80);
            poly.Read(samples, 0, samples.Length);
            if (!samples.Skip(441).Any(x => Math.Abs(x) > 0.0001f)) throw new InvalidOperationException("Remaining chord note stopped on partial release.");
            poly.SetManualNotes(new[] { -1, -1, -1, -1, -1, -1, -1 }, 80);
            poly.Read(samples, 0, samples.Length);
            if (samples.Skip(441).Any(x => x != 0)) throw new InvalidOperationException("Manual chord did not stop.");
            Console.WriteLine("PASS simulator manual chord: partial release and silence");
        }
        using (var sustained = new SimulatorAudio(AudioInstrument.Flute, startOutput: false))
        {
            var segment = new float[44100];
            sustained.Play(new ScoreNote(1, 0, false, 0), 8000, sustain: true);
            for (int i = 0; i < 10; i++) sustained.Read(segment, 0, segment.Length);
            if (!segment.Any(x => Math.Abs(x) > 0.00001f)) throw new InvalidOperationException("Held simulator note ended after eight seconds.");
            sustained.Stop();
            sustained.Read(segment, 0, segment.Length);
            if (segment.Skip(441).Any(x => x != 0)) throw new InvalidOperationException("Held simulator note did not stop on release.");
            Console.WriteLine("PASS simulator held audio beyond eight seconds and release");
        }
        VerifyAlignmentRuntimeSelection();
        foreach (AudioInstrument instrument in new[] { AudioInstrument.Piano, AudioInstrument.SalamanderPiano, AudioInstrument.LegacyPiano })
        {
            byte[] wave = PracticeAudioEngine.CreateToneWave(new ScoreNote(1, 0, false, 0), 280, instrument);
            if (wave.Length != 44 + 12348 * 2 || !wave.Skip(44).Any(value => value != 0))
                throw new InvalidOperationException("Practice SoundFont WAV is empty or has incorrect duration.");
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int failures = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                var scenarios = new List<string> { "idle", "lyrics-binding", "lyrics-words", "lyrics-audio", "lyrics-audio-close", "lyrics-search", "lyrics-search-close", "practice", "pending-save", "countdown", "playback" };
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PIANO_LYRICS_REAL_JOB"))) scenarios.Add("lyrics-real-sample");
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PIANO_LYRICS_SECOND_REAL_JOB"))) scenarios.Add("lyrics-second-real");
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PIANO_CCMZ_POLYPHONY_SOURCE"))) scenarios.Add("ccmz-polyphony");
                foreach (string test in scenarios)
                {
                    string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HarmonicaPlayerSmoke-" + Guid.NewGuid().ToString("N") + ".json");
                    PlayerWindow? window = null;
                    string? lyricTestDirectory = null;
                    int lookups = 0;
                    try
                    {
                        async Task<OnlineLyrics?> Lookup(string query, CancellationToken token)
                        {
                            lookups++;
                            if (query == "等待") { await Task.Delay(Timeout.Infinite, token); return null; }
                            if (query == "无结果") return null;
                            if (query == "断网") throw new System.Net.Http.HttpRequestException("test offline");
                            return query == "普通歌词"
                                ? new OnlineLyrics("纯文本歌词", false, query, "测试歌手")
                                : new OnlineLyrics("[00:00.00]搜索测试甲\n[00:01.00]搜索测试乙", true, query, "测试歌手");
                        }
                        async Task<AudioLyricResult> AudioLookup(string source, string query, IProgress<string> progress, CancellationToken token)
                        {
                            if (test == "lyrics-second-real")
                                return AudioLyricsAligner.ReadResult(Environment.GetEnvironmentVariable("PIANO_LYRICS_SECOND_REAL_JOB")!);
                            if (query == "等待") await Task.Delay(Timeout.Infinite, token);
                            if (query == "失败") throw new System.IO.IOException("test source unavailable");
                            var notes = ScoreParser.Parse("1 2 3 4", true);
                            var words = new[] { new BoundLyric(0, 1, query == "重算" ? "新" : "春") { AlignmentIndex = 0 },
                                new BoundLyric(1, 3, "风") { AlignmentIndex = 1, NeedsReview = true },
                                new BoundLyric(3, 4, "来") { AlignmentIndex = 2, EndLine = true } };
                            var bound = ScoreLyrics.FromWordBindings(notes, words, "test vocal") with
                            { SourceScoreSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(source))) };
                            var drafts = words.Select(w => new AudioLyricWord(w.AlignmentIndex, w.Text, w.EndLine, w.NeedsReview)).ToArray();
                            if (query == "缺字") drafts = drafts.Append(new AudioLyricWord(3, "缺", true, true)).ToArray();
                            return new AudioLyricResult(new ImportedScore("audio-smoke-" + System.IO.Path.GetFileNameWithoutExtension(path), 120,
                                "1 2 3 4", notes, Array.Empty<string>()) { Lyrics = bound },
                                drafts,
                                "https://example.invalid/test-recording", System.IO.Path.GetDirectoryName(path)!);
                        }
                        window = new PlayerWindow(path, dryRun: test is "countdown" or "playback",
                            lyricsLookup: test.StartsWith("lyrics-search") ? Lookup : null,
                            audioAligner: test.StartsWith("lyrics-audio") || test == "lyrics-second-real" ? AudioLookup : null);
                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        window.Closed += (_, _) => closed.TrySetResult();
                        window.Show();
                        await Task.Delay(100);
                        if (test == "ccmz-polyphony")
                        {
                            const System.Reflection.BindingFlags instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                            string source = Environment.GetEnvironmentVariable("PIANO_CCMZ_POLYPHONY_SOURCE")!;
                            var open = typeof(PlayerWindow).GetMethod("OpenScoreFileAsync", instance)!;
                            var current = typeof(PlayerWindow).GetMethod("CurrentPerformanceSource", instance)!;
                            var compile = typeof(PlayerWindow).GetMethod("CompilePerformanceFile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                            if (!await (Task<bool>)open.Invoke(window, new object?[] { source, null })! ||
                                (string?)current.Invoke(window, null) != source)
                                throw new InvalidOperationException("Loaded CCMZ was not selected for direct compilation.");
                            var (imported, bytes) = ((ImportedScore, byte[]))compile.Invoke(null, new object[] { source })!;
                            using var json = System.Text.Json.JsonDocument.Parse(bytes);
                            if (imported.Bpm != 78 || imported.Performance is not { Count: 693 } ||
                                imported.Performance.Count(n => n.StartBeats == 0) < 2 || !json.RootElement.TryGetProperty("polyphony", out _) ||
                                !json.RootElement.TryGetProperty("pedal", out var pedalEvents) || pedalEvents.GetArrayLength() < 10)
                                throw new InvalidOperationException($"The actual CCMZ did not retain both hands: BPM {imported.Bpm}, notes {imported.Performance?.Count}, starting notes {imported.Performance?.Count(n => n.StartBeats == 0)}.");
                            var localState = (LocalAudioState)typeof(PlayerWindow).GetMethod("CurrentLocalAudioState", instance)!.Invoke(window, null)!;
                            if (localState.PerformanceSourcePath != source || imported.SustainPedal is not { Count: > 0 })
                                throw new InvalidOperationException("Local audio did not receive the original CCMZ or its sustain pedal.");
                            if (ScoreParser.Parse(localState.ScoreText, true).First(n => n.Degree > 0) is not { Degree: 3, Sharp: false })
                                throw new InvalidOperationException("The CCMZ simile direction selected the accompaniment in the numbered-notation preview.");
                            if (Environment.GetEnvironmentVariable("PIANO_REFERENCE_AUDIT_DIR") is { Length: > 0 } audit)
                            {
                                var opening = imported with
                                {
                                    Performance = imported.Performance.Where(n => n.StartBeats + n.DurationBeats < 80).ToArray(),
                                    SustainPedal = imported.SustainPedal.Where(p => p.Beat < 80).ToArray()
                                };
                                foreach (var instrument in new[] { AudioInstrument.SalamanderPiano, AudioInstrument.HallPiano })
                                {
                                    string wav = System.IO.Path.Combine(audit, $"{instrument}.wav");
                                    var rendered = LocalAudioRenderer.RenderPerformanceToWaveFile(wav, opening, 78, default, instrument);
                                    using var audio = new System.IO.BinaryReader(System.IO.File.OpenRead(wav));
                                    audio.BaseStream.Position = 22;
                                    if (audio.ReadUInt16() != 2 || rendered.DurationSeconds < 45)
                                        throw new InvalidOperationException("The original score did not render as stereo.");
                                }
                                Console.WriteLine("PASS: original CCMZ piano accompaniment and pedal rendered in stereo with both piano presets");
                            }
                            if (Environment.GetEnvironmentVariable("PIANO_CCMZ_POLYPHONY_OUTPUT") is { Length: > 0 } output)
                                System.IO.File.WriteAllBytes(output, bytes);
                            lyricTestDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccmz-polyphony-" + Guid.NewGuid().ToString("N"));
                            System.IO.Directory.CreateDirectory(lyricTestDirectory);
                            string downloaded = System.IO.Path.Combine(lyricTestDirectory, System.IO.Path.GetFileName(source));
                            string automatic = System.IO.Path.Combine(lyricTestDirectory,
                                System.IO.Path.GetFileNameWithoutExtension(source) + "_多音.json");
                            var downloadedBytes = System.IO.File.ReadAllBytes(source);
                            System.IO.File.WriteAllText(System.IO.Path.ChangeExtension(downloaded, ".lrc"), "[00:00]测试歌词");
                            var onSaved = typeof(PlayerWindow).GetMethod("ImportDownloadedCcmzAsync", instance)!
                                .CreateDelegate<Func<string, byte[], ImportedScore, CancellationToken, Task<bool>>>(window);
                            var onlineWindow = new OnlineScoreWindow(window, onSaved);
                            try
                            {
                                var complete = typeof(OnlineScoreWindow).GetMethod("CompleteDownloadAsync", instance)!;
                                async Task<bool> CompleteDownload() => await (Task<bool>)complete.Invoke(onlineWindow,
                                    new object[] { new OnlineScore(imported.Title, "https://example.invalid/score"), downloaded,
                                        downloadedBytes, CancellationToken.None })!;
                                if (!await CompleteDownload() || (string?)current.Invoke(window, null) != downloaded ||
                                    !System.IO.File.ReadAllBytes(automatic).SequenceEqual(bytes))
                                    throw new InvalidOperationException("The online completion flow did not compile and import the downloaded score.");
                                var fileInfo = (TextBlock)typeof(PlayerWindow).GetField("fileInfo", instance)!.GetValue(window)!;
                                if (!fileInfo.Text.Contains("多音谱已就绪") || (string?)fileInfo.ToolTip != automatic)
                                    throw new InvalidOperationException("The current score did not expose the generated file.");
                                if (!await CompleteDownload() || System.IO.Directory.GetFiles(lyricTestDirectory, "*多音*.json").Length != 1)
                                    throw new InvalidOperationException("An identical download should reuse the existing export.");
                                System.IO.File.WriteAllText(automatic, "user file");
                                if (!await CompleteDownload() || System.IO.File.ReadAllText(automatic) != "user file" ||
                                    !System.IO.File.ReadAllBytes((string)fileInfo.ToolTip).SequenceEqual(bytes))
                                    throw new InvalidOperationException("An existing custom export was overwritten or the new export is invalid.");
                                Console.WriteLine("PASS: downloaded CCMZ automatically compiles and imports; repeated downloads reuse JSON and preserve edited files");
                            }
                            finally { onlineWindow.Close(); }
                            string plain = System.IO.Path.Combine(lyricTestDirectory, "single.txt");
                            System.IO.File.WriteAllText(plain, "1 2 3");
                            if (!await (Task<bool>)open.Invoke(window, new object?[] { plain, null })! || current.Invoke(window, null) != null)
                                throw new InvalidOperationException("Loading TXT retained the old CCMZ for direct compilation.");
                            Console.WriteLine("PASS: current CCMZ compiles 693 polyphonic notes; loading TXT clears source selection");
                        }
                        if (test == "idle")
                        {
                            var primary = Descendants(window).OfType<Grid>().Single(grid => Equals(grid.Tag, "primary-actions"));
                            var primaryTags = primary.Children.OfType<Button>().Select(button => button.Tag).ToArray();
                            if (primaryTags.Length != 2 || !primaryTags.Contains("primary-online") || !primaryTags.Contains("primary-local-audio"))
                                throw new InvalidOperationException("Primary action row must only contain online scores and local audio.");
                            var secondary = Descendants(window).OfType<Expander>().Single(expander => Equals(expander.Tag, "secondary-tools"));
                            if (secondary.IsExpanded)
                                throw new InvalidOperationException("Secondary tools must start collapsed.");
                        }
                        if (test == "practice")
                        {
                            var practiceButton = Descendants(window).OfType<Button>().Single(button => button.Content?.ToString() == "练习模式");
                            practiceButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            await Task.Delay(150);
                            var practice = app.Windows.OfType<PracticeModeWindow>().LastOrDefault(item => item.IsVisible);
                            if (practice == null || !practice.IsVisible)
                                throw new InvalidOperationException("练习模式窗口未能打开。");
                            if (!Descendants(practice).OfType<Canvas>().Any())
                                throw new InvalidOperationException("练习模式缺少三行简谱显示区域。");
                            var modes = Descendants(practice).OfType<ComboBox>().Single();
                            if (modes.Items.Count != 3)
                                throw new InvalidOperationException("演奏选择缺少练习模式。");
                            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                            var progressField = typeof(PracticeModeWindow).GetField("progress", flags)!;
                            var submit = typeof(PracticeModeWindow).GetMethod("SubmitSimpleDegree", flags)!;
                            var resetPractice = Descendants(practice).OfType<Button>().Single(b => Equals(b.Content, "重置进度"));
                            resetPractice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            practice.ApplyHostState(new PracticeModeState("【1_】 #2_ 3_", "模式回归", 120));
                            var session = (PracticeProgress)progressField.GetValue(practice)!;
                            submit.Invoke(practice, new object[] { 1 });
                            submit.Invoke(practice, new object[] { 2 });
                            submit.Invoke(practice, new object[] { 3 });
                            if (session.CurrentNoteIndex != 3 || session.CorrectCount != 3)
                                throw new InvalidOperationException("简单练习未接受连续提前输入。");
                            resetPractice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            modes.SelectedItem = PracticePerformanceModes.OptionOf(PracticePerformanceMode.Rhythm);
                            submit.Invoke(practice, new object[] { 2 });
                            if (session.WrongCount != 1 || session.CurrentNoteIndex != 0)
                                throw new InvalidOperationException("节奏练习错音推进。");
                            submit.Invoke(practice, new object[] { 1 });
                            submit.Invoke(practice, new object[] { 2 });
                            await Task.Delay(90);
                            if (session.CurrentNoteIndex != 0)
                                throw new InvalidOperationException("节奏练习接受了提前输入。");
                            await Task.Delay(240);
                            if (session.CurrentNoteIndex != 1 || session.CorrectCount != 1)
                                throw new InvalidOperationException("节奏练习音长结束后未推进。");
                            submit.Invoke(practice, new object[] { 2 });
                            modes.SelectedItem = PracticePerformanceModes.OptionOf(PracticePerformanceMode.Normal);
                            await Task.Delay(320);
                            if (session.CurrentNoteIndex != 1)
                                throw new InvalidOperationException("切换模式后旧节奏计时仍推进。");
                            modes.SelectedItem = PracticePerformanceModes.OptionOf(PracticePerformanceMode.Rhythm);
                            submit.Invoke(practice, new object[] { 2 });
                            resetPractice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            await Task.Delay(320);
                            if (session.CurrentNoteIndex != 0 || session.CorrectCount != 0)
                                throw new InvalidOperationException("重置后旧节奏计时仍推进。");
                            using (var practiceAudio = new PracticeAudioEngine())
                            {
                                practiceAudio.Play(new ScoreNote(1, 0, false, 0), 120);
                                await Task.Delay(30);
                                practiceAudio.Stop();
                            }
                            practice.Close();
                            await Task.Delay(50);
                        }
                        if (test == "lyrics-words")
                        {
                            var notes = ScoreParser.Parse("1 2 3 4", true);
                            var words = ScoreLyrics.FromWordBindings(notes, new[] {
                                new BoundLyric(0, 1, "春") { ReferenceScoreUrl = "https://example.invalid/reference", ReferenceScoreSha256 = new string('a', 64), PositionEvidence = "local-position-test" },
                                new BoundLyric(1, 3, "风") { NeedsReview = true, ReferenceScoreUrl = "https://example.invalid/reference", ReferenceScoreSha256 = new string('a', 64), AcousticDiagnostics = "vowel-pitch-mismatch" },
                                new BoundLyric(3, 4, "来") { EndLine = true } }, "test");
                            var dialog = new LyricsBindingDialog(notes, 120, "", words.ToJson()) { Owner = window };
                            _ = dialog.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                var grid = Descendants(dialog).OfType<DataGrid>().Single();
                                if (grid.Items.OfType<LyricsBindingRow>().Count() != 3 || grid.Columns.Count != 5)
                                    throw new InvalidOperationException("逐字窗口丢失音节或核对列。");
                                dialog.UpdateLayout();
                                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                                bitmap.Render(dialog);
                                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                                using (var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "lyrics-word-dialog.png"))) encoder.Save(file);
                                ((LyricsBindingRow)grid.Items[0]).Text = "秋";
                                Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "保存对应关系")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            }));
                            if (dialog.ShowDialog() != true || dialog.Result!.Syllables[1].EndNote != 3 || !dialog.Result.Syllables[1].NeedsReview)
                                throw new InvalidOperationException("逐字保存丢失拖腔或待核对状态。");
                            if (dialog.Result.Syllables[0].ReferenceScoreUrl != "" || dialog.Result.Syllables[1].ReferenceScoreSha256 != new string('a', 64)
                                || dialog.Result.Syllables[1].AcousticDiagnostics != "vowel-pitch-mismatch"
                                || dialog.Result.Syllables[0].PositionEvidence != "" || dialog.Result.Syllables[1].PositionEvidence != "")
                                throw new InvalidOperationException("带词谱依据未保留，或文字修改后仍沿用旧核实依据。");
                            var overlay = new LyricsWindow(new LyricsLayout()) { Owner = window };
                            overlay.ShowLyrics("春风来", "下一行");
                            var currentLine = Descendants(overlay).OfType<TextBlock>().Single(t => Equals(t.Tag, "lyrics-current-line"));
                            var nextLine = Descendants(overlay).OfType<TextBlock>().Single(t => Equals(t.Tag, "lyrics-next-line"));
                            overlay.ShowLyrics("", "下一行");
                            if (!overlay.IsVisible || currentLine.Text != "春风来" || currentLine.FontWeight != FontWeights.Bold ||
                                currentLine.HorizontalAlignment != nextLine.HorizontalAlignment || currentLine.Inlines.Count != 1 ||
                                nextLine.Opacity >= currentLine.Opacity)
                                throw new InvalidOperationException("整行高亮／句尾空档保留／上下同列显示失败。");
                            overlay.ShowLyrics("下一行", "再下一行");
                            if (currentLine.Text != "下一行" || nextLine.Text != "再下一行" ||
                                !((TranslateTransform)currentLine.RenderTransform).HasAnimatedProperties)
                                throw new InvalidOperationException("下一行没有滚动进入当前行。");
                            await Task.Delay(320);
                            if (Math.Abs(((TranslateTransform)currentLine.RenderTransform).Y) > 0.01)
                                throw new InvalidOperationException("滚动完成后当前行没有回到固定位置。");
                            overlay.ShowLyrics("最后一行", null);
                            overlay.ShowLyrics("", null);
                            if (!overlay.IsVisible || currentLine.Text != "最后一行")
                                throw new InvalidOperationException("末行在空白事件时被隐藏。");
                            overlay.HideLyrics();
                            if (overlay.IsVisible) throw new InvalidOperationException("显式停止未隐藏歌词。");
                            overlay.ShowLyrics("重新开始", "下一句");
                            if (((TranslateTransform)currentLine.RenderTransform).HasAnimatedProperties)
                                throw new InvalidOperationException("重新开始时不应滚入上一首残留的歌词。");
                            overlay.Close();
                        }
                        if (test == "lyrics-second-real")
                        {
                            var sample = AudioLyricsAligner.ReadResult(Environment.GetEnvironmentVariable("PIANO_LYRICS_SECOND_REAL_JOB")!);
                            string source = Environment.GetEnvironmentVariable("PIANO_LYRICS_SECOND_REAL_SOURCE")!;
                            var original = CcmzImporter.Import(System.IO.File.ReadAllBytes(source), "second real");
                            if (original.Lyrics != null || original.Bpm != 80 || sample.Words.Count != 462)
                                throw new InvalidOperationException("第二首原件不是预期的真实无词 CCMZ。");
                            using (var candidate = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(
                                System.IO.Path.Combine(Environment.GetEnvironmentVariable("PIANO_LYRICS_SECOND_REAL_JOB")!, "candidate-alignment.json"))))
                            {
                                int expectedDiagnostics = candidate.RootElement.GetProperty("algorithmVersion").GetInt32() >= 11 ? 42 : 32;
                                if (candidate.RootElement.TryGetProperty("positionReview", out var review) &&
                                    (sample.Score.Lyrics!.Syllables.Count(w => w.PositionEvidence != "") != review.GetProperty("corroborated").GetInt32() ||
                                     sample.Score.Lyrics.Syllables.Count(w => w.AcousticDiagnostics != "") != expectedDiagnostics))
                                    throw new InvalidOperationException("第二首局部位置依据或原唱诊断未完整导入。");
                            }
                            typeof(PlayerWindow).GetField("currentCcmzPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, source);
                            Descendants(window).OfType<TextBox>().Single(b => Equals(b.Tag, "lyrics-search-title")).Text = sample.Score.Title;
                            Descendants(window).OfType<Button>().Single(b => Equals(b.Tag, "audio-align-lyrics")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            for (int i = 0; i < 100 && typeof(PlayerWindow).GetField("audioAlignmentCancellation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window) != null; i++)
                                await Task.Delay(20);
                            if (app.Windows.OfType<LyricsBindingDialog>().Any(d => d.IsVisible))
                                throw new InvalidOperationException("第二首一键导入要求手动校准。");
                            var state = (LocalAudioState)typeof(PlayerWindow).GetMethod("CurrentLocalAudioState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
                            var notes = ScoreParser.Parse(state.ScoreText, true);
                            var measures = state.ScoreText.Split('\n');
                            if (measures.Take(measures.Length - 1).Any(line => ScoreParser.Parse(line, true).Sum(n => n.Beats) != 4) ||
                                ScoreParser.Parse(measures[^1], true).Sum(n => n.Beats) > 4)
                                throw new InvalidOperationException("第二首正式谱未按四拍小节排版。");
                            var bound = ScoreLyrics.Parse(state.LyricsBindingJson, notes);
                            string saved = (string)typeof(PlayerWindow).GetField("currentScorePath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                            if (!System.IO.File.Exists(saved) || !System.IO.File.Exists(ScoreLyrics.SidecarPath(saved)) ||
                                !bound.Syllables.SequenceEqual(sample.Score.Lyrics!.Syllables) || notes.Sum(n => n.Beats) != sample.Score.Notes.Sum(n => n.Beats))
                                throw new InvalidOperationException("第二首一键导入未完整自动保存。");
                            var stored = ScoreLyrics.Parse(System.IO.File.ReadAllText(ScoreLyrics.SidecarPath(saved)), notes);
                            if (!stored.Syllables.SequenceEqual(bound.Syllables)) throw new InvalidOperationException("第二首落盘丢失逐字绑定。");
                            var open = typeof(PlayerWindow).GetMethod("OpenScoreFileAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                            if (!await (Task<bool>)open.Invoke(window, new object?[] { source, null })!) throw new InvalidOperationException("第二首原件重导入失败。");
                            var restored = (LocalAudioState)typeof(PlayerWindow).GetMethod("CurrentLocalAudioState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
                            if (restored.ScoreText != state.ScoreText || restored.LyricsBindingJson != state.LyricsBindingJson)
                                throw new InvalidOperationException("第二首重导入丢失同音拆分或绑定。");
                            window.UpdateLayout();
                            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(window);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                            using (var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "lyrics-second-real.png"))) encoder.Save(file);
                            Console.WriteLine($"PASS: 第二首真实 CCMZ 一键自动保存/逐字重载/同源恢复，{bound.Syllables.Count} 字，{notes.Sum(n => n.Beats)} 拍");
                        }
                        if (test == "lyrics-real-sample")
                        {
                            var sample = AudioLyricsAligner.ReadResult(Environment.GetEnvironmentVariable("PIANO_LYRICS_REAL_JOB")!);
                            var sourceBinding = sample.Score.Lyrics!;
                            if (sample.Words.Count != 338 || sourceBinding.Syllables.Count != 338 || sample.Score.Notes.Sum(n => n.Beats) != 411.75)
                                throw new InvalidOperationException("真实人声样本逐字数量或总拍数不符。");
                            using (var candidate = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(sample.JobDirectory, "candidate-alignment.json"))))
                            {
                                int version = candidate.RootElement.GetProperty("algorithmVersion").GetInt32();
                                int newMelismas = version == 11 ? 2 : 0;
                                if (System.IO.File.Exists(System.IO.Path.Combine(sample.JobDirectory, "reference-score.json")) &&
                                    (sourceBinding.Syllables.Count(w => w.ReferenceScoreUrl != "") != 21 || sourceBinding.Syllables.Count(w => w.NeedsReview) != newMelismas
                                    || sourceBinding.Syllables.Count(w => w.AcousticDiagnostics != "") != 21 + newMelismas))
                                    throw new InvalidOperationException("真实样本未保留参考谱依据、原唱诊断或新增多音字疑点。");
                            }
                            var dialog = new LyricsBindingDialog(sample.Score.Notes, 108, "", sourceBinding.ToJson()) { Owner = window };
                            _ = dialog.Dispatcher.BeginInvoke(new Action(async () =>
                            {
                                await Task.Delay(150);
                                var reference = (System.Windows.Media.MediaPlayer)typeof(LyricsBindingDialog).GetField("referencePlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog)!;
                                bool openedAtWord = false;
                                reference.MediaOpened += (_, _) => openedAtWord = Math.Abs(reference.Position.TotalSeconds - (sourceBinding.Syllables[0].ReferenceStartSeconds!.Value - .12)) < .3;
                                Descendants(dialog).OfType<DataGrid>().Single().SelectedIndex = 0;
                                Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "试听原唱该字")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                for (int attempt = 0; attempt < 100 && !openedAtWord; attempt++) await Task.Delay(20);
                                if (!openedAtWord) throw new InvalidOperationException("原唱片段未定位到当前字。");
                                dialog.UpdateLayout();
                                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                                bitmap.Render(dialog);
                                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                                using (var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "lyrics-real-words.png"))) encoder.Save(file);
                                Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "保存对应关系")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            }));
                            if (dialog.ShowDialog() != true || !dialog.Result!.Syllables.SequenceEqual(
                                sourceBinding.Syllables.Select(w => w with { Text = LyricsTimeline.NormalizeText(w.Text) })))
                                throw new InvalidOperationException("真实人声样本经过校准窗口后丢失逐字锚点。");
                            lyricTestDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "piano-real-import-" + Guid.NewGuid().ToString("N"));
                            System.IO.Directory.CreateDirectory(lyricTestDirectory);
                            string originalPath = System.IO.Path.Combine(lyricTestDirectory, "original.ccmz");
                            using (var output = new System.IO.MemoryStream())
                            {
                                using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, true))
                                {
                                    foreach (var pair in new[] { ("data.ccmid", "midi.json"), ("data.ccxml", "score.json") })
                                    {
                                        using var entry = zip.CreateEntry(pair.Item1).Open();
                                        using var input = System.IO.File.OpenRead(System.IO.Path.Combine(sample.JobDirectory, pair.Item2));
                                        input.CopyTo(entry);
                                    }
                                }
                                System.IO.File.WriteAllBytes(originalPath, new byte[] { 1 }.Concat(output.ToArray()).ToArray());
                            }
                            string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(originalPath))).ToLowerInvariant();
                            var confirmed = dialog.Result! with { SourceScoreSha256 = sourceHash };
                            string priorTarget = System.IO.Path.Combine(AppContext.BaseDirectory, "简谱", $"{sample.Score.Title}_源谱 BPM：108.txt");
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(priorTarget)!);
                            System.IO.File.WriteAllText(priorTarget, sample.Score.Text);
                            (confirmed with { SourceScoreSha256 = new string('a', 64) }).Save(ScoreLyrics.SidecarPath(priorTarget), sample.Score.Notes);
                            typeof(PlayerWindow).GetMethod("LoadImportedScore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                                .Invoke(window, new object?[] { originalPath, sample.Score with { Lyrics = confirmed }, null });
                            var retained = ScoreLyrics.Parse(System.IO.File.ReadAllText(ScoreLyrics.SidecarPath(priorTarget)), sample.Score.Notes);
                            if (!string.Equals(retained.SourceScoreSha256, sourceHash, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("保留同音符绑定时未更新本次原件校验值。");
                            var openOriginal = typeof(PlayerWindow).GetMethod("OpenScoreFileAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                            if (!await (Task<bool>)openOriginal.Invoke(window, new object?[] { originalPath, null })!)
                                throw new InvalidOperationException("原始 CCMZ 重导入失败。");
                            var localState = (LocalAudioState)typeof(PlayerWindow).GetMethod("CurrentLocalAudioState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
                            var reloaded = ScoreLyrics.Parse(localState.LyricsBindingJson, ScoreParser.Parse(localState.ScoreText, true));
                            if (!reloaded.Syllables.SequenceEqual(confirmed.Syllables) || ScoreParser.Parse(localState.ScoreText, true).Sum(n => n.Beats) != 411.75)
                                throw new InvalidOperationException("原始 CCMZ 重导入覆盖了逐字校准或同音拆分。");
                        }
                        if (test.StartsWith("lyrics-audio"))
                        {
                            lyricTestDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "piano-audio-" + Guid.NewGuid().ToString("N"));
                            System.IO.Directory.CreateDirectory(lyricTestDirectory);
                            string source = System.IO.Path.Combine(lyricTestDirectory, "source.ccmz");
                            System.IO.File.WriteAllText(source, "test transport fixture");
                            typeof(PlayerWindow).GetField("currentCcmzPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, source);
                            var query = Descendants(window).OfType<TextBox>().Single(b => Equals(b.Tag, "lyrics-search-title"));
                            var button = Descendants(window).OfType<Button>().Single(b => Equals(b.Tag, "audio-align-lyrics"));
                            var align = typeof(PlayerWindow).GetMethod("AlignFromAudioAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                            query.Text = "等待";
                            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            await Task.Delay(20);
                            if (!button.IsEnabled || !Equals(button.Content, "取消逐字对齐")) throw new InvalidOperationException("逐字对齐无法取消。");
                            if (test != "lyrics-audio-close")
                            {
                                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                for (int i = 0; i < 100 && Equals(button.Content, "取消逐字对齐"); i++) await Task.Delay(20);
                                query.Text = "失败";
                                await (Task)align.Invoke(window, null)!;
                                query.Text = "测试";
                                await (Task)align.Invoke(window, null)!;
                                var state = (LocalAudioState)typeof(PlayerWindow).GetMethod("CurrentLocalAudioState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
                                var bound = ScoreLyrics.Parse(state.LyricsBindingJson, ScoreParser.Parse(state.ScoreText, true));
                                if (bound.Syllables.Count != 3 || bound.Syllables[1].EndNote != 3 || !bound.Syllables[1].NeedsReview)
                                    throw new InvalidOperationException("人声逐字候选未保存或未同步本机发声。");
                                if (app.Windows.OfType<LyricsBindingDialog>().Any(d => d.IsVisible))
                                    throw new InvalidOperationException("一键配词仍强制打开手动校准窗口。");
                                query.Text = "缺字";
                                await (Task)align.Invoke(window, null)!;
                                var unchanged = (LocalAudioState)typeof(PlayerWindow).GetMethod("CurrentLocalAudioState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
                                if (unchanged.LyricsBindingJson != state.LyricsBindingJson || unchanged.ScoreText != state.ScoreText)
                                    throw new InvalidOperationException("不完整自动配词替换了现有曲谱。");
                                query.Text = "重算";
                                await (Task)align.Invoke(window, null)!;
                                var updated = (LocalAudioState)typeof(PlayerWindow).GetMethod("CurrentLocalAudioState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
                                if (ScoreLyrics.Parse(updated.LyricsBindingJson, ScoreParser.Parse(updated.ScoreText, true)).Syllables[0].Text != "新")
                                    throw new InvalidOperationException("主动重新配词被同源旧绑定覆盖。");
                            }
                        }
                        if (test == "lyrics-binding")
                        {
                            var notes = ScoreParser.Parse("1 2 |\n3 4", true);
                            var dialog = new LyricsBindingDialog(notes, 120, "春风\n秋雨", "") { Owner = window };
                            _ = dialog.Dispatcher.BeginInvoke(new Action(async () =>
                            {
                                await Task.Delay(150);
                                var grid = Descendants(dialog).OfType<DataGrid>().Single();
                                var rows = grid.Items.OfType<LyricsBindingRow>().ToArray();
                                rows[0].Start = 2; rows[0].End = 2;
                                rows[1].Start = 3; rows[1].End = 4;
                                grid.Items.Refresh();
                                dialog.UpdateLayout();
                                var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                                image.Render(dialog);
                                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                                using (var output = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "lyrics-binding-dialog.png"))) encoder.Save(output);
                                Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "保存对应关系")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            }));
                            if (dialog.ShowDialog() != true || dialog.Result?.Cues[0].StartNote != 1)
                                throw new InvalidOperationException("歌词校准窗口未保存小节中间的起始音符。");

                            string xmlPath = System.IO.Path.ChangeExtension(path, ".musicxml");
                            string title = "smoke-lyrics-" + Guid.NewGuid().ToString("N");
                            string xml = "<score-partwise><work><work-title>" + title + "</work-title></work><part><measure implicit=\"yes\"><direction><sound tempo=\"120\"/></direction>" +
                                "<note><pitch><step>C</step><octave>4</octave></pitch><duration>1</duration><lyric><text>春</text><end-line/></lyric></note>" +
                                "<note><pitch><step>D</step><octave>4</octave></pitch><duration>1</duration><lyric><text>风</text><end-line/></lyric></note></measure></part></score-partwise>";
                            try
                            {
                                System.IO.File.WriteAllText(xmlPath, xml);
                                var open = typeof(PlayerWindow).GetMethod("OpenScoreFileAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                                if (!await (Task<bool>)open.Invoke(window, new object?[] { xmlPath, null })!) throw new InvalidOperationException("带词 MusicXML UI 导入失败。");
                                var field = typeof(PlayerWindow).GetField("currentScorePath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                                string saved = (string)field.GetValue(window)!;
                                if (!System.IO.File.Exists(ScoreLyrics.SidecarPath(saved))) throw new InvalidOperationException("正式 TXT 旁未保存歌词绑定。");
                                if (!await (Task<bool>)open.Invoke(window, new object?[] { saved, null })!) throw new InvalidOperationException("TXT 重新导入失败。");
                                string json = (string)typeof(PlayerWindow).GetField("lyricsBindingJson", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                                if (ScoreLyrics.Parse(json, ScoreParser.Parse(System.IO.File.ReadAllText(saved), true)).Cues.Count != 2)
                                    throw new InvalidOperationException("TXT 重新导入未恢复对应歌词。");
                                var localState = (LocalAudioState)typeof(PlayerWindow).GetMethod("CurrentLocalAudioState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
                                if (localState.LyricsBindingJson != json) throw new InvalidOperationException("本机发声未同步歌词绑定。");
                                var calibrated = ScoreLyrics.Create(ScoreParser.Parse(System.IO.File.ReadAllText(saved), true), new[] { new BoundLyric(1, 2, "已校准") }, "手动校准");
                                calibrated.Save(ScoreLyrics.SidecarPath(saved), ScoreParser.Parse(System.IO.File.ReadAllText(saved), true));
                                if (!await (Task<bool>)open.Invoke(window, new object?[] { xmlPath, null })! || (string)field.GetValue(window)! != saved)
                                    throw new InvalidOperationException("重复导入生成了多份正式 TXT。");
                                json = (string)typeof(PlayerWindow).GetField("lyricsBindingJson", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                                if (ScoreLyrics.Parse(json, notes.Take(2).ToArray()).Cues[0].Text != "已校准")
                                    throw new InvalidOperationException("重复导入覆盖了用户的校准结果。");
                                Console.WriteLine("PASS: 带词源谱 UI 导入 → TXT/绑定保存 → TXT 重载 → 本机发声同步");
                            }
                            finally { System.IO.File.Delete(xmlPath); }
                        }
                        if (test.StartsWith("lyrics-search"))
                        {
                            lyricTestDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "piano-search-" + Guid.NewGuid().ToString("N"));
                            System.IO.Directory.CreateDirectory(lyricTestDirectory);
                            string songPath = System.IO.Path.Combine(lyricTestDirectory, "父亲_源谱 BPM：72 (13).txt");
                            System.IO.File.WriteAllText(songPath, "1 2 3 4");
                            var open = typeof(PlayerWindow).GetMethod("OpenScoreFileAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                            if (!await (Task<bool>)open.Invoke(window, new object?[] { songPath, null })!) throw new InvalidOperationException("搜词测试曲谱未导入。");
                            var button = Descendants(window).OfType<Button>().Single(b => Equals(b.Tag, "search-lyrics"));
                            var queryBox = Descendants(window).OfType<TextBox>().Single(b => Equals(b.Tag, "lyrics-search-title"));
                            if (queryBox.Text != "父亲") throw new InvalidOperationException("未从文件名正确填入曲名。");
                            var search = typeof(PlayerWindow).GetMethod("SearchLyricsAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                            string sidecar = OnlineScoreClient.LyricsFilePath(songPath);
                            if (test == "lyrics-search-close")
                            {
                                queryBox.Text = "等待";
                                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                await Task.Delay(20);
                                if (!button.IsEnabled || !Equals(button.Content, "取消搜词")) throw new InvalidOperationException("搜词时无法取消。");
                            }
                            else
                            {
                                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                for (int i = 0; i < 150 && Equals(button.Content, "取消搜词"); i++) await Task.Delay(20);
                                if (!System.IO.File.Exists(sidecar) || lookups != 1) throw new InvalidOperationException("一键搜索未保存歌词或重复请求。");
                                string saved = System.IO.File.ReadAllText(sidecar);
                                var stateMethod = typeof(PlayerWindow).GetMethod("CurrentLocalAudioState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                                LocalAudioState State() => (LocalAudioState)stateMethod.Invoke(window, null)!;
                                if (!saved.Contains("[00:01.00]") || !State().LyricsText.Contains("搜索测试甲") || State().LyricsBindingJson.Length != 0)
                                    throw new InvalidOperationException("下载歌词未载入或被伪报为已校准。");
                                if (!await (Task<bool>)open.Invoke(window, new object?[] { songPath, null })! || !State().LyricsText.Contains("搜索测试甲"))
                                    throw new InvalidOperationException("重导入未恢复搜索下载的歌词。");
                                foreach (string query in new[] { "无结果", "断网" })
                                {
                                    queryBox.Text = query;
                                    await (Task)search.Invoke(window, null)!;
                                    if (System.IO.File.ReadAllText(sidecar) != saved || !State().LyricsText.Contains("搜索测试甲"))
                                        throw new InvalidOperationException("无结果或网络失败破坏了已有歌词。");
                                }
                                queryBox.Text = "等待";
                                var pending = (Task)search.Invoke(window, null)!;
                                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                await pending.WaitAsync(TimeSpan.FromSeconds(2));
                                if (System.IO.File.ReadAllText(sidecar) != saved || System.IO.Directory.GetFiles(lyricTestDirectory, "*.tmp").Length != 0)
                                    throw new InvalidOperationException("取消搜索破坏歌词或遗留临时文件。");
                                queryBox.Text = "普通歌词";
                                await (Task)search.Invoke(window, null)!;
                                if (State().LyricsText != "纯文本歌词" || State().LyricsTimingText.Length != 0)
                                    throw new InvalidOperationException("纯文本歌词回退未正确导入。");
                                var binding = ScoreLyrics.Create(ScoreParser.Parse("1 2 3 4", true), new[] { new BoundLyric(1, 4, "已校准") }, "手动校准");
                                typeof(PlayerWindow).GetMethod("ApplyScoreLyrics", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { binding, songPath });
                                int previousLookups = lookups;
                                await (Task)search.Invoke(window, null)!;
                                if (lookups != previousLookups || State().LyricsText != "已校准") throw new InvalidOperationException("搜索覆盖了已经确认的词音绑定。");
                                Console.WriteLine("PASS: 本地曲谱一键搜词 → 下载保存/载入/重载 → 无结果/断网/取消保护 → 保留校准");
                            }
                        }
                        if (test == "pending-save")
                        {
                            // A valid numeric edit queues the real debounced settings saver.
                            var number = Descendants(window).OfType<TextBox>().First(t => Equals(t.Tag, "gap") && !t.IsReadOnly);
                            number.Text = "30";
                        }
                        if (test is "countdown" or "playback")
                        {
                            var start = Descendants(window).OfType<Button>().Single(b => b.Content?.ToString()?.StartsWith("开始 ") == true);
                            start.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            await Task.Delay(test == "countdown" ? 100 : 3400);
                        }
                        var clock = Stopwatch.StartNew();
                        window.Close();
                        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Console.WriteLine($"PASS {test}: close {clock.ElapsedMilliseconds}ms");
                    }
                    finally
                    {
                        if (window?.IsVisible == true) window.Close();
                        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                        if (lyricTestDirectory != null) System.IO.Directory.Delete(lyricTestDirectory, true);
                    }
                }
            }
            catch (Exception e) { failures++; Console.Error.WriteLine(e); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return failures == 0 ? 0 : 1;
    }
    private static void VerifyAlignmentRuntimeSelection()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PianoRuntime-" + Guid.NewGuid().ToString("N"));
        string helper = System.IO.Path.Combine(root, "work", "lyrics-alignment");
        string bundled = System.IO.Path.Combine(helper, "python", "python.exe");
        string legacy = System.IO.Path.Combine(root, "legacy-python.exe");
        string? previousHome = Environment.GetEnvironmentVariable("PYTHONHOME");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(bundled)!);
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(helper, "venv"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(helper, "run_alignment.py"), "# fixture");
            System.IO.File.WriteAllText(bundled, "fixture");
            Environment.SetEnvironmentVariable("PYTHONHOME", "invalid-inherited-python");
            var info = AudioLyricsAligner.CreateStartInfo(root, "带 空格.ccmz", "歌曲", "任务目录");
            if (info.FileName != bundled || info.Environment.ContainsKey("PYTHONHOME") || info.Environment["MPLBACKEND"] != "Agg" ||
                !info.Environment["PYTHONPATH"]!.StartsWith(System.IO.Path.GetDirectoryName(bundled)!) ||
                !info.ArgumentList.Contains("带 空格.ccmz"))
                throw new Exception("Bundled runtime must ignore external Python and preserve argument boundaries.");
            System.IO.File.Delete(bundled);
            System.IO.File.WriteAllText(legacy, "fixture");
            System.IO.File.WriteAllText(System.IO.Path.Combine(helper, "venv", "pyvenv.cfg"), "executable = " + legacy);
            if (AudioLyricsAligner.CreateStartInfo(root, "source", "title", "job").FileName != legacy)
                throw new Exception("Existing development runtime no longer works.");
            System.IO.File.Delete(legacy);
            try { AudioLyricsAligner.CreateStartInfo(root, "source", "title", "job"); throw new Exception("Missing runtime was accepted."); }
            catch (System.IO.IOException) { }
            Console.WriteLine("PASS alignment runtime: bundled isolation, spaced arguments, existing venv, missing runtime");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYTHONHOME", previousHome);
            System.IO.Directory.Delete(root, true);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var seen = new HashSet<DependencyObject>();
        return Walk(node, seen).Skip(1);
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject node, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(node)) yield break;
        yield return node;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject dependency)
                foreach (var nested in Walk(dependency, seen)) yield return nested;
        if (node is Visual)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                foreach (var nested in Walk(VisualTreeHelper.GetChild(node, i), seen)) yield return nested;
    }
}
