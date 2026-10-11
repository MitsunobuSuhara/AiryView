using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AiryView;

// WPFの寸法はDIP、モニターの作業領域は画素なので、表示先の倍率で換算する。
internal sealed class WindowDisplayBounds
{
    private readonly Window window;
    private readonly FrameworkElement? content;
    private readonly Thickness contentMargin;
    private readonly double minimumWidth, minimumHeight, maximumWidth, maximumHeight;
    private HwndSource? source;
    private IntPtr handle, monitor;
    private bool pending, updating, moving, closed;

    private WindowDisplayBounds(Window window)
    {
        this.window = window;
        content = window.Content as FrameworkElement;
        contentMargin = content?.Margin ?? default;
        minimumWidth = window.MinWidth; minimumHeight = window.MinHeight;
        maximumWidth = window.MaxWidth; maximumHeight = window.MaxHeight;
        window.SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle);
            source?.AddHook(WindowMessage);
            UpdateBounds();
        };
        window.Loaded += (_, _) => QueueUpdate();
        window.DpiChanged += (_, _) => QueueUpdate();
        window.StateChanged += (_, _) => QueueUpdate();
        window.LocationChanged += (_, _) =>
        {
            if (handle != IntPtr.Zero && MonitorFromWindow(handle, 2) != monitor) QueueUpdate();
        };
        window.Closed += (_, _) => { closed = true; source?.RemoveHook(WindowMessage); };
    }

    internal static void Attach(Window window) => _ = new WindowDisplayBounds(window);

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x0024: // WM_GETMINMAXINFO: 非表示の標準枠を含めず、作業領域へ最大化する。
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info))
                {
                    var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                    limits.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
                    limits.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
                    // 処理済みにする以上、移動中も有効な追跡寸法を返す。
                    // 最大化の寸法と混同せず、Windows標準の仮想画面の制限を使う。
                    limits.MaxTrackSize = new NativePoint(
                        Math.Max(limits.MaxSize.X, GetSystemMetrics(59)),
                        Math.Max(limits.MaxSize.Y, GetSystemMetrics(60)));
                    double scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0;
                    limits.MinTrackSize.X = (int)Math.Min(Math.Ceiling(window.MinWidth * scale), limits.MaxSize.X);
                    limits.MinTrackSize.Y = (int)Math.Min(Math.Ceiling(window.MinHeight * scale), limits.MaxSize.Y);
                    Marshal.StructureToPtr(limits, lParam, false);
                    handled = true;
                }
                break;
            case 0x0231: moving = true; break; // WM_ENTERSIZEMOVE
            case 0x0232: moving = false; QueueUpdate(); break;
            case 0x007E: // WM_DISPLAYCHANGE: 縦横切替・画面の切断
            case 0x001A: // WM_SETTINGCHANGE: タスクバーの作業領域
            case 0x02E0: QueueUpdate(); break; // WM_DPICHANGED
        }
        return IntPtr.Zero;
    }

    private void QueueUpdate()
    {
        if (pending || updating || moving || closed) return;
        pending = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            pending = false;
            if (!closed) UpdateBounds();
        }));
    }

    private void UpdateBounds()
    {
        // 寸法制限の変更もWPFの再配置を起こすため、移動中は補正全体を待つ。
        if (handle == IntPtr.Zero || updating || moving || window.WindowState == WindowState.Minimized) return;
        var display = ReadDisplay(handle);
        if (display.WorkArea.IsEmpty) return;
        updating = true;
        try
        {
            monitor = MonitorFromWindow(handle, 2);
            var size = LogicalWorkSize(display.WorkArea, display.Scale);
            window.MinWidth = Math.Min(minimumWidth, size.Width);
            window.MinHeight = Math.Min(minimumHeight, size.Height);
            // 通常窓はモニターをまたげる。画面内への補正は移動終了後のFitで行う。
            window.MaxWidth = maximumWidth;
            window.MaxHeight = maximumHeight;
            if (content != null)
            {
                content.Margin = contentMargin;
                // 標準枠の最大化では外側の非表示枠が画面を越える。内容だけ内側へ収める。
                if (window.WindowState == WindowState.Maximized && GetWindowRect(handle, out var maximized))
                {
                    var area = display.WorkArea;
                    content.Margin = new Thickness(
                        contentMargin.Left + Math.Max(0, area.Left - maximized.Left) / display.Scale,
                        contentMargin.Top + Math.Max(0, area.Top - maximized.Top) / display.Scale,
                        contentMargin.Right + Math.Max(0, maximized.Right - area.Right) / display.Scale,
                        contentMargin.Bottom + Math.Max(0, maximized.Bottom - area.Bottom) / display.Scale);
                }
            }
            // 移動中は画面の境界で引き戻さず、手を離してから全体を収める。
            if (window.WindowState == WindowState.Normal && GetWindowRect(handle, out var native))
            {
                var bounds = native.ToRect();
                var fitted = Fit(bounds, display.WorkArea);
                if (bounds != fitted)
                    SetWindowPos(handle, IntPtr.Zero, (int)fitted.X, (int)fitted.Y,
                        (int)fitted.Width, (int)fitted.Height, 0x0014); // NOZORDER | NOACTIVATE
            }
        }
        finally { updating = false; }
    }

    internal static Size LogicalWorkSize(Rect workArea, double scale) => new(workArea.Width / scale, workArea.Height / scale);

    internal static Rect Fit(Rect bounds, Rect workArea)
    {
        double width = Math.Min(bounds.Width, workArea.Width), height = Math.Min(bounds.Height, workArea.Height);
        return new Rect(Math.Clamp(bounds.X, workArea.Left, workArea.Right - width),
            Math.Clamp(bounds.Y, workArea.Top, workArea.Bottom - height), width, height);
    }

    internal static (Rect WorkArea, double Scale) ReadDisplay(IntPtr hwnd)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return (Rect.Empty, 1);
        return (info.Work.ToRect(), Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
