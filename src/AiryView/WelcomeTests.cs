namespace AiryView;

internal static class WelcomeTests
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory("artifacts/welcome-tests");
        var results = new List<string>();
        void Check(bool condition, string name)
        {
            results.Add($"{(condition ? "PASS" : "FAIL")}: {name}");
            if (!condition) throw new Exception(name);
        }
        var originalTheme = ThemeManager.CurrentTheme;
        var window = new MainWindow { WindowState = WindowState.Normal, Height = 720 };
        try
        {
            window.Show();
            foreach (var theme in new[] { AppTheme.Modern, AppTheme.Classic })
            foreach (double width in new[] { 1000.0, 480.0 })
            {
                ThemeManager.Apply(theme);
                window.Width = width;
                await Task.Delay(180);
                window.UpdateLayout();
                var welcome = (FrameworkElement)window.FindName("Welcome");
                var buttons = Find<Button>(welcome).ToArray();
                Check(welcome.IsVisible && buttons.Length == 2 && buttons.All(b => b.IsVisible), $"{theme} 幅{width}: 共通の案内と2つの操作を表示");
                var text = Find<TextBlock>(welcome).Select(t => t.Text.Replace('\u00a0', ' ')).ToArray();
                Check(text.Contains("Open with ease. Print with precision.") && text.Contains("Open, edit, and print your documents."), $"{theme} 幅{width}: 英語の見出しと説明");
                Check(text.Contains("Open a file") && text.Contains("New note") && text.Contains("Or drag and drop a file here to open it."), $"{theme} 幅{width}: 英語の操作案内");
                Check(Find<TextBlock>(welcome).All(t => t.TransformToAncestor(window).TransformBounds(new Rect(t.RenderSize)).Right <= window.ActualWidth + 1), $"{theme} 幅{width}: 文章の右端が画面内");
                Capture(window, $"artifacts/welcome-tests/{theme}-{width}.png");
            }
            window.Height = 320;
            await Task.Delay(180);
            var scroll = (ScrollViewer)window.FindName("WelcomeScroll");
            Check(scroll.ScrollableHeight > 0, "低いウィンドウでは案内をスクロール可能");
            scroll.ScrollToEnd(); window.UpdateLayout();
            var drop = Find<TextBlock>((FrameworkElement)window.FindName("Welcome")).Last();
            Check(drop.TransformToAncestor(window).TransformBounds(new Rect(drop.RenderSize)).Bottom <= window.ActualHeight,
                "スクロール後は最後の操作案内まで表示");
            var note = Find<Button>((FrameworkElement)window.FindName("Welcome")).Last();
            note.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(180);
            Check(!((FrameworkElement)window.FindName("WelcomeScroll")).IsVisible, "新しいメモを開くと案内も非表示");
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(originalTheme);
            File.WriteAllLines("artifacts/welcome-test-results.txt", results);
        }
    }

    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }

    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
