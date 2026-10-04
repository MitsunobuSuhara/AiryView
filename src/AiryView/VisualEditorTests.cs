using System.Security.Cryptography;
using System.Windows.Documents;

namespace AiryView;

internal static class VisualEditorTests
{
    private static readonly List<string> results = [];
    private static void Check(bool value, string description)
    { if (!value) throw new Exception("FAIL: " + description); results.Add("PASS: " + description); }
    private static Color Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4]; converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
    private static BitmapSource Read(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }
    private static bool HasInk(BitmapSource bitmap, Int32Rect area)
    {
        int ink = 0;
        for (int y = area.Y; y < area.Y + area.Height; y++)
            for (int x = area.X; x < area.X + area.Width; x++)
            { Color p = Pixel(bitmap, x, y); if (p.R < 100 && p.G < 100 && p.B < 100) ink++; }
        return ink > 30;
    }
    private static void SavePreview(Window window, string path, FrameworkElement? popup = null)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (popup != null) dc.DrawRectangle(new VisualBrush(popup), null,
                new Rect(window.PointFromScreen(popup.PointToScreen(new Point())), new Size(popup.ActualWidth, popup.ActualHeight)));
        }
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static BitmapSource CheckArrowTipVisible(InlineShapeEditor editor, string description)
    {
        editor.Measure(new Size(editor.Width, editor.Height));
        editor.Arrange(new Rect(0, 0, editor.Width, editor.Height)); editor.UpdateLayout();
        var image = new RenderTargetBitmap((int)editor.Width, (int)editor.Height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, editor.Width, editor.Height));
        image.Render(background); image.Render(editor);
        Vector direction = editor.Mark.End - editor.Mark.Start; direction.Normalize();
        Point sample = editor.Mark.End - direction * 3;
        int black = 0;
        for (int y = (int)sample.Y - 1; y <= (int)sample.Y + 1; y++)
            for (int x = (int)sample.X - 1; x <= (int)sample.X + 1; x++)
            { var p = Pixel(image, x, y); if (p.A > 180 && p.R < 80 && p.G < 80 && p.B < 80) black++; }
        if (black < 2)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create("artifacts/arrow-tip-failure.png"); encoder.Save(output);
        }
        Check(black >= 2, description + $" at {sample}, black pixels={black}"); return image;
    }
    internal static async Task RunAsync(bool imagesOnly = false)
    {
        string folder = System.IO.Path.GetFullPath("artifacts/editor-tests"); Directory.CreateDirectory(folder);
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 40, 60));
        var bitmap = new RenderTargetBitmap(120, 80, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        var model = new VisualEditModel(bitmap, 120, 80);
        model.SaveImage(folder + "/source.png", folder + "/unused.png"); string source = folder + "/source.png";
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var arrowCases = Enumerable.Range(0, 8).Select(index =>
        {
            double angle = index * Math.PI / 4; Vector direction = new(Math.Cos(angle), Math.Sin(angle));
            return new EditMark("arrow", new Point(180, 180), new Point(180, 180) + direction * 100, "", Colors.Orange, 12);
        }).ToArray();
        foreach (var arrow in arrowCases)
        {
            Vector direction = arrow.End - arrow.Start; direction.Normalize(); Vector side = new(-direction.Y, direction.X);
            Geometry outline = EditDrawing.Outline(arrow, 360, 360);
            Check(!outline.FillContains(arrow.End + direction * 3) && !outline.FillContains(arrow.End - direction * 2 + side * 3)
                && outline.FillContains(arrow.End - direction * 3), "arrow tip is sharp without protruding shaft: " + Array.IndexOf(arrowCases, arrow));
        }
        var arrowHost = new Window { Width = 420, Height = 440, Title = "矢じり表示テスト" };
        try
        {
            arrowHost.Show();
            foreach (var arrow in arrowCases)
            {
                var editor = new InlineShapeEditor(arrow with { Color = Colors.Black, Size = 2 }, 1, new Size(360, 360), () => { }, () => { }, () => { });
                arrowHost.Content = editor;
                await arrowHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                CheckArrowTipVisible(editor, "thin arrow tip remains visible through handles in direction " + Array.IndexOf(arrowCases, arrow));
                editor.MoveBy(new Vector(5, 5)); editor.MoveEndpoint(false, new Vector(-3, 4));
                await arrowHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                CheckArrowTipVisible(editor, "arrow tip remains visible after moving and resizing in direction " + Array.IndexOf(arrowCases, arrow));
                editor.SetAppearance(12, Colors.Black); editor.UpdateZoom(2.5);
                await arrowHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(editor.ShortcutHintInPaletteForTest, "shape shortcut hint stays inside the palette at zoom");
                Check(Math.Abs(editor.TipHandleSizeForTest.Width * 2.5 - 14) < .01, "arrow tip handle matches the other endpoint at zoom");
                CheckArrowTipVisible(editor, "thick arrow tip remains above handles after zoom in direction " + Array.IndexOf(arrowCases, arrow));
            }
        }
        finally { arrowHost.Close(); }
        foreach (string kind in new[] { "arrow", "line", "rectangle", "ellipse", "highlight", "highlight-freehand", "highlight-text" })
        {
            var mark = new EditMark(kind, new(30, 30), new(100, 80), "", Colors.Blue, 3);
            var editor = new InlineShapeEditor(mark, 1, new Size(200, 120), () => { }, () => { }, () => { });
            var grips = editor.Children.OfType<System.Windows.Controls.Primitives.Thumb>().ToArray();
            var moveMarker = grips[2].Template.LoadContent() as Border;
            Check(grips.Length == 3 && Math.Abs(grips[0].Width - 14) < .01 && Math.Abs(grips[1].Width - 14) < .01
                && Math.Abs(grips[2].Width - 20) < .01 && moveMarker?.Child is TextBlock { Text: "✥" },
                kind + " uses equal endpoint circles and a move symbol");
        }
        var shortArrow = new EditMark("arrow", new(50, 50), new(56, 50), "", Colors.Orange, 30);
        Check(!EditDrawing.Outline(shortArrow, 100, 100).FillContains(new Point(59, 50)), "short thick arrow never projects its round shaft beyond the tip");
        var arrows = new VisualEditModel(null, 360, 360);
        foreach (var arrow in arrowCases) arrows.Add(arrow);
        arrows.SaveImage(folder + "/arrow-tips.png", source); arrows.SaveImage(folder + "/arrow-tips.jpg", source);
        BitmapSource savedArrows = Read(folder + "/arrow-tips.png");
        foreach (var arrow in arrowCases)
        {
            Vector direction = arrow.End - arrow.Start; direction.Normalize(); Point outside = arrow.End + direction * 3;
            Check(Pixel(savedArrows, (int)Math.Round(outside.X), (int)Math.Round(outside.Y)).A < 20, "saved PNG has no shaft beyond arrow tip: " + Array.IndexOf(arrowCases, arrow));
        }
        Check(!model.Dirty && !model.CanUndo, "initial frame is shared and clean");
        model.Add(new("line", new(10, 70), new(100, 70), "", Colors.Blue, 4));
        Check(model.Dirty && Pixel(model.Render(), 70, 70).B > 240, "line paints in canvas coordinates");
        model.Undo(); Check(!model.Dirty && Pixel(model.Render(), 70, 70).A == 0, "undo restores transparent original");
        model.Redo(); Check(model.Dirty && model.CanUndo, "redo restores edit");
        model.Rotate(1); Check(model.Frame.Width == 80 && model.Frame.Height == 120 && Pixel(model.Render(), 40, 20).R > 240, "clockwise rotation preserves original pixels and changes dimensions");
        model.Undo(); Check(model.Frame.Marks.Length == 1, "undo rotation restores editable overlay");
        model.Crop(new Rect(10, 10, 20, 30)); Check(model.Frame.Width == 20 && model.Frame.Height == 30 && Pixel(model.Render(), 10, 10).R > 240, "crop uses selected bounds");
        model.Resize(60, 90); Check(model.Frame.Width == 60 && model.Frame.Height == 90, "resize changes output dimensions");
        model.Undo(); model.Undo(); model.Undo();
        model.SaveImage(folder + "/transparent.png", source);
        model.SaveImage(folder + "/white.jpg", source);
        Check(Pixel(Read(folder + "/transparent.png"), 100, 50).A == 0, "PNG retains alpha");
        Color white = Pixel(Read(folder + "/white.jpg"), 100, 50); Check(white.R > 245 && white.G > 245 && white.B > 245, "JPEG uses white for transparency");
        Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "image source hash unchanged");
        try { model.SaveImage(source, source); Check(false, "source overwrite rejected"); } catch (IOException) { Check(true, "source overwrite rejected"); }
        try { model.Resize(32768, 32768); Check(false, "large allocation rejected"); } catch (IOException) { Check(model.Frame.Width == 120, "large allocation rejected without changing frame"); }
        model.Add(new("arrow", new(10, 40), new(100, 40), "", Colors.Green, 3));
        model.Add(new("text", new(5, 2), new(5, 2), "日本語", Colors.Black, 14));
        model.SaveImage(folder + "/annotated.png", source);
        Check(Pixel(Read(folder + "/annotated.png"), 90, 40).G > 80, "arrow and Japanese text save to PNG");
        var preview = new VisualEditorWindow(source, bitmap, 0, _ => Task.CompletedTask, _ => false);
        preview.Show(); await preview.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(preview.IsVisible, "image editor opens on demand");
        var editorBar = (WindowTitleBar)((DockPanel)preview.Content).Children[0];
        Check(Math.Abs(editorBar.ActualHeight - 30) < 1 && System.Windows.Shell.WindowChrome.GetWindowChrome(preview).CaptionHeight == 30,
            "editor title bar uses a compact 30 DIP height");
        var nativeMain = new MainWindow(showWelcome: false);
        nativeMain.Show();
        var mainBar = (WindowTitleBar)((DockPanel)nativeMain.Content).Children[0];
        Check(Math.Abs(mainBar.ActualHeight - editorBar.ActualHeight) < 1,
            "main and editor share the same title bar height");
        nativeMain.WindowState = WindowState.Normal;
        string normalIcon = ((System.Windows.Shapes.Path)mainBar.MaximizeButton.Content).Data.ToString();
        mainBar.MaximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(nativeMain.WindowState == WindowState.Maximized && ((System.Windows.Shapes.Path)mainBar.MaximizeButton.Content).Data.ToString() != normalIcon,
            "maximize changes to the overlapping restore icon");
        mainBar.MaximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(nativeMain.WindowState == WindowState.Normal, "restore caption button restores normal size");
        mainBar.MinimizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(nativeMain.WindowState == WindowState.Minimized, "minimize caption button minimizes the window");
        nativeMain.WindowState = WindowState.Normal;
        mainBar.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!nativeMain.IsVisible, "close caption button closes through the normal window handler");
        SavePreview(preview, folder + "/editor-title-bar.png");
        Check(preview.SaveButtonWidthForTest >= 153, "save-as button keeps enough width for its Japanese label");
        Check(preview.CentimeterTextForTest.Contains("3.18 cm") && preview.CentimeterTextForTest.Contains("2.12 cm") && preview.CentimeterTextForTest.Contains("96 dpi換算"), "image dimensions show clearly labeled centimeter estimates below pixels");
        Check(preview.ToolNamesForTest.SequenceEqual(new[] { "文字", "矢印", "線", "四角形", "円・楕円", "ハイライト" }) && preview.HighlightMethodNamesForTest.SequenceEqual(new[] { "範囲指定", "フリーハンド" }) && preview.ToolWidthForTest >= 140
            && preview.HighlightMethodWidthForTest >= 160, "highlight method keeps its full label visible beside the icon");
        Check(preview.ToolIconsReadyForTest, "all tool choices and highlight methods have right-side vector icons");
        Check(!preview.HighlightMethodVisibleForTest, "highlight method selector stays hidden during other tools");
        preview.SelectToolForTest("矢印");
        Check(preview.DrawingHintForTest.Contains("起点を押す") && preview.DrawingHintForTest.Contains("先端の位置で離す"),
            "choosing the arrow tool explains the drag direction before drawing");
        Check(preview.DrawingMarkForTest(new Point(10, 20), new Point(90, 40)) is { Kind: "arrow", Start.X: 10, Start.Y: 20, End.X: 90, End.Y: 40 },
            "arrow drawing uses the pressed point as the tail and the released point as the tip");
        preview.SelectToolForTest("ハイライト"); Check(preview.HighlightMethodVisibleForTest, "highlight selection reveals its two drawing methods"); preview.SelectToolForTest("文字");
        Check(preview.CropButtonTextForTest == "クリップ", "image editor labels the separate image operation clip");
        preview.StartCropForTest(); preview.SetCropSelectionForTest(new Rect(10, 10, 50, 35));
        Check(preview.CropModeForTest && preview.CropSelectionForTest is { Width: 50 } && preview.CropPopupOpenForTest && preview.CurrentModelForTest!.Frame.Width == 120, "clip selection opens a nearby popup without changing pixels");
        Check(preview.CropSizeTextForTest.Contains("50 × 35 px") && preview.CropSizeTextForTest.Contains("1.32 × 0.93 cm"), "clip selection shows pixel and centimeter size together");
        preview.SetCropSelectionForTest(new Rect(10, 10, 60, 40));
        Check(preview.CropSizeTextForTest.Contains("60 × 40 px") && preview.CropSizeTextForTest.Contains("1.59 × 1.06 cm"), "clip size label updates as the selection changes");
        preview.CancelCropForTest();
        Check(!preview.CropModeForTest && preview.CropSelectionForTest == null && !preview.CropPopupOpenForTest && preview.CurrentModelForTest!.Frame.Width == 120, "canceling clip closes the popup and leaves image unchanged");
        preview.StartCropForTest(); preview.SetCropSelectionForTest(new Rect(10, 10, 50, 35)); preview.ApplyCropForTest();
        Check(!preview.CropModeForTest && preview.CurrentModelForTest!.Frame.Width == 50 && preview.CurrentModelForTest.Frame.Height == 35, "crop changes image only after explicit confirmation");
        Check(preview.CentimeterTextForTest.Contains("1.32 cm") && preview.CentimeterTextForTest.Contains("0.93 cm"), "centimeter estimate updates after clip confirmation");
        preview.CurrentModelForTest!.Undo(); preview.RefreshForTest();
        var range = preview.HighlightForTest(false, new(20, 20), new(80, 40));
        Check(range.Kind == "highlight" && range.StrokePoints == null && range.Color == Colors.Yellow, "image range highlight uses a rectangle");
        var freehandPreview = preview.HighlightForTest(true, new(20, 20), new(50, 25), new(80, 40));
        Check(freehandPreview.Kind == "highlight-freehand" && freehandPreview.StrokePoints is { Length: 3 }, "image freehand highlight follows the drag");
        Check(preview.ActiveShapeEditor == null, "highlight preview does not commit an annotation during selection");
        preview.CancelEdits();
        double initialZoom = preview.EditorZoomForTest;
        Check(!preview.HandleEditorWheel(120, false, new Point(100, 100)) && preview.EditorZoomForTest == initialZoom, "plain wheel keeps ordinary scrolling in editor");
        Check(preview.HandleEditorWheel(120, true, new Point(100, 100)) && preview.EditorZoomForTest > initialZoom, "Ctrl wheel up zooms image editor");
        preview.HandleEditorWheel(-120, true, new Point(100, 100));
        Check(Math.Abs(preview.EditorZoomForTest - initialZoom) < .0001, "Ctrl wheel down reverses zoom");
        preview.BeginText(new Point(10, 10));
        Check(preview.ActiveTextEditor != null, "click creates an inline text box");
        Check(!preview.DeleteSelectedObject(), "Delete while typing does not remove a new text box");
        var edgeText = new InlineTextEditor(new Point(380, 20), "", 14, Colors.Black, 1,
            new Size(400, 300), () => { }, () => { });
        Check(Canvas.GetLeft(edgeText) + edgeText.Width <= 400, "text box resize handle remains inside the page at the right edge");
        var rightText = new InlineTextEditor(new Point(120, 20), "たぬき\n狐\nねこ", 14, Colors.Black, 1,
            new Size(400, 300), () => { }, () => { });
        double originalWidth = rightText.Width;
        rightText.MoveForTest(330, 20);
        Check(Canvas.GetLeft(rightText) == 330 && rightText.Width < originalWidth
            && Canvas.GetLeft(rightText) + rightText.Width <= 400 && rightText.PlainText == "たぬき\n狐\nねこ",
            "short multiline text moves right by shrinking unused frame width without changing text");
        Check(rightText.ResizeHandleOutsideInputForTest && rightText.ResizeHandlesForTest.Count == 8
            && rightText.MoveEdgesUseSizeAllForTest, "text frame has eight resize circles and move cursors along its edges");
        var draggedText = new InlineTextEditor(new Point(230, 155), "", 14, Colors.Black, 1,
            new Size(400, 300), () => { }, () => { });
        draggedText.SetBoxFromDrag(new Point(230, 155), new Point(70, 90));
        Check(Math.Abs(Canvas.GetLeft(draggedText) - 70) < .5 && Math.Abs(Canvas.GetTop(draggedText) - 90) < .5
            && Math.Abs(draggedText.Width - 160) < .5 && Math.Abs(draggedText.Input.Height - 55) < .5,
            "dragging either direction creates a text frame within the chosen rectangle");
        Check(Math.Abs(draggedText.Mark.TextBoxWidth!.Value - 160) < .5
            && Math.Abs(draggedText.Mark.TextBoxHeight!.Value - 55) < .5,
            "the drawn text frame size is retained for confirmation");
        rightText.MoveForTest(399, 20);
        Check(EditDrawing.TextShapes(rightText.Mark).All(shape => shape.Geometry.Bounds.Right <= 400),
            "moving to the page edge keeps actual text inside the page");
        var textCanvas = new Canvas { Width = 400, Height = 300, Background = Brushes.White };
        textCanvas.Children.Add(rightText);
        var textHost = new Window { Content = textCanvas, Width = 460, Height = 390, Title = "文字の右端テスト" };
        try
        {
            textHost.Show();
            await textHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var topEdge = rightText.MoveEdgesForTest[0];
            var edgeHit = rightText.InputHitTest(topEdge.TranslatePoint(new Point(topEdge.ActualWidth / 4, topEdge.ActualHeight / 2), rightText));
            Check(ReferenceEquals(edgeHit, topEdge) || edgeHit is DependencyObject edgeVisual && topEdge.IsAncestorOf(edgeVisual),
                "the visible text frame edge accepts pointer input for moving");
            var paragraph = (Paragraph)rightText.Input.Document.Blocks.FirstBlock!;
            Check(Math.Abs(paragraph.ContentStart.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward).Y
                - paragraph.ContentEnd.GetCharacterRect(System.Windows.Documents.LogicalDirection.Backward).Y) < 1,
                "moving short Japanese text to the right edge does not wrap its first line");
            Check(Math.Abs(rightText.ResizeHandleForTest.PointToScreen(new Point(0, rightText.ResizeHandleForTest.ActualHeight / 2)).Y
                - rightText.PointToScreen(new Point(0, rightText.ActualHeight)).Y) < 2,
                "bottom resize circle is centered on the text frame border");
            SavePreview(textHost, folder + "/right-edge-text.png");
            rightText.MoveForTest(100, 20);
            await textHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var firstRect = paragraph.ContentStart.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
            var caretPoint = rightText.Input.TranslatePoint(firstRect.TopLeft, textCanvas);
            Check(Math.Abs(caretPoint.X - rightText.Mark.Start.X) < .5,
                "committed text starts at the same horizontal position as the editable text");
            var beforeMove = rightText.Mark;
            rightText.MoveEdgesForTest[0].RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(12, 0)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
            Check(Math.Abs(rightText.Mark.Start.X - beforeMove.Start.X - 12) < .5 && rightText.PlainText == beforeMove.Text,
                "dragging the frame edge moves the text without changing its content");
            double beforeWidth = rightText.Width, beforeHeight = rightText.Input.Height;
            rightText.ResizeHandlesForTest[4].RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(18, 10)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
            Check(Math.Abs(rightText.Width - beforeWidth - 18) < .5 && Math.Abs(rightText.Input.Height - beforeHeight - 10) < .5,
                "dragging the bottom-right circle changes the text frame width and height");
            EditMark sizedText = rightText.Mark;
            var reopenedSized = new InlineTextEditor(sizedText.Start, sizedText.Text, sizedText.Size, sizedText.Color, 1,
                new Size(400, 300), () => { }, () => { }, null, sizedText.TextSegments, sizedText.TextBoxWidth, sizedText.TextBoxHeight);
            Check(Math.Abs(reopenedSized.Width - rightText.Width) < .5 && Math.Abs(reopenedSized.Input.Height - rightText.Input.Height) < .5,
                "text frame dimensions survive confirmation and reopening");
            textCanvas.LayoutTransform = new ScaleTransform(2.5, 2.5); rightText.UpdateZoom(2.5);
            await textHost.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(Math.Abs(rightText.ResizeHandleForTest.ActualWidth * 2.5 - 14) < 1,
                "resize circle retains its screen size at increased page zoom");
        }
        finally { textHost.Close(); }
        var formatHandle = edgeText.FormatHandleSizeForTest;
        edgeText.UpdateZoom(2.5);
        Check(edgeText.MoveEdgesUseSizeAllForTest && edgeText.FormatHandleSizeForTest == formatHandle
            && Math.Abs(edgeText.ResizeHandleScreenSizeForTest.Width * 2.5 - 14) < .01,
            "text frame cursors and resize circles remain usable when zoom changes");
        Check(!preview.ActiveTextEditor!.CanDeleteForTest, "new text has no delete button before it is committed");
        var pendingText = preview.ActiveTextEditor;
        await preview.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(preview.IsTextEditorChildForTest(pendingText!.Input.Document), "flow document click stays inside its text editor");
        Check(preview.IsTextEditorChildForTest(new Run("確認")),
            "rich-text Run click stays inside its text editor");
        Check(pendingText!.Input.Height < 50 && ScrollViewer.GetHorizontalScrollBarVisibility(pendingText.Input) == ScrollBarVisibility.Disabled,
            "empty rich text box starts compact without a horizontal scrollbar");
        Check(pendingText!.Ink == Colors.Black && !pendingText.PaletteOpenForTest, "text formatting stays hidden while typing");
        Check(pendingText.ColorPickerLabelForTest is Border && pendingText.ColorSwatchSizeForTest.Width == 20
            && pendingText.ColorChoiceCountForTest == 16 && pendingText.ColorPaletteGridForTest,
            "text color button and four-column palette show swatches without color names");
        pendingText.OpenFormattingForTest();
        Check(pendingText.PaletteOpenForTest, "text formatting opens from its corner button");
        pendingText.OpenColorPaletteForTest();
        for (int i = 0; i < 20 && PresentationSource.FromVisual(pendingText.ColorPaletteVisualForTest) == null; i++)
        {
            await Task.Delay(50);
            preview.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        }
        await preview.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(pendingText.ColorPaletteVisualForTest.ActualWidth < 220
            && pendingText.ColorPaletteVisualForTest.ActualHeight < 220,
            "sixteen colors fit in a compact four-by-four palette");
        SavePreview(preview, folder + "/text-color-grid.png", pendingText.ColorPaletteVisualForTest);
        pendingText.SelectFormattingForTest("shippori", 18, true);
        Check(pendingText.Mark is { FontId: "shippori", Size: 18, Bold: true }, "popup changes font size and bold without using the top toolbar");
        pendingText.SelectColorForTest(1);
        Check(pendingText.Ink == Colors.Red && pendingText.Mark is { FontId: "shippori", Size: 18, Bold: true }, "popup color selection preserves other formatting");
        pendingText.SelectColorForTest(0);
        pendingText.PlainText = "赤青";
        pendingText.SelectTextForTest(0, 1);
        pendingText.SelectColorForTest(1);
        Check(pendingText.Mark.TextSegments is { Length: > 1 } parts && parts.Any(part => part.Text == "赤" && part.Color == Colors.Red)
            && parts.Any(part => part.Text == "青" && part.Color == Colors.Black), "selected text alone changes color within one text box");
        pendingText.SelectFormattingForTest("shippori", 24, true);
        Check(pendingText.Mark.TextSegments is { Length: > 1 } sized && sized.Any(part => part.Text == "赤" && part.Size == 24)
            && sized.Any(part => part.Text == "青" && part.Size == 18), "selected text alone changes size within one text box");
        Check(EditDrawing.TextShapes(pendingText.Mark).Select(shape => shape.Color).Distinct().Count() == 2
            && EditDrawing.TextShapes(pendingText.Mark).All(shape => shape.Geometry.IsFrozen),
            "mixed text colors remain separate vector shapes for PDF saving");
        EditMark styledMark = pendingText.Mark;
        var reopened = new InlineTextEditor(styledMark.Start, styledMark.Text, styledMark.Size, styledMark.Color, 1,
            new Size(400, 300), () => { }, () => { }, segments: styledMark.TextSegments);
        reopened.SetDefaultFont(styledMark.FontId, styledMark.Bold);
        Check(reopened.IsInputSourceForTest(((Paragraph)reopened.Input.Document.Blocks.FirstBlock!).Inlines.FirstInline!),
            "clicking a rich-text Run is treated as text input rather than a visual object");
        Check(reopened.Mark.TextSegments is { Length: > 1 } restored && restored.Any(part => part.Text == "赤" && part.Size == 24 && part.Color == Colors.Red)
            && restored.Any(part => part.Text == "青" && part.Size == 18 && part.Color == Colors.Black),
            "reopening one text box preserves mixed character formatting");
        pendingText!.PlainText = "入力中";
        Check(!pendingText.PaletteOpenForTest, "typing hides the formatting palette");
        preview.HandleEditorWheel(120, true, new Point(100, 100));
        Check(ReferenceEquals(preview.ActiveTextEditor, pendingText) && pendingText.PlainText == "入力中" && preview.CurrentModelForTest!.Frame.Marks.Length == 0, "wheel zoom preserves live text input without committing it");
        preview.ActiveTextEditor!.PlainText = "日本語\n二行目"; preview.ActiveTextEditor.SetFont("shippori", true); preview.CommitEdits();
        Check(!pendingText.PaletteOpenForTest, "committing text closes the floating palette");
        var editedModel = preview.CurrentModelForTest!;
        Check(editedModel.Frame.Marks[0].FontId == "shippori" && editedModel.Frame.Marks[0].Bold, "inline text retains selected font and bold style");
        Check(editedModel.Frame.Marks.Length == 1 && editedModel.Frame.Marks[0].Text.Contains("二行目"), "inline text box commits multiline text");
        preview.SelectToolForTest("ハイライト");
        Check(preview.HitTextForTest(new Point(12, 12)) == 0, "text stays selectable while highlight is the active tool");
        preview.SelectToolForTest("文字");
        preview.BeginText(new Point(12, 12)); preview.ActiveTextEditor!.PlainText = "修正"; preview.CommitEdits();
        Check(editedModel.Frame.Marks.Length == 1 && editedModel.Frame.Marks[0].Text == "修正", "clicking text updates the existing object");
        editedModel.Undo(); Check(editedModel.Frame.Marks[0].Text.Contains("二行目"), "undo restores text before editing"); editedModel.Redo();
        preview.BeginText(new Point(12, 12)); preview.ActiveTextEditor!.PlainText = "取消"; preview.CancelEdits();
        Check(editedModel.Frame.Marks[0].Text == "修正", "canceling inline edit preserves committed text");
        preview.BeginText(new Point(12, 12));
        Check(preview.ActiveTextEditor!.CanDeleteForTest, "reselected committed text offers a delete button");
        Check(preview.ActiveTextEditor.ObjectSelected && preview.DeleteSelectedObject()
            && editedModel.Frame.Marks.Length == 0 && preview.ActiveTextEditor == null,
            "Delete removes a selected existing text box");
        editedModel.Undo();
        preview.BeginText(new Point(12, 12));
        preview.ActiveTextEditor!.DeleteForTest();
        Check(editedModel.Frame.Marks.Length == 0 && preview.ActiveTextEditor == null, "delete button removes committed text");
        editedModel.Undo(); Check(editedModel.Frame.Marks.Length == 1 && editedModel.Frame.Marks[0].Text == "修正", "undo restores deleted text");
        preview.BeginShape(new("arrow", new(20, 60), new(80, 60), "", Colors.Red, 3));
        var pendingArrow = preview.ActiveShapeEditor; var pendingMark = pendingArrow!.Mark;
        Check(pendingArrow.ColorPaletteGridForTest, "shape colors use the same swatch-only four-column palette");
        await preview.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(pendingArrow.PaletteOpenForTest, "selected arrow opens a nearby thickness and color palette");
        Check(pendingArrow.ShortcutHintInPaletteForTest, "shortcut hint belongs to the open shape palette");
        SavePreview(preview, folder + "/arrow-editing.png", pendingArrow.PaletteForTest);
        pendingArrow.SelectFormattingForTest(7, 4);
        Check(pendingArrow.Mark is { Size: 7 } && pendingArrow.Mark.Color == Colors.Orange && pendingArrow.Mark.Start == pendingMark.Start && pendingArrow.Mark.End == pendingMark.End, "arrow popup changes appearance without moving endpoints");
        pendingMark = pendingArrow.Mark;
        preview.HandleEditorWheel(-120, true, new Point(100, 100));
        Check(ReferenceEquals(preview.ActiveShapeEditor, pendingArrow) && pendingArrow.Mark == pendingMark, "wheel zoom preserves arrow handles and logical coordinates");
        preview.ActiveShapeEditor!.MoveBy(new Vector(10, -10)); preview.ActiveShapeEditor.MoveEndpoint(false, new Vector(10, -10));
        preview.ActiveShapeEditor.SetAppearance(5, Colors.Blue); preview.CommitEdits();
        Check(!pendingArrow.PaletteOpenForTest, "committing arrow closes its palette");
        Check(editedModel.Frame.Marks[1] is { Start.X: 30, Start.Y: 50, End.X: 100, End.Y: 40, Size: 5 } && editedModel.Frame.Marks[1].Color == Colors.Blue, "arrow handles move, resize, recolor and change thickness");
        preview.BeginShape(editedModel.Frame.Marks[1], 1); preview.ActiveShapeEditor!.MoveBy(new Vector(1000, 1000)); preview.CommitEdits();
        Check(editedModel.Frame.Marks.Length == 2 && editedModel.Frame.Marks[1].End.X == 120 && editedModel.Frame.Marks[1].Start.Y == 80, "moving existing arrow stays inside image without duplicating it");
        editedModel.Undo(); Check(editedModel.Frame.Marks[1].End == new Point(100, 40), "undo restores arrow position");
        var savedArrow = editedModel.Frame.Marks[1];
        preview.SelectToolForTest("ハイライト");
        Check(preview.HitShapeForTest(savedArrow.Start + (savedArrow.End - savedArrow.Start) * .5) == 1,
            "arrow stays selectable while highlight is the active tool");
        preview.SelectToolForTest("文字");
        preview.BeginShape(savedArrow, 1); preview.ActiveShapeEditor!.SelectFormattingForTest(12, 3); preview.CancelEdits();
        Check(editedModel.Frame.Marks[1] == savedArrow, "canceling palette changes preserves the existing arrow");
        preview.BeginShape(savedArrow, 1);
        Check(preview.DeleteSelectedObject() && editedModel.Frame.Marks.Length == 1,
            "Delete removes a selected arrow");
        editedModel.Undo();
        preview.BeginShape(savedArrow, 1); preview.ActiveShapeEditor!.DeleteForTest();
        Check(editedModel.Frame.Marks.Length == 1, "arrow popup deletes a committed arrow");
        editedModel.Undo(); Check(editedModel.Frame.Marks.Length == 2 && editedModel.Frame.Marks[1] == savedArrow, "undo restores deleted arrow");
        preview.BeginShape(new("line", new(10, 20), new(90, 20), "", Colors.Black, 2));
        preview.ActiveShapeEditor!.SelectFormattingForTest(6, 2); preview.CommitEdits();
        Check(editedModel.Frame.Marks.Last() is { Kind: "line", Size: 6 } && editedModel.Frame.Marks.Last().Color == Colors.RoyalBlue, "line popup formatting commits to the document");
        preview.BeginShape(editedModel.Frame.Marks[^1], 2); preview.ActiveShapeEditor!.DeleteForTest();
        Check(editedModel.Frame.Marks.Length == 2 && preview.ActiveShapeEditor == null, "line popup deletes a committed line");
        editedModel.Undo(); Check(editedModel.Frame.Marks.Length == 3 && editedModel.Frame.Marks[^1].Kind == "line", "undo restores deleted line");
        editedModel.Undo(); Check(editedModel.Frame.Marks.Length == 2, "line popup edit can be undone");
        preview.BeginShape(new("highlight", new(10, 10), new(80, 35), "", Colors.Yellow, 1));
        await preview.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(preview.ActiveShapeEditor!.HandlesClearOfPaletteForTest, "highlight corner handles remain clear of the nearby palette");
        preview.ActiveShapeEditor!.SelectFormattingForTest(1, 1);
        preview.ActiveShapeEditor.MoveEndpoint(false, new Vector(10, 5)); preview.CommitEdits();
        Check(editedModel.Frame.Marks.Last() is { Kind: "highlight", End.X: 90, End.Y: 40 } && editedModel.Frame.Marks.Last().Color == Colors.HotPink, "highlight palette changes color and corner handles resize the area");
        editedModel.Undo(); Check(editedModel.Frame.Marks.Length == 2, "highlight can be undone");
        PdfTextCharacter[] selectionChars = [new('A', new(.1,.1,.1,.1)), new('B', new(.2,.1,.1,.1)), new('C', new(.3,.1,.1,.1)), new('\n', Rect.Empty), new('D', new(.1,.4,.1,.1)), new('E', new(.2,.4,.1,.1))];
        var textBoxes = TextHighlightSelection.Select(selectionChars, new(100,100), new(15,15), new(25,45));
        Check(textBoxes.Length == 2 && textBoxes[0].Bottom < textBoxes[1].Top, "text highlight follows two lines without filling the gap");
        Check(TextHighlightSelection.Select(selectionChars, new(100,100), new(25,45), new(15,15)).SequenceEqual(textBoxes), "reverse text selection matches forward selection");
        Check(TextHighlightSelection.Select([], new(100,100), new(15,15), new(25,45)).Length == 0, "scan without text has no invented text highlight");
        var textHighlight = new EditMark("highlight-text", textBoxes[0].TopLeft, textBoxes[^1].BottomRight, "", Colors.Yellow, 1, HighlightBoxes: textBoxes);
        preview.BeginShape(textHighlight); preview.ActiveShapeEditor!.MoveBy(new Vector(5,5));
        Check(preview.ActiveShapeEditor.Mark.HighlightBoxes![0].TopLeft == textBoxes[0].TopLeft + new Vector(5,5) && textHighlight.HighlightBoxes![0] == textBoxes[0], "moving text highlight keeps line boxes and original undo state");
        preview.CancelEdits();
        Point[] strokePoints = [new(20,20),new(50,20),new(50,60),new(90,60)];
        var freehand = new EditMark("highlight-freehand", new(20,20), new(90,60), "", Colors.DeepSkyBlue, 10, StrokePoints: strokePoints);
        preview.BeginShape(freehand); preview.ActiveShapeEditor!.MoveEndpoint(false, new Vector(20,10));
        Check(preview.ActiveShapeEditor.Mark.StrokePoints![^1] == new Point(110,70) && strokePoints[^1] == new Point(90,60), "freehand handles resize the complete stroke without mutating history");
        preview.ActiveShapeEditor.SelectFormattingForTest(18, 1); preview.CommitEdits();
        Check(editedModel.Frame.Marks.Last() is { Size: 18, Kind: "highlight-freehand" } && editedModel.Frame.Marks.Last().Color == Colors.HotPink, "freehand width and color are editable in its palette");
        editedModel.Undo();
        var freehandImage = new VisualEditModel(null,120,80); freehandImage.Add(freehand);
        freehandImage.SaveImage(folder + "/freehand.png", source);
        var savedStroke = Read(folder + "/freehand.png");
        Check(Pixel(savedStroke,30,20).A == 96 && Pixel(savedStroke,30,50).A == 0, "freehand PNG follows the path and leaves the bounding-box interior clear");
        var rectangle = new EditMark("rectangle", new(20, 20), new(120, 80), "", Colors.Blue, 6);
        var ellipse = new EditMark("ellipse", new(20, 90), new(120, 150), "", Colors.Red, 6);
        Check(EditDrawing.Outline(rectangle, 180, 170).FillContains(new Point(21, 50)) && !EditDrawing.Outline(rectangle, 180, 170).FillContains(new Point(70, 50)), "rectangle draws its border without filling its interior");
        Check(EditDrawing.Outline(ellipse, 180, 170).FillContains(new Point(70, 91)) && !EditDrawing.Outline(ellipse, 180, 170).FillContains(new Point(70, 120)), "ellipse draws its curved border without filling its interior");
        var figures = new VisualEditModel(null, 180, 170); figures.Add(rectangle); figures.Add(ellipse);
        figures.SaveImage(folder + "/simple-shapes.png", source);
        var savedFigures = Read(folder + "/simple-shapes.png");
        Check(Pixel(savedFigures, 21, 50).B > 200 && Pixel(savedFigures, 70, 50).A == 0 && Pixel(savedFigures, 70, 91).R > 200 && Pixel(savedFigures, 70, 120).A == 0, "PNG keeps rectangle and ellipse outlines editable and unfilled");
        preview.BeginShape(rectangle with { End = new Point(90, 50) }); preview.ActiveShapeEditor!.MoveEndpoint(false, new Vector(10, 20)); preview.ActiveShapeEditor.SetAppearance(8, Colors.Green); preview.CommitEdits();
        Check(editedModel.Frame.Marks.Last() is { Kind: "rectangle", End.X: 100, End.Y: 70, Size: 8 } && editedModel.Frame.Marks.Last().Color == Colors.Green, "rectangle handles resize and recolor the shape");
        editedModel.Undo(); Check(editedModel.Frame.Marks.Length == 2, "rectangle placement can be undone");
        Color[] highlightColors = [Colors.Yellow, Colors.HotPink, Colors.DeepSkyBlue, Colors.ForestGreen, Colors.Orange, Colors.MediumPurple];
        var highlights = new VisualEditModel(null, 360, 50);
        for (int i = 0; i < highlightColors.Length; i++) highlights.Add(new("highlight", new(i * 60 + 5, 5), new(i * 60 + 55, 45), "", highlightColors[i], 1));
        highlights.SaveImage(folder + "/highlights.png", source);
        var highlightPng = Read(folder + "/highlights.png");
        for (int i = 0; i < highlightColors.Length; i++)
        {
            Color pixel = Pixel(highlightPng, i * 60 + 30, 20), expected = highlightColors[i];
            Check(Math.Abs(pixel.A - 96) <= 1 && Math.Abs(pixel.R - expected.R) <= 3 && Math.Abs(pixel.G - expected.G) <= 3 && Math.Abs(pixel.B - expected.B) <= 3, "PNG highlight preserves translucent color " + i);
        }
        editedModel.MarkSaved(); preview.Close();

        if (imagesOnly)
        {
            int initialCount = VisualEditorWindow.CreatedCount;
            var imageReader = new MainWindow(showWelcome: false);
            try { await imageReader.ShowFilesAsync([source]); Check(VisualEditorWindow.CreatedCount == initialCount, "normal image viewing never creates editor"); }
            finally { imageReader.Close(); }
            File.WriteAllLines("artifacts/image-editor-test-results.txt", results);
            return;
        }

        string arrowSource = folder + "/arrows-source.pdf"; SelfTest.CreateFixture(arrowSource, 1);
        using (var highlightPdf = new PdfDocument(arrowSource))
        {
            var baseRender = highlightPdf.Render(0, 595, 842);
            var pdfHighlights = new VisualEditModel(null, 595.2756, 841.8898);
            for (int i = 0; i < highlightColors.Length; i++) pdfHighlights.Add(new("highlight", new(60, 200 + i * 50), new(140, 225 + i * 50), "", highlightColors[i], 1));
            pdfHighlights.Add(freehand);
            pdfHighlights.Add(textHighlight with { Start = new(200,100), End = new(250,150), HighlightBoxes = [new(200,100,50,12),new(200,130,30,12)] });
            pdfHighlights.Add(rectangle with { Start = new(300,450), End = new(480,580) });
            pdfHighlights.Add(ellipse with { Start = new(300,620), End = new(480,760) });
            await VisualEditorWindow.SavePdfAsync(highlightPdf, new Dictionary<int, VisualEditModel> { [0] = pdfHighlights }, folder + "/highlights.pdf");
            using var saved = new PdfDocument(folder + "/highlights.pdf"); var render = saved.Render(0, 595, 842);
            Check(Pixel(render,30,20) != Pixel(baseRender,30,20) && Pixel(render,30,50) == Pixel(baseRender,30,50), "freehand PDF preserves the stroke path");
            Check(Pixel(render,210,105) != Pixel(baseRender,210,105) && Pixel(render,210,120) == Pixel(baseRender,210,120) && Pixel(render,210,135) != Pixel(baseRender,210,135), "text highlight PDF preserves separate lines and their gap");
            Check(Pixel(render,301,500) != Pixel(baseRender,301,500) && Pixel(render,390,500) == Pixel(baseRender,390,500), "PDF rectangle retains a transparent interior");
            Check(Pixel(render,390,621) != Pixel(baseRender,390,621) && Pixel(render,390,690) == Pixel(baseRender,390,690), "PDF ellipse retains a transparent interior");
            for (int i = 0; i < highlightColors.Length; i++)
            {
                Color before = Pixel(baseRender, 100, 210 + i * 50), after = Pixel(render, 100, 210 + i * 50), ink = highlightColors[i];
                bool Blend(byte actual, byte original, byte tint) => Math.Abs(actual - (original * 159 + tint * 96) / 255.0) <= 3;
                Check(Blend(after.R, before.R, ink.R) && Blend(after.G, before.G, ink.G) && Blend(after.B, before.B, ink.B), "PDF highlight blends with original content color " + i);
            }
        }
        using (var arrowPdf = new PdfDocument(arrowSource))
        {
            var pdfArrows = new VisualEditModel(null, 595.2756, 841.8898);
            foreach (var arrow in arrowCases) pdfArrows.Add(arrow);
            await VisualEditorWindow.SavePdfAsync(arrowPdf, new Dictionary<int, VisualEditModel> { [0] = pdfArrows }, folder + "/arrow-tips.pdf");
            using var saved = new PdfDocument(folder + "/arrow-tips.pdf"); var render = saved.Render(0, 595, 842);
            foreach (var arrow in arrowCases)
            {
                Vector direction = arrow.End - arrow.Start; direction.Normalize(); Point outside = arrow.End + direction * 3;
                Color pixel = Pixel(render, (int)Math.Round(outside.X), (int)Math.Round(outside.Y));
                Check(pixel.R > 245 && pixel.G > 245 && pixel.B > 245, "saved PDF has no shaft beyond arrow tip: " + Array.IndexOf(arrowCases, arrow));
            }
        }
        string fontSource = folder + "/fonts-source.pdf"; SelfTest.CreateFixture(fontSource, 1);
        using (var fontPdf = new PdfDocument(fontSource))
        {
            var pdfEditor = new VisualEditorWindow(fontPdf, 0, _ => Task.CompletedTask, _ => false);
            pdfEditor.Show();
            for (int i = 0; i < 100 && pdfEditor.CurrentModelForTest == null; i++) await Task.Delay(20);
            Check(pdfEditor.CurrentModelForTest != null, "PDF editor finishes loading for wheel input");
            await pdfEditor.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Point anchor = new(300, 200);
            for (int i = 0; i < 3; i++) pdfEditor.HandleEditorWheel(1200, true, anchor);
            await pdfEditor.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(pdfEditor.EditorZoomForTest == 8, "editor wheel respects maximum zoom");
            Point beforeZoom = pdfEditor.PaperPointForTest(anchor);
            pdfEditor.HandleEditorWheel(-120, true, anchor);
            await pdfEditor.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check((pdfEditor.PaperPointForTest(anchor) - beforeZoom).Length < 1, "PDF wheel keeps the paper point under the cursor when scrollable");
            for (int i = 0; i < 10; i++) pdfEditor.HandleEditorWheel(-1200, true, anchor);
            Check(pdfEditor.EditorZoomForTest == .01 && !fontPdf.Dirty && !pdfEditor.CurrentModelForTest!.Dirty, "editor wheel respects minimum zoom without editing the document");
            pdfEditor.Close();
            string selectablePath = folder + "/selectable-highlight.pdf"; PdfLinkTests.CreateFixture(selectablePath);
            using (var selectablePdf = new PdfDocument(selectablePath))
            {
                var highlightEditor = new VisualEditorWindow(selectablePdf, 0, _ => Task.CompletedTask, _ => false); highlightEditor.Show();
                for (int i = 0; i < 100 && highlightEditor.CurrentModelForTest == null; i++) await Task.Delay(20);
                Check(highlightEditor.ToolNamesForTest.SequenceEqual(new[] { "文字", "矢印", "線", "四角形", "円・楕円", "ハイライト" }) && highlightEditor.HighlightMethodNamesForTest.SequenceEqual(new[] { "範囲指定", "フリーハンド" }), "PDF editor nests two methods under highlight");
                var chars = selectablePdf.TextCharacters(0).Where(c => !c.RelativeBox.IsEmpty && !char.IsWhiteSpace(c.Character)).Take(2).ToArray();
                Check(chars.Length == 2, "PDF fixture has selectable text for automatic highlight");
                Point Center(PdfTextCharacter c) => new((c.RelativeBox.X + c.RelativeBox.Width / 2) * highlightEditor.CurrentModelForTest!.Frame.Width, (c.RelativeBox.Y + c.RelativeBox.Height / 2) * highlightEditor.CurrentModelForTest!.Frame.Height);
                var snapped = highlightEditor.HighlightForTest(false, Center(chars[0]), Center(chars[1]));
                Check(snapped.Kind == "highlight-text" && snapped.HighlightBoxes is { Length: > 0 }, "range highlight fits selectable PDF text");
                var scanFallback = highlightEditor.HighlightForTest(false, new(350, 550), new(370, 560));
                Check(scanFallback.Kind == "highlight" && scanFallback.StrokePoints == null, "range highlight uses a rectangle where PDF text is absent");
                var pdfFreehand = highlightEditor.HighlightForTest(true, new(350, 550), new(370, 560));
                Check(pdfFreehand.Kind == "highlight-freehand" && pdfFreehand.StrokePoints is { Length: 2 }, "freehand highlight follows a PDF drag");
                highlightEditor.Close();
            }
            var fontModel = new VisualEditModel(null, 595.2756, 841.8898);
            int row = 0;
            foreach (var choice in EditorFonts.Choices.Where(item => item.Folder != null))
            {
                Typeface face = EditorFonts.Typeface(choice.Id, false);
                File.AppendAllText(folder + "/font-resolution.txt", $"{choice.Id}: resolved={face.TryGetGlyphTypeface(out var details)} uri={details?.FontUri} Japanese={details?.CharacterToGlyphMap.ContainsKey('語')}\n");
                Check(face.TryGetGlyphTypeface(out var glyph) && glyph.FontUri.LocalPath.Replace('\\', '/').Contains("/Fonts/", StringComparison.OrdinalIgnoreCase) && glyph.CharacterToGlyphMap.ContainsKey('語'), $"bundled font loads Japanese glyphs without fallback: {choice.Label}");
                var mark = new EditMark("text", new(30, 40 + row * 80), new(), "日本語 漢字 ひらがな ABC", Colors.Black, 26, choice.Id);
                fontModel.Add(mark);
                var sample = new VisualEditModel(null, 600, 90); sample.Add(mark with { Start = new Point(10, 10) });
                sample.SaveImage(folder + "/font-" + choice.Id + ".png", source);
                Check(HasInk(Read(folder + "/font-" + choice.Id + ".png"), new Int32Rect(10, 10, 200, 40)), $"selected font saves to PNG: {choice.Label}");
                row++;
            }
            await VisualEditorWindow.SavePdfAsync(fontPdf, new Dictionary<int, VisualEditModel> { [0] = fontModel }, folder + "/fonts-drawn.pdf");
            using var saved = new PdfDocument(folder + "/fonts-drawn.pdf");
            BitmapSource rendered = saved.Render(0, 595, 842);
            for (int i = 0; i < row; i++) Check(HasInk(rendered, new Int32Rect(30, 40 + i * 80, 200, 40)), $"selected font saves to PDF row {i + 1}");
        }
        string rotationSource = folder + "/rotation-source.pdf", rotated = folder + "/rotation-drawn.pdf";
        SelfTest.CreateFixture(rotationSource, 4, mixedGeometry: true);
        using (var rotationPdf = new PdfDocument(rotationSource))
        {
            var rotationModels = new Dictionary<int, VisualEditModel>();
            for (int i = 0; i < 4; i++)
            {
                Size mm = rotationPdf.SizeMm(i); double w = mm.Width * 72 / 25.4, h = mm.Height * 72 / 25.4;
                var drawing = new VisualEditModel(null, w, h);
                drawing.Add(new("arrow", new(w * .15, h * .2), new(w * .75, h * .2), "", Colors.Red, 4));
                drawing.Add(new("line", new(w * .15, h * .6), new(w * .75, h * .6), "", Colors.Blue, 4));
                drawing.Add(new("text", new(w * .15, h * .35), new(), "日本語 ABC", Colors.Green, 24));
                rotationModels[i] = drawing;
            }
            await VisualEditorWindow.SavePdfAsync(rotationPdf, rotationModels, rotated);
        }
        using (var pdf = new PdfDocument(rotated))
        {
            for (int page = 0; page < 4; page++)
            {
                var size = pdf.SizeMm(page); int w = (int)Math.Round(size.Width * 72 / 25.4), h = (int)Math.Round(size.Height * 72 / 25.4);
                BitmapSource render = pdf.Render(page, w, h);
                Color red = Pixel(render, w / 2, (int)Math.Round(h * .2));
                Color blue = Pixel(render, w / 2, (int)Math.Round(h * .6));
                Check(red.R > 230 && red.G < 30 && blue.B > 230 && blue.R < 30, $"PDF {page * 90} degree rotation and offset CropBox: rendered arrow/line align");
            }
        }
        string fixture = folder + "/source.pdf"; SelfTest.CreateFixture(fixture, 2);
        using (var pdf = new PdfDocument(fixture))
        {
            byte[] pdfHash = SHA256.HashData(File.ReadAllBytes(fixture));
            pdf.Rotate(0, 1); var size = pdf.SizeMm(0);
            var pageModel = new VisualEditModel(null, size.Width * 72 / 25.4, size.Height * 72 / 25.4);
            pageModel.Add(new("arrow", new(20, 20), new(180, 20), "", Colors.Red, 4));
            pageModel.Add(new("text", new(20, 40), new(20, 40), "日本語の確認", Colors.Black, 14));
            var page2 = new VisualEditModel(null, 595, 842); page2.Add(new("text", new(20, 40), new(20, 40), "Second page", Colors.Black, 14));
            var pages = new Dictionary<int, VisualEditModel> { [0] = pageModel, [1] = page2 };
            await VisualEditorWindow.SavePdfAsync(pdf, pages, folder + "/annotated.pdf");
            using var saved = new PdfDocument(folder + "/annotated.pdf");
            Check(saved.Count == 2 && HasInk(saved.Render(0, 842, 595), new Int32Rect(20, 40, 120, 20)) && HasInk(saved.Render(1, 595, 842), new Int32Rect(20, 40, 100, 20)), "native PDF save retains Japanese and Latin text outlines on multiple pages");
            Check(Math.Abs(saved.SizeMm(0).Width - size.Width) < .1 && pdf.Dirty, "unsaved view rotation is retained in copy without clearing original dirty state");
            Check(pdfHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture))), "PDF source hash unchanged");
            Check(!pageModel.Dirty && !page2.Dirty, "saved pages marked clean");
            try { await VisualEditorWindow.SavePdfAsync(pdf, pages, fixture); Check(false, "PDF source overwrite rejected"); } catch (IOException) { Check(true, "PDF source overwrite rejected"); }
        }
        string forms = System.IO.Path.GetFullPath("artifacts/feature-tests/forms.pdf");
        using (var pdf = new PdfDocument(forms))
        {
            var pageModel = new VisualEditModel(null, 595, 842);
            pageModel.Add(new("text", new(20, 100), new(), "日本語", Colors.Red, 18));
            var pages = new Dictionary<int, VisualEditModel> { [0] = pageModel };
            await VisualEditorWindow.SavePdfAsync(pdf, pages, folder + "/forms-drawn.pdf");
            using var saved = new PdfDocument(folder + "/forms-drawn.pdf");
            Check(saved.PageText(0).Contains("Searchable document"), "original PDF text stays searchable");
            string protectedDestination = folder + "/preserved.pdf";
            File.WriteAllText(protectedDestination, "keep existing file");
            try { await VisualEditorWindow.SavePdfAsync(pdf, new Dictionary<int, VisualEditModel> { [99] = pageModel }, protectedDestination); Check(false, "invalid page rejected"); }
            catch (ArgumentOutOfRangeException) { Check(File.ReadAllText(protectedDestination) == "keep existing file", "failed save preserves destination"); }
        }
        using (var encrypted = new PdfDocument(System.IO.Path.GetFullPath("artifacts/feature-tests/encrypted.pdf"), "secret"))
        {
            var drawing = new VisualEditModel(null, 595, 842); drawing.Add(new("line", new(20, 100), new(180, 100), "", Colors.Blue, 4));
            await VisualEditorWindow.SavePdfAsync(encrypted, new Dictionary<int, VisualEditModel> { [0] = drawing }, folder + "/encrypted-drawn.pdf");
            try { using var withoutPassword = new PdfDocument(folder + "/encrypted-drawn.pdf"); Check(false, "encryption retained"); }
            catch (PdfPasswordException) { Check(true, "encryption retained"); }
            using var saved = new PdfDocument(folder + "/encrypted-drawn.pdf", "secret");
            Check(saved.PageText(0).Contains("Searchable document"), "encrypted copy reopens with original password");
        }
        using (var signed = new PdfDocument(System.IO.Path.GetFullPath("artifacts/feature-tests/signed.pdf")))
        {
            try { await VisualEditorWindow.SavePdfAsync(signed, new Dictionary<int, VisualEditModel>(), folder + "/signed-rejected.pdf"); Check(false, "signed PDF rejected"); }
            catch (IOException) { Check(!File.Exists(folder + "/signed-rejected.pdf"), "signed PDF rejected before writing"); }
        }
        int editors = VisualEditorWindow.CreatedCount;
        var reader = new MainWindow(showWelcome: false);
        try
        {
            await reader.ShowFilesAsync([source]);
            Check(((Button)reader.FindName("VisualEditButton")).Visibility == Visibility.Visible, "image has edit button");
            await reader.OpenPathsAsync([fixture]);
            Check(((Button)reader.FindName("VisualEditButton")).IsEnabled, "editable PDF has write button");
            Check(VisualEditorWindow.CreatedCount == editors, "normal image/PDF viewing never creates editor");
        }
        finally { reader.Close(); }
        File.WriteAllLines("artifacts/editor-test-results.txt", results);
    }
}
