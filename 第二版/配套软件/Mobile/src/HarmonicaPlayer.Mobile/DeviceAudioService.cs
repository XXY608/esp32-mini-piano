using HarmonicaPlayer.Mobile.Core;
#if ANDROID
using Android.Content;
using Android.Media;
#endif

namespace HarmonicaPlayer.Mobile;

// A foreground connection belongs to the app window, not to an individual tab.
public sealed class DeviceAudioService
{
    private readonly MobileAppState state;
    private readonly SemaphoreSlim transition = new(1, 1);
    private CancellationTokenSource? lifetime;
    private Task running = Task.CompletedTask;
    private bool foreground;
    private int revision;
    public string Status { get; private set; } = "未连接";
    public string? LastError { get; private set; }
    public event Action? Changed;

    public DeviceAudioService(MobileAppState state)
    {
        this.state = state;
        state.ConnectionChanged += Refresh;
    }

    public void SetForeground(bool active) { foreground = active; Refresh(); }
    public void Refresh() => _ = RefreshAsync();
    private async Task RefreshAsync()
    {
        int current = Interlocked.Increment(ref revision);
        lifetime?.Cancel();
        await transition.WaitAsync();
        try
        {
            await running;
            if (current != Volatile.Read(ref revision)) return;
            lifetime?.Dispose(); lifetime = null;
            if (!foreground || !state.AutoDeviceAudio || state.UseMock || state.LocalPracticeActive)
            {
                SetStatus(!foreground || state.LocalPracticeActive ? "接收已暂停" :
                    state.UseMock ? "模拟模式" : "未启用");
                running = Task.CompletedTask; return;
            }
            lifetime = new CancellationTokenSource();
            var token = lifetime.Token;
            string address = state.DeviceAddress;
            running = Task.Run(() => ConnectLoopAsync(address, token));
        }
        finally { transition.Release(); }
    }

    private async Task ConnectLoopAsync(string address, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var buffer = new DeviceAudioBuffer();
            using var player = new DeviceAudioPlayer(buffer);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                SetStatus("连接中…");
                using var client = new HttpEsp32Client(address, TimeSpan.FromMilliseconds(900));
                // Secure a working audio sink before asking the piano to mute its speaker.
                await player.StartAsync(attempt.Token);
                Task audio = player.RunAsync(attempt.Token);
                Task receiver = DeviceAudioSession.RunAsync(client, buffer, phone => SetStatus(phone ?
                    "手机发声中" : "琴端发声 · 手机已就绪"), attempt.Token);
                try { await await Task.WhenAny(audio, receiver); }
                finally
                {
                    attempt.Cancel(); buffer.Reset();
                    try { await Task.WhenAll(audio, receiver); }
                    catch (Exception error) when (error is OperationCanceledException or DeviceCommunicationException or InvalidOperationException) { }
                }
            }
            catch (Exception error)
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (player.Interrupted) { SetStatus("音频中断", error.Message); return; }
                SetStatus("等待重连", error.Message);
            }
            if (cancellationToken.IsCancellationRequested) return;
            try { await Task.Delay(2000, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SetStatus(string text, string? error = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LastError = error;
            if (Status == text) return;
            Status = text; Changed?.Invoke();
        });
    }
}

internal sealed class DeviceAudioPlayer : IDisposable
{
    private readonly DeviceAudioBuffer buffer;
    private readonly CancellationTokenSource interrupted = new();
    public bool Interrupted => interrupted.IsCancellationRequested;
#if ANDROID
    private AudioTrack? track;
    private AudioManager? manager;
    private FocusListener? listener;
#endif
    public DeviceAudioPlayer(DeviceAudioBuffer buffer) => this.buffer = buffer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if ANDROID
        manager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
        listener = new FocusListener(() => interrupted.Cancel());
#pragma warning disable CS0618, CA1422 // Preserve the supported API 21 audio path; these legacy APIs remain callable.
        if (manager is null || manager.RequestAudioFocus(listener, Android.Media.Stream.Music, AudioFocus.Gain) != AudioFocusRequest.Granted)
            throw new InvalidOperationException("手机未允许当前音频输出");
        int minimum = AudioTrack.GetMinBufferSize(DeviceAudioBuffer.SampleRate, ChannelOut.Mono, Android.Media.Encoding.Pcm16bit);
        if (minimum <= 0) throw new InvalidOperationException("手机不支持实时音频格式");
        track = new AudioTrack(Android.Media.Stream.Music, DeviceAudioBuffer.SampleRate,
            ChannelConfiguration.Mono, Android.Media.Encoding.Pcm16bit, Math.Max(minimum, 2048), AudioTrackMode.Stream);
#pragma warning restore CS0618, CA1422
        if (track.State != AudioTrackState.Initialized) throw new InvalidOperationException("手机音频设备初始化失败");
        track.Play();
#endif
        return Task.CompletedTask;
    }

    public Task RunAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
#if ANDROID
        var samples = new short[256];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            interrupted.Token.ThrowIfCancellationRequested();
            buffer.Render(samples, Environment.TickCount64);
            int offset = 0;
            while (offset < samples.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = track!.Write(samples, offset, samples.Length - offset);
                if (count <= 0) throw new InvalidOperationException("手机音频输出中断");
                offset += count;
            }
        }
#endif
    }, cancellationToken);

    public void Dispose()
    {
        buffer.Reset();
#if ANDROID
        if (track is not null)
        {
            try { track.Stop(); track.Flush(); }
            catch (Java.Lang.IllegalStateException) { }
            track.Release(); track.Dispose(); track = null;
        }
#pragma warning disable CS0618, CA1422 // Same API 21 focus registration is released here.
        if (manager is not null && listener is not null) manager.AbandonAudioFocus(listener);
#pragma warning restore CS0618, CA1422
        listener?.Dispose(); manager?.Dispose();
#endif
        interrupted.Dispose();
    }
#if ANDROID
    private sealed class FocusListener(Action lost) : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus change) { if ((int)change < 0) lost(); }
    }
#endif
}
