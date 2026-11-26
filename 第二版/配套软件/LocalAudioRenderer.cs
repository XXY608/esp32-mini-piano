using System.Buffers;
using System.IO;
using System.Text;

namespace HarmonicaPlayer;

public sealed record LocalAudioRenderInfo(double DurationSeconds, int SampleRate, long SampleCount);

/// <summary>
/// Renders the score to a self-contained PCM WAV file. This is deliberately
/// separate from NativeInput: the local-audio mode never sends keyboard or
/// mouse input to another window.
/// </summary>
public static class LocalAudioRenderer
{
    public const int SampleRate = 44_100;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const double MaxDurationSeconds = 15 * 60;
    private const double BaseFrequency = 261.6255653005986; // C4; score 1 is C4 by default.
    private static readonly int[] MajorScaleSemitones = { 0, 2, 4, 5, 7, 9, 11 };

    public static LocalAudioRenderInfo RenderToWaveFile(
        string path,
        IReadOnlyList<ScoreNote> notes,
        int bpm,
        int gapMs,
        CancellationToken cancellationToken = default,
        AudioInstrument instrument = AudioInstrument.Harmonica)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(notes);
        if (notes.Count == 0) throw new FormatException("没有可发声的音符。");
        if (bpm is < 20 or > 300) throw new FormatException("本机发声 BPM 范围为 20～300。");
        if (gapMs is < 10 or > 5000) throw new FormatException("本机发声留白范围为 10～5000 毫秒。");

        double beatSeconds = 60d / bpm;
        double totalBeats = notes.Sum(note => note.Beats);
        double durationSeconds = totalBeats * beatSeconds;
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new FormatException("谱面总时长无效。");
        if (durationSeconds > MaxDurationSeconds)
            throw new FormatException($"本机发声单次最多支持 {MaxDurationSeconds / 60:0} 分钟，请拆分谱面后播放。");
        if (notes.Any(note => note.Degree != 0 && note.Beats * beatSeconds * 1000d - gapMs < 40))
            throw new FormatException("最短发声音符减去留白后须至少 40 毫秒，请降低 BPM 或减少留白。");

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        file.Write(new byte[44]); // RIFF/WAVE header is patched after PCM is written.
        using var pcm = new PcmWriter(file);

#if PIANO_SOUNDFONTS
        using var sampled = SoundFontAudio.UsesSamples(instrument) ? new SoundFontAudio(instrument) : null;
        int channels = instrument == AudioInstrument.HallPiano ? 2 : Channels;
        void RenderSampled(long frames, bool fadeEnd = false)
        {
            if (channels == 2) sampled!.RenderStereo(frames, pcm.WriteStereo, cancellationToken, fadeEnd);
            else sampled!.Render(frames, pcm.WriteSample, cancellationToken, fadeEnd);
        }
#else
        int channels = Channels;
#endif
        double cursorSeconds = 0;
        foreach (ScoreNote note in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long totalFrames = FramesFor(note.Beats * beatSeconds);
            if (note.Degree == 0)
            {
#if PIANO_SOUNDFONTS
                if (sampled != null) RenderSampled(totalFrames,
                    fadeEnd: ReferenceEquals(note, notes[^1]));
                else
#endif
                    pcm.WriteSilence(totalFrames);
            }
            else
            {
                long gapFrames = Math.Min(totalFrames - 1, FramesFor(gapMs / 1000d));
                long activeFrames = totalFrames - gapFrames;
#if PIANO_SOUNDFONTS
                if (sampled != null)
                {
                    sampled.NoteOn(note);
                    RenderSampled(activeFrames);
                    sampled.NoteOff(note);
                    RenderSampled(gapFrames, fadeEnd: ReferenceEquals(note, notes[^1]));
                }
                else
#endif
                {
                    RenderTone(pcm, note, activeFrames, instrument, cancellationToken);
                    pcm.WriteSilence(gapFrames);
                }
            }
            cursorSeconds += note.Beats * beatSeconds;
        }

        WriteHeader(file, pcm, channels);
        return new LocalAudioRenderInfo(cursorSeconds, SampleRate, pcm.SampleCount);
    }

#if PIANO_SOUNDFONTS
    private enum EventKind { Pedal, NoteOff, NoteOn }
    private readonly record struct ScheduledEvent(long Frame, EventKind Kind, int NoteIndex, bool PedalDown = false);

    public static LocalAudioRenderInfo RenderPerformanceToWaveFile(string path, ImportedScore arrangement, int bpm,
        CancellationToken token, AudioInstrument instrument)
    {
        if (arrangement.Performance is not { Count: > 0 } notes) throw new FormatException("源谱没有可播放的多音数据。");
        if (bpm is < 20 or > 300) throw new FormatException("本机发声 BPM 范围为 20～300。");
        if (!SoundFontAudio.UsesSamples(instrument)) throw new FormatException("原合成钢琴只支持简谱，请为多音源谱选择采样音色。");
        double beatSeconds = 60d / bpm;
        double duration = notes.Max(n => n.StartBeats + n.DurationBeats) * beatSeconds;
        if (!double.IsFinite(duration) || duration <= 0 || duration > MaxDurationSeconds)
            throw new FormatException("多音谱时长无效，或超过本机单次播放上限 15 分钟。");
        var events = new List<ScheduledEvent>(notes.Count * 2);
        for (int index = 0; index < notes.Count; index++)
        {
            var note = notes[index];
            if (note.IsRest) continue;
            long start = note.StartBeats == 0 ? 0 : FramesFor(note.StartBeats * beatSeconds);
            long end = Math.Max(start + 1, FramesFor((note.StartBeats + note.DurationBeats) * beatSeconds));
            events.Add(new ScheduledEvent(start, EventKind.NoteOn, index));
            events.Add(new ScheduledEvent(end, EventKind.NoteOff, index));
        }
        if (arrangement.SustainPedal is { } pedals)
            foreach (var pedal in pedals)
                if (pedal.Beat >= 0 && pedal.Beat * beatSeconds <= duration)
                    events.Add(new ScheduledEvent(pedal.Beat == 0 ? 0 : FramesFor(pedal.Beat * beatSeconds), EventKind.Pedal, 0, pedal.Down));

        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        file.Write(new byte[44]);
        using var pcm = new PcmWriter(file);
        using var sampled = new SoundFontAudio(instrument);
        var channels = new int[notes.Count];
        var active = new int[128];
        long cursor = 0;
        foreach (var transition in events.OrderBy(e => e.Frame).ThenBy(e => e.Kind))
        {
            token.ThrowIfCancellationRequested();
            if (transition.Frame > cursor)
            {
                sampled.RenderStereo(transition.Frame - cursor, pcm.WriteStereo, token);
                cursor = transition.Frame;
            }
            if (transition.Kind == EventKind.Pedal)
            {
                sampled.Sustain(transition.PedalDown);
                continue;
            }
            var note = notes[transition.NoteIndex];
            int channel = channels[transition.NoteIndex];
            if (transition.Kind == EventKind.NoteOn)
            {
                // Different pitches can share a MIDI channel. Overlapping copies
                // of the same pitch need independent note-off messages.
                int occupied = active[note.MidiPitch] | (1 << 9);
                while ((occupied & (1 << channel)) != 0) channel++;
                if (channel >= 16) throw new FormatException("同一音高的重叠音符过多。");
                channels[transition.NoteIndex] = channel;
                active[note.MidiPitch] |= 1 << channel;
                sampled.NoteOn(channel, note.MidiPitch, note.Velocity);
            }
            else
            {
                sampled.NoteOff(channel, note.MidiPitch);
                active[note.MidiPitch] &= ~(1 << channel);
            }
        }
        sampled.Sustain(false);
        sampled.RenderStereo(FramesFor(SoundFontAudio.ReleaseSeconds(instrument)), pcm.WriteStereo, token, fadeEnd: true);
        WriteHeader(file, pcm, 2);
        return new LocalAudioRenderInfo(pcm.SampleCount / (2d * SampleRate), SampleRate, pcm.SampleCount);
    }
#endif

    private static void WriteHeader(FileStream file, PcmWriter pcm, int channels)
    {
        pcm.Flush();
        long dataBytes = pcm.SampleCount * sizeof(short);
        if (dataBytes > uint.MaxValue - 36)
            throw new FormatException("生成的音频文件过大。");

        file.Position = 0;
        using (var header = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true))
        {
            header.Write(Encoding.ASCII.GetBytes("RIFF"));
            header.Write((uint)(36 + dataBytes));
            header.Write(Encoding.ASCII.GetBytes("WAVE"));
            header.Write(Encoding.ASCII.GetBytes("fmt "));
            header.Write(16u); // PCM fmt chunk size
            header.Write((ushort)1); // PCM
            header.Write((ushort)channels);
            header.Write(SampleRate);
            header.Write(SampleRate * channels * BitsPerSample / 8);
            header.Write((ushort)(channels * BitsPerSample / 8));
            header.Write((ushort)BitsPerSample);
            header.Write(Encoding.ASCII.GetBytes("data"));
            header.Write((uint)dataBytes);
            header.Flush();
        }
        file.Flush(flushToDisk: true);
    }

    public static double FrequencyOf(ScoreNote note)
    {
        if (note.Degree is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(note));
        int semitones = MajorScaleSemitones[note.Degree - 1] + note.Octave * 12 + (note.Sharp ? 1 : 0);
        return BaseFrequency * Math.Pow(2, semitones / 12d);
    }

    private static long FramesFor(double seconds) => Math.Max(1, (long)Math.Round(seconds * SampleRate, MidpointRounding.AwayFromZero));

    private static void RenderTone(
        PcmWriter pcm,
        ScoreNote note,
        long frames,
        AudioInstrument instrument,
        CancellationToken cancellationToken)
    {
        double frequency = FrequencyOf(note);
        double phase = 0;
        double durationSeconds = frames / (double)SampleRate;

        for (long i = 0; i < frames; i++)
        {
            if ((i & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            double t = i / (double)SampleRate;
            phase += 2d * Math.PI * frequency * AudioInstrumentSynthesizer.PitchMultiplier(instrument, t) / SampleRate;
            double wave = AudioInstrumentSynthesizer.Sample(instrument, phase, t);
            double envelope = AudioInstrumentSynthesizer.Envelope(instrument, t, durationSeconds);
            pcm.WriteSample((short)Math.Clamp(
                wave * envelope * AudioInstrumentSynthesizer.Gain(instrument), short.MinValue, short.MaxValue));
        }
    }

    private sealed class PcmWriter : IDisposable
    {
        private readonly FileStream file;
        private readonly byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        private int buffered;
        private bool disposed;

        public PcmWriter(FileStream file) => this.file = file;
        public long SampleCount { get; private set; }

        public void WriteSample(short sample)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PcmWriter));
            if (buffered + sizeof(short) > buffer.Length) Flush();
            buffer[buffered++] = (byte)sample;
            buffer[buffered++] = (byte)(sample >> 8);
            SampleCount++;
        }

        public void WriteStereo(short left, short right)
        {
            WriteSample(left);
            WriteSample(right);
        }

        public void WriteSilence(long samples)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PcmWriter));
            Flush();
            Array.Clear(buffer);
            while (samples > 0)
            {
                int bytes = (int)Math.Min(buffer.Length, samples * sizeof(short));
                bytes -= bytes % sizeof(short);
                if (bytes == 0) bytes = sizeof(short);
                file.Write(buffer, 0, bytes);
                samples -= bytes / sizeof(short);
                SampleCount += bytes / sizeof(short);
            }
        }

        public void Flush()
        {
            if (buffered == 0) return;
            file.Write(buffer, 0, buffered);
            buffered = 0;
        }

        public void Dispose()
        {
            if (disposed) return;
            Flush();
            ArrayPool<byte>.Shared.Return(buffer);
            disposed = true;
        }
    }
}
