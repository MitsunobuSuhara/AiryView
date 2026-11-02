using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Shell;
using ShapePath = System.Windows.Shapes.Path;

namespace AiryView;

// 2画面で高さと操作を共有し、タイトルの文字とボタンだけを描く。
internal sealed class WindowTitleBar : DockPanel
{
    internal const double BarHeight = 30;
    internal Button ThemeButton { get; }
    internal Button MinimizeButton { get; }
    internal Button MaximizeButton { get; }
    internal Button CloseButton { get; }
    internal Button? FileMenuButton { get; private set; }
    internal Button? FileOpenItem { get; private set; }
    internal Button? FilePrintItem { get; private set; }
    internal Popup? FileMenuPopup { get; private set; }
    private readonly ShapePath themeIcon;
    private readonly ShapePath maximizeIcon;
    private readonly StackPanel titleItems;

    internal static void Install(Window window)
    {
        var content = (UIElement)window.Content;
        window.Content = null;
        var root = new DockPanel();
        window.Content = root;
        root.Children.Add(content);
        Install(window, root);
    }

    internal static WindowTitleBar Install(Window window, DockPanel root)
    {
        ThemeManager.TrackWindow(window);
        // WindowChromeで見た目だけを置換し、枠だけのドラッグ表示などOSの機能を残す。
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.UseLayoutRounding = true; window.SnapsToDevicePixels = true;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = BarHeight,
            // DWMの枠を有効にし、倍率をまたぐ移動枠を切り抜くSetWindowRgnを避ける。
            ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(1), UseAeroCaptionButtons = false });
        var bar = new WindowTitleBar(window);
        SetDock(bar, Dock.Top); root.Children.Insert(0, bar);
        return bar;
    }

    private WindowTitleBar(Window window)
    {
        Height = BarHeight;
        SetResourceReference(BackgroundProperty, "AiryTitleBarBackground");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        SetDock(buttons, Dock.Right); Children.Add(buttons);

        // テーマ切替ボタン（最小化の左隣）
        themeIcon = new ShapePath { Width = 13, Height = 13, Stroke = Brushes.White, Fill = Brushes.Transparent, StrokeThickness = 1.2, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        ThemeButton = AddCustomButton(buttons, themeIcon, "表示切替: ダークモード / ライトモード (Ctrl+Shift+Z)", () => ThemeManager.Toggle(window));
        UpdateThemeIcon(ThemeManager.CurrentTheme);

        ThemeManager.ThemeChanged += UpdateThemeIcon;
        window.Closed += (_, _) => ThemeManager.ThemeChanged -= UpdateThemeIcon;

        MinimizeButton = AddButton(buttons, "最小化", "M1,6 L11,6", () => window.WindowState = WindowState.Minimized);
        MaximizeButton = AddButton(buttons, "最大化／元に戻す", "M2,1.5 H10 Q10.5,1.5 10.5,2 V10 Q10.5,10.5 10,10.5 H2 Q1.5,10.5 1.5,10 V2 Q1.5,1.5 2,1.5 Z",
            () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        maximizeIcon = (ShapePath)MaximizeButton.Content;
        CloseButton = AddButton(buttons, "閉じる", "M1,1 L11,11 M11,1 L1,11", window.Close, close: true);

        void UpdateMaximizeIcon()
        {
            maximizeIcon.Data = Geometry.Parse(window.WindowState == WindowState.Maximized
                ? "M4,1 H10 Q11,1 11,2 V8 M2,3 H8 Q9,3 9,4 V10 Q9,11 8,11 H2 Q1,11 1,10 V4 Q1,3 2,3 Z"
                : "M2,1.5 H10 Q10.5,1.5 10.5,2 V10 Q10.5,10.5 10,10.5 H2 Q1.5,10.5 1.5,10 V2 Q1.5,1.5 2,1.5 Z");
        }
        window.StateChanged += (_, _) => UpdateMaximizeIcon(); UpdateMaximizeIcon();

        titleItems = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        titleItems.Children.Add(new Image { Source = window.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0) });
        var label = new TextBlock { FontSize = 12, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        titleItems.Children.Add(label); Children.Add(titleItems);
    }

    internal void AddFileMenu(Action open, Action print)
    {
        var items = new StackPanel();
        var card = new Border { Child = items, MinWidth = 160, Padding = new Thickness(4), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
        card.SetResourceReference(Border.BackgroundProperty, "AiryPopupBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "AiryStroke");
        FileMenuPopup = new Popup { Child = card, Placement = PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = false };
        FileOpenItem = AddFileAction(items, "開く", "Ctrl+O", () => { FileMenuPopup.IsOpen = false; open(); });
        FilePrintItem = AddFileAction(items, "印刷", "Ctrl+P", () => { FileMenuPopup.IsOpen = false; print(); });
        FilePrintItem.IsEnabled = false;
        var caption = new TextBlock { Text = "ファイル", FontSize = 12, Foreground = Brushes.White };
        FileMenuButton = AddCustomButton(titleItems, caption, "ファイル", () => FileMenuPopup.IsOpen = !FileMenuPopup.IsOpen, width: 62);
        FileMenuButton.Margin = new Thickness(12, 0, 0, 0);
        FileMenuPopup.PlacementTarget = FileMenuButton;
        FileMenuPopup.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { FileMenuPopup.IsOpen = false; e.Handled = true; } };
    }

    private static Button AddFileAction(Panel parent, string name, string shortcut, Action action)
    {
        var row = new DockPanel();
        var gesture = new TextBlock { Text = shortcut, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
        gesture.SetResourceReference(TextBlock.ForegroundProperty, "AiryInkSecondary");
        DockPanel.SetDock(gesture, Dock.Right); row.Children.Add(gesture);
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "AiryInk"); row.Children.Add(label);
        var button = new Button { Content = row, Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 28 };
        var surface = new FrameworkElementFactory(typeof(Border)); surface.Name = "Surface";
        surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        surface.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); surface.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var highlighted = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        highlighted.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("AiryButtonBackgroundHover"), "Surface"));
        template.Triggers.Add(highlighted);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45)); template.Triggers.Add(disabled);
        button.Template = template;
        AutomationProperties.SetName(button, name); button.Click += (_, _) => action(); parent.Children.Add(button);
        return button;
    }

    private void UpdateThemeIcon(AppTheme theme)
    {
        if (theme == AppTheme.Modern)
        {
            // モダン時は「太陽（Classicへ戻す）」アイコン
            themeIcon.Data = Geometry.Parse("M 6.5,3.5 A 3,3 0 1 1 6.5,9.5 A 3,3 0 1 1 6.5,3.5 M 6.5,0.5 L 6.5,2 M 6.5,11 L 6.5,12.5 M 0.5,6.5 L 2,6.5 M 11,6.5 L 12.5,6.5 M 2.2,2.2 L 3.3,3.3 M 9.7,9.7 L 10.8,10.8 M 2.2,10.8 L 3.3,9.7 M 9.7,3.3 L 10.8,2.2");
            themeIcon.Fill = new SolidColorBrush(Color.FromArgb(50, 117, 168, 187));
            themeIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AiryAccent");
            ThemeButton.ToolTip = "ライトモードへ切替（Ctrl+Shift+Z）";
        }
        else
        {
            // クラシック時は「きらめき／月（Modernへ）」アイコン
            themeIcon.Data = Geometry.Parse("M 6.5,0.5 L 7.8,4.8 L 12.5,6.5 L 7.8,8.2 L 6.5,12.5 L 5.2,8.2 L 0.5,6.5 L 5.2,4.8 Z");
            themeIcon.Fill = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
            themeIcon.Stroke = Brushes.White;
            ThemeButton.ToolTip = "ダークモードへ切替（Ctrl+Shift+Z）";
        }
    }

    private static Button AddCustomButton(Panel parent, FrameworkElement icon, string label, Action action, double width = 38)
    {
        var button = new Button { Content = icon, ToolTip = label, Width = width, Height = BarHeight, MinHeight = 0,
            Margin = new Thickness(0), Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "Surface";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), "Surface"));
        template.Triggers.Add(hover); button.Template = template;
        AutomationProperties.SetName(button, label);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => action(); parent.Children.Add(button); return button;
    }

    private static Button AddButton(Panel parent, string label, string geometry, Action action, bool close = false)
    {
        var icon = new ShapePath { Data = Geometry.Parse(geometry), Width = 12, Height = 12,
            Stroke = Brushes.White, StrokeThickness = 1, Stretch = Stretch.None, IsHitTestVisible = false };
        var button = new Button { Content = icon, ToolTip = label, Width = 46, Height = BarHeight, MinHeight = 0,
            Margin = new Thickness(0), Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "Surface";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(close ? Color.FromRgb(196, 43, 28) : Color.FromArgb(60, 255, 255, 255)), "Surface"));
        template.Triggers.Add(hover); button.Template = template;
        AutomationProperties.SetName(button, label);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => action(); parent.Children.Add(button); return button;
    }
}
