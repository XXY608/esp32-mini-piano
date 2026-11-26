using NAudio.Wave;

namespace HarmonicaPlayer;

// Continuous output lets note-off and replacement ramp the current waveform to zero.
internal sealed class SimulatorAudio : ISampleProvider, IDisposable
{
    private sealed class Voice(float[] samples, bool sustain, int slot = -1, int midi = -1) { public float[] Samples = samples; public bool Sustain = sustain; public int Slot = slot, Midi = midi; public int Position; public float Gain; public bool Releasing; }
    private readonly object gate = new();
    private readonly WaveOutEvent? output;
    private readonly Dictionary<(ScoreNote, AudioInstrument), float[]> cache = new();
    private readonly List<Voice> voices = new();
    private AudioInstrument instrument;
    private float volume = 1;
    private bool manualMode;
    private int[] manualNotes = Array.Empty<int>();
    private int manualLevel = 100;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);

    public SimulatorAudio(AudioInstrument instrument, bool startOutput = true)
    {
        this.instrument = instrument;
        if (!startOutput) return;
        output = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3 };
        output.Init(this);
        output.Play();
    }
    public void SetInstrument(AudioInstrument value) { lock (gate) { Release(); instrument = value; if (manualMode) SetManualNotes(manualNotes, manualLevel); } }
    public void SetVolume(int value) { lock (gate) volume = Math.Clamp(value, 0, 100) / 100f; }
    public void Play(ScoreNote note, int durationMs, bool sustain = false)
    {
        float[] samples;
        lock (gate)
        {
            var key = (note, instrument);
            if (durationMs == 8000 && cache.TryGetValue(key, out var cached)) samples = cached;
            else
            {
                byte[] wave = PracticeAudioEngine.CreateToneWave(note, durationMs, instrument);
                samples = new float[(wave.Length - 44) / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(wave, 44 + i * 2) / 32768f;
                if (durationMs == 8000) cache[key] = samples;
            }
            Release();
            manualMode = false;
            voices.Add(new Voice(samples, sustain && durationMs == 8000));
        }
    }
    private void Release() { foreach (var voice in voices) voice.Releasing = true; }
    public void Stop() { lock (gate) { Release(); manualMode = false; } }
    public void SetManualNotes(int[] notes, int level)
    {
        lock (gate)
        {
            if (!manualMode) { Release(); manualMode = true; }
            manualNotes = (int[])notes.Clone(); manualLevel = level;
            int active = notes.Count(midi => midi >= 36 && midi <= 96);
            volume = Math.Clamp(level, 0, 100) / (100f * Math.Max(1, active));
            for (int slot = 0; slot < 7; slot++)
            {
                int midi = slot < notes.Length ? notes[slot] : -1;
                var current = voices.LastOrDefault(voice => !voice.Releasing && voice.Slot == slot);
                if (current?.Midi == midi) continue;
                if (current != null) current.Releasing = true;
                if (midi < 36 || midi > 96) continue;
                var note = MobileSimulatorWindow.NoteFromMidi(midi);
                var key = (note, instrument);
                if (!cache.TryGetValue(key, out var samples))
                {
                    byte[] wave = PracticeAudioEngine.CreateToneWave(note, 8000, instrument);
                    samples = new float[(wave.Length - 44) / 2];
                    for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(wave, 44 + i * 2) / 32768f;
                    cache[key] = samples;
                }
                voices.Add(new Voice(samples, true, slot, midi));
            }
        }
    }
    public int Read(float[] buffer, int offset, int count)
    {
        lock (gate)
        {
            Array.Clear(buffer, offset, count);
            for (int i = 0; i < count; i++)
            {
                float sum = 0;
                for (int j = voices.Count - 1; j >= 0; j--)
                {
                    var voice = voices[j];
                    if (voice.Position >= voice.Samples.Length || (voice.Releasing && voice.Gain <= 0)) { voices.RemoveAt(j); continue; }
                    voice.Gain = Math.Clamp(voice.Gain + (voice.Releasing ? -1f / 441 : 1f / 220), 0, 1);
                                        float sample = voice.Samples[voice.Position];
                    if (voice.Sustain && !voice.Releasing)
                    {
                        const int loopStart = 2 * 44100, fade = 441;
                        int loopEnd = voice.Samples.Length - 4410;
                        if (voice.Position >= loopEnd - fade)
                        {
                            float blend = (voice.Position - (loopEnd - fade)) / (float)fade;
                            sample = sample * (1 - blend) + voice.Samples[loopStart + voice.Position - (loopEnd - fade)] * blend;
                        }
                        if (++voice.Position >= loopEnd) voice.Position = loopStart + fade;
                    }
                    else voice.Position++;
                    sum += sample * voice.Gain;
                }
                buffer[offset + i] = Math.Clamp(sum * volume, -1f, 1f);
            }
        }
        return count;
    }
    public void Dispose() { output?.Stop(); output?.Dispose(); }
}
