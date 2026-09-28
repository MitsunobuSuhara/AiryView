using System.Globalization;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Effects;
using ShapePath = System.Windows.Shapes.Path;
using Microsoft.Win32;

namespace AiryView;

internal sealed class ToolIconConverter : IValueConverter
{
    internal static Geometry For(string name) => name switch
    {
        "文字" => Geometry.Parse("M 2,4 L 19,4 M 10,4 L 10,17"),
        "矢印" => Geometry.Parse("M 2,17 L 17,3 M 9,3 L 17,3 17,11"),
        "線" => Geometry.Parse("M 2,17 L 18,3"),
        "四角形" => Geometry.Parse("M 3,4 L 17,4 17,16 3,16 Z"),
        "円・楕円" => new EllipseGeometry(new Rect(2, 5, 16, 10)),
        "ハイライト" => Geometry.Parse("M 3,4 L 17,4 17,16 3,16 Z M 5,8 L 15,8 M 5,12 L 15,12"),
        "範囲指定" => Geometry.Parse("M 3,3 L 17,3 17,17 3,17 Z M 1,1 L 5,1 M 15,19 L 19,19"),
        "フリーハンド" => Geometry.Parse("M 2,14 C 5,4 7,18 10,9 C 13,1 15,16 18,5"),
        _ => Geometry.Empty
    };
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => For(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

internal sealed class EditSurface : Canvas
{
    internal int EditingMarkIndex = -1;
    internal EditFrame? Frame;
    internal BitmapSource? BackgroundImage;
    internal EditMark? PendingMark;
    internal Rect? CropRect;
    internal string? CropSizeText;
    internal double CropHandleUnit = 1;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Frame == null) return;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
        EditDrawing.Draw(dc, Frame.Image ?? BackgroundImage, Width, Height, Frame.Marks.Where((_, index) => index != EditingMarkIndex));
        if (PendingMark != null) EditDrawing.DrawMark(dc, PendingMark);
        if (CropRect is { } crop)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 0, 100, 255)), new Pen(Brushes.DodgerBlue, 2 * CropHandleUnit) { DashStyle = DashStyles.Dash }, crop);
            foreach (Point corner in new[] { crop.TopLeft, crop.TopRight, crop.BottomRight, crop.BottomLeft })
                dc.DrawRectangle(Brushes.White, new Pen(Brushes.DodgerBlue, CropHandleUnit), new Rect(corner.X - 5 * CropHandleUnit, corner.Y - 5 * CropHandleUnit, 10 * CropHandleUnit, 10 * CropHandleUnit));
            if (!string.IsNullOrEmpty(CropSizeText))
            {
                var label = new FormattedText(CropSizeText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Yu Gothic UI"), 12 * CropHandleUnit, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double x = Math.Clamp(crop.Left, 0, Math.Max(0, Width - label.Width - 12 * CropHandleUnit));
                double y = Math.Clamp(crop.Top + 7 * CropHandleUnit, 0, Math.Max(0, Height - label.Height - 8 * CropHandleUnit));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 29, 41, 57)), null,
                    new Rect(x, y, label.Width + 12 * CropHandleUnit, label.Height + 8 * CropHandleUnit), 5 * CropHandleUnit, 5 * CropHandleUnit);
                dc.DrawText(label, new Point(x + 6 * CropHandleUnit, y + 4 * CropHandleUnit));
            }
        }
    }
}

// 閲覧中は生成しない。PDFも編集中の1ページだけをプレビューに保持する。
internal sealed class VisualEditorWindow : Window
{
    internal static int CreatedCount { get; private set; }
    private readonly PdfDocument? pdf;
    private readonly string source;
    private readonly Func<string, Task> open;
    private readonly Func<string, bool> isOpen;
    private readonly Dictionary<int, VisualEditModel> pages = [];
    private VisualEditModel? model;
    private int page;
    private bool busy, sizing, syncingFormat;
    private double zoom = 1;
    private Point? drag;
    private bool cropMode;
    private Rect? cropSelection;
    private Rect cropAtDragStart;
    private int cropHandle;
    private InlineTextEditor? textEditor;
    private InlineShapeEditor? shapeEditor;
    private readonly List<Point> freehandPoints = [];
    private readonly Dictionary<int, IReadOnlyList<PdfTextCharacter>> highlightCharacters = new();
    private string ToolName => tool.SelectedItem as string == "ハイライト" ? highlightMethod.SelectedItem as string ?? "範囲指定" : tool.SelectedItem as string ?? "文字";
    private bool IsCropTool => cropMode;
    private bool IsRangeHighlightTool => ToolName == "範囲指定";
    private bool IsFreehandTool => ToolName == "フリーハンド";
    private int editingIndex = -1;
    private readonly StackPanel controls = new();
    private readonly EditSurface surface = new() { Focusable = true, ClipToBounds = true };
    private readonly ScrollViewer viewer = new() { Background = new SolidColorBrush(Color.FromRgb(232, 237, 244)), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox tool = new() { Width = 142, Margin = new Thickness(4), SelectedIndex = 0, ToolTip = "文字：用紙をクリックして入力\n矢印・線・四角形・円／楕円：ドラッグして配置\nハイライト：選択後に範囲指定またはフリーハンドを選択\n配置した文字・図形はクリックして再編集" };
    private readonly ComboBox highlightMethod = new() { Width = 124, Margin = new Thickness(4), Visibility = Visibility.Collapsed, ToolTip = "ハイライトの方法\n範囲指定：PDFの文字には自動でフィット、画像では四角い範囲\nフリーハンド：描いた軌跡に沿う" };
    private readonly ComboBox color = new() { Width = 80, Margin = new Thickness(4), SelectedIndex = 1 };
    private readonly ComboBox font = new() { Width = 190, Margin = new Thickness(4), ToolTip = "文字のフォント" };
    private readonly CheckBox bold = new() { Content = "太字", Margin = new Thickness(5), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox fontSize = Field("24"), stroke = Field("3"), width = Field("1"), height = Field("1");
    private readonly CheckBox aspect = new() { Content = "縦横比を保つ", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6) };
    private readonly TextBlock status = new() { Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock centimeters = new() { Margin = new Thickness(10, 0, 10, 5), Foreground = Brushes.DimGray, FontSize = 12 };
    private readonly TextBlock pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
    private Button undo = null!, redo = null!, saveButton = null!;
    private Button cropButton = null!;
    private Popup? cropPopup;
    private TextBlock? cropPopupSize;
    private static TextBox Field(string value) => new() { Text = value, Width = 60, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
    private static DataTemplate ToolTemplate(double width)
    {
        var row = new FrameworkElementFactory(typeof(DockPanel)); row.SetValue(FrameworkElement.WidthProperty, width);
        var icon = new FrameworkElementFactory(typeof(ShapePath));
        icon.SetValue(DockPanel.DockProperty, Dock.Right); icon.SetValue(FrameworkElement.WidthProperty, 20.0); icon.SetValue(FrameworkElement.HeightProperty, 20.0);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 0, 0, 0)); icon.SetValue(ShapePath.StrokeProperty, Brushes.SlateGray);
        icon.SetValue(ShapePath.StrokeThicknessProperty, 1.7); icon.SetValue(ShapePath.StretchProperty, Stretch.None);
        icon.SetBinding(ShapePath.DataProperty, new Binding { Converter = new ToolIconConverter() }); row.AppendChild(icon);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetBinding(TextBlock.TextProperty, new Binding()); row.AppendChild(label);
        return new DataTemplate { VisualTree = row };
    }
    private static void Label(Panel panel, string value) => panel.Children.Add(new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) });
    private Button Button(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(3) };
        button.Click += (_, _) => { try { CommitEdits(); action(); } catch (Exception ex) { ShowError(ex); } };
        panel.Children.Add(button); return button;
    }
    private Button AsyncButton(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(3) };
        button.Click += async (_, _) => { try { CommitEdits(); await action(); } catch (Exception ex) { ShowError(ex); } };
        panel.Children.Add(button); return button;
    }
    private VisualEditorWindow(string source, bool isPdf, Func<string, Task> open, Func<string, bool> isOpen)
    {
        CreatedCount++; this.source = source; this.open = open; this.isOpen = isOpen;
        Title = isPdf ? "PDFに書き込み — AiryView" : "画像編集 — AiryView";
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/icon.ico"));
        Width = 1100; Height = 820; MinWidth = 740; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 14; Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));
        var root = new DockPanel(); Content = root;
        controls.Background = new SolidColorBrush(Color.FromRgb(250, 251, 253));
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        viewer.Content = new Border { Child = surface, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(viewer);
        viewer.PreviewMouseWheel += (_, e) =>
        {
            if (HandleEditorWheel(e.Delta, (Keyboard.Modifiers & ModifierKeys.Control) != 0, e.GetPosition(viewer))) e.Handled = true;
        };
        var commands = new WrapPanel { Margin = new Thickness(5) }; controls.Children.Add(commands);
        saveButton = AsyncButton(commands, "別名で保存…", SaveAsync);
        saveButton.MinWidth = 154;
        saveButton.Padding = new Thickness(14, 5, 14, 5);
        saveButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        undo = Button(commands, "元に戻す", () => { model?.Undo(); UpdateSurface(); });
        redo = Button(commands, "やり直す", () => { model?.Redo(); UpdateSurface(); });
        Button(commands, "－", () => SetZoom(zoom / 1.25)).ToolTip = "縮小 / Ctrl＋マウスホイール下";
        Button(commands, "＋", () => SetZoom(zoom * 1.25)).ToolTip = "拡大 / Ctrl＋マウスホイール上";
        Button(commands, "全体表示", Fit);
        if (isPdf)
        {
            AsyncButton(commands, "前のページ", () => LoadPage(page - 1)); commands.Children.Add(pageLabel);
            AsyncButton(commands, "次のページ", () => LoadPage(page + 1));
        }
        var writing = new WrapPanel { Margin = new Thickness(5) }; controls.Children.Add(writing);
        tool.ItemTemplate = ToolTemplate(105); highlightMethod.ItemTemplate = ToolTemplate(87);
        foreach (string name in new[] { "文字", "矢印", "線", "四角形", "円・楕円", "ハイライト" }) tool.Items.Add(name);
        foreach (string name in new[] { "範囲指定", "フリーハンド" }) highlightMethod.Items.Add(name);
        highlightMethod.SelectedIndex = 0;
        writing.Children.Add(tool); writing.Children.Add(highlightMethod); Label(writing, "文字は用紙をクリックして入力");
        font.ItemsSource = EditorFonts.Choices; font.SelectedIndex = 0;
        writing.Children.Add(font); writing.Children.Add(bold);
        foreach (string name in new[] { "赤", "黒", "青", "緑", "橙", "白", "黄", "ピンク", "水色", "紫" }) color.Items.Add(name);
        writing.Children.Add(color); Label(writing, "文字サイズ"); writing.Children.Add(FontSizeControls.Wrap(fontSize)); Label(writing, "線の太さ"); writing.Children.Add(FontSizeControls.Wrap(stroke, stroke: true));
        Label(writing, isPdf ? "pt" : "px");
        if (!isPdf)
        {
            var dimensions = new WrapPanel { Margin = new Thickness(5) }; controls.Children.Add(dimensions);
            cropButton = Button(dimensions, "クリップ", StartCrop);
            UpdateCropButtons();
            Button(dimensions, "左に90°", () => { model?.Rotate(-1); UpdateSurface(); Fit(); });
            Button(dimensions, "右に90°", () => { model?.Rotate(1); UpdateSurface(); Fit(); });
            Label(dimensions, "幅"); dimensions.Children.Add(width); Label(dimensions, "高さ"); dimensions.Children.Add(height);
            Label(dimensions, "px"); dimensions.Children.Add(aspect);
            Button(dimensions, "サイズ変更", () => { if (model == null) return; model.Resize(int.Parse(width.Text), int.Parse(height.Text)); UpdateSurface(); Fit(); });
            controls.Children.Add(centimeters);
            width.TextChanged += (_, _) => { KeepAspect(true); UpdateCentimeters(); };
            height.TextChanged += (_, _) => { KeepAspect(false); UpdateCentimeters(); };
        }
        else { fontSize.Text = "14"; stroke.Text = "2"; }
        status.Text = "文字は用紙をクリックして入力。矢印・線・図形・ハイライトはドラッグ。画像のクリップは範囲を調整し、近くの操作ウィンドウで確定します。別名で保存します。";
        surface.MouseLeftButtonDown += PointerDown; surface.MouseMove += PointerMove; surface.MouseLeftButtonUp += PointerUp;
        surface.LostMouseCapture += (_, _) => CancelDrag();
        Deactivated += (_, _) => { if (cropPopup != null) cropPopup.IsOpen = false; };
        Activated += (_, _) => UpdateCropPopup();
        tool.SelectionChanged += (_, _) => { CommitEdits(); CancelCrop(); highlightMethod.Visibility = tool.SelectedItem as string == "ハイライト" ? Visibility.Visible : Visibility.Collapsed; if (IsRangeHighlightTool || IsFreehandTool) color.SelectedIndex = 6; else if (ToolName == "文字") color.SelectedIndex = 1; if (IsFreehandTool) stroke.Text = "14"; };
        highlightMethod.SelectionChanged += (_, _) => { CommitEdits(); CancelDrag(); if (IsFreehandTool) stroke.Text = "14"; };
        fontSize.TextChanged += (_, _) => UpdateEditAppearance(); stroke.TextChanged += (_, _) => UpdateEditAppearance(); color.SelectionChanged += (_, _) => UpdateEditAppearance();
        font.SelectionChanged += (_, _) => UpdateEditAppearance(); bold.Checked += (_, _) => UpdateEditAppearance(); bold.Unchecked += (_, _) => UpdateEditAppearance();
        PreviewKeyDown += async (_, e) =>
        {
            if (busy) return;
            if (e.Key == Key.Delete && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase
                && DeleteSelectedObject()) { e.Handled = true; return; }
            if ((textEditor != null || shapeEditor != null) && e.Key == Key.Escape) { CancelEdits(); e.Handled = true; return; }
            if ((textEditor != null || shapeEditor != null) && e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { CommitEdits(); surface.Focus(); e.Handled = true; return; }
            if (e.Key == Key.Escape) { if (cropMode) CancelCrop(); else CancelDrag(); e.Handled = true; }
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (e.Key == Key.S) { e.Handled = true; try { await SaveAsync(); } catch (Exception ex) { ShowError(ex); } }
            if (Keyboard.FocusedElement is TextBox) return;
            if (shapeEditor != null && e.Key == Key.Z) { CancelEdits(); e.Handled = true; return; }
            if (model != null && (e.Key is Key.Z or Key.Y)) { if (e.Key == Key.Z) model.Undo(); else model.Redo(); UpdateSurface(); e.Handled = true; }
        };
        Closing += (_, e) =>
        {
            if (busy) { e.Cancel = true; return; }
            try { CommitEdits(); } catch (Exception ex) { e.Cancel = true; ShowError(ex); return; }
            if (pages.Values.Any(m => m.Dirty) && !SoftConfirmDialog.AskDiscard(this)) e.Cancel = true;
            if (!e.Cancel && cropPopup != null) cropPopup.IsOpen = false;
        };
    }
    internal VisualEditorWindow(string path, BitmapSource image, int rotation, Func<string, Task> open, Func<string, bool> isOpen)
        : this(path, false, open, isOpen)
    {
        if (rotation % 360 != 0) { var rotated = new TransformedBitmap(image, new RotateTransform(rotation)); rotated.Freeze(); image = rotated; }
        model = new VisualEditModel(image, image.PixelWidth, image.PixelHeight); pages[0] = model;
        fontSize.Text = Math.Clamp(Math.Round(image.PixelWidth / 50.0), 14, 120).ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) => { UpdateSurface(); Fit(); };
    }
    internal VisualEditorWindow(PdfDocument pdf, int page, Func<string, Task> open, Func<string, bool> isOpen)
        : this(pdf.Path, true, open, isOpen)
    {
        this.pdf = pdf; this.page = page;
        Loaded += async (_, _) => await LoadPage(page);
    }
    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, "編集できませんでした", MessageBoxButton.OK, MessageBoxImage.Warning);
    internal InlineTextEditor? ActiveTextEditor => textEditor;
    internal InlineShapeEditor? ActiveShapeEditor => shapeEditor;
    internal bool DeleteSelectedObject()
    {
        if (textEditor?.ObjectSelected == true) { textEditor.DeleteSelected(); return true; }
        if (shapeEditor != null) { shapeEditor.DeleteSelected(); return true; }
        return false;
    }
    internal VisualEditModel? CurrentModelForTest => model;
    private bool IsTextEditorChild(DependencyObject item)
    {
        for (DependencyObject? current = item; current != null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, textEditor) || ReferenceEquals(current, shapeEditor)) return true;
        return false;
    }
    internal void BeginText(Point point)
    {
        if (model == null) return;
        CommitEdits(); editingIndex = -1;
        for (int i = model.Frame.Marks.Length - 1; i >= 0; i--)
        {
            var candidate = model.Frame.Marks[i]; if (candidate.Kind != "text") continue;
            Rect box = EditDrawing.Outline(candidate, model.Frame.Width, model.Frame.Height).Bounds;
            box.Union(new Rect(candidate.Start, new Size(1, candidate.Size * 1.3)));
            box.Inflate(6 / zoom, 6 / zoom);
            if (box.Contains(point)) { editingIndex = i; break; }
        }
        EditMark mark = editingIndex >= 0 ? model.Frame.Marks[editingIndex] : Mark(point, point);
        if (editingIndex < 0 && model.Frame.Marks.Length >= 500) throw new IOException("1ページの書き込みは500個までです。");
        fontSize.Text = mark.Size.ToString(CultureInfo.CurrentCulture);
        font.SelectedItem = EditorFonts.Choices.FirstOrDefault(item => item.Id == mark.FontId) ?? EditorFonts.Choices[0]; bold.IsChecked = mark.Bold;
        Color[] colors = [Colors.Red, Colors.Black, Colors.RoyalBlue, Colors.ForestGreen, Colors.Orange, Colors.White, Colors.Yellow, Colors.HotPink, Colors.DeepSkyBlue, Colors.MediumPurple];
        color.SelectedIndex = Math.Max(0, Array.IndexOf(colors, mark.Color));
        textEditor = new InlineTextEditor(mark.Start, mark.Text, mark.Size, mark.Color, zoom,
            new Size(model.Frame.Width, model.Frame.Height), CommitEdits, CancelEdits,
            editingIndex >= 0 ? () => { model.Replace(editingIndex, null); CancelEdits(); UpdateSurface(); } : null, mark.TextSegments);
        if (mark.TextSegments is { Length: > 0 }) textEditor.SetDefaultFont(mark.FontId, mark.Bold);
        else textEditor.SetFont(mark.FontId, mark.Bold);
        textEditor.AppearanceChanged += SyncInlineFormatting;
        textEditor.SaveRequested += async () => { try { await SaveAsync(); } catch (Exception ex) { ShowError(ex); } };
        textEditor.ZoomRequested += delta => HandleEditorWheel(delta, true, Mouse.GetPosition(viewer));
        surface.EditingMarkIndex = editingIndex; surface.Children.Add(textEditor); surface.InvalidateVisual();
        status.Text = "Enterで改行。文字を選んで左下のAアイコンから書体・サイズ・色を変更できます。左上の四方向アイコンで移動、右下のつまみで大きさを調整。選択中の枠はDeleteで削除。Ctrl+Enterで確定、Escで取消。";
    }
    private void SyncInlineFormatting()
    {
        if (textEditor == null && shapeEditor == null) return;
        syncingFormat = true;
        try
        {
            EditMark mark = textEditor?.Mark ?? shapeEditor!.Mark;
            if (textEditor != null)
            {
                font.SelectedItem = EditorFonts.Choices.First(item => item.Id == mark.FontId);
                fontSize.Text = mark.Size.ToString(CultureInfo.CurrentCulture); bold.IsChecked = mark.Bold;
            }
            else stroke.Text = mark.Size.ToString(CultureInfo.CurrentCulture);
            Color[] inks = [Colors.Red, Colors.Black, Colors.RoyalBlue, Colors.ForestGreen, Colors.Orange, Colors.White, Colors.Yellow, Colors.HotPink, Colors.DeepSkyBlue, Colors.MediumPurple];
            color.SelectedIndex = Math.Max(0, Array.IndexOf(inks, mark.Color));
        }
        finally { syncingFormat = false; }
    }
    private void UpdateEditAppearance()
    {
        if (syncingFormat) return;
        var ink = new[] { Colors.Red, Colors.Black, Colors.RoyalBlue, Colors.ForestGreen, Colors.Orange, Colors.White, Colors.Yellow, Colors.HotPink, Colors.DeepSkyBlue, Colors.MediumPurple }[Math.Max(0, color.SelectedIndex)];
        if (textEditor != null && double.TryParse(fontSize.Text, out double size) && double.IsFinite(size) && size > 0 && size <= 1000) textEditor.SetAppearance(size, ink);
        if (textEditor != null) textEditor.SetFont((font.SelectedItem as EditorFont)?.Id ?? "MS Gothic", bold.IsChecked == true);
        if (shapeEditor != null && double.TryParse(stroke.Text, out double weight) && double.IsFinite(weight) && weight > 0 && weight <= 1000) shapeEditor.SetAppearance(weight, ink);
    }
    internal void BeginShape(EditMark mark, int index = -1)
    {
        if (model == null) return;
        CommitEdits();
        if (index < 0 && model.Frame.Marks.Length >= 500) throw new IOException("1ページの書き込みは500個までです。");
        editingIndex = index; stroke.Text = mark.Size.ToString(CultureInfo.CurrentCulture);
        Color[] colors = [Colors.Red, Colors.Black, Colors.RoyalBlue, Colors.ForestGreen, Colors.Orange, Colors.White, Colors.Yellow, Colors.HotPink, Colors.DeepSkyBlue, Colors.MediumPurple];
        color.SelectedIndex = Math.Max(0, Array.IndexOf(colors, mark.Color));
        shapeEditor = new InlineShapeEditor(mark, zoom, new Size(model.Frame.Width, model.Frame.Height), CommitEdits, CancelEdits,
            () => { if (editingIndex >= 0) model.Replace(editingIndex, null); CancelEdits(); UpdateSurface(); }, pdf == null ? "px" : "pt");
        shapeEditor.AppearanceChanged += SyncInlineFormatting;
        shapeEditor.SaveRequested += async () => { try { await SaveAsync(); } catch (Exception ex) { ShowError(ex); } };
        shapeEditor.ZoomRequested += delta => HandleEditorWheel(delta, true, Mouse.GetPosition(viewer));
        surface.EditingMarkIndex = index; surface.Children.Add(shapeEditor); surface.InvalidateVisual();
        status.Text = mark.Kind.StartsWith("highlight", StringComparison.Ordinal)
            ? "角の四角で範囲、中央の四角で位置を調整。近くのパレットで色を変更できます。ハイライトを選んだ状態でクリックすると再編集できます。"
            : "両端の四角で長さ・向きを調整。中央の四角で移動。近くのパレットで色・太さを変更できます。確定後も矢印・線をクリックして修正できます。";
    }
    private int HitShape(Point point)
    {
        if (model == null) return -1;
        for (int i = model.Frame.Marks.Length - 1; i >= 0; i--)
        {
            EditMark mark = model.Frame.Marks[i]; if (mark.Kind == "text") continue;
            if (mark.Kind.StartsWith("highlight", StringComparison.Ordinal))
            {
                if ((IsRangeHighlightTool || IsFreehandTool) && EditDrawing.Outline(mark, model.Frame.Width, model.Frame.Height).FillContains(point)) return i;
                continue;
            }
            if (mark.Kind is "rectangle" or "ellipse")
            {
                if (EditDrawing.Outline(mark with { Size = Math.Max(mark.Size, 6 / zoom) }, model.Frame.Width, model.Frame.Height).FillContains(point)) return i;
                continue;
            }
            Vector line = mark.End - mark.Start;
            double t = line.LengthSquared < .0001 ? 0 : Math.Clamp(Vector.Multiply(point - mark.Start, line) / line.LengthSquared, 0, 1);
            if ((point - (mark.Start + line * t)).Length <= Math.Max(6 / zoom, mark.Size * 2)) return i;
        }
        return -1;
    }
    internal void CommitEdits()
    {
        if (model == null) return;
        if (shapeEditor != null)
        {
            if (editingIndex >= 0) model.Replace(editingIndex, shapeEditor.Mark); else model.Add(shapeEditor.Mark);
            CancelEdits(); UpdateSurface(); return;
        }
        if (textEditor == null) return;
        var mark = textEditor.Mark;
        if (editingIndex >= 0) model.Replace(editingIndex, string.IsNullOrWhiteSpace(mark.Text) ? null : mark);
        else if (!string.IsNullOrWhiteSpace(mark.Text)) model.Add(mark);
        CancelEdits(); UpdateSurface();
    }
    internal void CancelEdits()
    {
        if (textEditor != null) { textEditor.ClosePalette(); surface.Children.Remove(textEditor); }
        if (shapeEditor != null) { shapeEditor.ClosePalette(); surface.Children.Remove(shapeEditor); }
        textEditor = null; shapeEditor = null; editingIndex = -1; surface.EditingMarkIndex = -1; surface.InvalidateVisual();
    }
    private void SetBusy(bool value) { busy = value; controls.IsEnabled = !value; surface.IsEnabled = !value; }
    private void UpdateSurface()
    {
        if (model == null) return;
        surface.Frame = model.Frame; surface.Width = model.Frame.Width; surface.Height = model.Frame.Height;
        surface.InvalidateVisual(); undo.IsEnabled = model.CanUndo; redo.IsEnabled = model.CanRedo;
        sizing = true; width.Text = Math.Round(model.Frame.Width).ToString(); height.Text = Math.Round(model.Frame.Height).ToString(); sizing = false;
        UpdateCentimeters();
        pageLabel.Text = pdf == null ? "" : $"{page + 1} / {pdf.Count}";
    }
    private void KeepAspect(bool fromWidth)
    {
        if (sizing || model == null || aspect.IsChecked != true) return;
        if (!int.TryParse(fromWidth ? width.Text : height.Text, out int value) || value < 1) return;
        double ratio = model.Frame.Width / model.Frame.Height;
        sizing = true; (fromWidth ? height : width).Text = Math.Max(1, Math.Round(fromWidth ? value / ratio : value * ratio)).ToString(); sizing = false;
    }
    private void UpdateCentimeters()
    {
        if (pdf != null) return;
        if (!int.TryParse(width.Text, out int pixelsWide) || !int.TryParse(height.Text, out int pixelsHigh) || pixelsWide < 1 || pixelsHigh < 1)
        {
            centimeters.Text = "cm換算：幅・高さを数値で入力してください";
            return;
        }
        centimeters.Text = $"cm換算：幅 約{pixelsWide * 2.54 / 96:0.00} cm　高さ 約{pixelsHigh * 2.54 / 96:0.00} cm（96 dpi換算・印刷時の目安）";
    }
    private void SetZoom(double value, Point? anchor = null)
    {
        double target = Math.Clamp(value, .01, 8);
        if (Math.Abs(target - zoom) < .0000001) return;
        viewer.UpdateLayout();
        Point? paperPoint = anchor.HasValue ? viewer.TranslatePoint(anchor.Value, surface) : null;
        zoom = target; surface.LayoutTransform = new ScaleTransform(zoom, zoom);
        surface.CropHandleUnit = 1 / zoom; surface.InvalidateVisual();
        textEditor?.UpdateZoom(zoom);
        UpdateCropPopup();
        if (paperPoint.HasValue && anchor.HasValue)
        {
            viewer.UpdateLayout();
            Point moved = surface.TranslatePoint(paperPoint.Value, viewer);
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + moved.X - anchor.Value.X);
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + moved.Y - anchor.Value.Y);
        }
    }
    // 子のテキスト入力欄がホイールを受け取る前に処理し、編集を確定せず表示だけ変える。
    internal bool HandleEditorWheel(int delta, bool control, Point anchor)
    {
        if (!control || delta == 0 || busy || model == null) return false;
        SetZoom(zoom * Math.Pow(1.12, Math.Clamp(delta / 120.0, -10, 10)), anchor);
        return true;
    }
    internal double EditorZoomForTest => zoom;
    internal string CentimeterTextForTest => centimeters.Text;
    internal double SaveButtonWidthForTest => saveButton.ActualWidth;
    internal string[] ToolNamesForTest => tool.Items.Cast<string>().ToArray();
    internal string[] HighlightMethodNamesForTest => highlightMethod.Items.Cast<string>().ToArray();
    internal bool ToolIconsReadyForTest => tool.ItemTemplate != null && highlightMethod.ItemTemplate != null &&
        tool.Items.Cast<string>().Concat(highlightMethod.Items.Cast<string>()).All(name => !ToolIconConverter.For(name).Bounds.IsEmpty);
    internal bool HighlightMethodVisibleForTest => highlightMethod.Visibility == Visibility.Visible;
    internal void SelectToolForTest(string name) => tool.SelectedItem = name;
    internal double ToolWidthForTest => tool.Width;
    internal bool CropModeForTest => cropMode;
    internal Rect? CropSelectionForTest => cropSelection;
    internal string CropSizeTextForTest => surface.CropSizeText ?? "";
    internal bool CropPopupOpenForTest => cropPopup?.IsOpen == true;
    internal string CropButtonTextForTest => cropButton.Content?.ToString() ?? "";
    internal void StartCropForTest() => StartCrop();
    internal void SetCropSelectionForTest(Rect value) => SetCropSelection(value);
    internal void ApplyCropForTest() => ApplyCrop();
    internal void CancelCropForTest() => CancelCrop();
    internal void RefreshForTest() { UpdateSurface(); Fit(); }
    private void UpdateCropButtons()
    {
        if (cropButton == null) return;
        cropButton.IsEnabled = !cropMode;
    }
    private Popup CreateCropPopup()
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "内側をドラッグ：移動　角をドラッグ：大きさ変更", FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 7) });
        cropPopupSize = new TextBlock { FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 7), Text = surface.CropSizeText ?? "" };
        root.Children.Add(cropPopupSize);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "クリップ", Width = 62, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.SlateGray });
        foreach (var (label, action) in new (string, Action)[] { ("確定", ApplyCrop), ("取消", CancelCrop) })
        {
            var button = new Button { Content = label, FontSize = 12, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(3, 0, 0, 0), Background = Brushes.White, BorderBrush = Brushes.LightGray };
            if (label == "確定") { button.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); button.Foreground = Brushes.White; button.BorderBrush = button.Background; }
            button.Click += (_, e) => { e.Handled = true; try { action(); } catch (Exception ex) { ShowError(ex); } };
            row.Children.Add(button);
        }
        root.Children.Add(row);
        var card = new Border { Child = root, Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = .18, Color = Colors.Black } };
        card.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CancelCrop(); e.Handled = true; } else if (e.Key == Key.Enter) { ApplyCrop(); e.Handled = true; } };
        return new Popup { Child = card, PlacementTarget = surface, Placement = PlacementMode.Relative, AllowsTransparency = true, StaysOpen = true };
    }
    private void UpdateCropPopup()
    {
        if (!cropMode || drag != null || cropSelection is not { Width: >= 1, Height: >= 1 } rect)
        {
            if (cropPopup != null) cropPopup.IsOpen = false;
            return;
        }
        cropPopup ??= CreateCropPopup();
        double x = Math.Clamp(rect.Left * zoom, 0, Math.Max(0, surface.Width * zoom - 290));
        double below = rect.Bottom * zoom + 8;
        double y = below + 70 <= surface.Height * zoom ? below : Math.Max(0, rect.Top * zoom - 70);
        cropPopup.HorizontalOffset = x; cropPopup.VerticalOffset = y; cropPopup.IsOpen = true;
    }
    private void StartCrop()
    {
        if (model == null) return;
        CancelEdits(); cropMode = true; SetCropSelection(null);
        status.Text = "クリップする範囲をドラッグ。選択後は内側をドラッグして移動、角で大きさを調整。近くの操作ウィンドウで確定または取消できます。";
        UpdateCropButtons();
    }
    private void SetCropSelection(Rect? value)
    {
        cropSelection = value is { } rect ? Rect.Intersect(rect, new Rect(0, 0, surface.Width, surface.Height)) : null;
        surface.CropRect = cropSelection;
        surface.CropSizeText = cropSelection is { Width: >= 1, Height: >= 1 } area ? CropSizeLabel(area) : null;
        if (cropPopupSize != null) cropPopupSize.Text = surface.CropSizeText ?? "";
        surface.InvalidateVisual(); UpdateCropButtons(); UpdateCropPopup();
    }
    private static string CropSizeLabel(Rect area)
    {
        int pixelsWide = (int)Math.Ceiling(area.Right) - (int)Math.Floor(area.Left);
        int pixelsHigh = (int)Math.Ceiling(area.Bottom) - (int)Math.Floor(area.Top);
        return $"{pixelsWide} × {pixelsHigh} px　約{pixelsWide * 2.54 / 96:0.00} × {pixelsHigh * 2.54 / 96:0.00} cm（96 dpi）";
    }
    private void ApplyCrop()
    {
        if (model == null || cropSelection is not { } rect) return;
        model.Crop(rect); CancelCrop(); UpdateSurface(); Fit();
        status.Text = "クリップしました。元に戻すこともできます。別名で保存してください。";
    }
    private void CancelCrop()
    {
        CancelDrag(); cropMode = false; surface.Cursor = Cursors.Arrow; SetCropSelection(null);
        if (cropPopup != null) cropPopup.IsOpen = false;
        UpdateCropButtons();
    }
    private int CropHandle(Point p)
    {
        if (cropSelection is not { } rect) return -1;
        double radius = 12 / zoom;
        Point[] corners = [rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft];
        for (int i = 0; i < corners.Length; i++) if ((p - corners[i]).Length <= radius) return i;
        return rect.Contains(p) ? 4 : -1;
    }
    private Rect AdjustCrop(Point start, Point current)
    {
        Vector delta = current - start;
        if (cropHandle == -1) return new Rect(start, current);
        if (cropHandle == 4) return new Rect(new Point(Math.Clamp(cropAtDragStart.X + delta.X, 0, surface.Width - cropAtDragStart.Width), Math.Clamp(cropAtDragStart.Y + delta.Y, 0, surface.Height - cropAtDragStart.Height)), cropAtDragStart.Size);
        Point opposite = cropHandle switch { 0 => cropAtDragStart.BottomRight, 1 => cropAtDragStart.BottomLeft, 2 => cropAtDragStart.TopLeft, _ => cropAtDragStart.TopRight };
        return new Rect(opposite, current);
    }
    internal EditMark HighlightForTest(bool freehand, params Point[] points)
    {
        if (points.Length < 2 || model == null) throw new ArgumentException("開始点と終了点が必要です。", nameof(points));
        int previousTool = tool.SelectedIndex, previousMethod = highlightMethod.SelectedIndex, previousColor = color.SelectedIndex;
        string previousStroke = stroke.Text;
        try
        {
            tool.SelectedItem = "ハイライト";
            highlightMethod.SelectedItem = freehand ? "フリーハンド" : "範囲指定";
            freehandPoints.Clear(); freehandPoints.AddRange(points);
            if (IsRangeHighlightTool && pdf != null && !highlightCharacters.ContainsKey(page)) highlightCharacters[page] = pdf.TextCharacters(page);
            return Mark(points[0], points[^1]);
        }
        finally { tool.SelectedIndex = previousTool; highlightMethod.SelectedIndex = previousMethod; color.SelectedIndex = previousColor; stroke.Text = previousStroke; freehandPoints.Clear(); }
    }
    internal Point PaperPointForTest(Point anchor) => viewer.TranslatePoint(anchor, surface);
    private void Fit()
    {
        if (model == null) return;
        viewer.UpdateLayout(); SetZoom(Math.Min(Math.Max(20, viewer.ViewportWidth - 40) / model.Frame.Width, Math.Max(20, viewer.ViewportHeight - 40) / model.Frame.Height));
    }
    private Point Position(MouseEventArgs e)
    {
        Point p = e.GetPosition(surface); return new Point(Math.Clamp(p.X, 0, surface.Width), Math.Clamp(p.Y, 0, surface.Height));
    }
    private EditMark Mark(Point start, Point end)
    {
        string kind = ToolName switch { "文字" => "text", "矢印" => "arrow", "四角形" => "rectangle", "円・楕円" => "ellipse", "範囲指定" => "highlight", _ => "line" };
        Color ink = new[] { Colors.Red, Colors.Black, Colors.RoyalBlue, Colors.ForestGreen, Colors.Orange, Colors.White, Colors.Yellow, Colors.HotPink, Colors.DeepSkyBlue, Colors.MediumPurple }[Math.Max(0, color.SelectedIndex)];
        if (IsRangeHighlightTool && pdf != null && model != null)
        {
            var boxes = TextHighlightSelection.Select(highlightCharacters[page], new Size(model.Frame.Width, model.Frame.Height), start, end);
            if (boxes.Length > 0)
            {
                Rect bounds = Rect.Empty; foreach (Rect box in boxes) bounds.Union(box);
                return new("highlight-text", bounds.TopLeft, bounds.BottomRight, "", ink, 1, HighlightBoxes: boxes);
            }
        }
        if (kind == "highlight") return new(kind, start, end, "", ink, 1);
        if (!double.TryParse(kind == "text" ? fontSize.Text : stroke.Text, out double size) || !double.IsFinite(size) || size <= 0 || size > 1000)
            throw new IOException("文字サイズ・線の太さは0より大きく1000以下で指定してください。");
        if (IsFreehandTool)
        {
            Point[] points = freehandPoints.ToArray(); Rect bounds = Rect.Empty; foreach (Point point in points) bounds.Union(point);
            if (bounds.IsEmpty) bounds = new Rect(start, end);
            if (bounds.Width < .1) bounds.Width = .1; if (bounds.Height < .1) bounds.Height = .1;
            return new("highlight-freehand", bounds.TopLeft, bounds.BottomRight, "", ink, size, StrokePoints: points);
        }
        return new(kind, start, end, "", ink, size, (font.SelectedItem as EditorFont)?.Id ?? "MS Gothic", bold.IsChecked == true);
    }
    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        if (model == null || busy) return;
        if ((textEditor != null || shapeEditor != null) && e.OriginalSource is DependencyObject origin && IsTextEditorChild(origin)) return;
        try
        {
            surface.Focus(); Point p = Position(e); CommitEdits();
            if (IsCropTool)
            {
                cropHandle = CropHandle(p); cropAtDragStart = cropSelection ?? Rect.Empty;
                drag = p; surface.CaptureMouse(); e.Handled = true; return;
            }
            int hit = HitShape(p);
            if (hit >= 0) BeginShape(model.Frame.Marks[hit], hit);
            else if (ToolName == "文字") BeginText(p);
            else
            {
                if (IsRangeHighlightTool && pdf != null && !highlightCharacters.ContainsKey(page)) highlightCharacters[page] = pdf.TextCharacters(page);
                freehandPoints.Clear(); freehandPoints.Add(p);
                if (!IsCropTool) _ = Mark(p, p); drag = p; surface.CaptureMouse();
            }
            e.Handled = true;
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void PointerMove(object sender, MouseEventArgs e)
    {
        if (drag is not { } start)
        {
            if (IsCropTool)
            {
                surface.Cursor = CropHandle(Position(e)) switch { 0 or 2 => Cursors.SizeNWSE, 1 or 3 => Cursors.SizeNESW, 4 => Cursors.SizeAll, _ => Cursors.Cross };
            }
            return;
        }
        try
        {
            Point point = Position(e);
            if (IsFreehandTool) AddFreehandPoint(point);
            if (IsCropTool) SetCropSelection(AdjustCrop(start, point)); else surface.PendingMark = Mark(start, point);
            surface.InvalidateVisual();
        }
        catch { CancelDrag(); }
    }
    private void PointerUp(object sender, MouseButtonEventArgs e)
    {
        if (drag is not { } start || model == null) return;
        Point end = Position(e);
        try
        {
            if (IsFreehandTool) AddFreehandPoint(end);
            bool enough = IsFreehandTool ? freehandPoints.Count > 1 : (end - start).Length * zoom >= 3;
            if (IsCropTool) { SetCropSelection(AdjustCrop(start, end)); CancelDrag(); UpdateCropPopup(); e.Handled = true; return; }
            EditMark? mark = enough ? Mark(start, end) : null; CancelDrag();
            if (!enough) return;
            if (mark != null) BeginShape(mark);
        }
        catch (Exception ex) { CancelDrag(); ShowError(ex); }
    }
    private void AddFreehandPoint(Point point)
    {
        if (freehandPoints.Count > 0 && (point - freehandPoints[^1]).Length * zoom < 2) return;
        if (freehandPoints.Count >= 2048) { var reduced = freehandPoints.Where((_, i) => i % 2 == 0).ToArray(); freehandPoints.Clear(); freehandPoints.AddRange(reduced); }
        freehandPoints.Add(point);
    }
    private void CancelDrag() { drag = null; freehandPoints.Clear(); surface.PendingMark = null; if (surface.IsMouseCaptured) surface.ReleaseMouseCapture(); surface.InvalidateVisual(); }
    private async Task LoadPage(int target)
    {
        if (busy || pdf == null || target < 0 || target >= pdf.Count) return;
        SetBusy(true);
        try
        {
            var size = pdf.SizeMm(target);
            double scale = Math.Min(144 / 25.4, 1800 / Math.Max(size.Width, size.Height));
            var bitmap = await Task.Run(() => pdf.Render(target, Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale))));
            page = target;
            if (!pages.TryGetValue(page, out model)) { model = new VisualEditModel(null, size.Width * 72 / 25.4, size.Height * 72 / 25.4); pages[page] = model; }
            surface.BackgroundImage = bitmap; UpdateSurface(); Fit();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    private async Task SaveAsync()
    {
        if (busy || model == null) return;
        CommitEdits();
        var dialog = new SaveFileDialog { Title = "原本を残して別名で保存", InitialDirectory = System.IO.Path.GetDirectoryName(source), FileName = System.IO.Path.GetFileNameWithoutExtension(source) + "_編集済み", Filter = pdf == null ? "PNG画像|*.png|JPEG画像|*.jpg;*.jpeg" : "PDF|*.pdf", DefaultExt = pdf == null ? ".png" : ".pdf", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        if (isOpen(dialog.FileName)) throw new IOException("開いているファイルには上書きできません。別の名前を指定してください。");
        SetBusy(true);
        try
        {
            if (pdf == null) model.SaveImage(dialog.FileName, source);
            else await SavePdfAsync(pdf, pages, dialog.FileName);
            status.Text = "保存しました: " + dialog.FileName;
            await open(dialog.FileName);
        }
        finally { SetBusy(false); }
    }
    internal static async Task SavePdfAsync(PdfDocument pdf, IReadOnlyDictionary<int, VisualEditModel> pages, string destination)
    {
        if (!pdf.CanEdit) throw new IOException("署名または編集制限のあるPDFには書き込めません。");
        if (string.Equals(System.IO.Path.GetFullPath(destination), System.IO.Path.GetFullPath(pdf.Path), StringComparison.OrdinalIgnoreCase)) throw new IOException("原本とは別の名前で保存してください。");
        var drawings = pages.Where(p => p.Value.Frame.Marks.Length > 0).Select(p =>
            new PdfDrawingPage(p.Key, p.Value.Frame.Width, p.Value.Frame.Height,
                p.Value.Frame.Marks.SelectMany(m => m.Kind == "text" && m.TextSegments is { Length: > 0 }
                    ? EditDrawing.TextShapes(m).Select(shape => new PdfDrawingShape(shape.Geometry, shape.Color))
                    : [new PdfDrawingShape(EditDrawing.Outline(m, p.Value.Frame.Width, p.Value.Frame.Height), EditDrawing.Ink(m))]).ToArray())).ToArray();
        if (drawings.Length == 0) throw new IOException("書き込みを追加してから保存してください。");
        await Task.Run(() => pdf.SaveDrawings(destination, drawings));
        foreach (var model in pages.Values) model.MarkSaved();
    }
}
