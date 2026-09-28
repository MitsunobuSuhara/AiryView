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
    private readonly Popup palette;
    private readonly TextBox sizePicker;
    private readonly Button colorPicker;
    private Button deleteButton = null!;
    private bool syncing;
    private Window? owner;
    private Point lastScreenPoint;
    internal event Action? AppearanceChanged;
    internal event Action? SaveRequested;
    internal event Action<int>? ZoomRequested;
    internal bool PaletteOpenForTest => palette.IsOpen;
    internal void DeleteForTest() => deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private readonly double unit;
    internal InlineShapeEditor(EditMark mark, double zoom, Size bounds, Action accept, Action cancel, Action remove, string sizeUnit = "pt")
    {
        Mark = mark; Width = bounds.Width; Height = bounds.Height; unit = 1 / zoom;
        bool highlight = mark.Kind.StartsWith("highlight", StringComparison.Ordinal);
        bool area = highlight && mark.Kind != "highlight-freehand";
        bool figure = mark.Kind is "rectangle" or "ellipse";
        start = Handle(highlight || figure ? "角をドラッグして大きさを変更" : "始点をドラッグ", Cursors.Cross); end = Handle(highlight || figure ? "反対の角をドラッグして大きさを変更" : "終点をドラッグ", Cursors.Cross); move = Handle("図形を移動", Cursors.SizeAll);
        start.DragDelta += (_, e) => { MoveEndpoint(true, new Vector(e.HorizontalChange, e.VerticalChange)); e.Handled = true; };
        end.DragDelta += (_, e) => { MoveEndpoint(false, new Vector(e.HorizontalChange, e.VerticalChange)); e.Handled = true; };
        move.DragDelta += (_, e) => { MoveBy(new Vector(e.HorizontalChange, e.VerticalChange)); e.Handled = true; };
        var root = new StackPanel();
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
        commands.Children.Add(new TextBlock { Text = highlight ? "ハイライト" : mark.Kind == "arrow" ? "矢印" : mark.Kind == "rectangle" ? "四角形" : mark.Kind == "ellipse" ? "円・楕円" : "線", Width = 70, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.SlateGray });
        foreach (var (label, action) in new (string, Action)[] { ("確定", accept), ("取消", cancel), ("削除", remove) })
        {
            var button = new Button { Content = label, FontSize = 12, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(3, 0, 0, 0), Background = Brushes.White, BorderBrush = Brushes.LightGray };
            if (label == "確定") { button.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); button.Foreground = Brushes.White; button.BorderBrush = button.Background; }
            if (label == "削除") deleteButton = button;
            button.Click += (_, e) => { e.Handled = true; action(); }; commands.Children.Add(button);
        }
        root.Children.Add(commands);
        var formats = new StackPanel { Orientation = Orientation.Horizontal };
        formats.Children.Add(new TextBlock { Text = "太さ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        sizePicker = new TextBox { Width = 54, Height = 30, Padding = new Thickness(5, 2, 5, 2), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "線の太さ", Margin = new Thickness(0, 0, 7, 0) };
        formats.Children.Add(FontSizeControls.Wrap(sizePicker, stroke: true));
        formats.Children.Add(new TextBlock { Text = sizeUnit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        if (area) { formats.Children.Clear(); formats.Children.Add(new TextBlock { Text = "角で範囲・中央で移動", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }); }
        colorPicker = new Button { Content = "● 色", ToolTip = highlight ? "ハイライトの色" : "矢印・線の色", MinWidth = 60, Height = 30, Background = Brushes.White, BorderBrush = Brushes.LightGray };
        formats.Children.Add(colorPicker); root.Children.Add(formats);
        var colors = new ContextMenu();
        var choices = highlight
            ? new (string, Color)[] { ("黄", Colors.Yellow), ("ピンク", Colors.HotPink), ("水色", Colors.DeepSkyBlue), ("緑", Colors.ForestGreen), ("橙", Colors.Orange), ("紫", Colors.MediumPurple) }
            : new (string, Color)[] { ("黒", Colors.Black), ("赤", Colors.Red), ("青", Colors.RoyalBlue), ("緑", Colors.ForestGreen), ("橙", Colors.Orange), ("白", Colors.White) };
        foreach (var (name, color) in choices)
        {
            var item = new MenuItem { Header = name, Icon = new Border { Width = 14, Height = 14, Background = new SolidColorBrush(color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) } };
            item.Click += (_, _) => { SetAppearance(Mark.Size, color); AppearanceChanged?.Invoke(); }; colors.Items.Add(item);
        }
        colorPicker.ContextMenu = colors;
        colorPicker.Click += (_, _) => { colors.PlacementTarget = colorPicker; colors.Placement = PlacementMode.Bottom; colors.IsOpen = true; };
        var card = new Border { Child = root, Padding = new Thickness(12), Margin = new Thickness(8), Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = .18, Color = Colors.Black } };
        card.SetValue(TextElement.FontFamilyProperty, new FontFamily("Yu Gothic UI")); card.SetValue(TextElement.FontSizeProperty, 12.0);
        palette = new Popup { Child = card, PlacementTarget = move, Placement = PlacementMode.Bottom, VerticalOffset = 6, AllowsTransparency = true, StaysOpen = true };
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
    internal void ClosePalette() { palette.IsOpen = false; if (colorPicker.ContextMenu != null) colorPicker.ContextMenu.IsOpen = false; }
    private void RepositionPalette()
    {
        if (!IsLoaded || !palette.IsOpen) return;
        Point now = move.PointToScreen(new Point());
        if ((now - lastScreenPoint).Length < .1) return;
        lastScreenPoint = now; palette.HorizontalOffset += .01; palette.HorizontalOffset -= .01;
    }
    internal void SelectFormattingForTest(double size, int colorIndex)
    {
        sizePicker.Text = size.ToString(CultureInfo.CurrentCulture);
        ((MenuItem)colorPicker.ContextMenu!.Items[colorIndex]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
    private Thumb Handle(string tip, Cursor cursor)
    {
        var thumb = new Thumb { Width = 12 * unit, Height = 12 * unit, Background = Brushes.LightBlue, BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(unit), Cursor = cursor, ToolTip = tip };
        Children.Add(thumb); return thumb;
    }
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
        syncing = true; sizePicker.Text = size.ToString(CultureInfo.CurrentCulture); colorPicker.Foreground = color == Colors.White ? Brushes.Gray : new SolidColorBrush(color); syncing = false;
        InvalidateVisual();
    }
    private void Refresh()
    {
        Point middle = Mark.Start + (Mark.End - Mark.Start) * .5;
        Place(start, Mark.Start); Place(end, Mark.End); Place(move, middle);
        RepositionPalette();
        InvalidateVisual();
    }
    private void Place(Thumb thumb, Point point) { SetLeft(thumb, point.X - thumb.Width / 2); SetTop(thumb, point.Y - thumb.Height / 2); }
    protected override void OnRender(DrawingContext context) { base.OnRender(context); EditDrawing.DrawMark(context, Mark); }
}
