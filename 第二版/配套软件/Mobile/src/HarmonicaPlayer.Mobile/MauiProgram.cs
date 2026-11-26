namespace HarmonicaPlayer.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

        builder.Services.AddSingleton<MobileAppState>();
        builder.Services.AddSingleton<DeviceAudioService>();
        builder.Services.AddSingleton<LocalSongStore>();
        builder.Services.AddSingleton<DeviceClientFactory>();
        builder.Services.AddSingleton<OnlineScorePage>();
        builder.Services.AddSingleton<HomePage>();
        builder.Services.AddSingleton<DevicePage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddSingleton<AppShell>();

        return builder.Build();
    }
}
