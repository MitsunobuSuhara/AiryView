using System.Windows.Media.Effects;

namespace AiryView;

// 保存確認だけに使う軽量なダイアログ。選択結果は従来の Yes / No / Cancel と同じ。
internal sealed class SoftConfirmDialog : Window
{
    private MessageBoxResult choice = MessageBoxResult.Cancel;
    internal MessageBoxResult ChoiceForTest => choice;

    private SoftConfirmDialog(Window owner, string title, string message, params (string Label, MessageBoxResult Result)[] actions)
    {
        Owner = owner;
        Title = title;
        Width = 470;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Yu Gothic UI");

        var card = new Border
        {
            Margin = new Thickness(14), Padding = new Thickness(22),
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(215, 224, 234)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Opacity = .2, Color = Colors.Black }
        };
        Content = card;
        var content = new StackPanel(); card.Child = content;

        var heading = new DockPanel(); content.Children.Add(heading);
        var close = new Button { Content = "×", Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "キャンセル" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        heading.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(36, 50, 68)), VerticalAlignment = VerticalAlignment.Center });

        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(66, 79, 97)), Margin = new Thickness(0, 18, 0, 22) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        content.Children.Add(buttons);
        foreach (var (label, result) in actions)
        {
            var button = new Button { Content = label, MinWidth = result == MessageBoxResult.No ? 120 : 84,
                Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4, 0, 0, 0),
                IsDefault = result == MessageBoxResult.Yes, IsCancel = result == MessageBoxResult.Cancel };
            if (result == MessageBoxResult.Yes)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(71, 121, 189));
                button.BorderBrush = button.Background;
                button.Foreground = Brushes.White;
            }
            button.Click += (_, _) => { choice = result; DialogResult = result != MessageBoxResult.Cancel; };
            buttons.Children.Add(button);
        }
    }

    internal static SoftConfirmDialog CreateSave(Window owner, string fileName) => new(owner, "未保存の変更",
        $"{fileName} の変更を保存しますか？",
        ("保存", MessageBoxResult.Yes), ("保存せず閉じる", MessageBoxResult.No), ("キャンセル", MessageBoxResult.Cancel));

    internal static MessageBoxResult AskSave(Window owner, string fileName)
    {
        var dialog = CreateSave(owner, fileName);
        dialog.ShowDialog();
        return dialog.choice;
    }

    internal static SoftConfirmDialog CreateDiscard(Window owner) => new(owner, "未保存の編集",
        "保存していない編集を破棄して閉じますか？",
        ("破棄して閉じる", MessageBoxResult.Yes), ("キャンセル", MessageBoxResult.Cancel));

    internal static bool AskDiscard(Window owner) => CreateDiscard(owner).ShowDialog() == true;
}
