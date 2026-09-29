using System.Windows.Controls.Primitives;
using System.Windows.Media.Effects;

namespace AiryView;

// 色名はツールチップだけに置き、選択面は色見本を一覧できる形にする。
internal sealed class EditorColorPalette
{
    private readonly Popup popup;
    private readonly UniformGrid grid = new() { Columns = 4 };
    private readonly List<Button> choices = [];

    internal int ChoiceCount => choices.Count;
    internal int Columns => grid.Columns;
    internal bool SwatchesOnly => choices.All(button => button.Content is Border && button.ToolTip is string);
    internal FrameworkElement Visual => (FrameworkElement)popup.Child;
    internal void OpenForTest() => popup.IsOpen = true;

    internal EditorColorPalette(Button target, (string Name, Color Color)[] colors, Action<Color> select)
    {
        popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom,
            VerticalOffset = 3, AllowsTransparency = true, StaysOpen = false };
        foreach (var (name, color) in colors)
        {
            var swatch = new Border { Width = 22, Height = 22, Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            var button = new Button { Content = swatch, ToolTip = name, Width = 38, Height = 38,
                Padding = new Thickness(3), Margin = new Thickness(2), Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(218, 226, 234)) };
            button.Click += (_, _) => { select(color); popup.IsOpen = false; };
            choices.Add(button); grid.Children.Add(button);
        }
        var frame = new Border { Child = grid, Padding = new Thickness(5), Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(185, 199, 211)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = .18 } };
        popup.Child = frame;
        target.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
    }

    internal void Close() => popup.IsOpen = false;
    internal void SelectForTest(int index) => choices[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
