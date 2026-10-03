using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;

namespace AiryView;

public partial class MainWindow
{
    private Image CreatePagePreviewImage(BitmapSource bitmap, int page)
    {
        double width = Math.Max(150, PagePreviewSidebar.Width - 56);
        var image = new Image { Source = bitmap, MaxWidth = width, MaxHeight = width * 1.5, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var large = new Image { Source = bitmap, MaxWidth = 480,
            MaxHeight = Math.Min(680, SystemParameters.WorkArea.Height * .75), Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(large, BitmapScalingMode.HighQuality);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = $"{page + 1} ページ", Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(large);
        var tip = new ToolTip { Content = content, Placement = PlacementMode.Right,
            MaxWidth = Math.Min(510, SystemParameters.WorkArea.Width * .6), PlacementTarget = image, Padding = new Thickness(12), HasDropShadow = true };
        image.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(image, 500);
        ToolTipService.SetShowDuration(image, 30000);
        return image;
    }

    private void PagePreviewResize(object sender, DragDeltaEventArgs e)
    {
        PagePreviewSidebar.Width = Math.Clamp(PagePreviewSidebar.Width + e.HorizontalChange, 206, Math.Min(460, Math.Max(206, ActualWidth * .45)));
        Viewer.Margin = new Thickness(PagePreviewSidebar.Width, 0, 0, 0);
        foreach (var button in previewButtons.Values)
        {
            if (button.Content is StackPanel panel && panel.Children[0] is Image image)
            {
                image.MaxWidth = PagePreviewSidebar.Width - 56;
                image.MaxHeight = image.MaxWidth * 1.5;
            }
        }
    }
}
