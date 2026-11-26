using System.IO;
using System.Text.Json;

namespace HarmonicaPlayer;

public sealed record AppSettings
{
    public HotkeyBinding Start { get; init; } = HotkeyBinding.DefaultStart;
    public HotkeyBinding Stop { get; init; } = HotkeyBinding.DefaultStop;
    public bool Rhythm { get; init; }
    public int Bpm { get; init; } = 120;
    public int Duration { get; init; } = 300;
    public int Gap { get; init; } = 20;
    public AudioInstrument Instrument { get; init; } = AudioInstrument.Harmonica;
    public int SpaceGap { get; init; } = 100;
    public int LineGap { get; init; } = 300;
    public bool LyricsEnabled { get; init; }
    public string LyricsText { get; init; } = "";
    public string LyricsTimingText { get; init; } = "";
    public string LyricsBindingJson { get; init; } = "";
    public string LyricsScorePath { get; init; } = "";
    public string LyricsFilePath { get; init; } = "";
    public LyricsLayout LyricsLayout { get; init; } = new();

    public void Validate()
    {
        if (Start is null || Stop is null) throw new FormatException("缺少快捷键设置。");
        HotkeyBinding.ValidatePair(Start, Stop);
        if (Bpm is < 20 or > 300 || Duration is < 80 or > 5000 || Gap is < 10 or > 5000 ||
            SpaceGap is < 0 or > 10000 || LineGap is < 0 or > 10000)
            throw new FormatException("设置数值超出允许范围。");
        if (!AudioInstrumentCatalog.IsSupported(Instrument))
            throw new FormatException("本机发声音色设置无效。");
        if (LyricsText is null || LyricsText.Length > 200000)
            throw new FormatException("歌词文本不得超过 200000 个字符。");
        if (LyricsTimingText is null || LyricsTimingText.Length > 200000)
            throw new FormatException("同步歌词文本不得超过 200000 个字符。");
        if (LyricsBindingJson is null || LyricsBindingJson.Length > ScoreLyrics.MaxJsonLength)
            throw new FormatException("歌词绑定数据不得超过 1MB。");
        if (LyricsScorePath is null || LyricsScorePath.Length > 4096 ||
            LyricsFilePath is null || LyricsFilePath.Length > 4096)
            throw new FormatException("歌词绑定文件路径无效或过长。");
        if (LyricsLayout is null) throw new FormatException("缺少歌词窗口布局设置。");
        LyricsLayout.Validate();
    }
}

public static class SettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "电子琴", "settings.json");
    private static string LegacyDefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarmonicaPlayer", "settings.json");
    public static AppSettings Load(string path, out string? warning)
    {
        warning = null;
        bool migratingLegacySettings = false;
        if (!File.Exists(path) && IsDefaultPath(path) && File.Exists(LegacyDefaultPath))
        {
            path = LegacyDefaultPath;
            migratingLegacySettings = true;
        }
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                ?? throw new FormatException("设置文件为空。");
            settings.Validate();
            if (migratingLegacySettings)
            {
                Save(DefaultPath, settings);
                warning = "已迁移旧版 HarmonicaPlayer 设置到电子琴。";
            }
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            warning = "无法读取原设置，已使用默认值：" + e.Message;
            return new();
        }
    }
    public static void Save(string path, AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool IsDefaultPath(string path) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(DefaultPath), StringComparison.OrdinalIgnoreCase);
}
