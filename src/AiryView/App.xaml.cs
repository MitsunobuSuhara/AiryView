using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
namespace AiryView;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = "Local\\AiryView.SingleInstance.v1";
    private const string InstancePipeName = "AiryView.OpenFiles.v1";
    private Mutex? instanceMutex;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    private const int AllowAnyProcess = -1;
    private const int RestoreWindow = 9;
    private const int ShowWindowWithoutResize = 5;
    private CancellationTokenSource? pipeCancellation;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--mixed-paper-test") || e.Args.Contains("--startup-test") || e.Args.Contains("--media-benchmark") || e.Args.Contains("--open-benchmark") || e.Args.Contains("--self-test") || e.Args.Contains("--ui-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AiryView.MainWindow.SuppressRecentFilesForTest = true;
            if (File.Exists("artifacts/test-failure.txt")) File.Delete("artifacts/test-failure.txt");
            try { if (e.Args.Contains("--mixed-paper-test")) await SelfTest.RunMixedPaperAsync(); else if (e.Args.Contains("--startup-test")) await SelfTest.RunStartupAsync(); else if (e.Args.Contains("--media-benchmark")) await SelfTest.RunMediaBenchmarkAsync(); else if (e.Args.Contains("--open-benchmark")) await SelfTest.RunOpenBenchmarkAsync(); else if (e.Args.Contains("--ui-test")) await SelfTest.RunUiAsync(); else SelfTest.Run(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/test-failure.txt", ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--unregister"))
        {
            if (MessageBox.Show("AiryViewのアプリ登録とショートカットを解除しますか？\n実行ファイルと設定は復旧用に残ります。", "AiryView 登録解除", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                DesktopInstaller.Unregister();
            Shutdown(0); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--make-check")
        {
            CalibrationPdf.Create(e.Args[1]); Shutdown(0); return;
        }
        if (e.Args.Contains("--install") || e.Args.Contains("--install-quiet"))
        {
            if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                try
                {
                    string exe = DesktopInstaller.ResolveShellPath(Environment.ProcessPath!);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", Arguments = e.Args.Contains("--install-quiet") ? "--install-quiet" : "--install", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
                    Shutdown(0);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "管理者確認が完了しませんでした"); Shutdown(1); }
                return;
            }
            string logFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiryView");
            Directory.CreateDirectory(logFolder);
            try
            {
                string installed = DesktopInstaller.Install();
                File.WriteAllText(System.IO.Path.Combine(logFolder, "install-result.txt"), installed);
                if (!e.Args.Contains("--install-quiet")) MessageBox.Show("スタートメニューに AiryView を登録しました。", "AiryView");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(System.IO.Path.Combine(logFolder, "install-error.txt"), ex.ToString());
                if (!e.Args.Contains("--install-quiet")) MessageBox.Show(ex.Message, "登録できませんでした");
                Shutdown(1);
            }
            return;
        }
        string[] files = e.Args.Where(File.Exists).Select(System.IO.Path.GetFullPath).ToArray();
        instanceMutex = new Mutex(true, InstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            AllowSetForegroundWindow(AllowAnyProcess);
            if (!await ForwardFilesAsync(files)) MessageBox.Show("起動中のAiryViewへファイルを渡せませんでした。少し待ってから開き直してください。", "AiryView");
            instanceMutex.Dispose(); instanceMutex = null; Shutdown(0); return;
        }
        var window = new MainWindow(showWelcome: files.Length == 0);
        MainWindow = window;
        pipeCancellation = new CancellationTokenSource();
        _ = StartFileListener(window, pipeCancellation.Token);
        window.Closed += (_, _) => { pipeCancellation.Cancel(); instanceMutex?.ReleaseMutex(); instanceMutex?.Dispose(); instanceMutex = null; };
        if (files.Length > 0) await window.ShowFilesAsync(files);
        else window.Show();
    }
    private static async Task<bool> ForwardFilesAsync(string[] files)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", InstancePipeName, System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(150);
                using var writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
                writer.Write(files.Length);
                foreach (string file in files) writer.Write(file);
                writer.Flush(); await pipe.FlushAsync(); return true;
            }
            catch (TimeoutException) { await Task.Delay(100); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
        return false;
    }

    internal static void BringWindowToFront(Window window)
    {
        bool minimized = window.WindowState == WindowState.Minimized;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        // 最小化直前の最大化状態はWindows自身に復元させる。
        if (handle != IntPtr.Zero) ShowWindow(handle, minimized ? RestoreWindow : ShowWindowWithoutResize);
        else if (minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        window.Focus();
    }
    internal static Task StartFileListener(MainWindow window, CancellationToken cancellationToken, string pipeName = InstancePipeName) =>
        Task.Run(() => ListenForFilesAsync(window, cancellationToken, pipeName));

    private static async Task ListenForFilesAsync(MainWindow window, CancellationToken cancellationToken, string pipeName)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.In, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                // 接続元の送信待ちをUIで行わない。途中で途切れた送信も解放する。
                using var receiveDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var abortRead = receiveDeadline.Token.Register(() => pipe.Dispose());
                using var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
                int count = Math.Clamp(reader.ReadInt32(), 0, 1000);
                string[] files = Enumerable.Range(0, count).Select(_ => reader.ReadString()).Where(File.Exists).ToArray();
                if (cancellationToken.IsCancellationRequested) break;
                await window.Dispatcher.InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    if (files.Length > 0) window.PrepareFileOpen();
                    BringWindowToFront(window);
                    if (files.Length > 0) window.OpenPaths(files);
                });
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
