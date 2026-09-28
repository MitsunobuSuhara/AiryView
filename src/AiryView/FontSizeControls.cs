using System.Globalization;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AiryView;

internal static class FontSizeControls
{
    internal static FrameworkElement Wrap(TextBox field, bool stroke = false)
    {
        var wrapper = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = field.Margin };
        field.Margin = new Thickness(0); wrapper.Children.Add(field);
        var arrows = new Grid { Width = 20, Height = 30 };
        arrows.RowDefinitions.Add(new RowDefinition()); arrows.RowDefinitions.Add(new RowDefinition());
        for (int row = 0; row < 2; row++)
        {
            int direction = row == 0 ? 1 : -1;
            var button = new RepeatButton { Content = row == 0 ? "▴" : "▾", FontSize = 10, Padding = new Thickness(0),
                Focusable = false, IsTabStop = false, Delay = 350, Interval = 80,
                ToolTip = stroke ? (row == 0 ? "線を太く / ↑" : "線を細く / ↓") : (row == 0 ? "文字を大きく / ↑" : "文字を小さく / ↓"), Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)), BorderThickness = new Thickness(1) };
            button.Click += (_, _) => Step(field, direction); Grid.SetRow(button, row); arrows.Children.Add(button);
        }
        wrapper.Children.Add(arrows);
        field.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down)
            { Step(field, e.Key == Key.Up ? 1 : -1); e.Handled = true; }
        };
        return wrapper;
    }
    internal static void Step(TextBox field, int direction)
    {
        if (!double.TryParse(field.Text, out double value) || !double.IsFinite(value)) value = 14;
        field.Text = Math.Clamp(value + direction, 1, 1000).ToString("0.##", CultureInfo.CurrentCulture);
    }
}
