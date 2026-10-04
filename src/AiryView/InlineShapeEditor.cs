using System.Globalization;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Effects;

namespace AiryView;

internal sealed class InlineShapeEditor : Canvas
{
    internal EditMark Mark { get; private set; }
    private readonly Thumb start, end, move;
    private readonly ArrowLayer? arrowPreview;
    private sealed class ArrowLayer(InlineShapeEditor editor) : FrameworkElement
    {
        protected override void OnRender(DrawingContext context)
        {
            // 太い軸で移動つまみを覆わず、先端だけは重なった丸より手前へ出す。
            context.PushClip(new EllipseGeometry(editor.Mark.End, 4 * editor.unit, 4 * editor.unit));
            EditDrawing.DrawMark(context, editor.Mark); context.Pop();
        }
    }
    private readonly Popup palette;
    private readonly TextBox sizePicker;
    private readonly Button colorPicker;
    private readonly Border colorSwatch;
    private readonly EditorColorPalette colorPalette;
    private readonly TextBlock shortcutHint;
    private readonly Action remove;
    private bool syncing;
    private Window? owner;
    private Point lastScreenPoint;
    internal event Action? AppearanceChanged;
    internal event Action? SaveRequested;
    internal event Action<int>? ZoomRequested;
    internal bool PaletteOpenForTest => palette.IsOpen;
    internal bool ColorPaletteGridForTest => colorPalette.Columns == 4 && colorPalette.SwatchesOnly;
    internal bool HandlesClearOfPaletteForTest
    {
        get
        {
            if (!palette.IsOpen || palette.Child is not FrameworkElement card || !card.IsLoaded) return false;
            static Rect ScreenRect(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
            Rect panel = ScreenRect(card);
            return !panel.IntersectsWith(ScreenRect(start)) && !panel.IntersectsWith(ScreenRect(end));
        }
    }
    internal void DeleteForTest() => DeleteSelected();
    internal void DeleteSelected() => remove();
    private double unit;
    internal InlineShapeEditor(EditMark mark, double zoom, Size bounds, Action accept, Action cancel, Action remove, string sizeUnit = "pt")
    {
        Mark = mark; Width = bounds.Width; Height = bounds.Height; unit = 1 / zoom; this.remove = remove;
        shortcutHint = new TextBlock { Text = "Ctrl+Enterで確定  ·  Deleteで削除", IsHitTestVisible = false, Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)) };
        shortcutHint.FontSize = 12; shortcutHint.Margin = new Thickness(0, 0, 0, 8);
        bool highlight = mark.Kind.StartsWith("highlight", StringComparison.Ordinal);
        bool area = highlight && mark.Kind != "highlight-freehand";
        bool figure = mark.Kind is "rectangle" or "ellipse";
        bool arrow = mark.Kind == "arrow";
        start = Handle(arrow ? "起点をドラッグ" : highlight || figure ? "角をドラッグして大きさを変更" : "始点をドラッグ", Cursors.Cross);
        end = Handle(arrow ? "先端をドラッグ" : highlight || figure ? "反対の角をドラッグして大きさを変更" : "終点をドラッグ", Cursors.Cross);
        move = Handle(arrow ? "矢印全体を移動" : "図形を移動", Cursors.SizeAll);
        if (arrow)
        {
            // つまみが重なっても矢じりを隠さず、操作は下のThumbへ通す。
            arrowPreview = new ArrowLayer(this) { Width = bounds.Width, Height = bounds.Height, IsHitTestVisible = false };
            SetZIndex(arrowPreview, 1); Children.Add(arrowPreview);
        }
        UpdateHandleSizes();
        start.DragDelta += (_, e) => { MoveEndpoint(true, new Vector(e.HorizontalChange, e.VerticalChange)); e.Handled = true; };
        end.DragDelta += (_, e) => { MoveEndpoint(false, new Vector(e.HorizontalChange, e.VerticalChange)); e.Handled = true; };
        move.DragDelta += (_, e) => { MoveBy(new Vector(e.HorizontalChange, e.VerticalChange)); e.Handled = true; };
        var root = new StackPanel(); root.Children.Add(shortcutHint);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
        commands.Children.Add(new TextBlock { Text = highlight ? "ハイライト" : mark.Kind == "arrow" ? "矢印" : mark.Kind == "rectangle" ? "四角形" : mark.Kind == "ellipse" ? "円・楕円" : "線", Width = 70, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.SlateGray });
        root.Children.Add(commands);
        if (arrow) root.Children.Add(new TextBlock { Text = "○ 起点  ──▶  先端\n端の丸で長さ・向き、中央の移動マークで移動", FontSize = 12,
            Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 9) });
        var formats = new StackPanel { Orientation = Orientation.Horizontal };
        formats.Children.Add(new TextBlock { Text = "太さ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        sizePicker = new TextBox { Width = 54, Height = 30, Padding = new Thickness(5, 2, 5, 2), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "線の太さ", Margin = new Thickness(0, 0, 7, 0) };
        formats.Children.Add(FontSizeControls.Wrap(sizePicker, stroke: true));
        formats.Children.Add(new TextBlock { Text = sizeUnit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        if (area) { formats.Children.Clear(); formats.Children.Add(new TextBlock { Text = "角の丸で範囲・中央の移動マークで移動", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }); }
        colorSwatch = new Border { Width = 18, Height = 18, Background = new SolidColorBrush(mark.Color),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
        colorPicker = new Button { Content = colorSwatch, ToolTip = highlight ? "ハイライトの色" : "矢印・線の色",
            Width = 46, Height = 34, Background = Brushes.White, BorderBrush = Brushes.LightGray };
        formats.Children.Add(colorPicker); root.Children.Add(formats);
        var choices = highlight
            ? new (string, Color)[] { ("黄", Colors.Yellow), ("ピンク", Colors.HotPink), ("水色", Colors.DeepSkyBlue), ("緑", Colors.ForestGreen), ("橙", Colors.Orange), ("紫", Colors.MediumPurple) }
            : new (string, Color)[] { ("黒", Colors.Black), ("赤", Colors.Red), ("青", Colors.RoyalBlue), ("緑", Colors.ForestGreen), ("橙", Colors.Orange), ("白", Colors.White) };
        colorPalette = new EditorColorPalette(colorPicker, choices,
            selected => { SetAppearance(Mark.Size, selected); AppearanceChanged?.Invoke(); });
        var card = new Border { Child = root, Padding = new Thickness(12), Margin = new Thickness(8), Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = .18, Color = Colors.Black } };
        card.SetValue(TextElement.FontFamilyProperty, new FontFamily("Yu Gothic UI")); card.SetValue(TextElement.FontSizeProperty, 12.0);
        palette = new Popup { Child = card, PlacementTarget = Mark.Start.Y >= Mark.End.Y ? start : end, Placement = PlacementMode.Bottom, VerticalOffset = 12, AllowsTransparency = true, StaysOpen = true };
        sizePicker.TextChanged += (_, _) =>
        {
            if (syncing || !double.TryParse(sizePicker.Text, out double size) || !double.IsFinite(size) || size <= 0 || size > 1000) return;
            SetAppearance(size, Mark.Color); AppearanceChanged?.Invoke();
        };
        card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { cancel(); e.Handled = true; }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Enter) { accept(); e.Handled = true; }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.S) { SaveRequested?.Invoke(); e.Handled = true; }
        };
        card.PreviewMouseWheel += (_, e) => { if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { ZoomRequested?.Invoke(e.Delta); e.Handled = true; } };
        Loaded += (_, _) => { owner = Window.GetWindow(this); if (owner != null) { owner.Deactivated += HidePalette; owner.Activated += ShowPalette; } palette.IsOpen = true; };
        Unloaded += (_, _) => { ClosePalette(); if (owner != null) { owner.Deactivated -= HidePalette; owner.Activated -= ShowPalette; } owner = null; };
        LayoutUpdated += (_, _) => RepositionPalette();
        SetAppearance(mark.Size, mark.Color); Refresh();
    }
    private void HidePalette(object? sender, EventArgs e) => ClosePalette();
    private void ShowPalette(object? sender, EventArgs e) { if (IsLoaded) palette.IsOpen = true; }
    internal void ClosePalette() { palette.IsOpen = false; colorPalette.Close(); }
    private void RepositionPalette()
    {
        // 画面の切替中はUnloaded前でも表示先が外れるため、座標変換できない。
        if (!IsLoaded || !palette.IsOpen || PresentationSource.FromVisual(move) == null) return;
        Point now = move.PointToScreen(new Point());
        if ((now - lastScreenPoint).Length < .1) return;
        lastScreenPoint = now; palette.HorizontalOffset += .01; palette.HorizontalOffset -= .01;
    }
    internal void SelectFormattingForTest(double size, int colorIndex)
    {
        sizePicker.Text = size.ToString(CultureInfo.CurrentCulture);
        colorPalette.SelectForTest(colorIndex);
    }
    private Thumb Handle(string tip, Cursor cursor)
    {
        var thumb = new Thumb { Width = 18 * unit, Height = 18 * unit, Background = Brushes.LightBlue, BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(unit), Cursor = cursor, ToolTip = tip };
        Children.Add(thumb); return thumb;
    }
    private void UpdateHandleSizes()
    {
        foreach (Thumb thumb in new[] { start, end, move })
        {
            thumb.Width = thumb.Height = (ReferenceEquals(thumb, move) ? 20 : 14) * unit;
            thumb.BorderThickness = new Thickness(unit);
            if (ReferenceEquals(thumb, move))
            {
                var marker = new FrameworkElementFactory(typeof(Border));
                marker.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(235, 241, 245, 249)));
                marker.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(20, 80, 106)));
                marker.SetValue(Border.BorderThicknessProperty, new Thickness(1.2 * unit));
                marker.SetValue(Border.CornerRadiusProperty, new CornerRadius(3 * unit));
                var symbol = new FrameworkElementFactory(typeof(TextBlock));
                symbol.SetValue(TextBlock.TextProperty, "✥");
                symbol.SetValue(TextBlock.FontSizeProperty, 15 * unit);
                symbol.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(29, 78, 110)));
                symbol.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                symbol.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                marker.AppendChild(symbol);
                thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = marker };
            }
            else
            {
                var ring = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
                ring.SetValue(System.Windows.Shapes.Shape.FillProperty, Brushes.Transparent);
                ring.SetValue(System.Windows.Shapes.Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(20, 80, 106)));
                ring.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.2 * unit);
                thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = ring };
            }
        }
    }
    internal void UpdateZoom(double zoom) { unit = 1 / zoom; UpdateHandleSizes(); Refresh(); }
    internal FrameworkElement PaletteForTest => (FrameworkElement)palette.Child;
    internal bool ShortcutHintInPaletteForTest => shortcutHint.Parent is StackPanel && shortcutHint.FontSize == 12;
    internal Size TipHandleSizeForTest => new(end.Width, end.Height);
    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, Width), Math.Clamp(point.Y, 0, Height));
    internal void MoveEndpoint(bool first, Vector delta)
    {
        Point startPoint = first ? Clamp(Mark.Start + delta) : Mark.Start;
        Point endPoint = first ? Mark.End : Clamp(Mark.End + delta);
        if (Mark.StrokePoints != null || Mark.HighlightBoxes != null)
        {
            Rect old = new(Mark.Start, Mark.End), next = new(startPoint, endPoint);
            if (next.Width < .1 || next.Height < .1) return;
            Point Map(Point p) => new(next.Left + (p.X - old.Left) * next.Width / Math.Max(.1, old.Width), next.Top + (p.Y - old.Top) * next.Height / Math.Max(.1, old.Height));
            Mark = Mark with { StrokePoints = Mark.StrokePoints?.Select(Map).ToArray(), HighlightBoxes = Mark.HighlightBoxes?.Select(b => new Rect(Map(b.TopLeft), Map(b.BottomRight))).ToArray() };
        }
        Mark = Mark with { Start = startPoint, End = endPoint }; Refresh();
    }
    internal void MoveBy(Vector delta)
    {
        delta.X = Math.Clamp(delta.X, -Math.Min(Mark.Start.X, Mark.End.X), Width - Math.Max(Mark.Start.X, Mark.End.X));
        delta.Y = Math.Clamp(delta.Y, -Math.Min(Mark.Start.Y, Mark.End.Y), Height - Math.Max(Mark.Start.Y, Mark.End.Y));
        Mark = Mark with { Start = Mark.Start + delta, End = Mark.End + delta,
            StrokePoints = Mark.StrokePoints?.Select(p => p + delta).ToArray(),
            HighlightBoxes = Mark.HighlightBoxes?.Select(b => new Rect(b.TopLeft + delta, b.Size)).ToArray() }; Refresh();
    }
    internal void SetAppearance(double size, Color color)
    {
        Mark = Mark with { Size = size, Color = color };
        syncing = true; sizePicker.Text = size.ToString(CultureInfo.CurrentCulture); colorSwatch.Background = new SolidColorBrush(color); syncing = false;
        UpdateArrowPreview(); InvalidateVisual();
    }
    private void Refresh()
    {
        Point middle = Mark.Start + (Mark.End - Mark.Start) * .5;
        Place(start, Mark.Start); Place(end, Mark.End); Place(move, middle);
        palette.PlacementTarget = Mark.Start.Y >= Mark.End.Y ? start : end;
        RepositionPalette();
        UpdateArrowPreview();
        InvalidateVisual();
    }
    private void UpdateArrowPreview()
    {
        arrowPreview?.InvalidateVisual();
    }
    private void Place(Thumb thumb, Point point) { SetLeft(thumb, point.X - thumb.Width / 2); SetTop(thumb, point.Y - thumb.Height / 2); }
    protected override void OnRender(DrawingContext context) { base.OnRender(context); EditDrawing.DrawMark(context, Mark); }
}
