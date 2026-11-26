using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HarmonicaPlayer;

public sealed class LyricsLayoutDialog : Window
{
    private readonly Canvas previewCanvas = new() { Width = 700, Height = 280 };
    private readonly Border sampleBox = new();
    private readonly TextBlock currentSampleText = new();
    private readonly TextBlock nextSampleText = new();
    private readonly Slider leftSlider;
    private readonly Slider topSlider;
    private readonly Slider widthSlider;
    private readonly Slider heightSlider;
    private readonly Slider fontSlider;
    private readonly TextBox currentColor = new() { Width = 110 };
    private readonly TextBox nextColor = new() { Width = 110 };
    private bool dragging;
    private Point dragStart;
    private double dragLeftStart;
    private double dragTopStart;
    private bool normalizing;

    public LyricsLayout Layout { get; private set; }

    public LyricsLayoutDialog(LyricsLayout initial, string sample)
    {
        Layout = initial.Normalize();
        Title = "歌词位置与大小预览";
        Width = 780;
        Height = 650;
        MinWidth = 700;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = BrushOf("#F5F7FA");

        leftSlider = MakeSlider(0, 80, Layout.Left * 100);
        topSlider = MakeSlider(0, 92, Layout.Top * 100);
        widthSlider = MakeSlider(15, 100, Layout.Width * 100);
        heightSlider = MakeSlider(8, 60, Layout.Height * 100);
        fontSlider = MakeSlider(16, 72, Layout.FontSize);
        currentColor.Text = Layout.CurrentColor;
        nextColor.Text = Layout.NextColor;
        foreach (var slider in new[] { leftSlider, topSlider, widthSlider, heightSlider, fontSlider })
            slider.ValueChanged += (_, _) => RenderPreview();
        currentColor.TextChanged += (_, _) => RenderPreview();
        nextColor.TextChanged += (_, _) => RenderPreview();

        var root = new Grid { Margin = new Thickness(22, 18, 22, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "先预览歌词出现位置和大小", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = BrushOf("#172B4D") });
        heading.Children.Add(new TextBlock
        {
            Text = "拖动预览中的歌词框调整位置，也可以用滑块精确设置。实际播放时会使用这里保存的布局。",
            Margin = new Thickness(0, 5, 0, 12), Foreground = BrushOf("#667085"), TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(heading, 0); root.Children.Add(heading);

        var previewFrame = new Border
        {
            Background = BrushOf("#1D2939"), BorderBrush = BrushOf("#98A2B3"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Center,
            Child = previewCanvas
        };
        previewCanvas.ClipToBounds = true;
        previewCanvas.MouseLeftButtonDown += (_, _) => { }; // Keep the canvas as a stable drag surface.
        var samples = LyricsTimeline.Tokenize(sample);
        currentSampleText.Text = samples.Count > 0 ? samples[0] : "当前歌词";
        nextSampleText.Text = samples.Count > 1 ? samples[1] : "下一句歌词";
        ConfigurePreviewText(currentSampleText, HorizontalAlignment.Center, VerticalAlignment.Center, TextAlignment.Center);
        ConfigurePreviewText(nextSampleText, HorizontalAlignment.Center, VerticalAlignment.Center, TextAlignment.Center);
        currentSampleText.FontWeight = FontWeights.Bold;
        nextSampleText.FontWeight = FontWeights.Normal;
        nextSampleText.Opacity = 0.55;
        currentSampleText.Margin = nextSampleText.Margin = new Thickness(18, 0, 18, 0);
        sampleBox.Background = Brushes.Transparent;
        sampleBox.BorderThickness = new Thickness(0);
        sampleBox.Padding = new Thickness(4, 2, 4, 2);
        sampleBox.Cursor = Cursors.SizeAll;
        var sampleGrid = new Grid();
        sampleGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sampleGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(currentSampleText, 0);
        Grid.SetRow(nextSampleText, 1);
        sampleGrid.Children.Add(currentSampleText);
        sampleGrid.Children.Add(nextSampleText);
        sampleBox.Child = sampleGrid;
        sampleBox.PreviewMouseLeftButtonDown += BeginDrag;
        sampleBox.PreviewMouseMove += Drag;
        sampleBox.PreviewMouseLeftButtonUp += EndDrag;
        previewCanvas.Children.Add(sampleBox);
        Grid.SetRow(previewFrame, 1); root.Children.Add(previewFrame);

        var controls = new Grid { Margin = new Thickness(0, 16, 0, 12) };
        for (int i = 0; i < 7; i++) controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        AddSlider(controls, "水平位置", leftSlider, "%", 0);
        AddSlider(controls, "垂直位置", topSlider, "%", 1);
        AddSlider(controls, "窗口宽度", widthSlider, "%", 2);
        AddSlider(controls, "窗口高度", heightSlider, "%", 3);
        AddSlider(controls, "歌词字号", fontSlider, "px", 4);
        AddTextBox(controls, "当前句颜色", currentColor, 5);
        AddTextBox(controls, "下一句颜色", nextColor, 6);
        Grid.SetRow(controls, 2); root.Children.Add(controls);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = MakeButton("取消", "#E9EEF8", "#25416C", 94);
        var apply = MakeButton("应用布局", "#197A62", "#FFFFFF", 112);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        apply.Click += (_, _) => { Layout = ReadLayout().Normalize(); DialogResult = true; Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        Grid.SetRow(buttons, 3); root.Children.Add(buttons);

        Loaded += (_, _) => RenderPreview();
    }

    private static Slider MakeSlider(double minimum, double maximum, double value) => new()
    {
        Minimum = minimum, Maximum = maximum, Value = value, TickFrequency = 1,
        IsSnapToTickEnabled = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3)
    };

    private static void AddSlider(Grid grid, string label, Slider slider, string unit, int row)
    {
        var labelBlock = new TextBlock { Text = label, Foreground = BrushOf("#344054"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(labelBlock, row); Grid.SetColumn(labelBlock, 0); grid.Children.Add(labelBlock);
        Grid.SetRow(slider, row); Grid.SetColumn(slider, 1); grid.Children.Add(slider);
        var value = new TextBlock { Foreground = BrushOf("#344054"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        slider.ValueChanged += (_, _) => value.Text = FormatValue(slider.Value, unit);
        value.Text = FormatValue(slider.Value, unit);
        Grid.SetRow(value, row); Grid.SetColumn(value, 2); grid.Children.Add(value);
    }

    private static void AddTextBox(Grid grid, string label, TextBox textBox, int row)
    {
        var labelBlock = new TextBlock { Text = label, Foreground = BrushOf("#344054"), VerticalAlignment = VerticalAlignment.Center };
        textBox.Margin = new Thickness(0, 3, 12, 3);
        Grid.SetRow(labelBlock, row); Grid.SetColumn(labelBlock, 0); grid.Children.Add(labelBlock);
        Grid.SetRow(textBox, row); Grid.SetColumn(textBox, 1); grid.Children.Add(textBox);
    }

    private static string FormatValue(double value, string unit) => $"{value:0} {unit}";

    private LyricsLayout ReadLayout() => new()
    {
        Left = leftSlider.Value / 100,
        Top = topSlider.Value / 100,
        Width = widthSlider.Value / 100,
        Height = heightSlider.Value / 100,
        FontSize = fontSlider.Value,
        CurrentColor = currentColor.Text,
        NextColor = nextColor.Text
    };

    private void RenderPreview()
    {
        if (!IsLoaded || normalizing) return;
        var layout = ReadLayout().Normalize();
        normalizing = true;
        leftSlider.Value = layout.Left * 100;
        topSlider.Value = layout.Top * 100;
        widthSlider.Value = layout.Width * 100;
        heightSlider.Value = layout.Height * 100;
        fontSlider.Value = layout.FontSize;
        normalizing = false;
        double left = previewCanvas.Width * layout.Left;
        double top = previewCanvas.Height * layout.Top;
        sampleBox.Width = previewCanvas.Width * layout.Width;
        sampleBox.Height = previewCanvas.Height * layout.Height;
        // The preview represents the desktop work area.  Scale the font with
        // that area as well as the lyric window; a fixed 65% scale makes two
        // rows collide whenever the previewed lyric window is short.
        double workAreaHeight = Math.Max(1, SystemParameters.WorkArea.Height);
        double previewScale = previewCanvas.Height / workAreaHeight;
        currentSampleText.FontSize = nextSampleText.FontSize = Math.Clamp(
            layout.FontSize * previewScale, 9, 32);
        currentSampleText.Foreground = BrushOf(layout.CurrentColor);
        nextSampleText.Foreground = BrushOf(layout.NextColor);
        Canvas.SetLeft(sampleBox, left);
        Canvas.SetTop(sampleBox, top);
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        dragging = true;
        dragStart = e.GetPosition(previewCanvas);
        dragLeftStart = leftSlider.Value;
        dragTopStart = topSlider.Value;
        sampleBox.CaptureMouse();
        e.Handled = true;
    }

    private void Drag(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        Point current = e.GetPosition(previewCanvas);
        double nextLeft = dragLeftStart + (current.X - dragStart.X) / previewCanvas.Width * 100;
        double nextTop = dragTopStart + (current.Y - dragStart.Y) / previewCanvas.Height * 100;
        leftSlider.Value = Math.Clamp(nextLeft, leftSlider.Minimum, Math.Min(leftSlider.Maximum, 100 - widthSlider.Value));
        topSlider.Value = Math.Clamp(nextTop, topSlider.Minimum, Math.Min(topSlider.Maximum, 100 - heightSlider.Value));
        e.Handled = true;
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        dragging = false;
        sampleBox.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static void ConfigurePreviewText(TextBlock text, HorizontalAlignment horizontal,
        VerticalAlignment vertical, TextAlignment alignment)
    {
        text.FontWeight = FontWeights.SemiBold;
        text.HorizontalAlignment = horizontal;
        text.VerticalAlignment = vertical;
        text.TextAlignment = alignment;
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private static Button MakeButton(string content, string background, string foreground, double minWidth)
    {
        var button = new Button { Content = content, MinWidth = minWidth, MinHeight = 36, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6), FontWeight = FontWeights.SemiBold };
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, BrushOf(background)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, BrushOf(foreground)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, BrushOf(background)));
        button.Style = style;
        return button;
    }

    private static SolidColorBrush BrushOf(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
