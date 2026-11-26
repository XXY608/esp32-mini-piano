using System.Text;
using HarmonicaPlayer;

namespace HarmonicaPlayer.Mobile.Core;

public sealed record TxtScoreDraft(
    string FileName,
    string Title,
    int? Bpm,
    string SourceText,
    ImportedScore? Score,
    string? ParseError,
    string BpmSource)
{
    public bool IsValid => Score is not null && string.IsNullOrWhiteSpace(ParseError);
    public IReadOnlyList<ScoreNote> Notes => Score?.Notes ?? Array.Empty<ScoreNote>();
}

public static class TxtScoreImporter
{
    public const int MaxTextBytes = 4 * 1024 * 1024;

    public static TxtScoreDraft Parse(
        string fileName,
        string sourceText,
        string? titleOverride = null,
        int? bpmOverride = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(sourceText);

        string title = string.IsNullOrWhiteSpace(titleOverride)
            ? BpmFileName.ReadTitle(fileName)
            : titleOverride.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = "未命名曲谱";

        int? bpm = bpmOverride;
        string bpmSource = bpmOverride is not null ? "用户输入" : "未标注";
        if (bpm is null && BpmFileName.TryRead(fileName, out int fileBpm, out _))
        {
            bpm = fileBpm;
            bpmSource = "文件名";
        }

        if (sourceText.Length == 0)
            return new(fileName, title, bpm, sourceText, null, "TXT 文件为空。", bpmSource);
        if (Encoding.UTF8.GetByteCount(sourceText) > MaxTextBytes)
            return new(fileName, title, bpm, sourceText, null, "TXT 文件超过 4MB，已拒绝解析。", bpmSource);

        try
        {
            List<ScoreNote> notes = ScoreParser.Parse(sourceText, rhythm: true);
            var score = new ImportedScore(
                title,
                bpm,
                ScoreTextFormatter.Format(notes),
                notes,
                Array.Empty<string>());
            return new(fileName, title, bpm, sourceText, score, null, bpmSource);
        }
        catch (FormatException error)
        {
            return new(fileName, title, bpm, sourceText, null, error.Message, bpmSource);
        }
    }

    public static async Task<string> ReadUtf8Async(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > MaxTextBytes)
                throw new FormatException("TXT 文件超过 4MB，已拒绝读取。");
            memory.Write(buffer, 0, read);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(memory.ToArray());
    }
}
