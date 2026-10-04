using System.Globalization;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Effects;

namespace AiryView;

// 入力欄と近くの書式パレットは編集時だけ生成する。パレットは用紙の外でも切れない。
internal sealed class InlineTextEditor : Border
{
    internal RichTextBox Input { get; }
    private readonly double inset, padding;
    // RichTextBoxはPagePaddingを0にしても、本文の左に5 DIPの余白を持つ。
    private const double InputTextLeftInset = 5;
    private readonly Size bounds;
    private double zoom;
    private readonly Popup palette;
    private readonly Popup formatPopup;
    private readonly Canvas handleLayer;
    private readonly TextBlock shortcutHint;
    private readonly Button formatHandle;
    private readonly Thumb[] moveEdges;
    private readonly Thumb[] resizeGrips;
    private readonly ComboBox fontPicker;
    private readonly TextBox sizePicker;
    private readonly ToggleButton boldPicker;
    private readonly Button colorPicker;
    private readonly Border colorSwatch;
    private readonly EditorColorPalette colorPalette;

    private readonly Action? remove;
    private readonly Dictionary<string, string> fontIds = new(StringComparer.OrdinalIgnoreCase);
    private bool syncing;
    private bool applyingFormat;
    private bool fittingHeight;
    private bool formattingRequested;
    private Window? owner;
    private Point lastScreenPoint;
    private Size lastEditorSize;
    internal event Action? AppearanceChanged;
    internal event Action? SaveRequested;
    internal event Action<int>? ZoomRequested;
    internal Color Ink { get; private set; }
    internal string FontId { get; private set; } = "MS Gothic";
    internal bool PaletteOpenForTest => palette.IsOpen;
    internal bool CanDeleteForTest => remove != null;
    internal bool MoveEdgesUseSizeAllForTest => moveEdges.Length == 4 && moveEdges.All(edge => edge.Cursor == Cursors.SizeAll);
    internal Size FormatHandleSizeForTest => new(formatHandle.Width, formatHandle.Height);
    internal Size ResizeHandleScreenSizeForTest => new(resizeGrips[4].Width, resizeGrips[4].Height);
    internal bool ResizeHandleOutsideInputForTest => !Input.IsAncestorOf(resizeGrips[4]);
    internal FrameworkElement ResizeHandleForTest => resizeGrips[4];
    internal IReadOnlyList<Thumb> ResizeHandlesForTest => resizeGrips;
    internal IReadOnlyList<Thumb> MoveEdgesForTest => moveEdges;
    internal int ColorChoiceCountForTest => colorPalette.ChoiceCount;
    internal bool ColorPaletteGridForTest => colorPalette.Columns == 4 && colorPalette.SwatchesOnly;
    internal FrameworkElement ColorPaletteVisualForTest => colorPalette.Visual;
    internal void OpenColorPaletteForTest() => colorPalette.OpenForTest();
    internal Size ColorSwatchSizeForTest => new(colorSwatch.Width, colorSwatch.Height);
    internal object? ColorPickerLabelForTest => colorPicker.Content;
    internal void MoveForTest(double x, double y) => MoveTo(x, y);
    internal void SetBoxFromDrag(Point start, Point end)
    {
        Rect box = new(start, end);
        double minWidth = Math.Min(bounds.Width, Math.Max(70 / zoom, Input.FontSize * 3));
        double margin = inset + padding;
        Canvas.SetLeft(this, box.Left);
        Canvas.SetTop(this, box.Top);
        Width = Math.Min(bounds.Width - box.Left, Math.Max(minWidth, box.Width));
        double maxHeight = Math.Max(Input.MinHeight, Math.Min(Input.MaxHeight, bounds.Height - box.Top - 2 * margin));
        Input.Height = Math.Clamp(box.Height - 2 * margin, Input.MinHeight, maxHeight);
        RepositionPalette();
    }
    internal bool ObjectSelected { get; private set; }
    internal void DeleteSelected() { if (ObjectSelected) remove?.Invoke(); }
    internal void DeleteForTest() { ObjectSelected = true; DeleteSelected(); }
    internal Point TextPosition => new(Canvas.GetLeft(this) + inset + padding + InputTextLeftInset, Canvas.GetTop(this) + inset + padding);

    internal InlineTextEditor(Point position, string text, double fontSize, Color ink, double zoom, Size bounds, Action accept, Action cancel, Action? remove = null, TextSegment[]? segments = null, double? boxWidth = null, double? boxHeight = null)
    {
        this.bounds = bounds; this.zoom = zoom;
        this.remove = remove; ObjectSelected = remove != null; Focusable = true;
        inset = 1 / zoom; padding = 4 / zoom;
        BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246)); BorderThickness = new Thickness(inset);
        CornerRadius = new CornerRadius(4 / zoom); Padding = new Thickness(padding); Background = Brushes.Transparent;
        Ink = ink;
        Input = new RichTextBox { AcceptsReturn = true, FontSize = fontSize, Foreground = new SolidColorBrush(ink),
            FontFamily = new FontFamily("MS Gothic"), BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Background = Brushes.Transparent, MinHeight = fontSize * 1.3, Height = Math.Max(fontSize * 1.6, 24 / zoom),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(fontSize * 2, bounds.Height - 2 * (inset + padding)) };
        Input.Document.PagePadding = new Thickness(0);
        LoadText(text, segments);
        formatHandle = new Button { Content = "A ▾", Width = 43, Height = 30, FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, MinHeight = 0,
            Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(2),
            ToolTip = "書式：選択した文字のフォント・大きさ・色を変更" };
        ToolTipService.SetInitialShowDelay(formatHandle, 200);
        handleLayer = new Canvas { Margin = new Thickness(-(inset + padding)), ClipToBounds = false };
        moveEdges = Enumerable.Range(0, 4).Select(_ => CreateMoveEdge()).ToArray();
        foreach (Thumb edge in moveEdges)
        {
            edge.DragDelta += (_, e) => { MoveTo(Canvas.GetLeft(this) + e.HorizontalChange / this.zoom, Canvas.GetTop(this) + e.VerticalChange / this.zoom); e.Handled = true; };
            handleLayer.Children.Add(edge);
        }
        resizeGrips = Enumerable.Range(0, 8).Select(CreateResizeGrip).ToArray();
        foreach (Thumb grip in resizeGrips) handleLayer.Children.Add(grip);
        handleLayer.SizeChanged += (_, _) => PositionHandles();
        var content = new Grid { ClipToBounds = false };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shortcutHint = new TextBlock { Text = remove == null ? "Ctrl+Enterで確定" : "Ctrl+Enterで確定  ·  Deleteで削除（枠選択時）",
            FontSize = 10 / zoom, Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)), Margin = new Thickness(0, 3 / zoom, 0, 0), IsHitTestVisible = false };
        Grid.SetRow(shortcutHint, 1); Grid.SetRowSpan(handleLayer, 2);
        content.Children.Add(Input); content.Children.Add(shortcutHint); content.Children.Add(handleLayer); Child = content;
        formatPopup = new Popup { Child = formatHandle, PlacementTarget = this, Placement = PlacementMode.Bottom,
            VerticalOffset = 11, AllowsTransparency = true, StaysOpen = true };

        var root = new StackPanel();
        var formats = new StackPanel { Orientation = Orientation.Horizontal }; root.Children.Add(formats);
        fontPicker = new ComboBox { ItemsSource = EditorFonts.Choices, SelectedIndex = 0, Width = 190, Height = 30, Margin = new Thickness(0, 0, 7, 0), ToolTip = "フォント" };
        sizePicker = new TextBox { Width = 54, Height = 30, Padding = new Thickness(5, 2, 5, 2), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "文字サイズ", Margin = new Thickness(0, 0, 7, 0) };
        boldPicker = new ToggleButton { Content = "B", FontWeight = FontWeights.Bold, Width = 32, Height = 30, ToolTip = "太字", Margin = new Thickness(0, 0, 7, 0) };
        colorSwatch = new Border { Width = 20, Height = 20, Background = new SolidColorBrush(ink),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
        colorPicker = ActionButton("", "文字の色"); colorPicker.Content = colorSwatch; colorPicker.MinWidth = 44; colorPicker.Height = 34;
        formats.Children.Add(fontPicker); formats.Children.Add(FontSizeControls.Wrap(sizePicker)); formats.Children.Add(boldPicker); formats.Children.Add(colorPicker);
        colorPalette = new EditorColorPalette(colorPicker, new (string, Color)[] { ("黒", Colors.Black), ("赤", Colors.Red),
            ("青", Colors.RoyalBlue), ("緑", Colors.ForestGreen), ("橙", Colors.Orange), ("白", Colors.White),
            ("濃い灰", Colors.DimGray), ("灰", Colors.Gray), ("濃い赤", Colors.DarkRed),
            ("黄", Colors.Gold), ("黄緑", Colors.YellowGreen), ("水色", Colors.DeepSkyBlue),
            ("紺", Colors.Navy), ("紫", Colors.MediumPurple), ("桃", Colors.HotPink),
            ("青緑", Color.FromRgb(20, 80, 106)) }, color => { SetColor(color); AppearanceChanged?.Invoke(); });
        var card = new Border { Child = root, Padding = new Thickness(12), Margin = new Thickness(8), Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = .18, Color = Colors.Black } };
        card.SetValue(TextElement.FontFamilyProperty, new FontFamily("Yu Gothic UI")); card.SetValue(TextElement.FontSizeProperty, 12.0);
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Width = Math.Min(bounds.Width, boxWidth ?? Math.Max(card.DesiredSize.Width / zoom, fontSize * 12));
        if (boxHeight.HasValue) Input.Height = Math.Clamp(boxHeight.Value, Input.FontSize * 1.3, Input.MaxHeight);
        palette = new Popup { Child = card, PlacementTarget = formatHandle, Placement = PlacementMode.Bottom, VerticalOffset = 3, AllowsTransparency = true, StaysOpen = true };
        formatHandle.Click += (_, _) => SetPaletteOpen(!formattingRequested);
        Input.GotKeyboardFocus += (_, _) => { ObjectSelected = false; SetPaletteOpen(false); };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (remove != null && e.OriginalSource is DependencyObject source && !IsInputSource(source))
            { ObjectSelected = true; Focus(); }
        };
        Input.TextChanged += (_, _) => { if (!applyingFormat) SetPaletteOpen(false); QueueFitHeight(); };
        Input.Loaded += (_, _) => QueueFitHeight();
        card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { cancel(); e.Handled = true; }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Enter) { accept(); e.Handled = true; }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.S) { SaveRequested?.Invoke(); e.Handled = true; }
        };
        card.PreviewMouseWheel += (_, e) => { if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { ZoomRequested?.Invoke(e.Delta); e.Handled = true; } };
        fontPicker.SelectionChanged += (_, _) => ChangeFontFamily(); sizePicker.TextChanged += (_, _) => ChangeSize();
        boldPicker.Checked += (_, _) => ChangeBold(); boldPicker.Unchecked += (_, _) => ChangeBold();
        SetAppearance(fontSize, ink); SetFont("MS Gothic", false);
        if (segments is { Length: > 0 }) LoadText(text, segments);
        double desiredLeft = position.X - inset - padding - InputTextLeftInset;
        double availableAtPosition = bounds.Width - Math.Max(0, desiredLeft);
        if (availableAtPosition >= Math.Min(120 / zoom, bounds.Width)) Width = Math.Min(Width, availableAtPosition);
        MoveTo(desiredLeft, position.Y - inset - padding);
        Loaded += (_, _) => { owner = Window.GetWindow(this); if (owner != null) { owner.Deactivated += HidePalette; owner.Activated += ShowPalette; } formatPopup.IsOpen = true; if (ObjectSelected) Focus(); else { Input.Focus(); Input.CaretPosition = Input.Document.ContentEnd; } };
        Unloaded += (_, _) => { ClosePalette(); if (owner != null) { owner.Deactivated -= HidePalette; owner.Activated -= ShowPalette; } owner = null; };
        LayoutUpdated += (_, _) => RepositionPalette();
    }
    private static Button ActionButton(string label, string tip) => new() { Content = label, ToolTip = tip, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(3, 0, 0, 0), MinHeight = 28, FontSize = 12, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)) };
    private bool IsInputSource(DependencyObject source) => ReferenceEquals(source, Input) || source is TextElement or FlowDocument
        || source is Visual && Input.IsAncestorOf(source);
    internal bool IsInputSourceForTest(DependencyObject source) => IsInputSource(source);
    private void SetPaletteOpen(bool open)
    {
        formattingRequested = open; palette.IsOpen = open;
        formatHandle.Background = open ? new SolidColorBrush(Color.FromRgb(219, 234, 254)) : Brushes.White;
        if (open) RepositionPalette();
    }
    internal void OpenFormattingForTest() => SetPaletteOpen(true);
    private void HidePalette(object? sender, EventArgs e) { palette.IsOpen = false; formatPopup.IsOpen = false; colorPalette.Close(); }
    private void ShowPalette(object? sender, EventArgs e) { if (!IsLoaded) return; formatPopup.IsOpen = true; if (formattingRequested) palette.IsOpen = true; }
    internal void ClosePalette() { SetPaletteOpen(false); formatPopup.IsOpen = false; colorPalette.Close(); }
    internal void UpdateZoom(double value)
    {
        zoom = value; lastEditorSize = default;
        shortcutHint.FontSize = 10 / zoom; shortcutHint.Margin = new Thickness(0, 3 / zoom, 0, 0);
        foreach (Thumb grip in resizeGrips) grip.Template = ResizeGripTemplate();
        PositionHandles(); RepositionPalette();
    }
    private static Thumb CreateMoveEdge()
    {
        var edge = new Thumb { Cursor = Cursors.SizeAll, ToolTip = "枠をドラッグして文字を移動" };
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        edge.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = chrome };
        return edge;
    }
    private Thumb CreateResizeGrip(int index)
    {
        Cursor[] cursors = [Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE];
        var grip = new Thumb { Cursor = cursors[index], ToolTip = "ドラッグして文字枠の大きさを変更",
            Width = 14 / zoom, Height = 14 / zoom, Template = ResizeGripTemplate() };
        grip.DragDelta += (_, e) => { ResizeFromGrip(index, e.HorizontalChange / zoom, e.VerticalChange / zoom); e.Handled = true; };
        return grip;
    }
    private ControlTemplate ResizeGripTemplate()
    {
        var circle = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        circle.SetValue(System.Windows.Shapes.Shape.FillProperty, Brushes.White);
        circle.SetValue(System.Windows.Shapes.Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(20, 80, 106)));
        circle.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.4 / zoom);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = circle };
    }
    private void PositionHandles()
    {
        double width = handleLayer.ActualWidth, height = handleLayer.ActualHeight;
        double edge = 8 / zoom, diameter = 14 / zoom;
        foreach (Thumb grip in resizeGrips) grip.Width = grip.Height = diameter;
        if (width <= 0 || height <= 0) return;
        for (int i = 0; i < moveEdges.Length; i++)
        {
            var strip = moveEdges[i];
            strip.Width = i % 2 == 0 ? width : edge;
            strip.Height = i % 2 == 0 ? edge : height;
            Canvas.SetLeft(strip, i == 1 ? width - edge : 0);
            Canvas.SetTop(strip, i == 2 ? height - edge : 0);
        }
        Point[] centers = [new(0, 0), new(width / 2, 0), new(width, 0), new(width, height / 2),
            new(width, height), new(width / 2, height), new(0, height), new(0, height / 2)];
        for (int i = 0; i < resizeGrips.Length; i++)
        {
            resizeGrips[i].Width = resizeGrips[i].Height = diameter;
            Canvas.SetLeft(resizeGrips[i], centers[i].X - diameter / 2);
            Canvas.SetTop(resizeGrips[i], centers[i].Y - diameter / 2);
        }
    }
    private void ResizeFromGrip(int index, double dx, double dy)
    {
        double left = Canvas.GetLeft(this), top = Canvas.GetTop(this), margin = inset + padding;
        double minWidth = Math.Min(bounds.Width, Math.Max(70 / zoom, Input.FontSize * 3));
        double minHeight = Input.FontSize * 1.3;
        if (index is 0 or 6 or 7)
        {
            double right = left + Width;
            double minLeft = -margin - InputTextLeftInset;
            double next = Math.Clamp(left + dx, minLeft, Math.Max(minLeft, right - minWidth));
            Width = right - next; Canvas.SetLeft(this, next);
        }
        else if (index is 2 or 3 or 4)
        {
            double available = Math.Max(1, bounds.Width - Math.Max(0, left));
            Width = Math.Clamp(Width + dx, Math.Min(minWidth, available), available);
        }
        if (index is 0 or 1 or 2)
        {
            double bottom = top + Input.Height + 2 * margin;
            double minTop = Math.Max(-margin, bottom - Input.MaxHeight - 2 * margin);
            double next = Math.Clamp(top + dy, minTop, Math.Max(minTop, bottom - minHeight - 2 * margin));
            Input.Height = bottom - next - 2 * margin; Canvas.SetTop(this, next);
        }
        else if (index is 4 or 5 or 6)
        {
            double available = Math.Max(minHeight, bounds.Height - Math.Max(0, top) - 2 * margin);
            Input.Height = Math.Clamp(Input.Height + dy, minHeight, Math.Min(Input.MaxHeight, available));
        }
        RepositionPalette();
    }
    private void RepositionPalette()
    {
        if (!IsLoaded || (!palette.IsOpen && !formatPopup.IsOpen)) return;
        Point now = PointToScreen(new Point());
        Size size = new(ActualWidth, ActualHeight);
        if ((now - lastScreenPoint).Length < .1 && Math.Abs(size.Width - lastEditorSize.Width) < .1 && Math.Abs(size.Height - lastEditorSize.Height) < .1) return;
        lastScreenPoint = now; lastEditorSize = size;
        if (formatPopup.IsOpen) { formatPopup.HorizontalOffset += .01; formatPopup.HorizontalOffset -= .01; }
        if (palette.IsOpen) { palette.HorizontalOffset += .01; palette.HorizontalOffset -= .01; }
    }
    private void MoveTo(double x, double y)
    {
        // 空いている枠幅ではなく、実際の文字幅で右端を制限する。
        double textWidth = EditDrawing.TextShapes(Mark with { Start = new Point(), End = new Point() })
            .Select(shape => shape.Geometry.Bounds.IsEmpty ? 0 : shape.Geometry.Bounds.Right).DefaultIfEmpty(0).Max();
        double minimumWidth = Math.Min(Width, Math.Max(70 / zoom, textWidth + 2 * (inset + padding) + 4 / zoom));
        double left = Math.Clamp(x, -inset - padding - InputTextLeftInset, Math.Max(0, bounds.Width - minimumWidth));
        Width = Math.Min(Width, bounds.Width - Math.Max(0, left));
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, Math.Clamp(y, -inset - padding, Math.Max(0, bounds.Height - Input.Height - 2 * (inset + padding)))); RepositionPalette();
    }
    private void ChangeFontFamily()
    {
        if (syncing || fontPicker.SelectedItem is not EditorFont selected) return;
        FontId = selected.Id; Input.FontFamily = EditorFonts.Family(FontId); fontIds[Input.FontFamily.Source] = FontId;
        ApplyFormat(TextElement.FontFamilyProperty, Input.FontFamily); AppearanceChanged?.Invoke();
    }
    private void ChangeSize()
    {
        if (syncing || !double.TryParse(sizePicker.Text, out double size) || !double.IsFinite(size) || size <= 0 || size > 1000) return;
        Input.FontSize = size; ApplyFormat(TextElement.FontSizeProperty, size); AppearanceChanged?.Invoke();
    }
    private void ChangeBold()
    {
        if (syncing) return;
        Input.FontWeight = boldPicker.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        ApplyFormat(TextElement.FontWeightProperty, Input.FontWeight); AppearanceChanged?.Invoke();
    }
    private void SetColor(Color color)
    {
        Ink = color; ApplyFormat(TextElement.ForegroundProperty, new SolidColorBrush(color));
        colorSwatch.Background = new SolidColorBrush(color);
    }
    private void ApplyFormat(DependencyProperty property, object value)
    {
        applyingFormat = true;
        try
        {
            bool whole = Input.Selection.IsEmpty;
            TextPointer caret = Input.CaretPosition;
            if (whole) Input.SelectAll();
            Input.Selection.ApplyPropertyValue(property, value);
            if (whole) Input.CaretPosition = caret;
        }
        finally { applyingFormat = false; }
        QueueFitHeight();
    }
    private void QueueFitHeight()
    {
        if (fittingHeight) return;
        fittingHeight = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            fittingHeight = false;
            if (!Input.IsLoaded) return;
            double needed = Input.ExtentHeight + 4 / zoom;
            if (needed > Input.Height + 1) Input.Height = Math.Min(Input.MaxHeight, needed);
        });
    }
    internal void SetAppearance(double fontSize, Color ink)
    {
        Input.FontSize = fontSize;
        ApplyFormat(TextElement.FontSizeProperty, fontSize);
        SetColor(ink);
        syncing = true; sizePicker.Text = fontSize.ToString(CultureInfo.CurrentCulture); syncing = false;
    }
    internal void SetFont(string id, bool bold)
    {
        SetDefaultFont(id, bold);
        ApplyFormat(TextElement.FontFamilyProperty, Input.FontFamily);
        ApplyFormat(TextElement.FontWeightProperty, Input.FontWeight);
    }
    internal void SetDefaultFont(string id, bool bold)
    {
        FontId = id; Input.FontFamily = EditorFonts.Family(id); Input.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        fontIds[Input.FontFamily.Source] = id;
        syncing = true; fontPicker.SelectedItem = EditorFonts.Choices.FirstOrDefault(item => item.Id == id) ?? EditorFonts.Choices[0]; boldPicker.IsChecked = bold; syncing = false;
    }
    internal void SelectFormattingForTest(string id, double size, bool bold) { fontPicker.SelectedItem = EditorFonts.Choices.First(item => item.Id == id); sizePicker.Text = size.ToString(CultureInfo.CurrentCulture); boldPicker.IsChecked = bold; }
    internal void SelectColorForTest(int index) => colorPalette.SelectForTest(index);
    private void LoadText(string text, TextSegment[]? segments)
    {
        Input.Document.Blocks.Clear();
        var current = new Paragraph { Margin = new Thickness(0) };
        Input.Document.Blocks.Add(current);
        foreach (var segment in segments is { Length: > 0 } ? segments : [new TextSegment(text, Ink, Input.FontSize, FontId, Input.FontWeight == FontWeights.Bold)])
        {
            string[] lines = segment.Text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) { current = new Paragraph { Margin = new Thickness(0) }; Input.Document.Blocks.Add(current); }
                if (lines[i].Length == 0) continue;
                FontFamily family = EditorFonts.Family(segment.FontId); fontIds[family.Source] = segment.FontId;
                current.Inlines.Add(new Run(lines[i]) { FontFamily = family, FontSize = segment.Size,
                    FontWeight = segment.Bold ? FontWeights.Bold : FontWeights.Normal, Foreground = new SolidColorBrush(segment.Color) });
            }
        }
    }
    internal string PlainText
    {
        get => string.Concat(ReadSegments().Select(segment => segment.Text));
        set { LoadText(value, null); Input.CaretPosition = Input.Document.ContentEnd; }
    }
    internal void SelectTextForTest(int start, int length)
    {
        Run run = Runs(((Paragraph)Input.Document.Blocks.FirstBlock!).Inlines).First();
        Input.Selection.Select(run.ContentStart.GetPositionAtOffset(start)!, run.ContentStart.GetPositionAtOffset(start + length)!);
    }
    private TextSegment[] ReadSegments()
    {
        var result = new List<TextSegment>();
        bool first = true;
        foreach (var paragraph in Input.Document.Blocks.OfType<Paragraph>())
        {
            if (!first) result.Add(new TextSegment("\n", Ink, Input.FontSize, FontId, Input.FontWeight == FontWeights.Bold));
            first = false;
            foreach (var run in Runs(paragraph.Inlines))
            {
                if (run.Text.Length == 0) continue;
                string id = fontIds.GetValueOrDefault(run.FontFamily.Source, FontId);
                Color color = (run.Foreground as SolidColorBrush)?.Color ?? Ink;
                result.Add(new TextSegment(run.Text, color, run.FontSize, id, run.FontWeight == FontWeights.Bold));
            }
        }
        return result.ToArray();
    }
    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            if (inline is Run run) yield return run;
            else if (inline is Span span) foreach (Run nested in Runs(span.Inlines)) yield return nested;
        }
    }
    internal EditMark Mark
    {
        get
        {
            TextSegment[] segments = ReadSegments();
            return new("text", TextPosition, TextPosition, string.Concat(segments.Select(s => s.Text)), Ink, Input.FontSize, FontId,
                Input.FontWeight == FontWeights.Bold, TextSegments: segments, TextBoxWidth: Width, TextBoxHeight: Input.Height);
        }
    }
}
