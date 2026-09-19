using System.Diagnostics;
using System.Drawing.Printing;
using System.Text;

namespace AiryView;

public static class SelfTest
{
    private static readonly List<string> Results = [];
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Results.Add("PASS: " + name);
    }
    private static bool Near(double a, double b, double tolerance = .05) => Math.Abs(a - b) <= tolerance;
    public static async Task RunUiAsync()
    {
        Directory.CreateDirectory("artifacts");
        string fixture = System.IO.Path.GetFullPath("artifacts/dimension-check.pdf");
        CreateFixture(fixture, 5);
        var window = new MainWindow(); window.Show();
        window.UpdateLayout();
        Check(window.TabCountForTest == 0 && ((FrameworkElement)window.FindName("Welcome")).IsVisible && !((FrameworkElement)window.FindName("TextEditorArea")).IsVisible, "単体起動の初期画面はメモを自動作成せず案内を表示");
        Check(!((FrameworkElement)window.FindName("PageControls")).IsVisible && !((FrameworkElement)window.FindName("PrintButton")).IsVisible, "初期表示にPDF用操作の一瞬の表示を挟まない");
        Capture(window, "artifacts/welcome-window.png");
        await window.NewTextAsync(); window.UpdateLayout();
        int firstTabCount = window.TabCountForTest;
        await window.NewTextAsync(); window.UpdateLayout();
        Check(window.TabCountForTest == firstTabCount + 1, "＋／Ctrl＋T相当で新しいメモタブを追加");
        Check(FindButtons((DependencyObject)((TabControl)window.FindName("Tabs")).SelectedItem).Any(b => b.Content as string == "×"), "各タブ内に閉じる×を表示");
        var blankEditor = (TextBox)window.FindName("TextEditor");
        blankEditor.Text = "新しいメモ";
        string blankPath = System.IO.Path.GetFullPath("artifacts/new-note.txt");
        Check(window.SaveTextToPathForTest(blankPath) && File.ReadAllText(blankPath, Encoding.UTF8) == "新しいメモ", "単体起動用の白紙TXTを作成して保存");
        string markdownFixture = System.IO.Path.GetFullPath("artifacts/markdown-view.md");
        File.WriteAllText(markdownFixture, "# 見出し\n\n本文と**太字**です。\n\n- 項目\n\n> 引用\n\n" + string.Join("\n\n", Enumerable.Repeat("スクロール確認用の本文です。", 80)));
        await window.OpenPathsAsync([markdownFixture]); window.UpdateLayout();
        var markdownViewer = (System.Windows.Controls.FlowDocumentScrollViewer)window.FindName("MarkdownViewer");
        Check(markdownViewer.Visibility == Visibility.Visible && markdownViewer.Document.Blocks.Count >= 4, "MarkdownをPDFと同じタブ内で整形表示");
        var selectionBadge = (FrameworkElement)window.FindName("TextSelectionBadge");
        var selectionInfo = (TextBlock)window.FindName("TextSelectionInfo");
        var markdownModeButton = (Button)window.FindName("MarkdownModeButton");
        Check(markdownModeButton.Visibility == Visibility.Visible && selectionInfo.Text.Contains("プレビュー / Preview") && selectionInfo.Text.Contains("MD"), "MarkdownのPreview状態と切替アイコンを表示");
        await window.ToggleMarkdownModeAsync(); window.UpdateLayout();
        var markdownEditor = (TextBox)window.FindName("TextEditor");
        Check(markdownEditor.IsVisible && markdownEditor.Text.Contains("# 見出し") && selectionInfo.Text.Contains("Source"), "MarkdownをSource表示へ切り替え");
        Check(window.ToggleWrapForTest() && markdownEditor.TextWrapping == TextWrapping.Wrap && markdownEditor.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && selectionInfo.Text.Contains("折返し ON / Wrap ON"), "Markdown Sourceの折り返しをON");
        Check(!window.ToggleWrapForTest() && markdownEditor.TextWrapping == TextWrapping.NoWrap && markdownEditor.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto && selectionInfo.Text.Contains("折返し OFF / Wrap OFF"), "Markdown Sourceの折り返しをOFF");
        Check(string.Equals(MainWindow.LastRecentFileForTest, markdownFixture, StringComparison.OrdinalIgnoreCase), "開いたMarkdownをWindows Recent登録対象にする");
        markdownEditor.Text += "\n\n## 編集確認\n保存される本文";
        string editedMarkdown = System.IO.Path.GetFullPath("artifacts/markdown-edited.md");
        Check(window.SaveTextToPathForTest(editedMarkdown) && File.ReadAllText(editedMarkdown, Encoding.UTF8).Contains("編集確認"), "Markdown Sourceを安全に保存");
        await window.ToggleMarkdownModeAsync(); window.UpdateLayout();
        Check(markdownViewer.Visibility == Visibility.Visible && window.SearchTextForTest("編集確認") == 1, "編集内容をPreview切替時に再整形");
        Check(((FrameworkElement)window.FindName("DocumentToolbar")).Visibility == Visibility.Visible && ((FrameworkElement)window.FindName("PageControls")).Visibility == Visibility.Collapsed && ((FrameworkElement)window.FindName("PrintButton")).Visibility == Visibility.Collapsed, "Markdownは文章用の倍率操作だけを表示");
        double markdownZoom = window.ActiveZoomForTest; window.ZoomByWheel(120); window.UpdateLayout();
        Check(window.ActiveZoomForTest > markdownZoom && markdownViewer.Zoom > 100, "Markdown・TXTをCtrlホイール相当で文字拡大");
        Check(window.SearchTextForTest("本文") >= 1 && ((FrameworkElement)window.FindName("TextSearchBar")).Visibility == Visibility.Visible, "Ctrl＋F用の文章検索と強調表示");
        window.ScrollMarkdownByWheel(-120); window.UpdateLayout();
        Check(window.MarkdownOffset >= 170, "Markdownのホイール1段で十分な距離をスクロール");
        Capture(window, "artifacts/markdown-window.png");        string textFixture = System.IO.Path.GetFullPath("artifacts/text-view.txt");
        File.WriteAllText(textFixture, "1行目\n2行目", Encoding.UTF8);
        await window.OpenPathsAsync([textFixture]); window.UpdateLayout();
        var textEditor = (TextBox)window.FindName("TextEditor");
        Check(textEditor.Visibility == Visibility.Visible && textEditor.IsReadOnly == false, "TXTを同じタブで編集表示");
        Check(window.SearchTextForTest("1行目") == 1 && textEditor.SelectedText == "1行目", "TXTをCtrl＋F相当で検索");
        var characterCount = MainWindow.CountCharacterWidths("ＡあA1");
        Check(characterCount == (4, 2, 2, 6), "全角・半角・半角換算の文字数を区別");
        Check(selectionBadge.Visibility == Visibility.Visible && selectionInfo.Text.Contains("選択 3字 / 3 chars") && selectionInfo.Text.Contains("全角 2 / Full 2") && selectionInfo.Text.Contains("半角 1 / Half 1") && selectionInfo.Text.Contains("幅 5 / Width 5"), "TXTの選択文字数を右下に表示");
        textEditor.Select(0, 0); window.UpdateLayout();
        Check(selectionInfo.Text.Contains("行 1 / Ln 1") && selectionInfo.Text.Contains("列 1 / Col 1") && selectionInfo.Text.Contains("UTF-8") && selectionInfo.Text.Contains("改行形式 LF / EOL LF"), "選択していないTXTで行・列・文字コードを表示");
        int textTabCount = window.TabCountForTest;
        await window.OpenPathsAsync([textFixture]); window.UpdateLayout();
        Check(window.TabCountForTest == textTabCount && string.Equals(window.CurrentPathForTest, textFixture, StringComparison.OrdinalIgnoreCase), "同じTXTを再度開くと既存タブへ移動");
        textEditor.Text += "\n追記"; window.SaveTextForTest();
        Check(File.ReadAllText(textFixture, Encoding.UTF8).Contains("追記"), "TXTをCtrl＋S相当で上書き保存");
        Check(((FrameworkElement)window.FindName("PrintButton")).Visibility == Visibility.Visible, "TXTで印刷ボタンを表示");
        var tabs = (TabControl)window.FindName("Tabs");
        var firstTextTab = tabs.Items.Cast<TabItem>().First(t => string.Equals(t.ToolTip?.ToString(), textFixture, StringComparison.OrdinalIgnoreCase));
        textEditor.Text = "1つ目の未保存内容";
        await window.NewTextAsync(); window.UpdateLayout();
        var secondTextTab = (TabItem)tabs.SelectedItem;
        textEditor.Text = "2つ目の未保存内容";
        tabs.SelectedItem = firstTextTab; await Task.Delay(100); window.UpdateLayout();
        Check(textEditor.Text == "1つ目の未保存内容", "複数TXTタブを切り替えて1つ目の未保存内容を保持");
        string firstSaved = System.IO.Path.GetFullPath("artifacts/multi-tab-first.txt");
        Check(window.SaveTextToPathForTest(firstSaved) && File.ReadAllText(firstSaved) == "1つ目の未保存内容", "選択中のTXTタブだけを正しく保存");
        tabs.SelectedItem = secondTextTab; await Task.Delay(100); window.UpdateLayout();
        Check(textEditor.Text == "2つ目の未保存内容", "複数TXTタブを切り替えて2つ目の未保存内容を保持");
        string secondSaved = System.IO.Path.GetFullPath("artifacts/multi-tab-second.txt");
        Check(window.SaveTextToPathForTest(secondSaved) && File.ReadAllText(secondSaved) == "2つ目の未保存内容", "別タブの内容を混ぜずに保存");
        Check(!Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(secondSaved)!, ".*.tmp").Any(), "安全な置換保存後に一時ファイルを残さない");
        string utf16Fixture = System.IO.Path.GetFullPath("artifacts/utf16-view.txt");
        File.WriteAllText(utf16Fixture, "UTF-16 日本語", new UnicodeEncoding(false, true));
        await window.OpenPathsAsync([utf16Fixture]); window.UpdateLayout();
        Check(textEditor.Text == "UTF-16 日本語", "UTF-16のBOMを本文へ混ぜずに読み込む");
        File.WriteAllText(utf16Fixture, "外部アプリの変更", new UnicodeEncoding(false, true));
        Check(window.CurrentTextHasExternalChangeForTest(), "開いた後の外部変更を検知");
        Check(window.FindName("TabsScroller") is ScrollViewer { HorizontalScrollBarVisibility: ScrollBarVisibility.Auto }, "多数のタブを横スクロール可能にする");
        var toolbar = (WrapPanel)window.FindName("DocumentToolbar");
        var tabsScroller = (ScrollViewer)window.FindName("TabsScroller");
        var tabsControl = (TabControl)window.FindName("Tabs");
        Check(Double.IsNaN(toolbar.Height) && toolbar.Margin.Top == 5 && toolbar.Margin.Bottom == 5 && tabsScroller.Margin.Bottom == 3 && tabsControl.MinHeight == 34 && tabsControl.Items.OfType<TabItem>().All(tab => tab.MinHeight == 34), "操作行とタブ行を省スペースに保つ");
        var extremeSvg = MainWindow.SvgRasterSize(1e30, 1);
        Check(extremeSvg.Width <= 8192 && extremeSvg.Height >= 1, "極端に細長いSVGのメモリ使用量を制限");
        var largeSvg = MainWindow.SvgRasterSize(100000, 100000);
        Check((long)largeSvg.Width * largeSvg.Height <= 16_000_000, "SVG描画を1600万画素以下に制限");
        bool invalidSvgRejected = false;
        try { MainWindow.SvgRasterSize(double.NaN, 100); } catch (IOException) { invalidSvgRejected = true; }
        Check(invalidSvgRejected, "不正なSVG寸法を描画前に拒否");
        string svgFixture = System.IO.Path.GetFullPath("artifacts/vector-view.svg");
        File.WriteAllText(svgFixture, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"640\" height=\"360\" viewBox=\"0 0 640 360\"><rect width=\"640\" height=\"360\" fill=\"#E6F3FF\"/><circle cx=\"180\" cy=\"180\" r=\"110\" fill=\"#2563EB\"/><text x=\"320\" y=\"205\" font-size=\"48\" fill=\"#192432\">SVG</text></svg>", Encoding.UTF8);
        await window.OpenPathsAsync([svgFixture]); window.UpdateLayout(); await Task.Delay(100);
        Check(((Image)window.FindName("ReaderImage")).Source is BitmapSource { PixelWidth: > 0 } && string.Equals(window.CurrentPathForTest, svgFixture, StringComparison.OrdinalIgnoreCase), "SVGを同じタブで表示");
        string webpFixture = System.IO.Path.GetFullPath("artifacts/webp-view.webp");
        using (var webpBitmap = new SkiaSharp.SKBitmap(48, 32))
        {
            webpBitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
            using var webpImage = SkiaSharp.SKImage.FromBitmap(webpBitmap);
            using var webpData = webpImage.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 100);
            File.WriteAllBytes(webpFixture, webpData.ToArray());
        }
        await window.OpenPathsAsync([webpFixture]); window.UpdateLayout(); await Task.Delay(100);
        Check(((Image)window.FindName("ReaderImage")).Source is BitmapSource { PixelWidth: 48 } && string.Equals(window.CurrentPathForTest, webpFixture, StringComparison.OrdinalIgnoreCase), "WebPを同じタブで表示");
        string imageFixture = System.IO.Path.GetFullPath("artifacts/image-view.png");
        var testBitmap = new System.Windows.Media.Imaging.WriteableBitmap(3200, 1800, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = Enumerable.Repeat((byte)180, 3200 * 1800 * 4).ToArray(); testBitmap.WritePixels(new Int32Rect(0, 0, 3200, 1800), pixels, 3200 * 4, 0);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(testBitmap));
        using (var imageFile = File.Create(imageFixture)) encoder.Save(imageFile);
        await window.OpenPathsAsync([imageFixture]); window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
        Check(((FrameworkElement)window.FindName("ImageViewer")).Visibility == Visibility.Visible && ((Image)window.FindName("ReaderImage")).Source is BitmapSource source && source.PixelWidth == 3200 && window.ActiveZoomForTest < 1, "大きなPNGを初回から画面幅に合わせて表示");
        double imageZoom = window.ActiveZoomForTest; window.ZoomByWheel(120); window.UpdateLayout();
        Check(window.ActiveZoomForTest > imageZoom && ((Image)window.FindName("ReaderImage")).Width > 0, "画像をCtrlホイール相当で拡大");
        Check(((FrameworkElement)window.FindName("RotationControls")).Visibility == Visibility.Visible && ((FrameworkElement)window.FindName("FitWidthButton")).Visibility == Visibility.Visible && ((FrameworkElement)window.FindName("ResetRotationButton")).Visibility == Visibility.Visible, "画像の回転・リセット・画面内フィットを表示");
        int tabsBeforeClose = window.TabCountForTest;
        window.CloseCurrentTabForTest(); window.UpdateLayout();
        Check(window.TabCountForTest == tabsBeforeClose - 1, "保存済みタブを閉じて復元履歴へ追加");
        Check(await window.RestoreClosedTabAsync(), "閉じたタブの復元を実行");
        window.UpdateLayout();
        Check(window.TabCountForTest == tabsBeforeClose && string.Equals(window.CurrentPathForTest, imageFixture, StringComparison.OrdinalIgnoreCase), "閉じた画像タブを同じファイルから復元");
        Check(!await window.RestoreClosedTabAsync(), "復元済みの履歴を重複して開かない");
        Capture(window, "artifacts/image-window.png");
        string selectablePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "print-check.pdf");
        await window.OpenPathsAsync([selectablePath]); window.UpdateLayout();
        string selectedPdfText = window.SelectPdfTextForTest(0, 0, 100);
        Check(!string.IsNullOrWhiteSpace(selectedPdfText), "PDFの文字情報を選択対象として取得");
        var selectionCanvas = (Canvas)((Grid)((StackPanel)window.FindName("PagesHost")).Children[0]).Children[1];
        Check(selectionCanvas.Children.Count > 0, "PDFの選択範囲を青い層で表示");
        Check(window.CopySelectedPdfForTest() && Clipboard.GetText() == selectedPdfText, "選択したPDF文字をクリップボードへコピー");
        using (var selectableDocument = new PdfDocument(selectablePath)) Check(selectableDocument.TextCharacters(0).Any(c => !c.RelativeBox.IsEmpty), "PDF文字の画面座標を取得");
        string uiPath = Environment.GetEnvironmentVariable("AIRYVIEW_UI_PDF") ?? fixture;
        using var uiDocument = new PdfDocument(uiPath);
        int pageTotal = uiDocument.Count;
        Check(pageTotal >= 2, "連続表示の検証対象が複数ページ");
        await window.OpenPathsAsync([uiPath]);
        window.UpdateLayout();
        Check(selectionBadge.Visibility == Visibility.Visible && selectionInfo.Text.Contains("縦 / Portrait") && selectionInfo.Text.Contains("原寸 / Actual size") && selectionInfo.Text.Contains("100%"), "PDFの原本サイズ・印刷モード・倍率を表示");
        int pdfTabCount = window.TabCountForTest;
        await window.OpenPathsAsync([uiPath]); window.UpdateLayout();
        Check(window.TabCountForTest == pdfTabCount && string.Equals(window.CurrentPathForTest, uiPath, StringComparison.OrdinalIgnoreCase), "同じPDFを再度開くと既存タブへ移動");
        window.WindowState = WindowState.Minimized; App.BringWindowToFront(window); window.UpdateLayout();
        Check(window.WindowState != WindowState.Minimized && window.IsVisible, "外部からファイルを開くと既存画面を最小化解除して表示");
        window.WindowState = WindowState.Maximized; App.BringWindowToFront(window); window.UpdateLayout();
        Check(window.WindowState == WindowState.Maximized, "外部からファイルを開いても最大化状態を維持");
        window.WindowState = WindowState.Minimized; App.BringWindowToFront(window); window.UpdateLayout();
        Check(window.WindowState == WindowState.Maximized, "最大化から最小化しても元の最大化状態へ復帰");
        Capture(window, "artifacts/viewer-window.png");
        var viewer = (ScrollViewer)window.FindName("Viewer");
        var host = (StackPanel)window.FindName("PagesHost");
        var pageNumber = (TextBox)window.FindName("PageNumber");
        var pageCount = (TextBlock)window.FindName("PageCount");
        Check(pageNumber.TextAlignment == TextAlignment.Center && pageNumber.HorizontalContentAlignment == HorizontalAlignment.Center && pageNumber.VerticalContentAlignment == VerticalAlignment.Center && pageCount.MinWidth >= 42 && pageCount.VerticalAlignment == VerticalAlignment.Center, "ページ番号と総ページ数を中央高さで整列");
        Check(host.Children.Count == pageTotal, "PDFの全ページを連続して配置");
        var firstSurface = (Grid)host.Children[0];
        var firstBitmap = (BitmapSource)((Image)firstSurface.Children[0]).Source;
        Check(Math.Abs(firstBitmap.PixelWidth - firstSurface.ActualWidth * VisualTreeHelper.GetDpi(window).DpiScaleX) <= 1, "100％表示を画面画素に合わせて二重縮小を避ける");
        Check(Near(firstSurface.Width, uiDocument.SizeMm(0).Width * 96 / 25.4), "高解像度でも100％の表示寸法を保持");
        Check(host.Children.Cast<Grid>().Select(g => ((Image)g.Children[0]).Source).OfType<BitmapSource>().Sum(b => (long)b.PixelWidth * b.PixelHeight) < 12_100_000, "高解像度描画も画面付近の画像予算内");
        viewer.ScrollToTop(); window.ScrollPdfByWheel(-120); window.UpdateLayout();
        Check(viewer.VerticalOffset >= 119, "PDFのホイール1段で120px進む");
        viewer.ScrollToTop();
        // ScrollToOffsetだけでは実際のホイール経路の不具合を見落とすため、入力イベントでも検証する。
        for (int i = 0; i < 35; i++)
        {
            ((Grid)host.Children[0]).RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent });
            await Task.Delay(20);
        }
        await Task.Delay(800); window.UpdateLayout();
        Check(viewer.VerticalOffset > ((Grid)host.Children[0]).Height, "ホイール操作で1ページ目を越えて進む");
        viewer.ScrollToTop(); await Task.Delay(400);
        var second = (Grid)host.Children[1];
        viewer.ScrollToVerticalOffset(second.TranslatePoint(new Point(), host).Y - 150);
        await Task.Delay(600); window.UpdateLayout();
        Check(((Image)((Grid)host.Children[0]).Children[0]).Source != null && ((Image)second.Children[0]).Source != null, "境目で前後のページを同時に描画");
        Capture(window, "artifacts/continuous-boundary.png");
        viewer.ScrollToBottom(); await Task.Delay(600); window.UpdateLayout();
        Check(pageNumber.Text == pageTotal.ToString(), "スクロールで最終ページに移動しページ番号を更新");
        Check(((Image)((Grid)host.Children[pageTotal - 1]).Children[0]).Source != null, "最終ページの内容を描画");
        Check(((Image)((Grid)host.Children[0]).Children[0]).Source == null, "画面から離れたページの画像を解放");
        var zoomButton = FindButtons((DependencyObject)window.FindName("DocumentToolbar")).First(b => b.Content as string == "＋");
        double oldHeight = second.Height;
        zoomButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(600); window.UpdateLayout();
        Check(second.Height > oldHeight && pageNumber.Text == pageTotal.ToString(), "連続表示の拡大後も閲覧ページを維持");
        var zoomInput = (TextBox)window.FindName("ZoomText");
        void EnterZoom(string text)
        {
            zoomInput.Text = text;
            zoomInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        }
        EnterZoom("100");
        await Task.Delay(300); window.UpdateLayout();
        Check(Near(second.Height, oldHeight) && zoomInput.Text == "100", "倍率手入力で100％へ正確に戻す");
        EnterZoom("125%");
        Check(Near(second.Height, oldHeight * 1.25), "％付きの倍率手入力を反映");
        EnterZoom("NaN");
        Check(Near(second.Height, oldHeight * 1.25) && zoomInput.Text == "125", "不正な表示倍率で状態を壊さない");
        EnterZoom("95"); window.ZoomByWheel(120);
        Check(Near(second.Height, oldHeight) && zoomInput.Text == "100", "Ctrlホイール拡大で100％に止まる");
        window.ZoomByWheel(120);
        Check(Near(second.Height, oldHeight), "連続ホイールでも100％で短く停止");
        await Task.Delay(500); window.ZoomByWheel(120);
        Check(second.Height > oldHeight, "100％から次のホイールで拡大できる");
        EnterZoom("105"); window.ZoomByWheel(-120);
        Check(Near(second.Height, oldHeight) && zoomInput.Text == "100", "Ctrlホイール縮小で100％に止まる");
        await Task.Delay(500); window.ZoomByWheel(-120);
        Check(second.Height < oldHeight, "100％から次のホイールで縮小できる");
        EnterZoom("100");
        Check(!FindButtons(window).Any(b => b.Content as string == "100%"), "100％ボタンを削除");
        var fit = (Button)window.FindName("FitWidthButton");
        Check(((Panel)fit.Parent).Children[((Panel)fit.Parent).Children.Count - 1] == fit, "画面幅に合わせるを右端に配置");
        Check(window.FindName("SelectRegion") == null && fit.ToolTip?.ToString() == "画面幅に合わせる / Fit to width", "範囲選択を削除して画面幅の文言に変更");
        window.WindowState = WindowState.Normal; window.Width = 860;
        await Task.Delay(200); window.UpdateLayout();
        fit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(second.Height < oldHeight, "画面幅に合わせて100％未満の倍率にする");
        fit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Near(second.Height, oldHeight) && zoomInput.Text == "100", "画面幅に合わせるを2回押すと100％へ戻る");
        fit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(second.Height < oldHeight, "100％から画面幅に合わせて表示できる");
        for (int i = 0; i < 40 && !Near(second.Height, oldHeight); i++) window.ZoomByWheel(120);
        Check(Near(second.Height, oldHeight) && zoomInput.Text == "100", "画面幅調整後の連続ホイールも100％で停止");
        fit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 40 && !Near(second.Height, oldHeight); i++) zoomButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Near(second.Height, oldHeight) && zoomInput.Text == "100", "画面幅調整後の＋ボタンも100％で停止");
        EnterZoom("105");
        FindButtons(window).First(b => b.Content as string == "−").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Near(second.Height, oldHeight), "−ボタンも100％で停止");
        Capture(window, "artifacts/continuous-last.png");
        viewer.ScrollToTop(); await Task.Delay(600); window.UpdateLayout();
        Check(pageNumber.Text == "1", "スクロールで先頭ページへ戻れる");
        using var doc = new PdfDocument(fixture);
        var print = new PrintWindow(doc, 0, null) { Owner = window };
        print.Show();
        await Task.Delay(1200);
        print.UpdateLayout();
        Capture(print, "artifacts/print-window.png");
        var printButton = (Button)print.FindName("PrintButton");
        Check(printButton.IsEnabled, "印刷プレビュー完了後に印刷可能");
        Check(print.InputBindings.OfType<System.Windows.Input.KeyBinding>().Any(b => b.Command == System.Windows.Input.ApplicationCommands.Print && b.Key == System.Windows.Input.Key.Enter && b.Modifiers == System.Windows.Input.ModifierKeys.Control), "Ctrl＋Enterを印刷コマンドに割り当て");
        Check(System.Windows.Input.ApplicationCommands.Print.CanExecute(null, print), "プレビュー完了時だけショートカットで印刷可能");
        print.FitToWorkArea(new Rect(0, 0, 1024, 600));
        print.UpdateLayout();
        Check(print.Top >= 0 && print.Top + print.ActualHeight <= 600, "小さい画面でも印刷ウィンドウを画面内に収める");
        Point printLocation = printButton.TranslatePoint(new Point(), print);
        Check(printButton.IsVisible && printLocation.Y >= 0 && printLocation.Y + printButton.ActualHeight < print.ActualHeight && printLocation.Y > print.ActualHeight / 2, "小画面でも下部の印刷ボタンを常時表示");
        Capture(print, "artifacts/print-small-window.png");
        var previewScroller = (ScrollViewer)print.FindName("PreviewScroller");
        var sideText = (TextBlock)print.FindName("SideLabel");
        void PreviewScroll(int delta) => previewScroller.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
        previewScroller.ScrollToBottom(); print.UpdateLayout();
        PreviewScroll(-120);
        for (int i = 0; i < 100 && !sideText.Text.StartsWith("2 /"); i++) await Task.Delay(100);
        Check(sideText.Text.StartsWith("2 /"), "印刷プレビューの下端でホイールから次の面へ");
        previewScroller.ScrollToTop(); print.UpdateLayout();
        PreviewScroll(120);
        for (int i = 0; i < 100 && !sideText.Text.StartsWith("1 /"); i++) await Task.Delay(100);
        Check(sideText.Text.StartsWith("1 /"), "印刷プレビューの上端でホイールから前の面へ");
        previewScroller.ScrollToTop(); print.UpdateLayout(); PreviewScroll(120);
        Check(sideText.Text.StartsWith("1 /"), "印刷プレビューの先頭から範囲外へ進まない");
        var modeBox = (ComboBox)print.FindName("ModeBox");
        modeBox.SelectedIndex = 2;
        Check(!printButton.IsEnabled, "設定変更後は古いプレビューで印刷させない");
        await print.RefreshAsync();
        Check(printButton.IsEnabled, "2アップのプレビュー更新後に印刷可能");
        Capture(print, "artifacts/print-two-up.png");
        var percentBox = (TextBox)print.FindName("PercentBox");
        foreach (string name in new[] { "PercentBox", "CopiesBox", "OverlapBox" })
        {
            var numberInput = (TextBox)print.FindName(name);
            numberInput.Select(0, 0);
            numberInput.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
            Check(numberInput.SelectionLength == numberInput.Text.Length, "印刷入力欄クリックで全選択: " + name);
        }
        FindButtons(print).First(b => (string?)b.Tag == "PercentBox:1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(percentBox.Text == "101", "倍率の上矢印で1％増やす");
        var copiesInput = (TextBox)print.FindName("CopiesBox");
        FindButtons(print).First(b => (string?)b.Tag == "CopiesBox:1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(copiesInput.Text == "2", "部数の上矢印で1部増やす");
        copiesInput.Text = "1";
        FindButtons(print).First(b => (string?)b.Tag == "CopiesBox:-1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(copiesInput.Text == "1", "部数の下矢印で1部未満にしない");
        modeBox.SelectedIndex = 0;
        percentBox.Text = "50";
        for (int i = 0; i < 100 && !printButton.IsEnabled; i++) await Task.Delay(100);
        Check(printButton.IsEnabled && ((TextBlock)print.FindName("SideLabel")).Text.Contains("50%"), "倍率変更が更新ボタンなしで自動反映");
        percentBox.Text = "150"; percentBox.Text = "75";
        for (int i = 0; i < 100 && !printButton.IsEnabled; i++) await Task.Delay(100);
        Check(printButton.IsEnabled && ((TextBlock)print.FindName("SideLabel")).Text.Contains("75%"), "連続入力では最後の倍率を反映");
        percentBox.Text = "NaN";
        await print.RefreshAsync();
        Check(!printButton.IsEnabled, "不正な倍率入力で印刷を止める");
        Check(!System.Windows.Input.ApplicationCommands.Print.CanExecute(null, print), "不正な倍率ならCtrl＋Enterの印刷も無効");
        percentBox.Text = "100";
        Check(((FrameworkElement)print.FindName("PosterSettings")).Visibility == Visibility.Collapsed, "通常の印刷で貼り合わせ幅を隠す");
        modeBox.SelectedIndex = 5;
        Check(((FrameworkElement)print.FindName("PosterSettings")).Visibility == Visibility.Visible, "ポスター選択時だけ貼り合わせ幅を表示");
        await print.RefreshAsync();
        Check(printButton.IsEnabled, "ポスターのプレビュー更新");
        Capture(print, "artifacts/print-poster.png");
        // 登録済みの物理プリンターもプレビューだけ検証する。印刷ジョブは送らない。
        var printerBox = (ComboBox)print.FindName("PrinterBox");
        foreach (string printerName in printerBox.Items.Cast<string>().ToArray())
        {
            printerBox.SelectedItem = printerName;
            modeBox.SelectedIndex = 0;
            await print.RefreshAsync();
            Check(printButton.IsEnabled, "プリンター切替・プレビュー: " + printerName);
        }
        printerBox.SelectedItem = "Microsoft Print to PDF";
        modeBox.SelectedIndex = 0;
        await print.RefreshAsync();
        var jobPrinter = (PrintDocument)typeof(PrintWindow).GetField("printer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(print)!;
        var originalController = jobPrinter.PrintController;
        jobPrinter.PrintController = new CancelPrintController();
        printButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(print.IsVisible, "印刷を取り消した場合はプレビューを残す");
        jobPrinter.PrintController = originalController;
        string jobPath = System.IO.Path.GetFullPath("artifacts/preview-print-job.pdf");
        jobPrinter.PrinterSettings.PrintToFile = true;
        jobPrinter.PrinterSettings.PrintFileName = jobPath;
        printButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!print.IsVisible && File.Exists(jobPath), "印刷データ送信成功後にプレビューを自動で閉じる");
        print.Close();
        using (var landscapeDoc = new PdfDocument(System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "print-check.pdf")))
        {
            var landscapePrint = new PrintWindow(landscapeDoc, 1, null) { Owner = window };
            landscapePrint.Show();
            var landscapeButton = (Button)landscapePrint.FindName("PrintButton");
            for (int i = 0; i < 100 && !landscapeButton.IsEnabled; i++) await Task.Delay(100);
            var previewCanvas = (Canvas)landscapePrint.FindName("Preview");
            Check(((ComboBox)landscapePrint.FindName("OrientationBox")).SelectedIndex == 1 && previewCanvas.Width > previewCanvas.Height, "A4横PDFの初回プレビューを横向きにする");
            landscapePrint.Close();
            var a3Print = new PrintWindow(landscapeDoc, 2, null) { Owner = window };
            a3Print.Show();
            var a3Button = (Button)a3Print.FindName("PrintButton");
            for (int i = 0; i < 100 && !a3Button.IsEnabled; i++) await Task.Delay(100);
            Check(((PaperSize)((ComboBox)a3Print.FindName("PaperBox")).SelectedItem).Kind == PaperKind.A3 && ((ComboBox)a3Print.FindName("OrientationBox")).SelectedIndex == 1, "A3横原本は用紙サイズもA3横に合わせる");
            a3Print.Close();
        }
        if (File.Exists("artifacts/feature-tests/forms.pdf"))
        {
            string formPath = Environment.GetEnvironmentVariable("AIRYVIEW_TOOL_PDF") ?? "artifacts/feature-tests/forms.pdf";
            using var forms = new PdfDocument(formPath);
            var metadata = await PdfHelper.RunAsync(new Dictionary<string,object?> { ["operation"]="inspect", ["source"]=formPath, ["password"]="" });
            int expectedFields = metadata.GetProperty("fields").GetArrayLength();
            var tools = new PdfToolsWindow(forms, 0, _ => { }, _ => Task.CompletedTask) { Owner = window };
            tools.Show();
            for (int i = 0; i < 100 && tools.FieldCount < expectedFields; i++) await Task.Delay(100);
            tools.UpdateLayout();
            Check(tools.FieldCount == expectedFields && tools.StatusMessage.Contains("入力欄"), "フォーム欄としおりを読み込んで表示: " + tools.StatusMessage);
            Check(FindButtons(tools).Any(b => b.Content as string == "入力内容を別名保存"), "フォーム編集画面を表示");
            Capture(tools, "artifacts/pdf-tools-window.png");
            tools.Close();
        }
        string positionText = System.IO.Path.GetFullPath("artifacts/reading-position.txt");
        File.WriteAllText(positionText, string.Join("\n", Enumerable.Range(1, 2000).Select(n => $"Line {n}: reading position")));
        await window.OpenPathsAsync([positionText]); window.UpdateLayout();
        textEditor.Select(500, 12);
        textEditor.ScrollToVerticalOffset(600); window.UpdateLayout();
        double savedTextOffset = textEditor.VerticalOffset;
        await window.OpenPathsAsync([markdownFixture]); window.UpdateLayout();
        await window.OpenPathsAsync([positionText]); window.UpdateLayout();
        Check(savedTextOffset > 0 && Near(textEditor.VerticalOffset, savedTextOffset) && textEditor.SelectionStart == 500 && textEditor.SelectionLength == 12, "TXTのタブ復帰でスクロール位置と選択を復元");

        await window.OpenPathsAsync([markdownFixture]); window.UpdateLayout();
        window.ScrollMarkdownByWheel(-120); window.UpdateLayout();
        double savedMarkdownOffset = window.MarkdownOffset;
        await window.OpenPathsAsync([positionText]); window.UpdateLayout();
        await window.OpenPathsAsync([markdownFixture]); window.UpdateLayout();
        Check(savedMarkdownOffset > 0 && Near(window.MarkdownOffset, savedMarkdownOffset), "Markdownプレビューの読んでいた位置を復元");

        await window.OpenPathsAsync([imageFixture]); window.UpdateLayout();
        var positionZoomInput = (TextBox)window.FindName("ZoomText");
        positionZoomInput.Text = "200";
        positionZoomInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        window.UpdateLayout();
        var imageViewer = (ScrollViewer)window.FindName("ImageViewer");
        imageViewer.ScrollToVerticalOffset(300); imageViewer.ScrollToHorizontalOffset(200); window.UpdateLayout();
        double savedImageY = imageViewer.VerticalOffset, savedImageX = imageViewer.HorizontalOffset;
        await window.OpenPathsAsync([positionText]); window.UpdateLayout();
        await window.OpenPathsAsync([imageFixture]); window.UpdateLayout();
        Check(savedImageY > 0 && Near(imageViewer.VerticalOffset, savedImageY) && Near(imageViewer.HorizontalOffset, savedImageX), "画像のタブ復帰で縦横スクロール位置を復元");
        var decodeTask = MainWindow.DecodeImageAsync(File.ReadAllBytes(imageFixture), ".png");
        bool dispatcherResponded = false;
        await window.Dispatcher.InvokeAsync(() => dispatcherResponded = true);
        var decodedImage = await decodeTask;
        Check(dispatcherResponded && decodedImage.IsFrozen && decodedImage.PixelWidth == 3200, "画像のバックグラウンド変換とUIへの受け渡し");
        await window.OpenPathsAsync([svgFixture]); window.UpdateLayout();
        var svgDisplay = (Image)window.FindName("ReaderImage");
        positionZoomInput.Text = "200";
        positionZoomInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        window.UpdateLayout();
        double svgDisplayWidth = svgDisplay.Width;
        imageViewer.ScrollToVerticalOffset(150); window.UpdateLayout();
        double svgScroll = imageViewer.VerticalOffset;
        int pixelsBefore = ((BitmapSource)svgDisplay.Source).PixelWidth;
        var svgWait = System.Diagnostics.Stopwatch.StartNew();
        while (((BitmapSource)svgDisplay.Source).PixelWidth <= pixelsBefore && svgWait.ElapsedMilliseconds < 10000)
            await Task.Delay(50);
        Check(((BitmapSource)svgDisplay.Source).PixelWidth > pixelsBefore, "SVGは拡大操作後に高解像度で再描画");
        Check(Near(svgDisplay.Width, svgDisplayWidth) && Near(imageViewer.VerticalOffset, svgScroll), "SVGの再描画で表示倍率と閲覧位置を維持");
        positionZoomInput.Text = "300";
        positionZoomInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        await window.OpenPathsAsync([imageFixture]); window.UpdateLayout();
        var pngSource = svgDisplay.Source;
        await Task.Delay(500);
        Check(ReferenceEquals(svgDisplay.Source, pngSource), "SVGの遅延描画が移動先タブの画像を上書きしない");
        window.Close();
        File.WriteAllLines("artifacts/ui-test-results.txt", Results);
    }
    private sealed class CancelPrintController : PrintController
    {
        public override void OnStartPrint(PrintDocument document, PrintEventArgs e) => e.Cancel = true;
    }
    private static IEnumerable<Button> FindButtons(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window); SaveImage(image, path);
    }
    public static void Run()
    {
        Directory.CreateDirectory("artifacts");
        string fixture = System.IO.Path.GetFullPath("artifacts/dimension-check.pdf");
        CreateFixture(fixture, 5);
        string resolvedFixture = DesktopInstaller.ResolveShellPath(fixture);
        Check(File.Exists(resolvedFixture) && File.ReadAllBytes(fixture).SequenceEqual(File.ReadAllBytes(resolvedFixture)), "ショートカット用の実パスが同じファイルを指す");
        Check(!resolvedFixture.StartsWith(@"\\?\", StringComparison.Ordinal), "シェル用のパスから拡張接頭辞を除去");
        if (File.Exists("artifacts/feature-tests/encrypted.pdf"))
        {
            bool refused = false;
            try { using var protectedDoc = new PdfDocument("artifacts/feature-tests/encrypted.pdf", "wrong"); }
            catch (PdfPasswordException) { refused = true; }
            Check(refused, "間違ったPDFパスワードを拒否");
            using var protectedOk = new PdfDocument("artifacts/feature-tests/encrypted.pdf", "secret");
            Check(protectedOk.Count == 2, "パスワード付きPDFを全ページ読込");
            using var filled = new PdfDocument("artifacts/feature-tests/filled.pdf");
            SaveImage(filled.Render(0,595,842), "artifacts/filled-native.png"); Check(filled.Count == 2, "保存した入力欄のPDFium描画");
            using var added = new PdfDocument("artifacts/feature-tests/added.pdf");
            Check(added.PageText(1).Contains("日本語の記入"), "日本語追記をPDFium検索で抽出");
            using var signed = new PdfDocument("artifacts/feature-tests/signed.pdf");
            Check(signed.HasSignatures && !signed.CanEdit, "署名済みPDFの編集を保護");
        }
        var watch = Stopwatch.StartNew();
        string? compat = Environment.GetEnvironmentVariable("AIRYVIEW_COMPAT_PDF");
        if (!string.IsNullOrEmpty(compat))
        {
            using var compatible = new PdfDocument(compat);
            Check(compatible.CanFill && !compatible.CanEdit, "Adobe系PDFのフォーム入力権限を維持");
            for (int i=0;i<compatible.Count;i++) SaveImage(compatible.Render(i,794,1123), $"artifacts/compat-native-{i+1}.png");
        }
        using var doc = new PdfDocument(fixture);
        Check(doc.Count == 5, "PDF読込・5ページ");
        Check(Near(doc.SizeMm(0).Width, 210) && Near(doc.SizeMm(0).Height, 297), "PDFのA4物理寸法");
        Check(doc.PrintPercent == 100, "新しいPDFの印刷倍率100%");
        var image = doc.Render(0, 794, 1123);
        SaveImage(image, "artifacts/pdf-render.png");
        Results.Add($"INFO: 読込＋初回描画 {watch.ElapsedMilliseconds} ms（合格基準未設定）");
        var paper = new Size(210, 297); var printable = new Rect(5, 5, 200, 287);
        var plan = PrintLayout.Build(doc.SizeMm, [0], paper, printable, new(PrintMode.Scale));
        Check(Near(plan[0].Items[0].Destination.Width, 210), "原寸で自動縮小しない");
        Check(PrintLayout.IsClipped(plan[0].Items[0], doc.SizeMm(0), null), "原寸の欠けを検出");
        plan = PrintLayout.Build(doc.SizeMm, [0], paper, printable, new(PrintMode.Scale, 50));
        Check(Near(plan[0].Items[0].Destination.Width, 105), "50%の物理寸法");
        plan = PrintLayout.Build(doc.SizeMm, [0], paper, printable, new(PrintMode.Fit));
        Check(!PrintLayout.IsClipped(plan[0].Items[0], doc.SizeMm(0), null), "用紙に合わせると欠けない");
        var selection = new Rect(10, 20, 100, 100);
        plan = PrintLayout.Build(doc.SizeMm, [0], paper, printable, new(PrintMode.Scale), selection);
        Check(Near(plan[0].Items[0].Clip.Width, 100) && Near(plan[0].Items[0].Clip.Height, 100), "選択範囲100mmを維持");
        plan = PrintLayout.Build(doc.SizeMm, [0, 1, 2, 3, 4], paper, printable, new(PrintMode.FourUp));
        Check(plan.Count == 2 && plan.SelectMany(s => s.Items).Select(p => p.Page).SequenceEqual([0, 1, 2, 3, 4]), "4アップの端数・ページ順");
        var landscape = new Size(297, 210); var wide = new Rect(5, 5, 287, 200);
        plan = PrintLayout.Build(doc.SizeMm, [0, 1, 2, 3], landscape, wide, new(PrintMode.Booklet));
        Check(plan[0].Items.Select(p => p.Page).SequenceEqual([3, 0]) && plan[1].Items.Select(p => p.Page).SequenceEqual([1, 2]), "4ページ小冊子の表裏順序");
        plan = PrintLayout.Build(doc.SizeMm, [0, 1, 2, 3, 4], landscape, wide, new(PrintMode.Booklet));
        Check(plan.Count == 4 && plan.SelectMany(s => s.Items).Count() == 5 && plan[0].Items.Single().Page == 0, "5ページ小冊子の末尾空白補充");
        plan = PrintLayout.Build(doc.SizeMm, [0, 1, 2, 3], landscape, wide, new(PrintMode.Booklet, RightBinding: true));
        Check(plan[0].Items.Select(p => p.Page).SequenceEqual([0, 3]), "右とじ小冊子");
        plan = PrintLayout.Build(doc.SizeMm, [0], paper, printable, new(PrintMode.Poster, 200, 5));
        Check(plan.Count == 9 && Near(plan[0].Items[0].Destination.Width, 420), "200%ポスター分割・倍率維持");
        Check(Near(plan[0].Items[0].Destination.X - plan[1].Items[0].Destination.X, 195), "ポスター5mmの重なり");
        Check(PrintLayout.ParsePages("1-3,5", 5).SequenceEqual([0, 1, 2, 4]), "ページ範囲入力");
        try { PrintLayout.ParsePages("0-8", 5); Check(false, "不正な範囲"); } catch (FormatException) { Check(true, "不正な範囲を拒否"); }
        try { PrintLayout.Build(doc.SizeMm, [0], paper, printable, new(PrintMode.Scale, double.NaN)); Check(false, "不正な倍率"); } catch (ArgumentException) { Check(true, "不正な倍率を拒否"); }
        doc.Rotate(0, 1);
        Check(Near(doc.SizeMm(0).Width, 297) && Near(doc.SizeMm(1).Width, 210), "現在ページだけ回転");
        doc.ResetRotation(0);
        Check(Near(doc.SizeMm(0).Width, 210), "現在ページの回転をリセット");
        doc.Rotate(0, 1);
        string copy = System.IO.Path.GetFullPath("artifacts/rotated.pdf");
        doc.SaveCopy(copy);
        using (var reopened = new PdfDocument(copy)) Check(Near(reopened.SizeMm(0).Width, 297) && reopened.Count == 5, "回転の保存・再読込");
        using (var original = new PdfDocument(fixture)) Check(Near(original.SizeMm(0).Width, 210), "元ファイルの維持");
        try { doc.SaveCopy(fixture); Check(false, "原本上書き"); } catch (IOException) { Check(true, "原本への上書きを拒否"); }
        if (File.Exists("artifacts/feature-tests/filled.pdf")) VirtualPrintForm();
        VirtualPrint(fixture, 50);
        VirtualPrint(fixture, 100);
        VirtualPrint(fixture, 200);
        File.WriteAllLines("artifacts/test-results.txt", Results);
    }

    private static void VirtualPrintForm()
    {
        using var doc = new PdfDocument("artifacts/feature-tests/filled.pdf");
        using var printer = new PrintDocument();
        printer.PrinterSettings.PrinterName = "Microsoft Print to PDF";
        if (!printer.PrinterSettings.IsValid) return;
        string output = System.IO.Path.GetFullPath("artifacts/form-printed.pdf");
        printer.PrinterSettings.PrintToFile = true; printer.PrinterSettings.PrintFileName = output;
        printer.PrintController = new StandardPrintController();
        printer.DefaultPageSettings.PaperSize = printer.PrinterSettings.PaperSizes.Cast<PaperSize>().First(p => p.Kind == PaperKind.A4);
        printer.DefaultPageSettings.Margins = new Margins(0,0,0,0);
        var (paper, printable) = PrinterOutput.GetGeometry(printer);
        var sheet = PrintLayout.Build(doc.SizeMm, [0], paper, printable, new(PrintMode.Scale, 100))[0];
        printer.PrintPage += (_, args) => { PrinterOutput.Draw(doc, sheet, args); args.HasMorePages = false; };
        printer.Print();
        using var result = new PdfDocument(output);
        var image = result.Render(0,595,842); SaveImage(image, "artifacts/form-printed.png");
        var pixels = new byte[595*842*4]; image.CopyPixels(pixels,595*4,0);
        int dark = 0;
        for(int y=115;y<140;y++) for(int x=50;x<180;x++) if(pixels[(y*595+x)*4]<160)dark++;
        Check(dark > 100, "日本語入力欄が実印刷経路でも出力される");
    }
    private static void VirtualPrint(string fixture, int percent)
    {
        using var doc = new PdfDocument(fixture);
        using var printer = new PrintDocument();
        printer.PrinterSettings.PrinterName = "Microsoft Print to PDF";
        if (!printer.PrinterSettings.IsValid) { Results.Add("SKIP: Microsoft Print to PDF がありません"); return; }
        string output = System.IO.Path.GetFullPath($"artifacts/printed-{percent}-percent.pdf");
        printer.PrinterSettings.PrintToFile = true; printer.PrinterSettings.PrintFileName = output;
        printer.PrinterSettings.Duplex = Duplex.Simplex;
        printer.PrintController = new StandardPrintController();
        printer.OriginAtMargins = false;
        printer.DefaultPageSettings.PaperSize = printer.PrinterSettings.PaperSizes.Cast<PaperSize>().First(p => p.Kind == (percent == 200 ? PaperKind.A3 : PaperKind.A4));
        printer.DefaultPageSettings.Landscape = percent == 200;
        printer.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        var (paper, printable) = PrinterOutput.GetGeometry(printer);
        File.AppendAllText("artifacts/print-diagnostics.txt", $"{percent}% Paper={paper} Printable={printable} Landscape={printer.DefaultPageSettings.Landscape}\n");
        var sheet = PrintLayout.Build(doc.SizeMm, [0], paper, printable, new(PrintMode.Scale, percent))[0];
        printer.PrintPage += (_, args) => { PrinterOutput.Draw(doc, sheet, args); args.HasMorePages = false; };
        printer.Print();
        Check(File.Exists(output) && new FileInfo(output).Length > 100, "実際のWindows印刷経路からPDF出力");
        using var result = new PdfDocument(output);
        Check(result.Count == 1 && Near(result.SizeMm(0).Width, paper.Width, .2), $"{percent}%印刷結果のページ数・用紙寸法");
        int width = (int)Math.Round(result.SizeMm(0).Width * 4);
        int height = (int)Math.Round(result.SizeMm(0).Height * 4);
        BitmapSource rendered = result.Render(0, width, height);
        SaveImage(rendered, $"artifacts/printed-{percent}-render.png");
        var pixels = new byte[width * height * 4]; rendered.CopyPixels(pixels, width * 4, 0);
        var placement = sheet.Items[0];
        int expectedY = (int)Math.Round((placement.Destination.Y + 197 * percent / 100.0) * 4);
        int expectedX = (int)Math.Round((placement.Destination.X + 20 * percent / 100.0) * 4);
        bool Dark(int x, int y)
        {
            int p = (y * width + x) * 4;
            return pixels[p] < 200 && pixels[p + 1] < 200 && pixels[p + 2] < 200;
        }
        int horizontal = 0, vertical = 0;
        for (int y = Math.Max(0, expectedY - 8); y < Math.Min(height, expectedY + 8); y++)
        {
            int run = 0;
            for (int x = 0; x < width; x++) { if (Dark(x, y)) { run++; horizontal = Math.Max(horizontal, run); } else run = 0; }
        }
        for (int x = Math.Max(0, expectedX - 8); x < Math.Min(width, expectedX + 8); x++)
        {
            int run = 0;
            for (int y = 0; y < height; y++) { if (Dark(x, y)) { run++; vertical = Math.Max(vertical, run); } else run = 0; }
        }
        Check(Math.Abs(horizontal - percent * 4) <= 4, $"{percent}%の水平基準線（{horizontal / 4.0:F2}mm、画像上の測定）");
        Check(Math.Abs(vertical - percent * 4) <= 4, $"{percent}%の垂直基準線（{vertical / 4.0:F2}mm、画像上の測定）");
    }

    public static void SaveImage(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    public static void CreateFixture(string path, int count)
    {
        var objects = new List<string>();
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Count {count} /Kids [{string.Join(' ', Enumerable.Range(0, count).Select(i => $"{3 + i * 2} 0 R"))}] >>");
        for (int i = 0; i < count; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.2756 841.8898] /Contents {4 + i * 2} 0 R >>");
            // 横・縦100mmの基準線と、ページごとに数が変わる識別用の四角。
            string content = "0 0 0 RG 0.8 w 56.6929 283.4646 m 340.1575 283.4646 l S 56.6929 283.4646 m 56.6929 566.9292 l S\n";
            for (int n = 0; n <= i; n++) content += $"{60 + 22 * n} 730 14 14 re f\n";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        using var stream = File.Create(path); var offsets = new List<long>();
        void Write(string text) { byte[] bytes = Encoding.ASCII.GetBytes(text); stream.Write(bytes); }
        Write("%PDF-1.7\n");
        for (int i = 0; i < objects.Count; i++) { offsets.Add(stream.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        long xref = stream.Position; Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
