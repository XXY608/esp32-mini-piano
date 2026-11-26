using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text;
using Microsoft.Win32;

namespace HarmonicaPlayer;

public sealed class OnlineScoreWindow : Window
{
    private readonly TextBox keyword = new();
    private readonly Button search = new() { Content = "搜索曲谱" };
    private readonly Button openSite = new() { Content = "打开虫虫钢琴" };
    private readonly TextBlock status = new();
    private readonly StackPanel results = new();
    private readonly Func<string, byte[], ImportedScore, CancellationToken, Task<bool>> onCcmzSaved;
    private CancellationTokenSource? searchCancellation;
    private CancellationTokenSource? downloadCancellation;
    private bool closing;

    public OnlineScoreWindow(Window owner, Func<string, byte[], ImportedScore, CancellationToken, Task<bool>> onCcmzSaved)
    {
        this.onCcmzSaved = onCcmzSaved ?? throw new ArgumentNullException(nameof(onCcmzSaved));
        Owner = owner;
        Title = "在线曲谱 / CCMZ";
        Width = 820; Height = 680; MinWidth = 680; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = BrushOf("#F5F7FA"); FontSize = 14;

        StyleTextBox(keyword); keyword.MinWidth = 360; keyword.Height = 38;
        keyword.ToolTip = "输入歌曲名、演唱者、曲作者或制谱者";
        StyleButton(search, "#2F6FED", "#FFFFFF", 110);
        StyleButton(openSite, "#E9EEF8", "#25416C", 132);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new Border { Background = BrushOf("#FFFFFF"), BorderBrush = BrushOf("#E4E8EF"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(22, 18, 22, 16) };
        var headerPanel = new StackPanel();
        headerPanel.Children.Add(new TextBlock { Text = "虫虫钢琴在线曲谱", FontSize = 23, FontWeight = FontWeights.Bold, Foreground = BrushOf("#172B4D") });
        headerPanel.Children.Add(new TextBlock { Text = "下载 CCMZ 后自动编译多音谱并导入。多音 JSON 和 CCMZ 保存在同一文件夹；下载的原唱歌词可在导入后校准。", Margin = new Thickness(0, 5, 0, 0), Foreground = BrushOf("#667085"), TextWrapping = TextWrapping.Wrap });
        header.Child = headerPanel; Grid.SetRow(header, 0); root.Children.Add(header);

        var body = new Grid { Margin = new Thickness(22, 18, 22, 18) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var searchRow = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(openSite, Dock.Right); searchRow.Children.Add(openSite);
        DockPanel.SetDock(search, Dock.Right); searchRow.Children.Add(search);
        searchRow.Children.Add(keyword);
        body.Children.Add(searchRow);
        status.Foreground = BrushOf("#667085"); status.Margin = new Thickness(0, 12, 0, 10); status.TextWrapping = TextWrapping.Wrap; Grid.SetRow(status, 1); body.Children.Add(status);
        var resultScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = results };
        Grid.SetRow(resultScroll, 2); body.Children.Add(resultScroll);
        Grid.SetRow(body, 1); root.Children.Add(body);

        var footer = new Border { Background = BrushOf("#FFFFFF"), BorderBrush = BrushOf("#E4E8EF"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(22, 11, 22, 11), Child = new TextBlock { Text = "提示：页面没有公开 CCMZ 时，播放器不会绕过登录、购买或权限限制。", Foreground = BrushOf("#667085"), TextWrapping = TextWrapping.Wrap } };
        Grid.SetRow(footer, 2); root.Children.Add(footer);

        search.Click += async (_, _) => await SearchAsync();
        openSite.Click += (_, _) => OpenUrl(OnlineScoreClient.SiteUrl);
        keyword.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } };
        Loaded += (_, _) => keyword.Focus();
        Closing += (_, _) =>
        {
            closing = true;
            searchCancellation?.Cancel();
            downloadCancellation?.Cancel();
        };
        status.Text = "输入关键词后搜索；也可以直接打开网站浏览曲谱。";
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(keyword.Text)) { status.Text = "请输入歌曲名、演唱者或制谱者。"; keyword.Focus(); return; }
        searchCancellation?.Cancel();
        var requestCancellation = new CancellationTokenSource();
        searchCancellation = requestCancellation;
        SetBusy(true); results.Children.Clear(); status.Text = "正在搜索虫虫钢琴…";
        try
        {
            var found = await OnlineScoreClient.SearchAsync(keyword.Text, requestCancellation.Token);
            foreach (var score in found) results.Children.Add(CreateResult(score));
            status.Text = found.Count == 0 ? "没有找到结果，可以换一个关键词或打开网站搜索。" : $"找到 {found.Count} 首曲谱。选择一首后可打开详情页或下载 CCMZ。";
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            status.Text = "搜索失败：" + e.Message;
            results.Children.Add(new TextBlock { Text = "请检查网络连接，或点击“打开虫虫钢琴”在浏览器中继续。", Foreground = BrushOf("#B54708"), Margin = new Thickness(0, 8, 0, 0) });
        }
        finally
        {
            if (ReferenceEquals(searchCancellation, requestCancellation)) searchCancellation = null;
            requestCancellation.Dispose();
            SetBusy(false);
        }
    }

    private Border CreateResult(OnlineScore score)
    {
        var card = new Border { Background = BrushOf("#FFFFFF"), BorderBrush = BrushOf("#E4E8EF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(13), Margin = new Thickness(0, 0, 0, 9) };
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = score.Title, FontWeight = FontWeights.SemiBold, Foreground = BrushOf("#172B4D"), TextWrapping = TextWrapping.Wrap });
        info.Children.Add(new TextBlock { Text = score.Url, Foreground = BrushOf("#98A2B3"), FontSize = 12, Margin = new Thickness(0, 4, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(info, 0); grid.Children.Add(info);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var view = new Button { Content = "打开详情页" }; StyleButton(view, "#E9EEF8", "#25416C", 100);
        var download = new Button { Content = "下载 CCMZ" }; StyleButton(download, "#197A62", "#FFFFFF", 100);
        view.Click += (_, _) => OpenUrl(score.Url); download.Click += async (_, _) => await DownloadAsync(score, download);
        buttons.Children.Add(view); buttons.Children.Add(download); Grid.SetColumn(buttons, 1); grid.Children.Add(buttons); card.Child = grid;
        return card;
    }

    private async Task DownloadAsync(OnlineScore score, Button button)
    {
        if (downloadCancellation != null || closing) return;
        var dialog = new SaveFileDialog { Filter = "CCMZ 曲谱|*.ccmz|所有文件|*.*", FileName = OnlineScoreClient.SafeFileName(score.Title) + ".ccmz", AddExtension = true, OverwritePrompt = true, InitialDirectory = GetDownloadsDirectory() };
        if (dialog.ShowDialog(this) != true) return;
        var requestCancellation = new CancellationTokenSource();
        downloadCancellation = requestCancellation;
        button.IsEnabled = false; SetBusy(true); status.Text = "正在读取曲谱页面并下载 CCMZ…";
        try
        {
            byte[] data = await OnlineScoreClient.DownloadCcmzAsync(score.Url, requestCancellation.Token);
            bool imported = await CompleteDownloadAsync(score, dialog.FileName, data, requestCancellation.Token);
            if (imported)
            {
                Close();
            }
            else
            {
                status.Text = $"CCMZ 已保存，但自动导入失败，文件已保留：{dialog.FileName}";
            }
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested) { status.Text = "已取消 CCMZ 下载。"; }
        catch (Exception e) { status.Text = "下载失败：" + e.Message; }
        finally
        {
            if (ReferenceEquals(downloadCancellation, requestCancellation)) downloadCancellation = null;
            requestCancellation.Dispose();
            button.IsEnabled = true; SetBusy(false);
        }
    }

    private async Task<bool> CompleteDownloadAsync(OnlineScore score, string path, byte[] data, CancellationToken token)
    {
        await File.WriteAllBytesAsync(path, data, token);
        status.Text = "已保存 CCMZ，正在读取谱面和歌词…";
        var preview = await Task.Run(() => CcmzImporter.Import(data, score.Title, preserveTiming: true), token);
        string lyricMessage = preview.Lyrics != null ? "已读取源谱歌词" : await EnsureLyricsAsync(score.Title, path, token);
        status.Text = lyricMessage + "，正在编译多音谱并导入…";
        return await onCcmzSaved(path, data, preview, token);
    }

    private static async Task<string> EnsureLyricsAsync(string scoreTitle, string ccmzPath, CancellationToken token)
    {
        if (HasLyricsSidecar(ccmzPath)) return "已发现同名歌词";
        try
        {
            OnlineLyrics? lyrics = await OnlineScoreClient.FindLyricsAsync(scoreTitle, token);
            if (lyrics == null) return "未找到在线歌词";
            string lyricsPath = OnlineScoreClient.LyricsFilePath(ccmzPath);
            string text = lyrics.IsSynced ? lyrics.Text : LyricsTimeline.NormalizeText(lyrics.Text);
            await File.WriteAllTextAsync(lyricsPath, text, new UTF8Encoding(false), token);
            return lyrics.IsSynced ? "已下载原唱 LRC，请校准与曲谱的对应关系" : "已下载歌词，请校准起始音符";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return "在线歌词搜索失败，已继续导入曲谱";
        }
    }

    private static bool HasLyricsSidecar(string scorePath)
    {
        string directory = Path.GetDirectoryName(scorePath) ?? AppContext.BaseDirectory;
        string stem = Path.GetFileNameWithoutExtension(scorePath);
        return new[] { ".lyrics.txt", ".lrc", ".lyrics", ".lyric.txt" }
            .Select(suffix => Path.Combine(directory, stem + suffix))
            .Any(File.Exists);
    }

    private static string GetDownloadsDirectory()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(path) ? path : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    private void SetBusy(bool busy)
    {
        bool active = busy || searchCancellation != null || downloadCancellation != null || closing;
        search.IsEnabled = !active; openSite.IsEnabled = !active; keyword.IsEnabled = !active; Cursor = active ? Cursors.Wait : Cursors.Arrow;
    }

    private static SolidColorBrush BrushOf(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static void StyleTextBox(TextBox box)
    {
        box.Padding = new Thickness(8, 5, 8, 5); box.BorderBrush = BrushOf("#D0D5DD"); box.BorderThickness = new Thickness(1); box.Background = BrushOf("#FFFFFF"); box.Foreground = BrushOf("#172B4D"); box.VerticalContentAlignment = VerticalAlignment.Center;
    }

    private static void StyleButton(Button button, string background, string foreground, double minWidth)
    {
        button.MinWidth = minWidth; button.MinHeight = 38; button.Margin = new Thickness(0, 0, 8, 0); button.Padding = new Thickness(11, 6, 11, 6); button.FontWeight = FontWeights.SemiBold;
        var style = new Style(typeof(Button)); style.Setters.Add(new Setter(Control.BackgroundProperty, BrushOf(background))); style.Setters.Add(new Setter(Control.ForegroundProperty, BrushOf(foreground))); style.Setters.Add(new Setter(Control.BorderBrushProperty, BrushOf(background))); style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.OpacityProperty, 0.86)); style.Triggers.Add(hover); button.Style = style;
    }
}
