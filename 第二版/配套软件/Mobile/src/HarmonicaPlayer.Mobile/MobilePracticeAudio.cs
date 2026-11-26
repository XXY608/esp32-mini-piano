using HarmonicaPlayer;
#if ANDROID
using Android.Media;
#endif

namespace HarmonicaPlayer.Mobile;

public interface IPracticeAudio : IDisposable
{
    void Play(ScoreNote note, AudioInstrument instrument, double volume, int durationMilliseconds = 260);
    void Enqueue(ScoreNote note, AudioInstrument instrument, double volume, int durationMilliseconds = 260);
    Task PlaySequenceAsync(
        IReadOnlyList<ScoreNote> notes,
        AudioInstrument instrument,
        int bpm,
        double volume,
        CancellationToken cancellationToken = default);
    void Stop();
}

public static class PracticeAudioFactory
{
    public static IPracticeAudio Create()
    {
#if ANDROID
        return new AndroidPracticeAudio();
#else
        return new SilentPracticeAudio();
#endif
    }
}

internal sealed class SilentPracticeAudio : IPracticeAudio
{
    public void Play(ScoreNote note, AudioInstrument instrument, double volume, int durationMilliseconds = 260) { }
    public void Enqueue(ScoreNote note, AudioInstrument instrument, double volume, int durationMilliseconds = 260) { }
    public Task PlaySequenceAsync(IReadOnlyList<ScoreNote> notes, AudioInstrument instrument, int bpm, double volume,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Stop() { }
    public void Dispose() { }
}

#if ANDROID
internal sealed class AndroidPracticeAudio : IPracticeAudio
{
    private const int SampleRate = LocalAudioRenderer.SampleRate;
    private const int NoteGapMilliseconds = 20;
    private readonly object gate = new();
    private readonly Queue<QueuedNote> queuedNotes = new();
    private readonly SemaphoreSlim queueSignal = new(0);
    private readonly CancellationTokenSource queueLifetime = new();
    private CancellationTokenSource queuedPlaybackCancellation = new();
    private readonly Task queueWorker;
    private AudioTrack? track;
    private CancellationTokenSource? sequenceCancellation;
    private bool disposed;

    public AndroidPracticeAudio()
    {
        queueWorker = Task.Run(ProcessQueueAsync);
    }

    public void Play(ScoreNote note, AudioInstrument instrument, double volume, int durationMilliseconds = 260)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Stop();
        if (note.Degree == 0) return;

        PlayImmediate(note, instrument, volume, durationMilliseconds);
    }

    public void Enqueue(ScoreNote note, AudioInstrument instrument, double volume, int durationMilliseconds = 260)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (note.Degree == 0) return;
        if (durationMilliseconds is < 40 or > PracticeTiming.MaxDurationMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));

        lock (gate) queuedNotes.Enqueue(new QueuedNote(note, instrument, volume, durationMilliseconds));
        queueSignal.Release();
    }

    private void PlayImmediate(ScoreNote note, AudioInstrument instrument, double volume, int durationMilliseconds)
    {
        short[] samples = CreateTone(note, instrument, durationMilliseconds);
        AudioTrack next = CreateTrack(samples.Length * sizeof(short));
        next.SetStereoVolume((float)Math.Clamp(volume, 0, 1), (float)Math.Clamp(volume, 0, 1));
        next.Write(samples, 0, samples.Length);
        AudioTrack? oldTrack;
        lock (gate)
        {
            oldTrack = track;
            track = next;
        }
        ReleaseTrack(oldTrack);
        next.Play();
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
                lock (gate)
                {
                    if (queuedNotes.Count == 0) continue;
                    queued = queuedNotes.Dequeue();
                    cancellationToken = queuedPlaybackCancellation.Token;
                    if (cancellationToken.IsCancellationRequested) continue;
                    PlayImmediate(queued.Note, queued.Instrument, queued.Volume, queued.DurationMilliseconds);
                }

                try
                {
                    await Task.Delay(queued.DurationMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Stop() interrupts the current queued note and clears the rest.
                }
            }
        }
        catch (OperationCanceledException) when (queueLifetime.IsCancellationRequested)
        {
        }
    }

    public async Task PlaySequenceAsync(
        IReadOnlyList<ScoreNote> notes,
        AudioInstrument instrument,
        int bpm,
        double volume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (notes.Count == 0) return;
        if (bpm is < 20 or > 300) throw new ArgumentOutOfRangeException(nameof(bpm));

        Stop();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (gate) sequenceCancellation = linked;
        AudioTrack? localTrack = null;
        try
        {
            short[] samples = await Task.Run(
                () => CreateSequence(notes, instrument, bpm, linked.Token), linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            localTrack = CreateTrack(samples.Length * sizeof(short));
            localTrack.SetStereoVolume((float)Math.Clamp(volume, 0, 1), (float)Math.Clamp(volume, 0, 1));
            localTrack.Write(samples, 0, samples.Length);
            lock (gate)
            {
                if (linked.IsCancellationRequested) return;
                track = localTrack;
            }
            localTrack.Play();
            int totalMilliseconds = Math.Clamp(
                (int)Math.Round(notes.Sum(note => note.Beats) * 60000d / bpm, MidpointRounding.AwayFromZero),
                1, 15 * 60 * 1000);
            await Task.Delay(totalMilliseconds + 40, linked.Token);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(sequenceCancellation, linked)) sequenceCancellation = null;
                if (ReferenceEquals(track, localTrack)) track = null;
            }
            ReleaseTrack(localTrack);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        AudioTrack? oldTrack;
        lock (gate)
        {
            queuedNotes.Clear();
            queuedPlaybackCancellation.Cancel();
            queuedPlaybackCancellation = new CancellationTokenSource();
            cancellation = sequenceCancellation;
            oldTrack = cancellation is null ? track : null;
            if (cancellation is null) track = null;
        }
        cancellation?.Cancel();
        ReleaseTrack(oldTrack);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        queueLifetime.Cancel();
        Stop();
    }

    private static AudioTrack CreateTrack(int bytes)
    {
        return new AudioTrack(
            Android.Media.Stream.Music,
            SampleRate,
            ChannelConfiguration.Mono,
            Encoding.Pcm16bit,
            Math.Max(bytes, 4096),
            AudioTrackMode.Static);
    }

    private static short[] CreateTone(ScoreNote note, AudioInstrument instrument, int durationMilliseconds)
    {
        int frames = Math.Max(1, FramesFor(durationMilliseconds / 1000d));
        var samples = new short[frames];
        RenderTone(samples, 0, frames, note, instrument);
        return samples;
    }

    private static short[] CreateSequence(
        IReadOnlyList<ScoreNote> notes,
        AudioInstrument instrument,
        int bpm,
        CancellationToken cancellationToken)
    {
        double beatSeconds = 60d / bpm;
        double totalSeconds = notes.Sum(note => note.Beats) * beatSeconds;
        if (!double.IsFinite(totalSeconds) || totalSeconds <= 0 || totalSeconds > 15 * 60)
            throw new FormatException("手机播放单次最多支持 15 分钟。");

        long totalFrames = notes.Sum(note => (long)FramesFor(note.Beats * beatSeconds));
        if (totalFrames > int.MaxValue) throw new FormatException("手机音频文件过大。");
        var output = new short[(int)totalFrames];
        int cursor = 0;
        foreach (ScoreNote note in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int noteFrames = FramesFor(note.Beats * beatSeconds);
            int gapFrames = note.Degree == 0
                ? noteFrames
                : Math.Min(noteFrames - 1, FramesFor(NoteGapMilliseconds / 1000d));
            int activeFrames = noteFrames - gapFrames;
            if (note.Degree != 0) RenderTone(output, cursor, activeFrames, note, instrument);
            cursor += noteFrames;
        }
        return output;
    }

    private static void RenderTone(short[] output, int offset, int frames, ScoreNote note, AudioInstrument instrument)
    {
        double frequency = LocalAudioRenderer.FrequencyOf(note);
        double phase = 0;
        double durationSeconds = frames / (double)SampleRate;
        for (int index = 0; index < frames; index++)
        {
            double time = index / (double)SampleRate;
            phase += 2d * Math.PI * frequency * AudioInstrumentSynthesizer.PitchMultiplier(instrument, time) / SampleRate;
            double wave = AudioInstrumentSynthesizer.Sample(instrument, phase, time);
            double envelope = AudioInstrumentSynthesizer.Envelope(instrument, time, durationSeconds);
            output[offset + index] = (short)Math.Clamp(
                wave * envelope * AudioInstrumentSynthesizer.Gain(instrument), short.MinValue, short.MaxValue);
        }
    }

    private static int FramesFor(double seconds) =>
        Math.Max(1, (int)Math.Round(seconds * SampleRate, MidpointRounding.AwayFromZero));

    private static void ReleaseTrack(AudioTrack? value)
    {
        if (value is null) return;
        try { value.Stop(); } catch (Exception) { }
        // Static tracks can retain already-written PCM after Stop(). Flush it
        // before releasing the track so a rapid sequence of touch notes cannot
        // leak the previous notes into the next interaction.
        try { value.Flush(); } catch (Exception) { }
        value.Release();
        value.Dispose();
    }

    private readonly record struct QueuedNote(
        ScoreNote Note,
        AudioInstrument Instrument,
        double Volume,
        int DurationMilliseconds);
}
#endif
