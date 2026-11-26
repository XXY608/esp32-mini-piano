namespace HarmonicaPlayer.Mobile;

public sealed class AppShell : Shell
{
    public AppShell(HomePage homePage, DevicePage devicePage, OnlineScorePage onlineScorePage, SettingsPage settingsPage)
    {
        Title = "电子琴";
        Shell.SetTabBarBackgroundColor(this, Color.FromArgb("#FFFFFF"));
        Shell.SetTabBarTitleColor(this, Ui.Ink);
        Shell.SetTabBarUnselectedColor(this, Ui.Muted);
        Items.Add(new TabBar
        {
            Items =
            {
                new ShellContent { Title = "曲库", Icon = new FontImageSource { Glyph = "♫", Size = 21, Color = Ui.Ink }, Route = "home", Content = homePage },
                new ShellContent { Title = "选曲", Icon = new FontImageSource { Glyph = "♪", Size = 22, Color = Ui.Ink }, Route = "online", Content = onlineScorePage },
                new ShellContent { Title = "电子琴", Icon = new FontImageSource { Glyph = "●", Size = 20, Color = Ui.Ink }, Route = "device", Content = devicePage },
                new ShellContent { Title = "设置", Icon = new FontImageSource { Glyph = "⚙", Size = 21, Color = Ui.Ink }, Route = "settings", Content = settingsPage }
            }
        });
    }
}
