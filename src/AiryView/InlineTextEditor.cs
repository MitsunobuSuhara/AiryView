using System.Globalization;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Effects;

namespace AiryView;

// 入力欄と近くの書式パレットは編集時だけ生成する。パレットは用紙の外でも切れない。
internal sealed class InlineTextEditor : Border
{
    internal TextBox Input { get; }
    private readonly double inset, padding;
    private readonly Size bounds;
    private double zoom;
    private readonly Popup palette;
    private readonly Popup movePopup;
    private readonly Popup formatPopup;
    private readonly Button formatHandle;
    private readonly ComboBox fontPicker;
    private readonly TextBox sizePicker;
    private readonly ToggleButton boldPicker;
    private readonly Button colorPicker;
    private readonly Button? deleteButton;
    private bool syncing;
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
    internal bool CanDeleteForTest => deleteButton != null;
    internal void DeleteForTest() => deleteButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal Point TextPosition => new(Canvas.GetLeft(this) + inset + padding, Canvas.GetTop(this) + inset + padding);

    internal InlineTextEditor(Point position, string text, double fontSize, Color ink, double zoom, Size bounds, Action accept, Action cancel, Action? remove = null)
    {
        this.bounds = bounds; this.zoom = zoom;
        inset = 1 / zoom; padding = 4 / zoom;
        BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246)); BorderThickness = new Thickness(inset);
        CornerRadius = new CornerRadius(4 / zoom); Padding = new Thickness(padding); Background = Brushes.Transparent;
        Input = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MaxLength = 5000,
            FontFamily = new FontFamily("MS Gothic"), BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Background = Brushes.Transparent, MinHeight = fontSize * 1.3,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(fontSize * 2, bounds.Height - 2 * (inset + padding)) };
        formatHandle = new Button { Content = "書式 ▾", Width = 62 / zoom, Height = 25 / zoom, FontSize = 11 / zoom,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, MinHeight = 0,
            Padding = new Thickness(4 / zoom, 0, 4 / zoom, 0), Margin = new Thickness(2 / zoom),
            ToolTip = "文字のフォント・大きさ・色を変更" };
        var grip = new Thumb { Cursor = Cursors.SizeAll, ToolTip = "ドラッグして文字を移動", Width = 30 / zoom, Height = 25 / zoom, Margin = new Thickness(2 / zoom) };
        var gripChrome = new FrameworkElementFactory(typeof(Border));
        gripChrome.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(241, 245, 249)));
        gripChrome.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(203, 213, 225)));
        gripChrome.SetValue(Border.BorderThicknessProperty, new Thickness(1 / zoom));
        gripChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4 / zoom));
        var gripText = new FrameworkElementFactory(typeof(TextBlock));
        gripText.SetValue(TextBlock.TextProperty, "✥"); gripText.SetValue(TextBlock.FontSizeProperty, 19 / zoom);
        gripText.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(71, 85, 105)));
        gripText.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        gripText.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        gripChrome.AppendChild(gripText); grip.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = gripChrome };
        grip.DragDelta += (_, e) => { MoveTo(Canvas.GetLeft(this) + e.HorizontalChange / this.zoom, Canvas.GetTop(this) + e.VerticalChange / this.zoom); e.Handled = true; };
        var resizeGrip = new Thumb { Cursor = Cursors.SizeNWSE, ToolTip = "ドラッグして入力欄の幅と高さを変更", Width = 22 / zoom, Height = 22 / zoom, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        var resizeChrome = new FrameworkElementFactory(typeof(Border));
        resizeChrome.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)));
        resizeChrome.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(59, 130, 246)));
        resizeChrome.SetValue(Border.BorderThicknessProperty, new Thickness(1 / zoom));
        resizeChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(3 / zoom));
        var resizeIcon = new FrameworkElementFactory(typeof(TextBlock));
        resizeIcon.SetValue(TextBlock.TextProperty, "◢"); resizeIcon.SetValue(TextBlock.FontSizeProperty, 14 / zoom);
        resizeIcon.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(59, 130, 246)));
        resizeIcon.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        resizeIcon.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        resizeChrome.AppendChild(resizeIcon); resizeGrip.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = resizeChrome };
        resizeGrip.DragDelta += (_, e) =>
        {
            Width = Math.Clamp(Width + e.HorizontalChange / this.zoom, Math.Min(120 / this.zoom, bounds.Width), bounds.Width);
            Input.MinHeight = Math.Clamp(Input.MinHeight + e.VerticalChange / this.zoom, Input.FontSize * 1.3, Math.Max(Input.FontSize * 1.3, bounds.Height - 2 * (inset + padding)));
            e.Handled = true;
        };
        var inputArea = new Grid();
        movePopup = new Popup { Child = grip, PlacementTarget = this, Placement = PlacementMode.Top,
            VerticalOffset = -3, AllowsTransparency = true, StaysOpen = true };
        formatPopup = new Popup { Child = formatHandle, PlacementTarget = this, Placement = PlacementMode.Bottom,
            VerticalOffset = 3, AllowsTransparency = true, StaysOpen = true };
        inputArea.Children.Add(Input); inputArea.Children.Add(resizeGrip); Child = inputArea;

        var root = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 9) }; root.Children.Add(heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right); heading.Children.Add(actions);
        var done = ActionButton("確定", "確定 / Ctrl+Enter"); done.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); done.Foreground = Brushes.White; done.BorderBrush = done.Background;
        done.Click += (_, _) => accept(); actions.Children.Add(done);
        var dismiss = ActionButton("×", "入力を取り消す / Esc"); dismiss.Click += (_, _) => cancel(); actions.Children.Add(dismiss);
        if (remove != null)
        {
            deleteButton = ActionButton("削除", "確定済みの文字を削除（元に戻せます）");
            deleteButton.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            deleteButton.Click += (_, _) => remove(); actions.Children.Add(deleteButton);
        }
        var formats = new StackPanel { Orientation = Orientation.Horizontal }; root.Children.Add(formats);
        fontPicker = new ComboBox { ItemsSource = EditorFonts.Choices, SelectedIndex = 0, Width = 190, Height = 30, Margin = new Thickness(0, 0, 7, 0), ToolTip = "フォント" };
        sizePicker = new TextBox { Width = 54, Height = 30, Padding = new Thickness(5, 2, 5, 2), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "文字サイズ", Margin = new Thickness(0, 0, 7, 0) };
        boldPicker = new ToggleButton { Content = "B", FontWeight = FontWeights.Bold, Width = 32, Height = 30, ToolTip = "太字", Margin = new Thickness(0, 0, 7, 0) };
        colorPicker = ActionButton("● 色", "文字の色"); colorPicker.MinWidth = 54; colorPicker.Height = 30;
        formats.Children.Add(fontPicker); formats.Children.Add(FontSizeControls.Wrap(sizePicker)); formats.Children.Add(boldPicker); formats.Children.Add(colorPicker);
        var colors = new ContextMenu();
        foreach (var (name, color) in new (string, Color)[] { ("黒", Colors.Black), ("赤", Colors.Red), ("青", Colors.RoyalBlue), ("緑", Colors.ForestGreen), ("橙", Colors.Orange), ("白", Colors.White) })
        {
            var item = new MenuItem { Header = name, Icon = new Border { Width = 14, Height = 14, Background = new SolidColorBrush(color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) } };
            item.Click += (_, _) => { SetAppearance(Input.FontSize, color); AppearanceChanged?.Invoke(); }; colors.Items.Add(item);
        }
        colorPicker.ContextMenu = colors;
        colorPicker.Click += (_, _) => { colors.PlacementTarget = colorPicker; colors.Placement = PlacementMode.Bottom; colors.IsOpen = true; };
        var card = new Border { Child = root, Padding = new Thickness(12), Margin = new Thickness(8), Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = .18, Color = Colors.Black } };
        card.SetValue(TextElement.FontFamilyProperty, new FontFamily("Yu Gothic UI")); card.SetValue(TextElement.FontSizeProperty, 12.0);
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Width = Math.Min(bounds.Width, Math.Max(card.DesiredSize.Width / zoom, fontSize * 12));
        palette = new Popup { Child = card, PlacementTarget = formatHandle, Placement = PlacementMode.Bottom, VerticalOffset = 3, AllowsTransparency = true, StaysOpen = true };
        formatHandle.Click += (_, _) => SetPaletteOpen(!formattingRequested);
        Input.GotKeyboardFocus += (_, _) => SetPaletteOpen(false);
        Input.TextChanged += (_, _) => SetPaletteOpen(false);
        card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { cancel(); e.Handled = true; }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Enter) { accept(); e.Handled = true; }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.S) { SaveRequested?.Invoke(); e.Handled = true; }
        };
        card.PreviewMouseWheel += (_, e) => { if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { ZoomRequested?.Invoke(e.Delta); e.Handled = true; } };
        fontPicker.SelectionChanged += (_, _) => ChangeFormatting(); sizePicker.TextChanged += (_, _) => ChangeFormatting();
        boldPicker.Checked += (_, _) => ChangeFormatting(); boldPicker.Unchecked += (_, _) => ChangeFormatting();
        SetAppearance(fontSize, ink); SetFont("MS Gothic", false); MoveTo(position.X - inset - padding, position.Y - inset - padding);
        Loaded += (_, _) => { owner = Window.GetWindow(this); if (owner != null) { owner.Deactivated += HidePalette; owner.Activated += ShowPalette; } movePopup.IsOpen = true; formatPopup.IsOpen = true; Input.Focus(); Input.CaretIndex = Input.Text.Length; };
        Unloaded += (_, _) => { ClosePalette(); if (owner != null) { owner.Deactivated -= HidePalette; owner.Activated -= ShowPalette; } owner = null; };
        LayoutUpdated += (_, _) => RepositionPalette();
    }
    private static Button ActionButton(string label, string tip) => new() { Content = label, ToolTip = tip, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(3, 0, 0, 0), MinHeight = 28, FontSize = 12, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)) };
    private void SetPaletteOpen(bool open)
    {
        formattingRequested = open; palette.IsOpen = open;
        formatHandle.Background = open ? new SolidColorBrush(Color.FromRgb(219, 234, 254)) : Brushes.White;
        if (open) RepositionPalette();
    }
    internal void OpenFormattingForTest() => SetPaletteOpen(true);
    private void HidePalette(object? sender, EventArgs e) { palette.IsOpen = false; movePopup.IsOpen = false; formatPopup.IsOpen = false; }
    private void ShowPalette(object? sender, EventArgs e) { if (!IsLoaded) return; movePopup.IsOpen = true; formatPopup.IsOpen = true; if (formattingRequested) palette.IsOpen = true; }
    internal void ClosePalette() { SetPaletteOpen(false); movePopup.IsOpen = false; formatPopup.IsOpen = false; if (colorPicker.ContextMenu != null) colorPicker.ContextMenu.IsOpen = false; }
    internal void UpdateZoom(double value) { zoom = value; RepositionPalette(); }
    private void RepositionPalette()
    {
        if (!IsLoaded || (!palette.IsOpen && !movePopup.IsOpen && !formatPopup.IsOpen)) return;
        Point now = PointToScreen(new Point());
        Size size = new(ActualWidth, ActualHeight);
        if ((now - lastScreenPoint).Length < .1 && Math.Abs(size.Width - lastEditorSize.Width) < .1 && Math.Abs(size.Height - lastEditorSize.Height) < .1) return;
        lastScreenPoint = now; lastEditorSize = size;
        if (movePopup.IsOpen) { movePopup.HorizontalOffset += .01; movePopup.HorizontalOffset -= .01; }
        if (formatPopup.IsOpen) { formatPopup.HorizontalOffset += .01; formatPopup.HorizontalOffset -= .01; }
        if (palette.IsOpen) { palette.HorizontalOffset += .01; palette.HorizontalOffset -= .01; }
    }
    private void MoveTo(double x, double y)
    {
        Canvas.SetLeft(this, Math.Clamp(x, -inset - padding, Math.Max(0, bounds.Width - Math.Min(Width, 40))));
        Canvas.SetTop(this, Math.Clamp(y, -inset - padding, Math.Max(0, bounds.Height - Input.FontSize * 1.3))); RepositionPalette();
    }
    private void ChangeFormatting()
    {
        if (syncing || fontPicker.SelectedItem is not EditorFont selected || !double.TryParse(sizePicker.Text, out double size) || !double.IsFinite(size) || size <= 0 || size > 1000) return;
        SetFont(selected.Id, boldPicker.IsChecked == true); SetAppearance(size, Ink); AppearanceChanged?.Invoke();
    }
    internal void SetAppearance(double fontSize, Color ink)
    {
        Ink = ink; Input.FontSize = fontSize; Input.Foreground = new SolidColorBrush(ink); TextBlock.SetLineHeight(Input, fontSize * 1.3);
        syncing = true; sizePicker.Text = fontSize.ToString(CultureInfo.CurrentCulture); colorPicker.Foreground = ink == Colors.White ? Brushes.Gray : Input.Foreground; syncing = false;
    }
    internal void SetFont(string id, bool bold)
    {
        FontId = id; Input.FontFamily = EditorFonts.Family(id); Input.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        syncing = true; fontPicker.SelectedItem = EditorFonts.Choices.FirstOrDefault(item => item.Id == id) ?? EditorFonts.Choices[0]; boldPicker.IsChecked = bold; syncing = false;
    }
    internal void SelectFormattingForTest(string id, double size, bool bold) { fontPicker.SelectedItem = EditorFonts.Choices.First(item => item.Id == id); sizePicker.Text = size.ToString(CultureInfo.CurrentCulture); boldPicker.IsChecked = bold; }
    internal void SelectColorForTest(int index) => ((MenuItem)colorPicker.ContextMenu!.Items[index]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    internal EditMark Mark => new("text", TextPosition, TextPosition, Input.Text, Ink, Input.FontSize, FontId, Input.FontWeight == FontWeights.Bold);
}
