using System.Globalization;
using HarmonicaPlayer;

namespace HarmonicaPlayer.Mobile;

public sealed class SettingsPage : ContentPage
{
    private readonly MobileAppState state;
    private readonly Entry addressEntry = new() { Keyboard = Keyboard.Url };
    private readonly Switch mockSwitch = new();
    private readonly Entry timeoutEntry = new() { Keyboard = Keyboard.Numeric };
    private readonly Picker instrumentPicker = new() { Title = "手机音色" };
    private readonly Label messageLabel = new() { TextColor = Ui.Ink, IsVisible = false };

    public SettingsPage(MobileAppState state)
    {
        this.state = state;
        instrumentPicker.ItemsSource = AudioInstrumentCatalog.Options.ToList();
        instrumentPicker.ItemDisplayBinding = new Binding(nameof(AudioInstrumentOption.Name));
        Title = "设置";
        BackgroundColor = Ui.PageBackground;

        var saveButton = Ui.PrimaryButton("保存");
        saveButton.Clicked += async (_, _) => await SaveAsync();
        var advanced = Ui.Card(new VerticalStackLayout
        {
            Spacing = 6,
            Children = { Ui.Row(Ui.Caption("模拟电子琴"), mockSwitch), Ui.Caption("连接超时（秒）"), timeoutEntry }
        });
        SemanticProperties.SetDescription(mockSwitch, "模拟电子琴");
        SemanticProperties.SetDescription(timeoutEntry, "连接超时秒数");
        advanced.IsVisible = false;
        var advancedToggle = Ui.TextButton("高级");
        advancedToggle.Clicked += (_, _) =>
        {
            advanced.IsVisible = !advanced.IsVisible;
            advancedToggle.Text = advanced.IsVisible ? "收起高级设置" : "高级";
        };
        Content = new ScrollView
        {
            Content = Ui.PageLayout(Ui.Caption("电子琴地址"), addressEntry, Ui.Caption("手机音色"),
                instrumentPicker, saveButton, messageLabel, advancedToggle, advanced)
        };
        Appearing += (_, _) => LoadSettings();
    }

    private void LoadSettings()
    {
        messageLabel.IsVisible = false;
        addressEntry.Text = state.DeviceAddress;
        mockSwitch.IsToggled = state.UseMock;
        timeoutEntry.Text = state.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        instrumentPicker.SelectedItem = AudioInstrumentCatalog.Options.First(option => option.Value == state.Instrument);
    }

    private async Task SaveAsync()
    {
        messageLabel.IsVisible = true;
        string address = addressEntry.Text?.Trim() ?? string.Empty;
        if (!mockSwitch.IsToggled && (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")))
        {
            messageLabel.TextColor = Colors.DarkRed;
            messageLabel.Text = "设备地址必须是 http:// 或 https:// 地址。";
            return;
        }
        if (!int.TryParse(timeoutEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int timeout) || timeout is < 2 or > 60)
        {
            messageLabel.TextColor = Colors.DarkRed;
            messageLabel.Text = "超时必须是 2～60 秒的整数。";
            return;
        }

        if (!string.IsNullOrWhiteSpace(address)) state.DeviceAddress = address;
        state.UseMock = mockSwitch.IsToggled;
        state.TimeoutSeconds = timeout;
        if (instrumentPicker.SelectedItem is AudioInstrumentOption option)
            state.Instrument = option.Value;
        messageLabel.TextColor = Colors.DarkGreen;
        messageLabel.Text = "已保存";
        await Task.CompletedTask;
    }
}
