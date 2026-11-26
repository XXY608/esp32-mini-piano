using HarmonicaPlayer.Mobile.Core;
using HarmonicaPlayer;

namespace HarmonicaPlayer.Mobile;

public sealed class MobileAppState
{
    private const string DeviceAddressKey = "mobile.deviceAddress";
    private const string UseMockKey = "mobile.useMock";
    private const string TimeoutKey = "mobile.timeoutSeconds";
    private const string DeviceVolumeKey = "mobile.deviceVolume";
    private const string InstrumentKey = "mobile.instrument";
    private const string CommunicationDefaultsVersionKey = "mobile.communicationDefaultsVersion";
    public event Action? ConnectionChanged;
    private bool localPracticeActive;
    public bool LocalPracticeActive
    {
        get => localPracticeActive;
        set { if (localPracticeActive == value) return; localPracticeActive = value; ConnectionChanged?.Invoke(); }
    }
    public bool AutoDeviceAudio
    {
        get => Preferences.Default.Get("mobile.autoDeviceAudio", true);
        set { if (AutoDeviceAudio == value) return; Preferences.Default.Set("mobile.autoDeviceAudio", value); ConnectionChanged?.Invoke(); }
    }

    public MobileAppState()
    {
        // The first real-device build must not remain silently bound to the
        // old Mock default or the old hotspot example address.
        if (Preferences.Default.Get(CommunicationDefaultsVersionKey, 0) < 1)
        {
            string address = Preferences.Default.Get(DeviceAddressKey, string.Empty);
            if (string.IsNullOrWhiteSpace(address) || address == "http://192.168.43.100")
                Preferences.Default.Set(DeviceAddressKey, "http://192.168.4.1");
            Preferences.Default.Set(UseMockKey, false);
            Preferences.Default.Set(CommunicationDefaultsVersionKey, 1);
        }
    }

    public string DeviceAddress
    {
        get => Preferences.Default.Get(DeviceAddressKey, "http://192.168.4.1");
        set { if (DeviceAddress == value.Trim()) return; Preferences.Default.Set(DeviceAddressKey, value.Trim()); ConnectionChanged?.Invoke(); }
    }

    public bool UseMock
    {
        get => Preferences.Default.Get(UseMockKey, false);
        set { if (UseMock == value) return; Preferences.Default.Set(UseMockKey, value); ConnectionChanged?.Invoke(); }
    }

    public int TimeoutSeconds
    {
        get => Math.Clamp(Preferences.Default.Get(TimeoutKey, 8), 2, 60);
        set => Preferences.Default.Set(TimeoutKey, Math.Clamp(value, 2, 60));
    }

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    public int DeviceVolumePercent
    {
        get => Math.Clamp(Preferences.Default.Get(DeviceVolumeKey, 80), 0, 100);
        set => Preferences.Default.Set(DeviceVolumeKey, Math.Clamp(value, 0, 100));
    }

    public AudioInstrument Instrument
    {
        get => AudioInstrumentCatalog.Normalize((AudioInstrument)Preferences.Default.Get(InstrumentKey, (int)AudioInstrument.Harmonica));
        set => Preferences.Default.Set(InstrumentKey, (int)AudioInstrumentCatalog.Normalize(value));
    }
}

public sealed class LocalSongStore : JsonSongStore
{
    public LocalSongStore() : base(Path.Combine(FileSystem.AppDataDirectory, "songs.json")) { }
}

public sealed class DeviceClientFactory
{
    private readonly MobileAppState state;
    private readonly MockEsp32Client mock = new();

    public DeviceClientFactory(MobileAppState state) => this.state = state;

    public IDeviceClient Create() => state.UseMock
        ? mock
        : new HttpEsp32Client(state.DeviceAddress, state.Timeout);
}
