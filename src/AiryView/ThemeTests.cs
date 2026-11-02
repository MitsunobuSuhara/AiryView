using System.Security.Cryptography;
using System.Windows.Documents;

namespace AiryView;

internal static class ThemeTests
{
    private static readonly List<string> results = [];
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        results.Add("PASS: " + name);
    }
    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
    private static Color Resource(string name) => ColorOf((Brush)Application.Current.Resources[name]);
    private static double Luminance(Color color)
    {
        static double Linear(byte value) { double c = value / 255.0; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
    private static bool Readable(Brush foreground, Brush background)
    {
        double a = Luminance(ColorOf(foreground)), b = Luminance(ColorOf(background));
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelHeight * bitmap.PixelWidth * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return SHA256.HashData(pixels);
    }
    private static void Capture(Window window, string name, FrameworkElement? popup = null)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        if (popup?.IsVisible == true)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(popup), null,
                new Rect(window.PointFromScreen(popup.PointToScreen(new Point())), new Size(popup.ActualWidth, popup.ActualHeight)));
            bitmap.Render(visual);
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create($"artifacts/theme-tests/{name}.png"); encoder.Save(file);
    }
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory("artifacts/theme-tests");
        string markdownFixture = Path.GetFullPath("artifacts/theme-tests/preview.md");
        File.WriteAllText(markdownFixture, "# 見出し\n\n本文を読みやすく表示します。\n\n> 引用も読みやすく表示します。\n");
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiryView", "window.json");
        string? settingsBefore = File.Exists(settings) ? File.ReadAllText(settings) : null;
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 800, 560));
            dc.DrawText(new FormattedText("画像・PDF編集の確認", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), 28, Brushes.Black, 1), new Point(40, 35));
        }
        var source = new RenderTargetBitmap(800, 560, 96, 96, PixelFormats.Pbgra32); source.Render(drawing); source.Freeze();
        var main = new MainWindow(); main.Show();
        using var pdf = new PdfDocument(Path.Combine(AppContext.BaseDirectory, "Samples", "print-check.pdf"));
        var imageEditor = new VisualEditorWindow("theme-test.png", source, 0, _ => Task.CompletedTask, _ => false);
        var pdfEditor = new VisualEditorWindow(pdf, 0, _ => Task.CompletedTask, _ => false);
        imageEditor.Show(); pdfEditor.Show();
        for (int i = 0; pdfEditor.CurrentModelForTest == null && i < 100; i++) await Task.Delay(50);
        Check(pdfEditor.CurrentModelForTest != null, "PDF編集画面の準備");
        var originalImage = Pixels(imageEditor.CurrentModelForTest!.Render());
        var originalPdf = Pixels(pdfEditor.CurrentModelForTest!.Render());
        var print = new PrintWindow(pdf, 0, null); print.Show();
        var tools = new PdfToolsWindow(pdf, 0, _ => { }, _ => Task.CompletedTask); tools.Show();
        foreach (var theme in new[] { AppTheme.Modern, AppTheme.Classic, AppTheme.Modern })
        {
            ThemeManager.Apply(theme); await Task.Delay(350);
            string name = theme.ToString().ToLowerInvariant();
            var titleBar = (WindowTitleBar)((DockPanel)main.Content).Children[0];
            titleBar.FileMenuButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var fileCard = (Border)titleBar.FileMenuPopup!.Child;
            Check(titleBar.FileMenuPopup.IsOpen
                && ColorOf(fileCard.Background) == Resource("AiryPopupBackground")
                && Descendants<TextBlock>(titleBar.FileOpenItem!).Any(label => label.Text == "開く" && Readable(label.Foreground, fileCard.Background)),
                $"{theme}: タイトルバーのファイルメニュー");
            Capture(main, name + "-file-menu", fileCard);
            titleBar.FileMenuPopup.IsOpen = false;
            foreach (var editor in new[] { imageEditor, pdfEditor })
            {
                editor.Activate(); editor.UpdateLayout();
                Check(ColorOf(editor.Background) == Resource("AiryEditSurface"), $"{theme}: 編集画面が切替に追従");
                var labels = Descendants<TextBlock>(editor).Where(t => t.Text is "A" or "線" || t.Text.StartsWith("ツールを選び") || t.Text.StartsWith("文字はドラッグ")).ToArray();
                Check(labels.Length >= 2 && labels.All(t => Readable(t.Foreground, editor.Background)), $"{theme}: 項目名のコントラスト4.5以上");
                Check(Descendants<ScrollViewer>(editor).Any(v => v.Background is SolidColorBrush brush && brush.Color == Resource("AiryEditCanvas")), $"{theme}: 用紙周囲が切替に追従");
                editor.BeginText(new Point(50, 100));
                var text = editor.ActiveTextEditor!; text.Input.AppendText("さっと開いて、きっちり印刷。");
                await Task.Delay(100); text.OpenFormattingForTest();
                await Task.Delay(100);
                Check(ColorOf(((Border)text.PaletteForTest).Background) == Resource("AiryPopupBackground"), $"{theme}: 文字パレット");
                Check(text.Ink == Colors.Black && ColorOf(text.Input.Foreground) == Colors.Black, $"{theme}: 原稿の黒文字を維持");
                Check(ColorOf(((Border)text.ColorPaletteVisualForTest).Background) == Resource("AiryPopupBackground"), $"{theme}: 色パレット");
                Capture(editor, name + (editor == imageEditor ? "-image" : "-pdf"), text.PaletteForTest);
                editor.CancelEdits();
            }
            Check(Pixels(imageEditor.CurrentModelForTest!.Render()).SequenceEqual(originalImage), $"{theme}: 画像出力の画素を維持");
            Check(Pixels(pdfEditor.CurrentModelForTest!.Render()).SequenceEqual(originalPdf), $"{theme}: PDF原稿の画素を維持");
            imageEditor.Activate();
            imageEditor.BeginShape(new EditMark("arrow", new Point(100, 180), new Point(400, 250), "", Colors.Black, 4));
            await Task.Delay(100);
            var shape = imageEditor.ActiveShapeEditor!;
            Check(ColorOf(((Border)shape.PaletteForTest).Background) == Resource("AiryPopupBackground"), $"{theme}: 図形パレット");
            Capture(imageEditor, name + "-shape", shape.PaletteForTest); imageEditor.CancelEdits();
            Check(ColorOf(print.Background) == Resource("AirySurface"), $"{theme}: 印刷画面");
            var printButton = (Button)print.FindName("PrintButton");
            Check(Descendants<TextBlock>(printButton).All(t => Readable(t.Foreground, printButton.Background)), $"{theme}: 強調ボタンの文字を読める");
            Check(ColorOf(((Canvas)print.FindName("Preview")).Background) == Colors.White, $"{theme}: 印刷用紙は白");
            Capture(print, name + "-print"); Capture(tools, name + "-pdf-tools");
            var dialog = SoftConfirmDialog.CreateSave(main, "確認.pdf"); dialog.Show(); await Task.Delay(50);
            Check(ColorOf(((Border)dialog.Content).Background) == Resource("AiryPopupBackground"), $"{theme}: 保存確認");
            Capture(dialog, name + "-confirm"); dialog.Close();
            var discard = SoftConfirmDialog.CreateDiscard(imageEditor); discard.Show(); await Task.Delay(50);
            Check(!discard.AllowsTransparency && discard.Effect == null && discard.Background is SolidColorBrush,
                $"{theme}: 破棄確認を不透明な面に鮮明に描画");
            Capture(discard, name + "-discard"); discard.Close();
            await main.NewTextAsync();
            var note = (TextBox)main.FindName("TextEditor");
            note.Text = "たぬきごろりら　ぶらちゃ　可愛い　とっても";
            main.Activate(); note.Focus(); note.Select(9, 9);
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(note.SelectionOpacity == (theme == AppTheme.Modern ? .85 : .6) && ColorOf(note.SelectionBrush) == Resource("AiryEditorSelection")
                && Readable(note.Foreground, note.SelectionBrush), $"{theme}: メモの選択範囲と文字が明瞭");
            Capture(main, name + "-note-selection");
            note.Text = ""; main.CloseCurrentTabForTest();
            await main.OpenPathsAsync([markdownFixture]); main.UpdateLayout();
            var markdown = (FlowDocumentScrollViewer)main.FindName("MarkdownViewer");
            var quote = markdown.Document.Blocks.OfType<Paragraph>().First(block => block.Background != null);
            Check(markdown.Visibility == Visibility.Visible && Readable(markdown.Document.Foreground, markdown.Background)
                && Readable(quote.Foreground, quote.Background) && ColorOf(quote.Background) == Resource("AiryQuoteBackground"),
                $"{theme}: Markdownの見出し・本文・引用を読める");
            Capture(main, name + "-markdown-preview"); main.CloseCurrentTabForTest();
            Check(ColorOf(((Grid)main.FindName("ContentGrid")).Background) == Resource("AiryCanvas"), $"{theme}: タブを閉じても背景を維持");
            Capture(main, name + "-welcome");
        }
        // 開いたままのパレットも、別ウィンドウからの切替に追従する。
        imageEditor.Activate(); imageEditor.BeginText(new Point(50, 100));
        await Task.Delay(100); imageEditor.ActiveTextEditor!.OpenFormattingForTest(); await Task.Delay(100);
        var livePalette = (Border)imageEditor.ActiveTextEditor!.PaletteForTest;
        ThemeManager.Toggle(imageEditor); await Task.Delay(50);
        Check(ColorOf(livePalette.Background) == Resource("AiryPopupBackground"), "開いた文字パレットの即時切替");
        imageEditor.CancelEdits();
        tools.Close(); print.Close(); pdfEditor.Close(); imageEditor.Close(); main.Close();
        Check((File.Exists(settings) ? File.ReadAllText(settings) : null) == settingsBefore, "検証で利用者のテーマ・画面寸法を変更しない");
        File.WriteAllLines("artifacts/theme-test-results.txt", results);
    }
}
