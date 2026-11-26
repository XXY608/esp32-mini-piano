namespace HarmonicaPlayer;

public enum AudioInstrument
{
    Harmonica,
    Piano,
    Flute,
    Violin,
    Guitar,
    Organ,
    MusicBox,
    SalamanderPiano,
    LegacyPiano,
    HallPiano
}

public sealed record AudioInstrumentOption(AudioInstrument Value, string Name, string Description)
{
    public override string ToString() => Name;
}

public static class AudioInstrumentCatalog
{
    public static IReadOnlyList<AudioInstrumentOption> Options { get; } = new[]
    {
        new AudioInstrumentOption(AudioInstrument.Harmonica, "口琴", "明亮、带轻微颤音的簧片音色"),
        new AudioInstrumentOption(AudioInstrument.Piano, "钢琴", "起音清晰、逐渐衰减的键盘音色"),
        new AudioInstrumentOption(AudioInstrument.Flute, "长笛", "柔和、平滑的气流音色"),
        new AudioInstrumentOption(AudioInstrument.Violin, "小提琴", "持续性强、带弓弦颤音的音色"),
        new AudioInstrumentOption(AudioInstrument.Guitar, "木吉他", "拨弦起音、自然衰减的音色"),
        new AudioInstrumentOption(AudioInstrument.Organ, "管风琴", "持续、饱满的多泛音音色"),
        new AudioInstrumentOption(AudioInstrument.MusicBox, "八音盒", "清脆、短促的金属音色")
    };

    public static IReadOnlyList<AudioInstrumentOption> WindowsOptions { get; } = Options
        .Select(option => new AudioInstrumentOption(option.Value,
            option.Value == AudioInstrument.Piano ? "钢琴 · GS" : option.Name,
            "GeneralUser GS 社区音源"))
        .Concat(new[] {
            new AudioInstrumentOption(AudioInstrument.SalamanderPiano, "钢琴 · Salamander", "Yamaha C5 三角钢琴真实采样"),
            new AudioInstrumentOption(AudioInstrument.HallPiano, "钢琴 · 音乐厅", "Salamander 三角钢琴采样，带立体声空间和琴音余韵"),
            new AudioInstrumentOption(AudioInstrument.LegacyPiano, "钢琴 · 原合成", "原版合成钢琴，供试听对比")
        }).ToArray();

    public static AudioInstrument Normalize(AudioInstrument value) =>
        Enum.IsDefined(value) ? value : AudioInstrument.Harmonica;

    public static string NameOf(AudioInstrument value) =>
        WindowsOptions.FirstOrDefault(option => option.Value == Normalize(value))?.Name ?? "口琴";

    public static bool IsSupported(AudioInstrument value) => Enum.IsDefined(value);
}

public static class AudioInstrumentSynthesizer
{
    public static double PitchMultiplier(AudioInstrument instrument, double timeSeconds)
    {
        instrument = AudioInstrumentCatalog.Normalize(instrument);
        return instrument switch
        {
            AudioInstrument.Harmonica => 1d + 0.0025d * Math.Sin(2d * Math.PI * 5.2d * timeSeconds),
            AudioInstrument.Violin => 1d + 0.0045d * Math.Sin(2d * Math.PI * 5.4d * timeSeconds),
            AudioInstrument.Flute => 1d + 0.0015d * Math.Sin(2d * Math.PI * 4.8d * timeSeconds),
            AudioInstrument.Organ => 1d + 0.0004d * Math.Sin(2d * Math.PI * 4d * timeSeconds),
            _ => 1d
        };
    }

    public static double Sample(AudioInstrument instrument, double phase, double timeSeconds)
    {
        instrument = AudioInstrumentCatalog.Normalize(instrument);
        return instrument switch
        {
            AudioInstrument.Piano or AudioInstrument.LegacyPiano =>
                0.72d * Math.Sin(phase) +
                0.22d * Math.Sin(phase * 2.01d) +
                0.13d * Math.Sin(phase * 3.01d) +
                0.07d * Math.Sin(phase * 4.2d),
            AudioInstrument.Flute =>
                0.88d * Math.Sin(phase) +
                0.10d * Math.Sin(phase * 2d) +
                0.04d * Math.Sin(phase * 3d) +
                0.012d * Math.Sin(phase * 17d + timeSeconds * 2.1d),
            AudioInstrument.Violin =>
                0.54d * Math.Sin(phase) +
                0.25d * Math.Sin(phase * 2d) +
                0.15d * Math.Sin(phase * 3d) +
                0.095d * Math.Sin(phase * 4d) +
                0.055d * Math.Sin(phase * 5d),
            AudioInstrument.Guitar =>
                0.72d * Math.Sin(phase) +
                0.24d * Math.Sin(phase * 2d) +
                0.13d * Math.Sin(phase * 3d) +
                0.075d * Math.Sin(phase * 4d),
            AudioInstrument.Organ =>
                0.62d * Math.Sin(phase) +
                0.28d * Math.Sin(phase * 2d) +
                0.18d * Math.Sin(phase * 3d) +
                0.11d * Math.Sin(phase * 4d),
            AudioInstrument.MusicBox =>
                0.58d * Math.Sin(phase) +
                0.30d * Math.Sin(phase * 2.01d) +
                0.18d * Math.Sin(phase * 3.99d) +
                0.10d * Math.Sin(phase * 6.02d),
            _ =>
                0.58d * Math.Sin(phase) +
                0.24d * Math.Sin(phase * 2d) +
                0.12d * Math.Sin(phase * 3d) +
                0.06d * Math.Sin(phase * 4d)
        };
    }

    public static double Envelope(AudioInstrument instrument, double timeSeconds, double durationSeconds)
    {
        instrument = AudioInstrumentCatalog.Normalize(instrument);
        double attackSeconds = instrument switch
        {
            AudioInstrument.Flute or AudioInstrument.Violin => 0.045d,
            AudioInstrument.Organ => 0.025d,
            AudioInstrument.Piano or AudioInstrument.LegacyPiano or AudioInstrument.Guitar or AudioInstrument.MusicBox => 0.004d,
            _ => 0.008d
        };
        double attack = Math.Min(1d, timeSeconds / Math.Max(0.001d, attackSeconds));
        double releaseSeconds = instrument switch
        {
            AudioInstrument.Flute or AudioInstrument.Violin or AudioInstrument.Organ => 0.08d,
            AudioInstrument.Piano or AudioInstrument.LegacyPiano => 0.12d,
            AudioInstrument.Guitar or AudioInstrument.MusicBox => 0.045d,
            _ => 0.045d
        };
        double release = timeSeconds >= durationSeconds - releaseSeconds
            ? Math.Max(0d, (durationSeconds - timeSeconds) / releaseSeconds)
            : 1d;
        double body = instrument switch
        {
            AudioInstrument.Piano or AudioInstrument.LegacyPiano => 0.24d + 0.76d * Math.Exp(-3.2d * timeSeconds),
            AudioInstrument.Guitar => Math.Exp(-2.35d * timeSeconds),
            AudioInstrument.MusicBox => Math.Exp(-4.2d * timeSeconds),
            AudioInstrument.Organ => 0.94d + 0.06d * Math.Sin(2d * Math.PI * 4d * timeSeconds),
            _ => 1d
        };
        return attack * release * body;
    }

    public static double Gain(AudioInstrument instrument) =>
        AudioInstrumentCatalog.Normalize(instrument) switch
        {
            AudioInstrument.Piano or AudioInstrument.LegacyPiano => 10_500d,
            AudioInstrument.Flute => 12_500d,
            AudioInstrument.Violin => 9_500d,
            AudioInstrument.Guitar => 12_000d,
            AudioInstrument.Organ => 10_000d,
            AudioInstrument.MusicBox => 12_000d,
            _ => 11_000d
        };
}
