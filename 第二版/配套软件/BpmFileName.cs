using System.IO;
using System.Text.RegularExpressions;

namespace HarmonicaPlayer;

public static class BpmFileName
{
    private static readonly Regex Pattern = new(
        @"bpm\s*(?:[=:：]\s*|\s*)(\d{2,3})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TitleSuffixPattern = new(
        @"(?:[_\s-]*\u6e90\u8c31\s*)?bpm\s*(?:[=:\uFF1A]?\s*)\d{2,3}\s*(?:[（(]\d+[)）])?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string ReadTitle(string fileName)
    {
        string title = Path.GetFileNameWithoutExtension(fileName).Trim();
        string withoutBpm = TitleSuffixPattern.Replace(title, string.Empty).Trim(' ', '_', '-');
        return string.IsNullOrWhiteSpace(withoutBpm) ? title : withoutBpm;
    }

    public static bool TryRead(string fileName, out int bpm, out string message)
    {
        bpm = 0;
        string name = Path.GetFileNameWithoutExtension(fileName);
        Match match = Pattern.Match(name);
        if (!match.Success)
        {
            message = "文件名未包含有效 BPM";
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, out bpm) || bpm is < 20 or > 300)
        {
            message = $"文件名中的 BPM {match.Groups[1].Value} 超出允许范围（20～300）";
            bpm = 0;
            return false;
        }

        message = $"已从文件名读取 BPM：{bpm}";
        return true;
    }
}
