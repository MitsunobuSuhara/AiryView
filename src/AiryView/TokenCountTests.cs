namespace AiryView;

internal static class TokenCountTests
{
    internal static async Task RunAsync()
    {
        var results = new List<string>();
        void Check(bool passed, string label)
        {
            if (!passed) throw new Exception("FAIL: " + label);
            results.Add("PASS: " + label);
        }
        // OpenAI Cookbookのo200k_base例と同じ入力・期待値で照合する。
        foreach (var (text, count) in new[] { ("", 0), ("Hello, world!", 4), ("antidisestablishmentarianism", 6), ("2 + 2 = 4", 7), ("お誕生日おめでとう", 8) })
            Check(DocumentTokenCounter.Count(text) == count, "参照値に一致: " + (text.Length == 0 ? "空文字" : text));
        string folder = System.IO.Path.GetFullPath("artifacts/token-tests"); Directory.CreateDirectory(folder);
        string source = "# 見出し\n\nHello, world!\n\nお誕生日おめでとう\n";
        string path = System.IO.Path.Combine(folder, "sample.md"); File.WriteAllText(path, source);
        var main = new MainWindow(); main.Show();
        await main.OpenPathsAsync([path]);
        var info = (TextBlock)main.FindName("TextSelectionInfo");
        var badge = (Border)main.FindName("TextSelectionBadge");
        var editor = (TextBox)main.FindName("TextEditor");
        async Task WaitCount()
        {
            for (int i = 0; i < 500 && info.Text.Contains("計数中"); ++i) await Task.Delay(20);
            main.UpdateLayout(); Check(!info.Text.Contains("計数中") && !info.Text.Contains("計数できません"), "非同期計数が完了");
        }
        await WaitCount();
        Check(info.Text.Contains("プレビュー") && info.Text.Contains("トークン数 " + DocumentTokenCounter.Count(source).ToString("N0")), "MDプレビューで記号を含む全体を計数");
        Check(info.Text.Contains(DocumentTokenCounter.Basis), "OpenAI・o200k_base基準を常時明示");
        Check(!info.Text.Contains("API") && badge.ToolTip?.ToString() == DocumentTokenCounter.Explanation, "詳細説明は情報欄のツールチップだけに設定");
        await main.ToggleMarkdownModeAsync(); await WaitCount();
        Check(info.Text.Contains("ソース") && info.Text.Contains("トークン数 " + DocumentTokenCounter.Count(source).ToString("N0")), "ソース編集でも同じ全体数を表示");
        editor.Select(source.IndexOf("Hello", StringComparison.Ordinal), "Hello, world!".Length); await WaitCount();
        Check(info.Text.Contains("トークン数 選択 4 / 全体 "), "選択範囲は単独の文字列として計数");
        editor.Select(0, 0); editor.Text = "お誕生日おめでとう"; await WaitCount();
        Check(info.Text.Contains("トークン数 8（"), "編集後の全文数を更新");
        editor.Text = "Hello, world!"; editor.Text = "2 + 2 = 4"; await WaitCount();
        Check(info.Text.Contains("トークン数 7（"), "続けて編集しても古い数値を表示しない");
        Check(main.SaveTextToPathForTest(path), "検証用MDを保存");
        await main.ToggleMarkdownModeAsync(); await WaitCount();
        Check(info.Text.Contains("プレビュー") && info.Text.Contains("トークン数 7（"), "再びプレビューへ戻っても数値を維持");
        main.WindowState = WindowState.Normal; main.Width = 860; main.Height = 560; main.UpdateLayout();
        Check(badge.TranslatePoint(new Point(0, 0), main).X >= 0 && info.ActualWidth < main.ActualWidth, "最小幅でも情報欄を画面内に収める");
        Capture(main, System.IO.Path.Combine(folder, "preview.png"));
        foreach (string extension in new[] { ".txt", ".json", ".csv" })
        {
            string dataPath = System.IO.Path.Combine(folder, "sample" + extension); File.WriteAllText(dataPath, "Hello, world!");
            await main.OpenPathsAsync([dataPath]); await WaitCount();
            Check(info.Text.Contains("トークン数 4（"), extension + "でもトークン数を表示");
        }
        Capture(main, System.IO.Path.Combine(folder, "editor.png"));
        main.Close();
        var reopened = new MainWindow(); reopened.Show(); await reopened.OpenPathsAsync([path]);
        var reInfo = (TextBlock)reopened.FindName("TextSelectionInfo");
        for (int i = 0; i < 500 && reInfo.Text.Contains("計数中"); ++i) await Task.Delay(20);
        Check(reInfo.Text.Contains("トークン数 7（"), "保存後のアプリ再起動でも正しい数を表示"); reopened.Close();
        File.WriteAllLines("artifacts/token-test-results.txt", results);
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
