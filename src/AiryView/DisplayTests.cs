using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace AiryView;

internal static class DisplayTests
{
    private static readonly List<string> results = [];
    private static void Check(bool ok, string name)
    {
        results.Add($"{(ok ? "PASS" : "FAIL")}: {name}");
        if (!ok) throw new Exception(name);
    }

    internal static async Task RunAsync()
    {
        Directory.CreateDirectory("artifacts/display-tests");
        var theme = ThemeManager.CurrentTheme;
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 540, Height = 900 };
        try
        {
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var work = new Rect(-1080, 240, 1080, 1848);
                Check(WindowDisplayBounds.LogicalWorkSize(work, scale).Width == 1080 / scale, $"倍率{scale}: 作業領域をDIPへ換算");
                Check(work.Contains(WindowDisplayBounds.Fit(new Rect(-900, 400, 1800, 2000), work)), $"倍率{scale}: 負座標の画面へ収める");
            }
            string fixture = System.IO.Path.GetFullPath("artifacts/display-tests/portrait.md");
            File.WriteAllText(fixture, "# 縦置きモニターの表示確認\n\n" + string.Join("\n\n", Enumerable.Range(1, 75)
                .Select(n => $"- {n}：" + string.Concat(Enumerable.Repeat("狭い画面でも本文を折り返し、右端の文字まで読めます。", 8)))) + "\n\n最終段落の確認");
            window.Show();
            await window.OpenPathsAsync([fixture]);
            await Settle(window);
            var viewer = (FlowDocumentScrollViewer)window.FindName("MarkdownViewer");
            foreach (var style in new[] { AppTheme.Modern, AppTheme.Classic })
            {
                ThemeManager.Apply(style);
                foreach (double width in new[] { 480.0, 540.0, 720.0 })
                {
                    window.Width = width;
                    foreach (double zoom in new[] { 100.0, 200.0 })
                    {
                        viewer.Zoom = zoom;
                        await Settle(window);
                        CheckContent(window, $"{style} 幅{width} 倍率{zoom}");
                    }
                }
            }
            window.Width = 480;
            Check(window.SearchTextForTest("狭い画面") == 75, "狭幅で75段落の検索結果を表示");
            await Settle(window);
            var search = (Panel)window.FindName("TextSearchBar");
            Check(Find<Button>(search).All(b => InsideWindow(b, window)), "狭幅でも検索を閉じるボタンまで表示");
            Find<Button>(search).Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            viewer.Zoom = 100;
            ThemeManager.Apply(AppTheme.Modern);
            var hwnd = new WindowInteropHelper(window).Handle;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                window.WindowState = WindowState.Normal;
                await Settle(window);
                var area = screen.WorkingArea;
                SetWindowPos(hwnd, IntPtr.Zero, area.Left, area.Top, Math.Min(area.Width, 1400), Math.Min(area.Height, 1000), 0x0014);
                await Settle(window);
                CheckOnMonitor(window, screen.DeviceName + " 通常");
                window.WindowState = WindowState.Maximized;
                await Settle(window);
                CheckOnMonitor(window, screen.DeviceName + " 最大化");
                CheckContent(window, screen.DeviceName + " 最大化");
                Capture(window, $"artifacts/display-tests/{screen.DeviceName.Replace("\\", "").Replace(".", "")}-max.png");
                window.WindowState = WindowState.Normal;
                await Settle(window);
                CheckOnMonitor(window, screen.DeviceName + " 元に戻す");
                await window.ToggleMarkdownModeAsync();
                await Settle(window);
                var editor = (TextBox)window.FindName("TextEditor");
                var horizontal = Find<ScrollBar>(editor).First(b => b.Orientation == Orientation.Horizontal);
                Check(horizontal.IsVisible && OnScreen(horizontal, window), screen.DeviceName + " ソースの横バーが画面内");
                editor.ScrollToHorizontalOffset(180);
                await Settle(window);
                Check(editor.HorizontalOffset > 0, screen.DeviceName + " 横スクロール操作");
                await window.ToggleMarkdownModeAsync();
            }
            // 画面幅より大きい最小幅が指定されても、画面外へ押し出さない。
            var secondary = new Window { Width = 1180, Height = 840, MinWidth = 1060, MinHeight = 680, Content = new DockPanel() };
            WindowTitleBar.Install(secondary, (DockPanel)secondary.Content);
            WindowDisplayBounds.Attach(secondary);
            try
            {
                secondary.Show();
                var portrait = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.WorkingArea.Width).First().WorkingArea;
                SetWindowPos(new WindowInteropHelper(secondary).Handle, IntPtr.Zero, portrait.Left, portrait.Top, portrait.Width, portrait.Height, 0x0014);
                await Settle(secondary);
                secondary.WindowState = WindowState.Maximized;
                await Settle(secondary);
                CheckOnMonitor(secondary, "大きい最小幅の編集窓");
            }
            finally { secondary.Close(); }
            var startupArea = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.WorkingArea.Width).First().WorkingArea;
            var reopened = new MainWindow { WindowState = WindowState.Normal };
            try
            {
                var reopenedHandle = new WindowInteropHelper(reopened).EnsureHandle();
                SetWindowPos(reopenedHandle, IntPtr.Zero, startupArea.Left, startupArea.Top, startupArea.Width, startupArea.Height, 0x0014);
                await Settle(reopened);
                reopened.WindowState = WindowState.Maximized;
                reopened.Show(); await Settle(reopened);
                await reopened.OpenPathsAsync([fixture]); await Settle(reopened);
                Check(WindowDisplayBounds.ReadDisplay(reopenedHandle).WorkArea.Left == startupArea.Left, "指定画面で新しいウィンドウを表示");
                CheckOnMonitor(reopened, "新しいウィンドウの最大化");
                CheckContent(reopened, "新しいウィンドウの最大化");
            }
            finally { reopened.Close(); }
        }
        finally
        {
            window.Close(); ThemeManager.Apply(theme);
            File.WriteAllLines("artifacts/display-test-results.txt", results);
        }
    }

    private static async Task Settle(Window window) { await Task.Delay(180); window.UpdateLayout(); }

    private static void CheckContent(MainWindow window, string label)
    {
        var viewer = (FlowDocumentScrollViewer)window.FindName("MarkdownViewer");
        var scroll = Find<ScrollViewer>(viewer).First();
        var bar = Find<ScrollBar>(viewer).First(b => b.Orientation == Orientation.Vertical);
        var title = Find<WindowTitleBar>(window).First();
        Check(OnScreen(title.CloseButton, window) && OnScreen(title.MaximizeButton, window), label + " 右上の操作が画面内");
        Check(bar.IsVisible && OnScreen(bar, window) && scroll.ScrollableHeight > 0, label + " 縦バーが画面内");
        Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, label + " 本文が幅内で折り返す");
        scroll.ScrollToEnd(); window.UpdateLayout();
        Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, label + " 最終段落までスクロール");
        scroll.ScrollToHome(); window.UpdateLayout();
    }

    private static bool OnScreen(FrameworkElement element, Window window)
    {
        var display = WindowDisplayBounds.ReadDisplay(new WindowInteropHelper(window).Handle).WorkArea;
        var a = element.PointToScreen(new Point());
        var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        display.Inflate(1, 1);
        if (!display.Contains(a) || !display.Contains(b)) results.Add($"INFO: 画面外 {element.GetType().Name}: {a} - {b}, area={display}");
        return display.Contains(a) && display.Contains(b) && element.ActualWidth > 0 && InsideWindow(element, window);
    }

    private static bool InsideWindow(FrameworkElement element, Window window)
    {
        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1;
    }

    private static void CheckOnMonitor(Window window, string label)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var display = WindowDisplayBounds.ReadDisplay(hwnd);
        results.Add($"INFO: {label}: work={display.WorkArea}, dpi={display.Scale}, window={window.ActualWidth}x{window.ActualHeight}");
        var title = Find<WindowTitleBar>(window).First();
        var limits = new int[10];
        SendMessage(hwnd, 0x0024, IntPtr.Zero, limits);
        Check(limits[6] >= Math.Min(Math.Ceiling(window.MinWidth * display.Scale), display.WorkArea.Width)
            && limits[7] >= Math.Min(Math.Ceiling(window.MinHeight * display.Scale), display.WorkArea.Height), label + " 手動縮小の最小寸法を維持");
        Check(OnScreen(title.CloseButton, window) && OnScreen(title.MinimizeButton, window), label + " タイトル操作が作業領域内");
        Check(window.ActualWidth <= display.WorkArea.Width / display.Scale + 1 && window.ActualHeight <= display.WorkArea.Height / display.Scale + 1,
            label + " ウィンドウ寸法が作業領域内");
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }

    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, [In, Out] int[] data);
}
