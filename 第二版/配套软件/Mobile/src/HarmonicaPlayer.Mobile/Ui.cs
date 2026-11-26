namespace HarmonicaPlayer.Mobile;

internal static class Ui
{
    public static readonly Color PageBackground = Color.FromArgb("#F6F8FB");
    public static readonly Color Ink = Color.FromArgb("#1D3557");
    public static readonly Color Muted = Color.FromArgb("#607080");

    public static Label Heading(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontAttributes = FontAttributes.Bold,
        TextColor = Ink
    };

    public static Label Caption(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextColor = Muted
    };

    public static Grid Row(View leading, View trailing, bool equalWidth = false)
    {
        var row = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(equalWidth ? GridLength.Star : GridLength.Auto)
            }
        };
        row.Add(leading);
        row.Add(trailing, 1);
        return row;
    }

    public static Button TextButton(string text) => new()
    {
        Text = text,
        TextColor = Ink,
        BackgroundColor = Colors.Transparent,
        MinimumHeightRequest = 48,
        Padding = new Thickness(8, 0),
        FontSize = 14
    };

    public static Button MoreButton(string description)
    {
        var button = TextButton("⋯");
        button.WidthRequest = 48;
        button.FontSize = 24;
        SemanticProperties.SetDescription(button, description);
        return button;
    }

    public static VerticalStackLayout PageLayout(params View[] children)
    {
        var stack = new VerticalStackLayout { Padding = new Thickness(16, 12), Spacing = 10 };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    public static Frame Card(View content, int padding = 14) => new()
    {
        Content = content,
        Padding = padding,
        CornerRadius = 14,
        BorderColor = Color.FromArgb("#DCE4EC"),
        BackgroundColor = Colors.White,
        HasShadow = false
    };

    public static Frame DarkCard(View content) => new()
    {
        Content = content,
        Padding = 16,
        CornerRadius = 18,
        BorderColor = Color.FromArgb("#33404A"),
        BackgroundColor = Color.FromArgb("#1D252C"),
        HasShadow = false
    };

    public static Button PrimaryButton(string text) => new()
    {
        Text = text,
        BackgroundColor = Ink,
        TextColor = Colors.White,
        CornerRadius = 10,
        MinimumHeightRequest = 48,
        Padding = new Thickness(16, 10)
    };

    public static Button SecondaryButton(string text) => new()
    {
        Text = text,
        BackgroundColor = Color.FromArgb("#E8EEF5"),
        TextColor = Ink,
        CornerRadius = 10,
        MinimumHeightRequest = 48,
        Padding = new Thickness(12, 8),
        FontSize = 13
    };
}
