using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HarmonicaPlayer;

public sealed record AudioLyricWord(int Index, string Text, bool EndLine, bool NeedsReview, double StartSeconds = 0, double EndSeconds = 0);
public sealed record AudioLyricResult(ImportedScore Score, IReadOnlyList<AudioLyricWord> Words, string SourceUrl, string JobDirectory);

public static class AudioLyricsAligner
{
    public static async Task<AudioLyricResult> RunAsync(string workspace, string sourcePath, string title,
        IProgress<string> progress, CancellationToken token)
    {
        string helper = Path.Combine(workspace, "work", "lyrics-alignment");
        string job = Path.Combine(helper, "jobs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var info = CreateStartInfo(workspace, sourcePath, title, job);
        Directory.CreateDirectory(job);
        using var jobOwner = new AlignmentProcessJob();
        using var process = Process.Start(info) ?? throw new IOException("无法启动人声对齐进程。");
        try { jobOwner.Assign(process); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        using var cancel = token.Register(() =>
        {
            jobOwner.Terminate();
        });
        var errors = new StringBuilder();
        async Task ReadLines(StreamReader reader, bool isError)
        {
            while (await reader.ReadLineAsync() is string line)
            {
                if (isError) { if (errors.Length < 12000) errors.AppendLine(line); }
                else if (!string.IsNullOrWhiteSpace(line)) progress.Report(line.Length > 500 ? line[..500] : line);
            }
        }
        await Task.WhenAll(ReadLines(process.StandardOutput, false), ReadLines(process.StandardError, true), process.WaitForExitAsync());
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new IOException((errors.Length == 0 ? "人声对齐未完成。" : errors.ToString().Trim()) + " 处理记录：" + job);
        return ReadResult(job);
    }

    internal static ProcessStartInfo CreateStartInfo(string workspace, string sourcePath, string title, string job)
    {
        string helper = Path.GetFullPath(Path.Combine(workspace, "work", "lyrics-alignment"));
        string interpreter = Path.Combine(helper, "python", "python.exe");
        bool bundled = File.Exists(interpreter);
        if (!bundled)
        {
            // The development venv launcher would leave an untracked child;
            // start its actual interpreter inside our existing Windows job.
            string config = Path.Combine(helper, "venv", "pyvenv.cfg");
            interpreter = File.Exists(config) ? File.ReadLines(config).Select(line => line.Split('=', 2))
                .Where(pair => pair.Length == 2 && pair[0].Trim() == "executable")
                .Select(pair => pair[1].Trim()).FirstOrDefault() ?? "" : "";
        }
        string script = Path.Combine(helper, "run_alignment.py");
        if (!File.Exists(interpreter) || !File.Exists(script))
            throw new IOException("未找到完整的本机配词运行库，请重新运行电子琴安装程序修复。曲谱和已保存的歌词仍会保留。");
        var info = new ProcessStartInfo(interpreter)
        {
            WorkingDirectory = helper, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-u", script, "--source", sourcePath, "--title", title, "--job", job })
            info.ArgumentList.Add(argument);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment.Remove("PYTHONHOME");
        info.Environment["PYTHONPATH"] = Path.Combine(helper, bundled ? "python" : "venv", "Lib", "site-packages");
        info.Environment["PYTHONNOUSERSITE"] = "1";
        // TIFA saves diagnostic plots; it must not create Tk windows or depend
        // on the developer machine's Tcl installation in this background job.
        info.Environment["MPLBACKEND"] = "Agg";
        return info;
    }

    public static AudioLyricResult ReadResult(string job)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(job, "candidate-alignment.json")));
        var tokens = document.RootElement.GetProperty("tokens");
        if (tokens.GetArrayLength() > 20000) throw new FormatException("逐字候选超过数量限制。");
        var words = tokens.EnumerateArray().Select(t => new AudioLyricWord(t.GetProperty("alignmentIndex").GetInt32(),
            t.GetProperty("text").GetString() ?? "", t.GetProperty("endLine").GetBoolean(), t.GetProperty("needsReview").GetBoolean(),
            t.GetProperty("start").GetDouble(), t.GetProperty("end").GetDouble())).ToArray();
        var imported = CcmzImporter.Import(File.ReadAllBytes(Path.Combine(job, "aligned.ccmz")), "人声辅助曲谱");
        if (imported.Lyrics == null || imported.Lyrics.Syllables.Count == 0)
            throw new FormatException("没有得到有效的逐字音符位置。");
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(job, "audio", "provenance.json")));
        string url = provenance.RootElement.GetProperty("webpage_url").GetString() ?? "";
        var byIndex = words.ToDictionary(w => w.Index);
        var tokenByIndex = tokens.EnumerateArray().ToDictionary(t => t.GetProperty("alignmentIndex").GetInt32());
        string ReferenceValue(int index, string property) => tokenByIndex[index].TryGetProperty("referenceScoreEvidence", out var evidence)
            && evidence.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
        string AcousticReasons(int index)
        {
            var item = tokenByIndex[index];
            if (!item.TryGetProperty("acousticReviewReasons", out var reasons)) item.TryGetProperty("reviewReasons", out reasons);
            return reasons.ValueKind == JsonValueKind.Array ? string.Join("; ", reasons.EnumerateArray().Select(r => r.GetString())) : "";
        }
        string PositionEvidence(int index) => tokenByIndex[index].TryGetProperty("positionEvidence", out var evidence)
            ? evidence.GetRawText() : "";
        var normalized = ScoreLyrics.FromWordBindings(imported.Notes, imported.Lyrics.Syllables.Select(w => w with
            { Text = LyricsTimeline.NormalizeText(w.Text) }).ToArray(), "人声自动逐字对齐");
        var binding = normalized with
        {
            ReferenceAudioPath = Path.Combine(job, "separated", "htdemucs", "reference", "vocals.wav"), ReferenceUrl = url,
            SourceScoreSha256 = provenance.RootElement.GetProperty("source_ccmz_sha256").GetString() ?? "",
            Syllables = normalized.Syllables.Select(w => w with
            { ReferenceStartSeconds = byIndex[w.AlignmentIndex].StartSeconds, ReferenceEndSeconds = byIndex[w.AlignmentIndex].EndSeconds,
                ReferenceScoreUrl = ReferenceValue(w.AlignmentIndex, "sourceUrl"),
                ReferenceScoreSha256 = ReferenceValue(w.AlignmentIndex, "sourceSha256"),
                AcousticDiagnostics = AcousticReasons(w.AlignmentIndex), PositionEvidence = PositionEvidence(w.AlignmentIndex) }).ToArray()
        };
        binding.Validate(imported.Notes);
        imported = imported with { Lyrics = binding };
        return new AudioLyricResult(imported, words, url, job);
    }

    private sealed class AlignmentProcessJob : IDisposable
    {
        private readonly SafeFileHandle handle;
        public AlignmentProcessJob()
        {
            handle = new SafeFileHandle(CreateJobObject(IntPtr.Zero, null), true);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var limit = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref limit, (uint)Marshal.SizeOf<ExtendedLimits>()))
            { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new System.ComponentModel.Win32Exception(error); }
        }
        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Terminate() => TerminateJobObject(handle, 1);
        public void Dispose() => handle.Dispose();
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}
