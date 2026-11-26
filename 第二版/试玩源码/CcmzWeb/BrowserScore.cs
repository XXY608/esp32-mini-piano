using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonicaPlayer;
using HarmonicaPlayer.Mobile.Core;

namespace CcmzWeb;

internal static partial class BrowserScore
{
    public static void Main() { }

    [JSExport]
    internal static string Convert(byte[] bytes, string title, string id)
    {
        try
        {
            var imported = CcmzImporter.Import(bytes, title, preserveTiming: true, includePerformance: true);
            var song = SongPayloadFactory.Create(imported, id);
            return JsonSerializer.Serialize(new Conversion(song, null), BrowserJson.Default.Conversion);
        }
        catch (FormatException error)
        {
            return JsonSerializer.Serialize(new Conversion(null, error.Message), BrowserJson.Default.Conversion);
        }
    }
}

internal sealed record Conversion(SongPayload? Song, string? Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Conversion))]
internal sealed partial class BrowserJson : JsonSerializerContext;
