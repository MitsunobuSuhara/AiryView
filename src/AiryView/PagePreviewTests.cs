using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;

namespace AiryView;

internal static class PagePreviewTests
{
    internal static async Task RunAsync(string path)
    {
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiryView", "window.json");
        string? before = File.Exists(settings) ? File.ReadAllText(settings) : null;
        var originalTheme = ThemeManager.CurrentTheme;
        var main = new MainWindow();
        try
        {
            main.Show(); main.WindowState = WindowState.Normal; main.Width = 1180; main.Height = 840; await main.OpenPathsAsync([path]); ThemeManager.Apply(AppTheme.Modern);
            main.TogglePagePreviewForTest();
            for (int i = 0; i < 600 && main.PagePreviewCountForTest < 3; i++) await Task.Delay(50);
            var items = (StackPanel)main.FindName("PagePreviewItems");
            if (items.Children.Count < 3) throw new Exception("複数ページのサムネイルが揃わない");
            var button = (Button)items.Children[2];
            var image = (Image)((StackPanel)button.Content).Children[0];
            var bitmap = (BitmapSource)image.Source;
            if (bitmap.PixelWidth < 600 || image.MaxWidth <= 150) throw new Exception("サムネイルの解像度・表示幅が不足");
            var sidebar = (Border)main.FindName("PagePreviewSidebar");
            var resize = Find<Thumb>(sidebar).First(thumb => thumb.Width == 8);
            resize.RaiseEvent(new DragDeltaEventArgs(140, 0)); main.UpdateLayout();
            if (sidebar.Width != 400 || image.MaxWidth != 344) throw new Exception("一覧幅と画像サイズが追従しない");
            main.ClickPagePreviewForTest(2);
            if (main.CurrentPdfPageForTest != 2) throw new Exception("ページ移動が失敗");
            var tip = (ToolTip)image.ToolTip; tip.IsOpen = true; await Task.Delay(150); main.UpdateLayout();
            if (!tip.IsOpen || Find<Image>(tip).First().Source != image.Source) throw new Exception("鮮明な拡大画像を表示できない");
            Directory.CreateDirectory("artifacts/page-preview-tests");
            Capture(main, "sidebar"); Capture(tip, "hover"); tip.IsOpen = false;
            main.TogglePagePreviewForTest();
            if (main.PagePreviewVisibleForTest) throw new Exception("一覧が閉じない");
            File.WriteAllText("artifacts/page-preview-test-results.txt", "PASS: 高解像度・幅変更・ページ移動・拡大表示・一覧終了");
        }
        finally
        {
            main.Close(); ThemeManager.Apply(originalTheme);
            if (before != null) File.WriteAllText(settings, before); else if (File.Exists(settings)) File.Delete(settings);
        }
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
    private static void Capture(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create($"artifacts/page-preview-tests/{name}.png"); encoder.Save(stream);
    }
}
