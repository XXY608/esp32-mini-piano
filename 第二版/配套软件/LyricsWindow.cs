using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Animation;

namespace HarmonicaPlayer;

public sealed class LyricsWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x80;
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtTransparent = -1;
    private readonly TextBlock currentText = new();
    private readonly TextBlock nextText = new();
    private readonly TextBlock outgoingText = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly Grid lyricsRoot = new() { Margin = new Thickness(4), ClipToBounds = true };
    private readonly TextBlock titleText = new();
    private readonly Button settingsButton = new() { Content = "\u2699", Width = 32, Height = 28, FontSize = 19, IsTabStop = false };
    private readonly DispatcherTimer settingsHideTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private HwndSource? source;
    private LyricsLayout layout = new();

    public event Action? SettingsRequested;
    public event Action<LyricsLayout>? LayoutChanged;

    public LyricsWindow(LyricsLayout initialLayout)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;

        ConfigureText(currentText, HorizontalAlignment.Center, VerticalAlignment.Center, TextAlignment.Center);
        ConfigureText(nextText, HorizontalAlignment.Center, VerticalAlignment.Center, TextAlignment.Center);
        ConfigureText(outgoingText, HorizontalAlignment.Center, VerticalAlignment.Center, TextAlignment.Center);
        ConfigureText(titleText, HorizontalAlignment.Center, VerticalAlignment.Center, TextAlignment.Center);
        currentText.Margin = nextText.Margin = outgoingText.Margin = new Thickness(18, 0, 18, 0);
        currentText.Tag = "lyrics-current-line";
        nextText.Tag = "lyrics-next-line";
        currentText.FontWeight = FontWeights.Bold;
        nextText.FontWeight = FontWeights.Normal;
        nextText.Opacity = 0.55;
        foreach (var text in new[] { currentText, nextText, outgoingText }) text.RenderTransform = new TranslateTransform();
        titleText.Visibility = Visibility.Collapsed;
        settingsButton.HorizontalAlignment = HorizontalAlignment.Right;
        settingsButton.VerticalAlignment = VerticalAlignment.Top;
        settingsButton.Margin = new Thickness(0, 2, 2, 0);
        settingsButton.Foreground = Brushes.White;
        settingsButton.Background = Brushes.Transparent;
        settingsButton.BorderBrush = Brushes.Transparent;
        settingsButton.Visibility = Visibility.Collapsed;
        settingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        settingsButton.MouseEnter += (_, _) => ShowSettingsButton();
        settingsButton.MouseLeave += (_, _) => ScheduleSettingsHide();
        currentText.MouseLeftButtonDown += DragOverlay;
        nextText.MouseLeftButtonDown += DragOverlay;
        titleText.MouseLeftButtonDown += DragOverlay;
        foreach (TextBlock text in new[] { currentText, nextText, titleText })
        {
            text.MouseEnter += (_, _) => ShowSettingsButton();
            text.MouseLeave += (_, _) => ScheduleSettingsHide();
        }
        settingsHideTimer.Tick += (_, _) => HideSettingsButtonIfPointerLeft();

        var root = lyricsRoot;
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(currentText, 0);
        Grid.SetRow(nextText, 1);
        Grid.SetRowSpan(titleText, 2);
        root.Children.Add(outgoingText);
        root.Children.Add(currentText);
        root.Children.Add(nextText);
        root.Children.Add(titleText);
        root.Children.Add(settingsButton);
        Content = root;

        SourceInitialized += (_, _) => MakeClickThrough();
        Closed += (_, _) => source?.RemoveHook(WindowHook);
        ApplyLayout(initialLayout);
    }

    public void ApplyLayout(LyricsLayout layout)
    {
        layout = layout.Normalize();
        this.layout = layout;
        Rect area = SystemParameters.WorkArea;
        Width = Math.Max(180, area.Width * layout.Width);
        Height = Math.Max(48, area.Height * layout.Height);
        Left = area.Left + area.Width * layout.Left;
        Top = area.Top + area.Height * layout.Top;
        currentText.FontSize = nextText.FontSize = outgoingText.FontSize = titleText.FontSize = layout.FontSize;
        currentText.Foreground = BrushOf(layout.CurrentColor);
        nextText.Foreground = BrushOf(layout.NextColor);
        outgoingText.Foreground = BrushOf(layout.NextColor);
        titleText.Foreground = BrushOf(layout.NextColor);
    }

    public void ShowTitle(string text)
    {
        ResetScroll();
        titleText.Text = text;
        titleText.Visibility = Visibility.Visible;
        currentText.Visibility = Visibility.Collapsed;
        nextText.Visibility = Visibility.Collapsed;
        HideSettingsButton();
        ShowOverlay();
    }

    public void ShowLyrics(string current, string? next)
    {
        // End-of-phrase markers still serve the binding timeline, but the
        // display holds the last sung line through rests until the next starts.
        if (string.IsNullOrWhiteSpace(current)) return;
        bool advance = IsVisible && titleText.Visibility != Visibility.Visible &&
            currentText.Text.Length > 0 && currentText.Text != current;
        string previous = currentText.Text;
        ResetScroll();
        currentText.Text = current;
        nextText.Text = next ?? "";
        titleText.Visibility = Visibility.Collapsed;
        currentText.Visibility = Visibility.Visible;
        nextText.Visibility = string.IsNullOrWhiteSpace(next) ? Visibility.Hidden : Visibility.Visible;
        HideSettingsButton();
        ShowOverlay();
        if (advance)
        {
            outgoingText.Text = previous;
            double distance = Math.Max(layout.FontSize * 1.3, lyricsRoot.ActualHeight / 2);
            var duration = TimeSpan.FromMilliseconds(260);
            void Move(TextBlock text, double from, double to) => ((TranslateTransform)text.RenderTransform)
                .BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, to, duration)
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Move(outgoingText, 0, -distance);
            outgoingText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, duration));
            Move(currentText, distance, 0);
            Move(nextText, distance, 0);
        }
    }

    private void ResetScroll()
    {
        foreach (var text in new[] { currentText, nextText, outgoingText })
            ((TranslateTransform)text.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
        outgoingText.BeginAnimation(OpacityProperty, null);
        outgoingText.Text = "";
    }

    private void ShowOverlay()
    {
        if (!IsVisible) Show();
        Topmost = true;
    }

    public void HideLyrics()
    {
        ResetScroll();
        if (IsVisible) Hide();
    }

    private static void ConfigureText(TextBlock text, HorizontalAlignment horizontal,
        VerticalAlignment vertical, TextAlignment alignment)
    {
        text.FontWeight = FontWeights.SemiBold;
        text.HorizontalAlignment = horizontal;
        text.VerticalAlignment = vertical;
        text.TextAlignment = alignment;
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private static SolidColorBrush BrushOf(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private void MakeClickThrough()
    {
        var helper = new WindowInteropHelper(this);
        source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WindowHook);
        if (helper.Handle == IntPtr.Zero) return;
        IntPtr current = GetWindowLongPtr(helper.Handle, GwlExstyle);
        SetWindowLongPtr(helper.Handle, GwlExstyle,
            new IntPtr(current.ToInt64() | WsExNoActivate | WsExToolWindow));
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmNcHitTest)
        {
            handled = true;
            return IsInteractiveAt(lParam) ? new IntPtr(HtClient) : new IntPtr(HtTransparent);
        }
        return IntPtr.Zero;
    }

    private bool IsInteractiveAt(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        var screenPoint = new Point(unchecked((short)(value & 0xFFFF)), unchecked((short)((value >> 16) & 0xFFFF)));
        Point point = PointFromScreen(screenPoint);
        return ContainsPoint(settingsButton, point) || ContainsPoint(currentText, point) ||
               ContainsPoint(nextText, point) || ContainsPoint(titleText, point);
    }

    private bool ContainsPoint(FrameworkElement element, Point point)
    {
        if (element.Visibility != Visibility.Visible || !element.IsLoaded) return false;
        Rect bounds = element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
        return bounds.Contains(point);
    }

    private void DragOverlay(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { return; }
        finally { SaveDraggedLayout(); }
        e.Handled = true;
    }

    private void ShowSettingsButton()
    {
        settingsHideTimer.Stop();
        settingsButton.Visibility = Visibility.Visible;
    }

    private void ScheduleSettingsHide()
    {
        settingsHideTimer.Stop();
        settingsHideTimer.Start();
    }

    private void HideSettingsButtonIfPointerLeft()
    {
        settingsHideTimer.Stop();
        if (!settingsButton.IsMouseOver && !currentText.IsMouseOver && !nextText.IsMouseOver && !titleText.IsMouseOver)
            settingsButton.Visibility = Visibility.Collapsed;
    }

    private void HideSettingsButton()
    {
        settingsHideTimer.Stop();
        settingsButton.Visibility = Visibility.Collapsed;
    }

    private void SaveDraggedLayout()
    {
        Rect area = SystemParameters.WorkArea;
        layout = (layout with
        {
            Left = (Left - area.Left) / area.Width,
            Top = (Top - area.Top) / area.Height
        }).Normalize();
        LayoutChanged?.Invoke(layout);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
}
