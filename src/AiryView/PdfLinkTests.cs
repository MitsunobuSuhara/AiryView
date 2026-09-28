using System.Text;
using System.Windows.Threading;

namespace AiryView;

internal static class PdfLinkTests
{
    internal static async Task RunAsync(string? sample)
    {
        Directory.CreateDirectory("artifacts");
        var results = new List<string>();
        void Check(bool value, string label) { if (!value) throw new Exception(label); results.Add("PASS: " + label); }
        string path = System.IO.Path.GetFullPath("artifacts/link-test.pdf"); CreateFixture(path);
        using (var pdf = new PdfDocument(path))
        {
            for (int turn = 0; turn < 4; turn++)
            {
                for (int row = 0; row < 5; row++)
                {
                    Point point = new(.2, (100 + row * 60) / 600.0);
                    for (int r = 0; r < turn; r++) point = new(1 - point.Y, point.X);
                    Check(pdf.InternalLinkPageAt(0, point) == (row < 3 ? 1 : -1), $"crop and rotation {turn * 90}, link type {row}");
                }
                pdf.Rotate(0, 1);
            }
            Check(pdf.InternalLinkPageAt(0, new(.01, .01)) == -1, "blank area has no link");
            Check(pdf.InternalLinkPageAt(0, new(double.NaN, .5)) == -1, "invalid coordinates rejected");
        }
        var window = new MainWindow(showWelcome: false); window.Show();
        async Task Idle() { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
        int Page() => int.Parse(((TextBox)window.FindName("PageNumber")).Text);
        try
        {
            await window.ShowFilesAsync([path]); await Idle();
            window.PdfPointerForTest(0, new(.2, 1.0 / 6)); await Idle();
            Check(Page() == 2, "click navigates to internal destination");
            window.GoPageForTest(0); await Idle();
            window.PdfPointerForTest(0, new(.2, 1.0 / 6), new(.5, 1.0 / 6), new(.2, 1.0 / 6)); await Idle();
            Check(Page() == 1, "drag away and back never follows link");
            window.PdfPointerForTest(0, new(.2, 1.0 / 6), new Point(.5, 1.0 / 6)); await Idle();
            Check(Page() == 1 && window.CopySelectedPdfForTest(), "drag over linked text retains selection and copy");
            var viewer = (ScrollViewer)window.FindName("Viewer"); double before = viewer.VerticalOffset;
            window.ScrollPdfByWheel(-120); await Idle(); Check(viewer.VerticalOffset > before, "ordinary wheel still scrolls");
            if (!string.IsNullOrEmpty(sample))
            {
                using var pdf = new PdfDocument(sample);
                int[] targets = [1, 3, 5, 6, 8, 9, 11, 12, 14, 18, 20, 21, 22];
                double[] centers = [264.1323,294.8823,324.8823,354.8823,385.6323,415.6323,445.6323,475.6323,506.3823,536.3823,566.3823,596.3823,627.1323];
                for (int i = 0; i < targets.Length; i++)
                    Check(pdf.InternalLinkPageAt(0, new(100 / 594.95996, centers[i] / 841.91998)) == targets[i] && pdf.InternalLinkPageAt(0, new(530 / 594.95996, centers[i] / 841.91998)) == targets[i], $"manual chapter {i + 1}: title and page number target {targets[i] + 1}");
                await window.ShowFilesAsync([sample]); await Idle();
                foreach (int chapter in new[] { 4, 10 }) foreach (double x in new[] { 100.0, 530.0 })
                {
                    window.GoPageForTest(0); await Idle();
                    window.PdfPointerForTest(0, new(x / 594.95996, centers[chapter] / 841.91998)); await Idle();
                    Check(Page() == targets[chapter] + 1, $"manual click chapter {chapter + 1} at x={x} goes to {targets[chapter] + 1}");
                }
            }
        }
        finally { window.Close(); }
        File.WriteAllLines("artifacts/pdf-link-test-results.txt", results);
    }
    internal static void CreateFixture(string path)
    {
        string content = "BT /F1 14 Tf 70 527 Td (Go to chapter two) Tj ET";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests << /Names [(chapter) [6 0 R /Fit]] >> >> >>",
            "<< /Type /Pages /Count 2 /Kids [4 0 R 6 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 440 660] /CropBox [20 30 420 630] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R /Annots [8 0 R 9 0 R 10 0 R 11 0 R 12 0 R] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Resources << >> /Contents 7 0 R >>",
            "<< /Length 0 >>\nstream\nendstream"
        };
        string[] actions = ["/A << /S /GoTo /D [6 0 R /Fit] >>", "/Dest [6 0 R /Fit]", "/Dest (chapter)", "/A << /S /URI /URI (https://example.invalid/) >>", "/A << /S /GoTo /D [99 /Fit] >>"];
        for (int i = 0; i < actions.Length; i++) objects.Add($"<< /Type /Annot /Subtype /Link /Rect [60 {510 - i * 60} 380 {550 - i * 60}] {actions[i]} >>");
        using var stream = File.Create(path); var offsets = new List<long>();
        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.7\n");
        for (int i = 0; i < objects.Count; i++) { offsets.Add(stream.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        long xref = stream.Position; Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
