using System.Text;

namespace AiryView;

internal static class TextFileTests
{
    internal static async Task RunAsync()
    {
        string folder = System.IO.Path.GetFullPath("artifacts/text-file-tests");
        Directory.CreateDirectory(folder);
        var results = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL: " + label);
            results.Add("PASS: " + label);
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (var fixture in new[]
        {
            (Name: "utf8.JSON", Encoding: (Encoding)new UTF8Encoding(false), Text: "{\n  \"名前\": \"日本語\",\n  \"値\": 0\n}\n"),
            (Name: "bom.json", Encoding: (Encoding)new UTF8Encoding(true), Text: "{\"名前\":\"日本語\"}\r\n"),
            (Name: "utf16.json", Encoding: (Encoding)new UnicodeEncoding(false, true), Text: "{\"値\":1}\r\n"),
            (Name: "utf8.csv", Encoding: (Encoding)new UTF8Encoding(false), Text: "名前,備考\n日本語,\"カンマ,引用\"\n\"複数\n行\",\"引用\"\"符\"\n"),
            (Name: "bom.CSV", Encoding: (Encoding)new UTF8Encoding(true), Text: "名前,値\r\n日本語,0\r\n"),
            (Name: "shift-jis.csv", Encoding: Encoding.GetEncoding(932), Text: "名前,値\r\n日本語,0\r\n")
        })
        {
            string path = System.IO.Path.Combine(folder, fixture.Name);
            File.WriteAllText(path, fixture.Text, fixture.Encoding);
            byte[] original = File.ReadAllBytes(path);
            var window = new MainWindow(); window.Show();
            await window.OpenPathsAsync([path]); window.UpdateLayout();
            var editor = (TextBox)window.FindName("TextEditor");
            Check(editor.IsVisible && !editor.IsReadOnly && editor.Text == fixture.Text, fixture.Name + "を文字編集画面で開く");
            Check(((Button)window.FindName("MarkdownModeButton")).Visibility == Visibility.Collapsed, fixture.Name + "をMarkdownとして扱わない");
            Check(window.SaveTextForTest() && File.ReadAllBytes(path).SequenceEqual(original), fixture.Name + "を保存して引用符・改行・BOM・文字コードを保持");
            Check(window.SearchTextForTest(fixture.Text.Contains("名前") ? "名前" : "値") == 1, fixture.Name + "で検索を利用できる");
            string edited = fixture.Text.Replace("日本語", "編集後").Replace(":1", ":2");
            editor.Text = edited;
            Check(window.SaveTextForTest(), fixture.Name + "の編集を上書き保存");
            Check(File.ReadAllBytes(path).SequenceEqual(fixture.Encoding.GetPreamble().Concat(fixture.Encoding.GetBytes(edited))), fixture.Name + "の編集後も文字コードを保持");
            var format = TextFileFormats.SaveFormat(path);
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            Check(format.Extension == extension && format.Filter.Contains("*" + extension), fixture.Name + "の別名保存の拡張子を維持");
            string copy = System.IO.Path.Combine(folder, "copy-" + fixture.Name);
            Check(window.SaveTextToPathForTest(copy) && File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(path)), fixture.Name + "を別名保存");
            window.Close();

            var reopened = new MainWindow(); reopened.Show();
            await reopened.OpenPathsAsync([copy]); reopened.UpdateLayout();
            var reopenedEditor = (TextBox)reopened.FindName("TextEditor");
            Check(reopenedEditor.Text == edited, fixture.Name + "をアプリの再起動後に再読込");
            File.AppendAllText(copy, " ", fixture.Encoding);
            Check(reopened.CurrentTextHasExternalChangeForTest(), fixture.Name + "の外部変更を検知");
            Capture(reopened, System.IO.Path.Combine(folder, fixture.Name + ".png"));
            reopened.Close();
        }
        var dropWindow = new MainWindow(); dropWindow.Show();
        foreach (string name in new[] { "utf8.JSON", "bom.CSV" })
        {
            string path = System.IO.Path.Combine(folder, name);
            int before = dropWindow.TabCountForTest;
            await dropWindow.OpenDroppedPathsAsync([path]);
            dropWindow.UpdateLayout();
            Check(dropWindow.TabCountForTest == before + 1 && ((TextBox)dropWindow.FindName("TextEditor")).IsVisible, name + "をドラッグ＆ドロップで開く");
        }
        dropWindow.Close();
        Check(TextFileFormats.OpenFilter.Contains("*.json") && TextFileFormats.OpenFilter.Contains("*.csv"), "開くダイアログでJSON・CSVを選べる");
        File.WriteAllLines("artifacts/text-file-test-results.txt", results);
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
