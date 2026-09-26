using System.Threading;
using System.Windows.Shell;

namespace AiryView;

// Explorerの応答待ちが、ファイルの初回描画を止めないようにする。
internal sealed class RecentFileHistory(Action<string> register)
{
    internal static readonly RecentFileHistory Shared = new(RegisterWithWindows);
    private static bool initialized;
    private readonly object gate = new();
    private readonly Queue<string> pending = new();
    private bool running;

    internal void Enqueue(string path)
    {
        lock (gate)
        {
            // Shellが長時間応答しない場合も履歴待ちを際限なく保持しない。
            if (pending.Count == 64) pending.Dequeue();
            pending.Enqueue(path);
            if (running) return;
            var worker = new Thread(Drain) { IsBackground = true, Name = "AiryView recent files" };
            worker.SetApartmentState(ApartmentState.STA);
            running = true;
            try { worker.Start(); }
            catch { running = false; throw; }
        }
    }

    private void Drain()
    {
        while (true)
        {
            string path;
            lock (gate)
            {
                if (pending.Count == 0) { running = false; return; }
                path = pending.Dequeue();
            }
            try { register(path); }
            catch { /* 履歴の登録失敗で閲覧や後続の履歴登録を中断しない。 */ }
        }
    }

    private static void RegisterWithWindows(string path)
    {
        if (!initialized)
        {
            // ApplyはSTAが必要だが、ApplicationのUIスレッドへの取付けは不要。
            new JumpList { ShowRecentCategory = true, ShowFrequentCategory = false }.Apply();
            initialized = true;
        }
        JumpList.AddToRecentCategory(path);
    }
}
