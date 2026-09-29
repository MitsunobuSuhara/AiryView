using System.Windows.Media.Imaging;
using System.IO;

namespace AiryView;

public static class ThemePreviewCapture
{
    public static async Task CaptureBothAsync()
    {
        Directory.CreateDirectory("artifacts/theme-previews");

        // 1. Capture Modern Theme
        {
            ThemeManager.Apply(AppTheme.Modern);
            var window = new MainWindow(showWelcome: true) { Width = 1180, Height = 800 };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(500);
            window.UpdateLayout();
            SaveWindow(window, "artifacts/theme-previews/modern-welcome.png");

            // Open a note to capture Modern with tabs and toolbar
            await window.NewTextAsync();
            window.UpdateLayout();
            await Task.Delay(200);
            SaveWindow(window, "artifacts/theme-previews/modern-editor.png");
            window.Close();
        }

        // 2. Capture Classic Theme
        {
            ThemeManager.Apply(AppTheme.Classic);
            var window = new MainWindow(showWelcome: true) { Width = 1180, Height = 800 };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(500);
            window.UpdateLayout();
            SaveWindow(window, "artifacts/theme-previews/classic-welcome.png");

            await window.NewTextAsync();
            window.UpdateLayout();
            await Task.Delay(200);
            SaveWindow(window, "artifacts/theme-previews/classic-editor.png");
            window.Close();
        }

        // Reset to Modern for user default
        ThemeManager.Apply(AppTheme.Modern);
    }

    private static void SaveWindow(Window window, string path)
    {
        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
