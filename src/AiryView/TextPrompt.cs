namespace AiryView;
public static class TextPrompt
{
    public static string? Ask(Window owner, string title, string message, bool secret = false)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var window = new Window { Owner = owner, Title = title, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Content = panel };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
        var text = new TextBox(); var password = new PasswordBox { Padding = new Thickness(7) };
        panel.Children.Add(secret ? password : text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", IsDefault = true }; var cancel = new Button { Content = "キャンセル", IsCancel = true };
        ok.Click += (_,_) => window.DialogResult = true; buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        WindowTitleBar.Install(window);
        window.Loaded += (_,_) => { if (secret) password.Focus(); else text.Focus(); };
        return window.ShowDialog() == true ? (secret ? password.Password : text.Text) : null;
    }
}
