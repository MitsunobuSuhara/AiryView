using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Shell;
using ShapePath = System.Windows.Shapes.Path;

namespace AiryView;

// 2画面で高さと操作を共有し、タイトルの文字とボタンだけを描く。
internal sealed class WindowTitleBar : DockPanel
{
    internal const double BarHeight = 30;
    internal Button MinimizeButton { get; }
    internal Button MaximizeButton { get; }
    internal Button CloseButton { get; }
    private readonly ShapePath maximizeIcon;

    internal static WindowTitleBar Install(Window window, DockPanel root)
    {
        window.WindowStyle = WindowStyle.None;
        window.UseLayoutRounding = true; window.SnapsToDevicePixels = true;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = BarHeight,
            ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        var bar = new WindowTitleBar(window);
        SetDock(bar, Dock.Top); root.Children.Insert(0, bar);
        return bar;
    }

    private WindowTitleBar(Window window)
    {
        Height = BarHeight;
        Background = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(20, 47, 64), 0),
            new(Color.FromRgb(20, 80, 106), .48),
            new(Color.FromRgb(12, 52, 76), 1)
        }, new Point(0, 0), new Point(1, 0));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        SetDock(buttons, Dock.Right); Children.Add(buttons);
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
        var title = new DockPanel { Margin = new Thickness(9, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new Image { Source = window.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0) });
        var label = new TextBlock { FontSize = 12, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        title.Children.Add(label); Children.Add(title);
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
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(close ? Color.FromRgb(196, 43, 28) : Color.FromRgb(60, 60, 60)), "Surface"));
        template.Triggers.Add(hover); button.Template = template;
        AutomationProperties.SetName(button, label);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => action(); parent.Children.Add(button); return button;
    }
}
