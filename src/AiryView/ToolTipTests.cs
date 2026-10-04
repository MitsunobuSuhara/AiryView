using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AiryView;

internal static class ToolTipTests
{
    internal static async Task RunAsync()
    {
        var results = new List<string>();
        void Check(bool passed, string name)
        {
            if (!passed) throw new Exception("FAIL: " + name);
            results.Add("PASS: " + name);
        }
        var window = new Window { Width = 320, Height = 220, Title = "ツールチップの回帰検証" };
        var button = new Button { Content = "＋" }; window.Content = button;
        var guard = new ToolTipGuard(window);
        window.Show(); window.Activate(); await Task.Delay(100);
        var tip = new ToolTip { Content = "拡大", PlacementTarget = button };
        button.ToolTip = tip;
        Check(guard.CanShow, "通常画面では説明を表示できる");
        tip.IsOpen = true; await Task.Delay(50);
        Check(tip.IsOpen, "通常画面の拡大ボタンの説明を表示");
        guard.RunModal(() =>
        {
            Check(!tip.IsOpen, "ダイアログを開く前に既存の説明を閉じる");
            Check(!guard.CanShow, "ダイアログ中の説明表示を抑止");
            tip.IsOpen = true;
            Check(!tip.IsOpen, "ダイアログ中に遅れて開く説明も閉じる");
            guard.RunModal(() => { Check(!guard.CanShow, "入れ子のダイアログも抑止"); return true; });
            Check(!guard.CanShow, "内側のダイアログ終了後も抑止を維持");
            return false;
        });
        Check(guard.CanShow, "キャンセル後に説明表示を復帰");
        bool nativeDialogSeen = false, nativeTipBlocked = false, nativeExistingClosed = false;
        var ownerHandle = new WindowInteropHelper(window).Handle;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
            {
                var className = new StringBuilder(256); GetClassName(handle, className, className.Capacity);
                if (GetWindow(handle, 4) != ownerHandle || className.ToString() != "#32770") return true;
                nativeDialogSeen = true;
                nativeExistingClosed = !tip.IsOpen;
                tip.IsOpen = true;
                nativeTipBlocked = !tip.IsOpen;
                timer.Stop(); PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                return false;
            }, IntPtr.Zero);
        };
        tip.IsOpen = true;
        timer.Start();
        var dialogResult = guard.RunModal(() => new Microsoft.Win32.OpenFileDialog { Filter = TextFileFormats.OpenFilter }.ShowDialog(window));
        timer.Stop();
        Check(nativeDialogSeen, "実際のWindowsの開くダイアログを表示");
        Check(nativeExistingClosed && nativeTipBlocked, "実際の開くダイアログに説明が重ならない");
        Check(dialogResult != true && guard.CanShow, "実際の開くダイアログを閉じた後に説明表示を復帰");
        try { guard.RunModal<bool>(() => throw new IOException("検証用")); } catch (IOException) { }
        Check(guard.CanShow, "ダイアログ例外後も説明表示を復帰");
        tip.IsOpen = true; await Task.Delay(50);
        Check(tip.IsOpen, "復帰後に説明を表示できる");
        var other = new Window { Owner = window, Width = 240, Height = 160, Title = "別画面" };
        other.Show(); other.Activate(); await Task.Delay(100);
        Check(!tip.IsOpen && !guard.CanShow, "画面が非アクティブになったら説明を閉じる");
        tip.IsOpen = true;
        Check(!tip.IsOpen, "非アクティブ画面の遅れた説明表示を抑止");
        other.Close(); window.Activate(); await Task.Delay(100);
        window.IsEnabled = false;
        Check(!guard.CanShow, "無効化された画面の説明表示を抑止");
        window.IsEnabled = true; window.Close();
        var main = new MainWindow(); main.Show(); main.Activate(); await Task.Delay(100);
        var zoom = ((WrapPanel)main.FindName("DocumentToolbar")).Children.OfType<Button>().First(b => b.ToolTip as string == "拡大");
        tip = new ToolTip { Content = "拡大", PlacementTarget = zoom }; zoom.ToolTip = tip;
        ownerHandle = new WindowInteropHelper(main).Handle;
        nativeDialogSeen = nativeExistingClosed = nativeTipBlocked = false;
        tip.IsOpen = true; timer.Start();
        typeof(MainWindow).GetMethod("OpenClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(main, [main, new RoutedEventArgs()]);
        timer.Stop();
        Check(nativeDialogSeen && nativeExistingClosed && nativeTipBlocked, "メイン画面の開く処理でも拡大の説明がダイアログに重ならない");
        main.Activate(); await Task.Delay(100); tip.IsOpen = true;
        Check(tip.IsOpen, "メイン画面で開くをキャンセルした後の説明表示を復帰");
        tip.IsOpen = false; main.Close();
        Directory.CreateDirectory("artifacts");
        File.WriteAllLines("artifacts/tooltip-test-results.txt", results);
    }

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
