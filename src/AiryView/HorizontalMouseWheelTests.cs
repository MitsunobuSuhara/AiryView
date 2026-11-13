using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AiryView;

internal static class HorizontalMouseWheelTests
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    internal static async Task RunAsync()
    {
        var results = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); results.Add("PASS: " + name); }
        async Task Layout(Window w) => await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 2400, Height = 1800, Background = Brushes.LightBlue } };
        var window = new Window { Width = 640, Height = 480, Content = scroll, Title = "横スクロール検証" };
        var wheel = HorizontalMouseWheel.Install(window);
        try
        {
            window.Show(); await Layout(window);
            var point = scroll.TranslatePoint(new Point(150, 150), window);
            Point physical = window.PointToScreen(point);
            long coordinates = ((long)(ushort)(short)physical.Y << 16) | (ushort)(short)physical.X;
            void Native(int delta) => SendMessage(new WindowInteropHelper(window).Handle, 0x020E,
                new IntPtr((long)(ushort)(short)delta << 16), new IntPtr(coordinates));
            Native(120); await Layout(window);
            Check(scroll.HorizontalOffset > 0 && scroll.VerticalOffset == 0, "native right wheel moves horizontally only");
            double right = scroll.HorizontalOffset;
            Native(-120); await Layout(window);
            Check(scroll.HorizontalOffset < right, "native left wheel reverses direction");
            Native(30); await Layout(window);
            Check(scroll.HorizontalOffset > 0 && scroll.HorizontalOffset < right, "small native delta remains proportional");
            Check(wheel.HandleShiftWheel(point, -120, ModifierKeys.Shift), "Shift wheel is handled over content");
            await Layout(window);
            Check(scroll.HorizontalOffset > right, "Shift wheel down moves right");
            double before = scroll.HorizontalOffset;
            Check(!wheel.HandleShiftWheel(point, 120, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl wheel remains available for zoom");
            Check(!wheel.HandleShiftWheel(point, 120, ModifierKeys.None) && scroll.HorizontalOffset == before, "ordinary vertical wheel remains untouched");
            scroll.ScrollToRightEnd(); await Layout(window); Native(120); await Layout(window);
            Check(scroll.HorizontalOffset == scroll.ScrollableWidth && scroll.VerticalOffset == 0, "right edge does not become vertical scrolling");
            scroll.Content = new Border { Width = 100, Height = 100, Background = Brushes.White }; await Layout(window);
            Check(!wheel.HandleShiftWheel(point, -120, ModifierKeys.Shift), "content that fits is not intercepted");
        }
        finally { window.Close(); }
        // 実際の閲覧画面のフック経路も、利用者データを使わず確認する。
        var main = new MainWindow();
        try
        {
            main.Show(); await Layout(main);
            var viewer = (ScrollViewer)main.FindName("ImageViewer");
            viewer.Visibility = Visibility.Visible;
            viewer.Content = new Border { Width = 3000, Height = 1200, Background = Brushes.LightGreen };
            await Layout(main);
            Point physical = viewer.PointToScreen(new Point(100, 100));
            long coordinates = ((long)(ushort)(short)physical.Y << 16) | (ushort)(short)physical.X;
            SendMessage(new WindowInteropHelper(main).Handle, 0x020E, new IntPtr(120L << 16), new IntPtr(coordinates));
            await Layout(main);
            Check(viewer.HorizontalOffset > 0, "main image viewer receives native horizontal wheel through title-bar installation");
            viewer.Visibility = Visibility.Collapsed;
            var pdfViewer = (ScrollViewer)main.FindName("Viewer");
            pdfViewer.Visibility = Visibility.Visible;
            pdfViewer.Content = new Border { Width = 3000, Height = 1200, Background = Brushes.LightBlue };
            await Layout(main);
            physical = pdfViewer.PointToScreen(new Point(100, 100));
            coordinates = ((long)(ushort)(short)physical.Y << 16) | (ushort)(short)physical.X;
            SendMessage(new WindowInteropHelper(main).Handle, 0x020E, new IntPtr(120L << 16), new IntPtr(coordinates));
            await Layout(main);
            Check(pdfViewer.HorizontalOffset > 0 && pdfViewer.VerticalOffset == 0, "main PDF viewport scrolls horizontally without paging vertically");
        }
        finally { main.Close(); }
        Directory.CreateDirectory("artifacts/horizontal-scroll");
        File.WriteAllLines("artifacts/horizontal-scroll/results.txt", results);
    }
}
