using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;

namespace AiryView;

internal static class ScrollBarTests
{
    internal static async Task RunAsync()
    {
        var original = ThemeManager.CurrentTheme;
        var input = new TextBox { Text = new string('W', 600), FontSize = 18,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
        var window = new Window { Width = 560, Height = 180, Content = input, Title = "横スクロール検証" };
        try
        {
            window.Show();
            foreach (var theme in new[] { AppTheme.Modern, AppTheme.Classic })
            {
                ThemeManager.Apply(theme); window.UpdateLayout(); await Task.Delay(100);
                var bar = Find<ScrollBar>(input).First(b => b.Orientation == Orientation.Horizontal);
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                if (!bar.IsVisible || bar.ActualHeight < 22 || track.Thumb.ActualWidth < 24)
                    throw new Exception("横バーの表示またはつまみの寸法が不足");
                input.ScrollToHorizontalOffset(0); window.UpdateLayout();
                ScrollBar.PageRightCommand.Execute(null, bar); window.UpdateLayout();
                if (input.HorizontalOffset <= 0) throw new Exception("レール右側のクリックで移動しない");
                ScrollBar.LineLeftCommand.Execute(null, bar); window.UpdateLayout();
                double before = input.HorizontalOffset;
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(30, 0)); window.UpdateLayout();
                if (input.HorizontalOffset <= before) throw new Exception("つまみのドラッグで移動しない");
                ScrollBar.ScrollToLeftEndCommand.Execute(null, bar); window.UpdateLayout();
                if (input.HorizontalOffset != 0) throw new Exception("左端へ戻らない");
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory("artifacts/scroll-tests");
                using var stream = File.Create($"artifacts/scroll-tests/{theme}.png"); encoder.Save(stream);
            }
        }
        finally { window.Close(); ThemeManager.Apply(original); }
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
}
