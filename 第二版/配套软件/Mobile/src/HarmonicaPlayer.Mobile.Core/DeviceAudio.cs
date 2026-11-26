using System.Security.Cryptography;
using System.Text.Json;

namespace HarmonicaPlayer.Mobile.Core;

public readonly record struct DeviceAudioEvent(uint Sequence, uint Milliseconds, int Midi, int Volume,
    int Instrument, uint Trigger, bool Click);

public sealed record DeviceAudioPacket(bool Reset, bool PhoneOutput, IReadOnlyList<DeviceAudioEvent> Events)
{
    public static DeviceAudioPacket Parse(JsonElement root)
    {
        if (!root.TryGetProperty("phoneOutput", out var route) || route.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new DeviceCommunicationException("实时发声需要电子琴固件 0.6.13 或更新版本。");
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array || events.GetArrayLength() > 64)
            throw new DeviceCommunicationException("设备音频事件格式错误。");
        var result = new List<DeviceAudioEvent>();
        foreach (var row in events.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 7 ||
                !row[0].TryGetUInt32(out uint seq) || !row[1].TryGetUInt32(out uint ms) ||
                !row[2].TryGetInt32(out int midi) || midi is < -1 or > 127 ||
                !row[3].TryGetInt32(out int volume) || volume is < 0 or > 100 ||
                !row[4].TryGetInt32(out int instrument) || instrument is < 0 or > 10 ||
                !row[5].TryGetUInt32(out uint trigger) || !row[6].TryGetInt32(out int click) || click is < 0 or > 1)
                throw new DeviceCommunicationException("设备音频事件数值错误。");
            result.Add(new(seq, ms, midi, volume, instrument, trigger, click == 1));
        }
        return new(root.TryGetProperty("reset", out var reset) && reset.ValueKind == JsonValueKind.True, route.GetBoolean(), result);
    }
}

public interface IDeviceAudioClient
{
    Task<DeviceResponse> SetPhoneAudioAsync(uint session, bool enabled, CancellationToken cancellationToken);
    Task<DeviceAudioPacket> GetAudioEventsAsync(uint session, uint after, CancellationToken cancellationToken);
}

public static class DeviceAudioSession
{
    public static async Task RunAsync(IDeviceAudioClient client, DeviceAudioBuffer buffer,
        Action<bool> routeChanged, CancellationToken cancellationToken)
    {
        uint session = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        uint cursor = 0;
        bool? phone = null;
        try
        {
            var response = await client.SetPhoneAudioAsync(session, true, cancellationToken);
            if (!response.Ok) throw new DeviceCommunicationException(response.Message);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packet = await client.GetAudioEventsAsync(session, cursor, cancellationToken);
                buffer.Accept(packet, Environment.TickCount64);
                bool changed = phone != packet.PhoneOutput;
                if (changed) { phone = packet.PhoneOutput; routeChanged(packet.PhoneOutput); }
                if (packet.Events.Count > 0) cursor = packet.Events[^1].Sequence;
                // A route switch may arrive without a new note. Ask for the current held note.
                if (changed) cursor = 0;
                await Task.Delay(35, cancellationToken);
            }
        }
        finally
        {
            buffer.Reset();
            // Even an interrupted acquire may have reached the ESP32. Release that exact owner.
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await client.SetPhoneAudioAsync(session, false, release.Token); }
            catch (Exception error) when (error is OperationCanceledException or DeviceCommunicationException or HttpRequestException) { }
        }
    }
}

// One streaming voice follows timestamped device events. Android's audio writer owns rendering.
public sealed class DeviceAudioBuffer
{
    public const int SampleRate = 22050;
    private readonly object gate = new();
    private readonly Queue<(long Time, DeviceAudioEvent Event)> pending = new();
    private bool phone, hasClock, click;
    private uint baseDevice, trigger;
    private long baseLocal;
    private int midi = -1, soundingMidi = -1, volume, instrument, clickFrames, blendFrames;
    private double phase, level, clickPhase, lastValue, blendFrom;

    public void Accept(DeviceAudioPacket packet, long now)
    {
        lock (gate)
        {
            if (!packet.PhoneOutput) { ResetNoLock(); return; }
            if (!phone || packet.Reset) { pending.Clear(); hasClock = false; }
            phone = true;
            foreach (var e in packet.Events)
            {
                if (!hasClock) { baseDevice = e.Milliseconds; baseLocal = now + 60; hasClock = true; }
                long at = baseLocal + unchecked(e.Milliseconds - baseDevice);
                if (at < now || at > now + 500) { baseDevice = e.Milliseconds; baseLocal = at = now + 60; }
                if (pending.Count >= 128) pending.Clear(); // bounded device ring plus one polling batch
                pending.Enqueue((at, e));
            }
        }
    }

    public void Reset() { lock (gate) ResetNoLock(); }
    private void ResetNoLock()
    {
        pending.Clear(); phone = hasClock = click = false;
        midi = soundingMidi = -1; volume = clickFrames = blendFrames = 0; phase = level = clickPhase = lastValue = blendFrom = 0;
    }

    public void Render(short[] samples, long now)
    {
        lock (gate)
        {
            if (!phone) { Array.Clear(samples); return; }
            for (int i = 0; i < samples.Length; i++)
            {
                long time = now + i * 1000L / SampleRate;
                while (pending.TryPeek(out var next) && next.Time <= time)
                {
                    pending.Dequeue(); var e = next.Event;
                    if (e.Midi >= 0 && (e.Midi != midi || e.Trigger != trigger || e.Instrument != instrument))
                    {
                        blendFrom = lastValue; blendFrames = SampleRate / 125;
                        soundingMidi = e.Midi; phase = 0; level = 0;
                    }
                    midi = e.Midi; volume = e.Volume; instrument = e.Instrument; trigger = e.Trigger;
                    if (e.Click && !click) { clickFrames = SampleRate / 40; clickPhase = 0; }
                    click = e.Click;
                }
                double target = midi >= 0 ? volume / 100d * .18 : 0;
                if (instrument == 0 && midi >= 0) target *= Math.Min(1, Math.Pow(2, (48 - midi) / 12d));
                level += Math.Clamp(target - level, -.18 / (SampleRate * .008), .18 / (SampleRate * .008));
                if (soundingMidi >= 0) phase = (phase + 440 * Math.Pow(2, (soundingMidi - 69) / 12d) / SampleRate) % 1;
                // Same light synthesis choices as the web receiver; SD sample banks stay on the piano.
                double wave = instrument == 1 ? 1 - 4 * Math.Abs(phase - .5) : instrument == 2 ? (phase < .5 ? 1 : -1) : Math.Sin(phase * 2 * Math.PI);
                double value = wave * level;
                if (clickFrames > 0)
                {
                    clickPhase += 2 * Math.PI * 1600 / SampleRate;
                    value += Math.Sin(clickPhase) * volume / 100d * .06 * clickFrames-- / (SampleRate / 40d);
                }
                if (blendFrames > 0)
                {
                    double weight = blendFrames-- / (double)(SampleRate / 125);
                    value = value * (1 - weight) + blendFrom * weight;
                }
                lastValue = value;
                samples[i] = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
            }
        }
    }
}
