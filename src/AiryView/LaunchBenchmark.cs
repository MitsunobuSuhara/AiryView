using System.Diagnostics;
namespace AiryView;
internal static class LaunchBenchmark
{
    internal static async Task RunAsync()
    {
        string? path = Environment.GetEnvironmentVariable("AIRYVIEW_BENCH_FILE");
        var window = new MainWindow(showWelcome: string.IsNullOrEmpty(path)) { Width = 1180, Height = 840, WindowState = WindowState.Normal };
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler rendered = (_, _) => { if (window.IsVisible) frame.TrySetResult(); };
        CompositionTarget.Rendering += rendered;
        try
        {
            if (string.IsNullOrEmpty(path)) window.Show(); else await window.ShowFilesAsync([path]);
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var process = Process.GetCurrentProcess();
            double elapsed = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
            File.AppendAllText("artifacts/launch-benchmark.csv", FormattableString.Invariant($"{(string.IsNullOrEmpty(path) ? "standalone" : System.IO.Path.GetExtension(path))},{elapsed:F2},{process.WorkingSet64}\n"));
        }
        finally { CompositionTarget.Rendering -= rendered; window.Close(); }
    }
}
