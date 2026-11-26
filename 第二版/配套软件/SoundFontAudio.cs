using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HarmonicaPlayer;

// Windows-only SoundFont renderer. Banks stay loaded; each playback owns the
// synth lock so practice and score rendering cannot change each other's notes.
public sealed class SoundFontAudio : IDisposable
{
    private const string Library = "piano-fluidsynth";
    private static readonly object Gate = new();
    private static readonly Dictionary<AudioInstrument, (IntPtr Synth, IntPtr Settings, int Bank)> Synths = new();
    private sealed record HallPreset(double Gain, double RoomSize, double Damping, double Width, double Level,
        double OutputGain, double StereoWidth, double BassGain, double TrebleGain, double TailSeconds);
    private static readonly Lazy<HallPreset> Hall = new(() =>
        JsonSerializer.Deserialize<HallPreset>(File.ReadAllText(Path.Combine(AssetDirectory, "Piano-Hall.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new FormatException("音乐厅音色包无效。"));
    private readonly IntPtr synth;
    private readonly HallPreset? voicing;
    private bool disposed;
    private readonly float[] left = new float[4096], right = new float[4096];
    private double bassLeft, bassRight, smoothLeft, smoothRight;
    private static readonly double BassStep = 1 - Math.Exp(-2 * Math.PI * 230 / LocalAudioRenderer.SampleRate);
    private static readonly double TrebleStep = 1 - Math.Exp(-2 * Math.PI * 2500 / LocalAudioRenderer.SampleRate);

    static SoundFontAudio()
    {
        NativeLibrary.SetDllImportResolver(typeof(SoundFontAudio).Assembly, (name, assembly, search) =>
            name == Library ? NativeLibrary.Load(Path.Combine(AssetDirectory, "fluidsynth", "bin", "libfluidsynth-3.dll"),
                assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32) : IntPtr.Zero);

    }

    public static string AssetDirectory
    {
        get
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "work", "soundfonts");
                if (File.Exists(Path.Combine(candidate, "GeneralUser-GS.sf2"))) return candidate;
            }
            throw new FileNotFoundException("缺少社区音源，请将完整 work/soundfonts 文件夹放在电子琴旁。");
        }
    }

    public static bool UsesSamples(AudioInstrument instrument) => instrument != AudioInstrument.LegacyPiano;

    public SoundFontAudio(AudioInstrument instrument)
    {
        Monitor.Enter(Gate);
        try
        {
            instrument = AudioInstrumentCatalog.Normalize(instrument);
            var bankType = instrument is AudioInstrument.SalamanderPiano or AudioInstrument.HallPiano ? instrument : AudioInstrument.Piano;
            bool hall = bankType == AudioInstrument.HallPiano;
            voicing = hall ? Hall.Value : null;
            if (!Synths.TryGetValue(bankType, out var item))
            {
                IntPtr settings = new_fluid_settings();
                IntPtr created = IntPtr.Zero;
                try
                {
                    fluid_settings_setnum(settings, "synth.sample-rate", LocalAudioRenderer.SampleRate);
                    fluid_settings_setnum(settings, "synth.gain", hall ? Hall.Value.Gain : 0.65);
                    fluid_settings_setint(settings, "synth.reverb.active", hall ? 1 : 0);
                    fluid_settings_setint(settings, "synth.chorus.active", 0);
                    fluid_settings_setint(settings, "synth.cpu-cores", 1);
                    created = new_fluid_synth(settings);
                    if (created == IntPtr.Zero) throw new InvalidOperationException("无法初始化社区音源播放器。");
                    if (hall)
                    {
                        var preset = Hall.Value;
                        if (fluid_synth_set_reverb_group_roomsize(created, -1, preset.RoomSize) != 0 ||
                            fluid_synth_set_reverb_group_damp(created, -1, preset.Damping) != 0 ||
                            fluid_synth_set_reverb_group_width(created, -1, preset.Width) != 0 ||
                            fluid_synth_set_reverb_group_level(created, -1, preset.Level) != 0)
                            throw new FormatException("音乐厅音色包的混响参数超出范围。");
                    }
                    string path = Path.Combine(AssetDirectory, bankType == AudioInstrument.Piano ? "GeneralUser-GS.sf2" : "Salamander.sf2");
                    if (!File.Exists(path)) throw new FileNotFoundException("缺少音源文件：" + path);
                    int bank = fluid_synth_sfload(created, path, 1);
                    if (bank < 0) throw new InvalidOperationException("无法载入音源：" + path);
                    item = (created, settings, bank);
                    Synths.Add(bankType, item);
                }
                catch { if (created != IntPtr.Zero) delete_fluid_synth(created); delete_fluid_settings(settings); throw; }
            }
            synth = item.Synth;
            int program = instrument switch
            {
                AudioInstrument.Harmonica => 22, AudioInstrument.Flute => 73,
                AudioInstrument.Violin => 40, AudioInstrument.Guitar => 24,
                AudioInstrument.Organ => 19, AudioInstrument.MusicBox => 10, _ => 0
            };
            for (int channel = 0; channel < 16; channel++)
            {
                if (channel == 9) continue;
                fluid_synth_all_sounds_off(synth, channel);
                fluid_synth_cc(synth, channel, 64, 0);
                if (fluid_synth_program_select(synth, channel, item.Bank, 0, program) != 0)
                    throw new InvalidOperationException("音源没有所选乐器。");
                if (hall) fluid_synth_cc(synth, channel, 91, 52);
            }
        }
        catch { Monitor.Exit(Gate); throw; }
    }

    public void NoteOn(ScoreNote note) => fluid_synth_noteon(synth, 0, MidiKey(note), 100);
    public void NoteOff(ScoreNote note) => fluid_synth_noteoff(synth, 0, MidiKey(note));
    public void NoteOn(int channel, int midi, int velocity) => fluid_synth_noteon(synth, channel, midi, velocity);
    public void NoteOff(int channel, int midi) => fluid_synth_noteoff(synth, channel, midi);
    public void Sustain(bool down)
    {
        for (int channel = 0; channel < 16; channel++)
            if (channel != 9) fluid_synth_cc(synth, channel, 64, down ? 127 : 0);
    }
    public static double ReleaseSeconds(AudioInstrument instrument) => instrument == AudioInstrument.HallPiano ? Hall.Value.TailSeconds : 0.9;
    private static int MidiKey(ScoreNote note) => 60 + new[] { 0, 2, 4, 5, 7, 9, 11 }[note.Degree - 1] + 12 * note.Octave + (note.Sharp ? 1 : 0);

    public void Render(long frames, Action<short> write, CancellationToken cancellationToken = default, bool fadeEnd = false)
        => RenderChannels(frames, (l, r, fade) => write(ToPcm((l + r) * 0.5 * fade)), cancellationToken, fadeEnd);

    public void RenderStereo(long frames, Action<short, short> write, CancellationToken cancellationToken = default, bool fadeEnd = false)
        => RenderChannels(frames, (l, r, fade) => write(ToPcm(l * fade), ToPcm(r * fade)), cancellationToken, fadeEnd);

    private static short ToPcm(double value) => (short)Math.Clamp(value * 32767, short.MinValue, short.MaxValue);

    private void RenderChannels(long frames, Action<float, float, double> write, CancellationToken cancellationToken, bool fadeEnd)
    {
        long remaining = frames;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(left.Length, remaining);
            if (fluid_synth_write_float(synth, count, left, 0, 1, right, 0, 1) != 0)
                throw new InvalidOperationException("社区音源生成音频失败。");
            for (int i = 0; i < count; i++)
            {
                double fade = fadeEnd ? Math.Min(1, (remaining - i - 1) / 220d) : 1;
                float l = left[i], r = right[i];
                if (voicing is { } preset)
                {
                    double a = Shape(l, ref bassLeft, ref smoothLeft, preset);
                    double b = Shape(r, ref bassRight, ref smoothRight, preset);
                    double mid = (a + b) * 0.5 * preset.OutputGain;
                    double side = (a - b) * 0.5 * preset.OutputGain * preset.StereoWidth;
                    l = Limit(mid + side);
                    r = Limit(mid - side);
                }
                write(l, r, fade);
            }
            remaining -= count;
        }
    }

    private static double Shape(double sample, ref double bass, ref double smooth, HallPreset preset)
    {
        bass += (sample - bass) * BassStep;
        smooth += (sample - smooth) * TrebleStep;
        return sample + (preset.BassGain - 1) * bass + (preset.TrebleGain - 1) * (sample - smooth);
    }

    private static float Limit(double sample)
    {
        double excess = Math.Max(0, Math.Abs(sample) - 0.75);
        return (float)(excess == 0 ? sample : Math.CopySign(0.75 + excess / (1 + 4 * excess), sample));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (int channel = 0; channel < 16; channel++) fluid_synth_all_sounds_off(synth, channel);
        Monitor.Exit(Gate);
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr new_fluid_settings();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void delete_fluid_settings(IntPtr settings);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_settings_setnum(IntPtr settings, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, double value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_settings_setint(IntPtr settings, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr new_fluid_synth(IntPtr settings);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void delete_fluid_synth(IntPtr synth);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_sfload(IntPtr synth, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int reset);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_program_select(IntPtr synth, int channel, int bank, int bankNumber, int preset);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_noteon(IntPtr synth, int channel, int key, int velocity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_noteoff(IntPtr synth, int channel, int key);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_cc(IntPtr synth, int channel, int controller, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_set_reverb_group_roomsize(IntPtr synth, int group, double value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_set_reverb_group_damp(IntPtr synth, int group, double value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_set_reverb_group_width(IntPtr synth, int group, double value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_set_reverb_group_level(IntPtr synth, int group, double value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_all_sounds_off(IntPtr synth, int channel);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int fluid_synth_write_float(IntPtr synth, int frames, [Out] float[] left, int offsetLeft, int incrementLeft, [Out] float[] right, int offsetRight, int incrementRight);
}

