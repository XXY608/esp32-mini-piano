using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HarmonicaPlayer;

public sealed class MobileSimulatorWindow : Window
{
    private static readonly Lazy<Task> onlineBridge = new(() => Task.Run(OnlineBridge.RunAsync));
    private readonly WebView2 browser = new();
    private readonly ComboBox instruments = new() { Width = 160, Margin = new Thickness(6), DisplayMemberPath = nameof(AudioInstrumentOption.Name) };
    private readonly TextBlock status = new() { Text = "正在启动模拟器…", Margin = new Thickness(8) };
    private SimulatorAudio? deviceAudio, localAudio;
    private AudioInstrument instrument;
    private bool closed;
    private string? pageUri;
    private readonly string? storageFolder;

    public MobileSimulatorWindow(AudioInstrument initialInstrument = AudioInstrument.Harmonica, string? storageFolder = null)
    {
        this.storageFolder = storageFolder;
        instrument = AudioInstrumentCatalog.Normalize(initialInstrument);
        Title = "电子琴 · 手机版（实体琴模拟）";
        Width = 1100; Height = 900; MinWidth = 760; MinHeight = 600;
        var root = new DockPanel();
        var bar = new DockPanel();
        var back = new Button { Content = "返回电脑版", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6) };
        back.Click += (_, _) => Close();
        instruments.ItemsSource = AudioInstrumentCatalog.WindowsOptions;
        instruments.SelectedItem = AudioInstrumentCatalog.WindowsOptions.First(x => x.Value == instrument);
        instruments.SelectionChanged += (_, _) => {
            if (instruments.SelectedItem is not AudioInstrumentOption selected) return;
            instrument = selected.Value;
            try { deviceAudio?.SetInstrument(instrument); localAudio?.SetInstrument(instrument); }
            catch (Exception error) { status.Text = "Windows 音色加载失败：" + error.Message; }
        };
        DockPanel.SetDock(back, Dock.Left); bar.Children.Add(back);
        var label = new TextBlock { Text = "Windows 音色：", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Left); bar.Children.Add(label);
        DockPanel.SetDock(instruments, Dock.Left); bar.Children.Add(instruments); bar.Children.Add(status);
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar); root.Children.Add(browser);
        Content = root;
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { closed = true; deviceAudio?.Dispose(); localAudio?.Dispose(); browser.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        try
        {
            _ = onlineBridge.Value;
            string folder = storageFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarmonicaPlayer", "mobile-simulator");
            Directory.CreateDirectory(folder);
            string html = Path.Combine(folder, "simulator.html");
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("HarmonicaPlayer.MobileSimulator.html") ?? throw new InvalidOperationException("模拟器资源缺失。"))
            using (var file = File.Create(html)) await source.CopyToAsync(file);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(folder, "webview-data"));
            if (closed) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (closed) return;
            pageUri = new Uri(html).AbsoluteUri;
            await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.__pianoWindowsAudio = true;");
            browser.CoreWebView2.WebMessageReceived += OnAudioMessage;
            browser.CoreWebView2.NavigationCompleted += (_, args) => status.Text = args.IsSuccess ? "按键 Z X C V B N M、逗号、句号、斜杠" : "模拟器加载失败：" + args.WebErrorStatus;
            browser.Source = new Uri(html);
        }
        catch (Exception error)
        {
            if (!closed) status.Text = "无法启动手机版模拟：" + error.Message;
        }
    }

    private void OnAudioMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (closed || !string.Equals(e.Source, pageUri, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            string? channel = root.GetProperty("channel").GetString();
            if (channel == "device" && root.TryGetProperty("manualNotes", out var manualNotes))
            {
                var output = deviceAudio ??= new SimulatorAudio(instrument);
                output.SetManualNotes(manualNotes.EnumerateArray().Select(item => item.GetInt32()).ToArray(), root.GetProperty("volume").GetInt32());
                return;
            }
            int midi = root.GetProperty("midi").GetInt32();
            if (channel is not ("device" or "local")) return;
            if (midi < 0) { if (channel == "device") deviceAudio?.Stop(); else localAudio?.Stop(); return; }
            ScoreNote note = NoteFromMidi(midi);
            SimulatorAudio audio;
            if (channel == "device") audio = deviceAudio ??= new SimulatorAudio(instrument);
            else audio = localAudio ??= new SimulatorAudio(instrument);
            int duration = channel == "device" ? 8000 : root.GetProperty("duration").GetInt32();
            if (channel == "device") audio.SetVolume(root.GetProperty("volume").GetInt32());
            audio.Play(note, Math.Clamp(duration, 80, 8000), sustain: channel == "device");
        }
        catch (Exception error)
        { status.Text = "Windows 本机发声失败：" + error.Message; }
    }

    public static ScoreNote NoteFromMidi(int midi)
    {
        if (midi is < 36 or > 96) throw new ArgumentOutOfRangeException(nameof(midi));
        if (midi == 96) return new ScoreNote(7, 2, true, 0);
        int octave = midi / 12 - 5;
        int[] degree = { 1, 1, 2, 2, 3, 4, 4, 5, 5, 6, 6, 7 };
        bool[] sharp = { false, true, false, true, false, false, true, false, true, false, true, false };
        int pitchClass = midi % 12;
        return new ScoreNote(degree[pitchClass], octave, sharp[pitchClass], 0);
    }
}
