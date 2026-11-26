using HarmonicaPlayer;
using HarmonicaPlayer.Mobile.Core;

namespace HarmonicaPlayer.Mobile;

internal static class MobileFileImport
{
    internal static async Task<(StoredSong Song, IReadOnlyList<string> Warnings)?> PickAsync(Page page)
    {
        FileResult? file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "选择曲谱" });
        if (file is null) return null;

        await using Stream stream = await file.OpenReadAsync();
        byte[] data = await MobileScoreFiles.ReadAsync(stream);
        string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension == ".json")
        {
            SongPayload payload = MobileScoreFiles.ImportDeviceJson(data);
            var notes = payload.Notes.Select((n, i) => new ScoreNote(n.Pitch, n.Octave, n.Sharp, i, Beats: n.DurationBeats)).ToArray();
            return (new StoredSong(payload, ScoreTextFormatter.FormatWithMeasures(notes, 4), DateTimeOffset.UtcNow), Array.Empty<string>());
        }

        ImportedScore? imported = await ImportAsync(page, data, file.FileName);
        if (imported is null) return null;
        string source = extension == ".txt"
            ? new System.Text.UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF') : imported.Text;
        return (new StoredSong(SongPayloadFactory.Create(imported), source, DateTimeOffset.UtcNow), imported.Warnings);
    }

    internal static async Task<ImportedScore?> ImportAsync(Page page, byte[] data, string filename)
    {
        int? track = null;
        if (Path.GetExtension(filename).ToLowerInvariant() is ".mid" or ".midi")
        {
            var tracks = await Task.Run(() => MidiImporter.GetTracks(data));
            if (tracks.Count > 1)
            {
                string[] labels = new[] { "全部演奏音轨" }.Concat(tracks.Select(t => $"{t.Index + 1}. {t.Name} · {t.NoteCount}音符")).ToArray();
                string? choice = await page.DisplayActionSheetAsync("选择要导入的 MIDI 音轨", "取消", null, labels);
                int index = Array.IndexOf(labels, choice);
                if (index < 0) return null;
                if (index > 0) track = tracks[index - 1].Index;
            }
        }
        return await Task.Run(() => MobileScoreFiles.Import(data, filename, track, includePerformance: true));
    }
}
