namespace HarmonicaPlayer.Mobile;

public sealed class App : Application
{
    private readonly AppShell shell;
    private readonly DeviceAudioService audio;
    public App(AppShell shell, DeviceAudioService audio)
    {
        this.shell = shell;
        this.audio = audio;
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(shell);
        window.Created += (_, _) => audio.SetForeground(true);
        window.Resumed += (_, _) => audio.SetForeground(true);
        window.Stopped += (_, _) => audio.SetForeground(false);
        window.Destroying += (_, _) => audio.SetForeground(false);
        return window;
    }
}
