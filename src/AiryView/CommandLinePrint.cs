using System.Drawing.Printing;

namespace AiryView;

// 外部帳票ソフトからもプレビューと同じ用紙計画・描画経路を使う。
internal static class CommandLinePrint
{
    internal static int Run(string[] args)
    {
        string? resultPath = null;
        try
        {
            var options = new Dictionary<string, string>();
            if (args.Length % 2 != 0) throw new ArgumentException("印刷オプションには値が必要です。");
            for (int i = 0; i < args.Length; i += 2)
            {
                if (args[i] is not ("--print" or "--result" or "--printer" or "--output")
                    || !options.TryAdd(args[i], args[i + 1]))
                    throw new ArgumentException("不明または重複した印刷オプションです。");
            }
            string input = System.IO.Path.GetFullPath(options["--print"]);
            string? candidate = options.GetValueOrDefault("--result");
            if (candidate != null && string.Equals(input, System.IO.Path.GetFullPath(candidate), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("入力PDFを結果ファイルで上書きできません。");
            if (candidate != null && options.TryGetValue("--output", out string? candidateOutput)
                && string.Equals(System.IO.Path.GetFullPath(candidate), System.IO.Path.GetFullPath(candidateOutput), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("出力PDFと結果ファイルは別にしてください。");
            resultPath = candidate;
            using var document = new PdfDocument(input);
            if (!document.CanPrint) throw new UnauthorizedAccessException("このPDFは印刷が許可されていません。");
            using var printer = new PrintDocument();
            if (options.TryGetValue("--printer", out string? name)) printer.PrinterSettings.PrinterName = name;
            if (!printer.PrinterSettings.IsValid) throw new IOException("使用できるプリンターがありません。");
            printer.PrintController = new StandardPrintController();
            printer.DocumentName = System.IO.Path.GetFileName(input);
            printer.OriginAtMargins = false;
            printer.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            printer.PrinterSettings.Copies = 1;
            printer.PrinterSettings.Duplex = Duplex.Simplex;
            printer.DefaultPageSettings.Color = true;
            if (options.TryGetValue("--output", out string? output))
            {
                if (printer.PrinterSettings.PrinterName != "Microsoft Print to PDF")
                    throw new ArgumentException("ファイル出力にはMicrosoft Print to PDFを指定してください。");
                output = System.IO.Path.GetFullPath(output);
                if (File.Exists(output)) throw new IOException("出力先が既に存在します。");
                if (resultPath != null && string.Equals(output, System.IO.Path.GetFullPath(resultPath), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("出力PDFと結果ファイルは別にしてください。");
                printer.PrinterSettings.PrintToFile = true;
                printer.PrinterSettings.PrintFileName = output;
            }
            var plan = PrintPaperPlan.Build(printer, document.SizeMm,
                Enumerable.Range(0, document.Count).ToArray(), new(PrintMode.Scale), true);
            if (!PrintPaperPlan.Print(document, printer, plan)) throw new IOException("印刷が中止されました。");
            WriteResult(resultPath, new { success = true, pages = document.Count,
                printer = printer.PrinterSettings.PrinterName });
            return 0;
        }
        catch (Exception ex)
        {
            try { WriteResult(resultPath, new { success = false, error = ex.Message }); } catch { }
            return 1;
        }
    }

    private static void WriteResult(string? path, object value)
    {
        if (path != null) File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(value));
    }
}
