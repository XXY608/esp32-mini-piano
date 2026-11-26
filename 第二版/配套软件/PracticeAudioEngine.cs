using System.Media;
using System.IO;
using System.Text;

namespace HarmonicaPlayer;

/// <summary>
/// Short local note player for practice mode. It never calls SendInput and
/// keeps one preloaded PCM sound per supported pitch for low input latency.
/// </summary>
public sealed class PracticeAudioEngine : IDisposable
{
    public const int SampleRate = 44_100;
    public const int DefaultNoteDurationMs = 280;

    private readonly Dictionary<(int Pitch, int DurationMs), SoundPlayer> players = new();
    private readonly object queueGate = new();
    private readonly Queue<QueuedNote> queuedNotes = new();
    private readonly SemaphoreSlim queueSignal = new(0);
    private readonly CancellationTokenSource queueLifetime = new();
    private CancellationTokenSource queuedPlaybackCancellation = new();
    private readonly Task queueWorker;
    private bool disposed;
    private readonly bool preload;
    private int volume = 100;
    public AudioInstrument Instrument { get; private set; }

    public PracticeAudioEngine(AudioInstrument instrument = AudioInstrument.Harmonica, bool preload = true)
    {
        Instrument = AudioInstrumentCatalog.Normalize(instrument);
        this.preload = preload;
        if (preload) PreloadAll();
        queueWorker = Task.Run(ProcessQueueAsync);
    }

    public void SetInstrument(AudioInstrument instrument)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        AudioInstrument normalized = AudioInstrumentCatalog.Normalize(instrument);
        if (normalized == Instrument) return;
        Stop();
        foreach (SoundPlayer player in players.Values) player.Dispose();
        players.Clear();
        Instrument = normalized;
        if (preload) PreloadAll();
    }

    public void SetVolume(int value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        value = Math.Clamp(value, 0, 100);
        if (value == volume) return;
        Stop();
        foreach (SoundPlayer player in players.Values) player.Dispose();
        players.Clear();
        volume = value;
    }

    private void PreloadAll()
    {
        for (int octave = -1; octave <= 1; octave++)
        for (int degree = 1; degree <= 7; degree++)
        {
            Preload(new ScoreNote(degree, octave, false, 0));
            Preload(new ScoreNote(degree, octave, true, 0));
        }
    }

    public void Play(ScoreNote note, int durationMs = DefaultNoteDurationMs)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (note.Degree == 0) return;
        if (durationMs is < 40 or > PracticeTiming.MaxDurationMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        Stop();
        lock (queueGate) PlayImmediate(note, durationMs);
    }

    public void Enqueue(ScoreNote note, int durationMs)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(note);
        if (note.Degree == 0) return;
        if (durationMs is < 40 or > PracticeTiming.MaxDurationMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(durationMs));

        lock (queueGate) queuedNotes.Enqueue(new QueuedNote(note, durationMs));
        queueSignal.Release();
    }

    private void PlayImmediate(ScoreNote note, int durationMs)
    {
        int pitch = KeyOf(note);
        var cacheKey = (pitch, durationMs);
        if (!players.TryGetValue(cacheKey, out SoundPlayer? player))
        {
            Preload(note, durationMs);
            player = players[cacheKey];
        }
        foreach (SoundPlayer other in players.Values) other.Stop();
        player.Play();
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                await queueSignal.WaitAsync(queueLifetime.Token).ConfigureAwait(false);
                QueuedNote queued;
                CancellationToken cancellationToken;
                lock (queueGate)
                {
                    if (queuedNotes.Count == 0) continue;
                    queued = queuedNotes.Dequeue();
                    cancellationToken = queuedPlaybackCancellation.Token;
                    if (cancellationToken.IsCancellationRequested) continue;
                    PlayImmediate(queued.Note, queued.DurationMs);
                }

                try
                {
                    await Task.Delay(queued.DurationMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Stop() cancels the current queued note immediately.
                }
            }
        }
        catch (OperationCanceledException) when (queueLifetime.IsCancellationRequested)
        {
        }
    }

    public void Stop()
    {
        if (disposed) return;
        lock (queueGate)
        {
            queuedNotes.Clear();
            queuedPlaybackCancellation.Cancel();
            queuedPlaybackCancellation = new CancellationTokenSource();
            foreach (SoundPlayer player in players.Values) player.Stop();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        lock (queueGate)
        {
            disposed = true;
            queueLifetime.Cancel();
            queuedPlaybackCancellation.Cancel();
            queuedNotes.Clear();
            foreach (SoundPlayer player in players.Values)
            {
                player.Stop();
                player.Dispose();
            }
            players.Clear();
        }
    }

    private readonly record struct QueuedNote(ScoreNote Note, int DurationMs);

    public static byte[] CreateToneWave(
        ScoreNote note,
        int durationMs = DefaultNoteDurationMs,
        AudioInstrument instrument = AudioInstrument.Harmonica)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.Degree is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(note));
        if (note.Octave is < -2 or > 2) throw new ArgumentOutOfRangeException(nameof(note));
        if (durationMs is < 40 or > PracticeTiming.MaxDurationMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(durationMs));

        int frameCount = Math.Max(1, (int)Math.Round(SampleRate * durationMs / 1000d, MidpointRounding.AwayFromZero));
        int dataBytes = checked(frameCount * sizeof(short));
        using var stream = new MemoryStream(44 + dataBytes);
        using (var header = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            header.Write(Encoding.ASCII.GetBytes("RIFF"));
            header.Write(36 + dataBytes);
            header.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            header.Write(16);
            header.Write((short)1);
            header.Write((short)1);
            header.Write(SampleRate);
            header.Write(SampleRate * sizeof(short));
            header.Write((short)sizeof(short));
            header.Write((short)16);
            header.Write(Encoding.ASCII.GetBytes("data"));
            header.Write(dataBytes);
        }

        if (SoundFontAudio.UsesSamples(instrument))
        {
            using var sampled = new SoundFontAudio(instrument);
            using var sampledPcm = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            int releaseFrames = Math.Min(frameCount / 3, SampleRate / 10);
            sampled.NoteOn(note);
            sampled.Render(frameCount - releaseFrames, sampledPcm.Write);
            sampled.NoteOff(note);
            sampled.Render(releaseFrames, sampledPcm.Write, fadeEnd: true);
            return stream.ToArray();
        }

        instrument = AudioInstrumentCatalog.Normalize(instrument);
        double frequency = LocalAudioRenderer.FrequencyOf(note);
        double phase = 0;
        using var pcm = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        for (int index = 0; index < frameCount; index++)
        {
            double t = index / (double)SampleRate;
            phase += 2d * Math.PI * frequency * AudioInstrumentSynthesizer.PitchMultiplier(instrument, t) / SampleRate;
            double wave = AudioInstrumentSynthesizer.Sample(instrument, phase, t);
            double envelope = AudioInstrumentSynthesizer.Envelope(instrument, t, durationMs / 1000d);
            pcm.Write((short)Math.Clamp(
                wave * envelope * AudioInstrumentSynthesizer.Gain(instrument), short.MinValue, short.MaxValue));
        }
        return stream.ToArray();
    }

    private void Preload(ScoreNote note, int durationMs = DefaultNoteDurationMs)
    {
        var key = (KeyOf(note), durationMs);
        if (players.ContainsKey(key)) return;
        byte[] wave = CreateToneWave(note, durationMs, Instrument);
        if (volume != 100)
            for (int i = 44; i + 1 < wave.Length; i += 2)
            {
                short sample = (short)(wave[i] | wave[i + 1] << 8);
                short adjusted = (short)(sample * volume / 100);
                wave[i] = (byte)adjusted; wave[i + 1] = (byte)(adjusted >> 8);
            }
        var stream = new MemoryStream(wave, writable: false);
        var player = new SoundPlayer(stream);
        player.Load();
        players[key] = player;
    }

    private static int KeyOf(ScoreNote note) => ((note.Octave + 1) * 7 + note.Degree - 1) * 2 + (note.Sharp ? 1 : 0);
}
