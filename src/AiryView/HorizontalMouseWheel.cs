using System.Windows.Input;
using System.Windows.Interop;

namespace AiryView;

// WPFが標準で転送しない横ホイール入力を、ポインター下の閲覧領域へ渡す。
internal sealed class HorizontalMouseWheel
{
    private readonly Window window;
    private HwndSource? source;
    private HorizontalMouseWheel(Window window)
    {
        this.window = window;
        window.SourceInitialized += Attach;
        window.Closed += (_, _) => { source?.RemoveHook(Hook); source = null; };
        window.PreviewMouseWheel += (_, e) =>
        {
            if (HandleShiftWheel(e.GetPosition(window), e.Delta, Keyboard.Modifiers)) e.Handled = true;
        };
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Attach(null, EventArgs.Empty);
    }
    internal static HorizontalMouseWheel Install(Window window) => new(window);
    private void Attach(object? sender, EventArgs e)
    {
        if (source != null) return;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook(Hook);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x020E) return IntPtr.Zero; // WM_MOUSEHWHEEL: 正方向は右
        int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
        long coordinates = lParam.ToInt64();
        Point screen = new(unchecked((short)(coordinates & 0xffff)), unchecked((short)((coordinates >> 16) & 0xffff)));
        handled = ScrollAt(window.PointFromScreen(screen), delta);
        return IntPtr.Zero;
    }
    internal bool HandleShiftWheel(Point point, int delta, ModifierKeys modifiers)
        => modifiers == ModifierKeys.Shift && ScrollAt(point, -delta);
    private bool ScrollAt(Point point, int delta)
    {
        if (delta == 0) return false;
        DependencyObject? hit = window.InputHitTest(point) as DependencyObject;
        while (hit != null)
        {
            if (hit is ScrollViewer scroll && scroll.IsVisible && scroll.ScrollableWidth > 0
                && scroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
            {
                // 120未満の細かな入力も比例配分し、縦移動やページ送りへ流さない。
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + delta / 120.0 * 48);
                return true;
            }
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        return false;
    }
}
