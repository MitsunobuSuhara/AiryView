using System.Drawing.Printing;

namespace AiryView;

internal record PrintSheet(Sheet Layout, PageSettings Settings);

internal static class PrintPaperPlan
{
    internal static PaperSize? MatchPaper(IEnumerable<PaperSize> papers, Size source)
    {
        double shorter = Math.Min(source.Width, source.Height), longer = Math.Max(source.Width, source.Height);
        return papers.Select(p => (Paper: p,
                ShortError: Math.Abs(Math.Min(p.Width, p.Height) * .254 - shorter),
                LongError: Math.Abs(Math.Max(p.Width, p.Height) * .254 - longer)))
            .Where(p => p.ShortError < 2 && p.LongError < 2)
            .OrderBy(p => p.ShortError + p.LongError).Select(p => p.Paper).FirstOrDefault();
    }

    internal static List<PrintSheet> Build(PrintDocument printer, Func<int, Size> sourceSize, int[] pages,
        PrintOptions options, bool matchOriginal, Rect? selection = null)
    {
        if (!matchOriginal)
        {
            var geometry = PrinterOutput.GetGeometry(printer);
            return PrintLayout.Build(sourceSize, pages, geometry.Paper, geometry.Printable, options, selection)
                .Select(s => new PrintSheet(s, (PageSettings)printer.DefaultPageSettings.Clone())).ToList();
        }
        if (options.Mode is not (PrintMode.Scale or PrintMode.Fit))
            throw new ArgumentException("原本の用紙に合わせる設定は、原寸・指定倍率または用紙に合わせる印刷で使えます。");
        var supported = printer.PrinterSettings.PaperSizes.Cast<PaperSize>().ToArray();
        var output = new List<PrintSheet>();
        var geometries = new Dictionary<(int Kind, int Width, int Height, bool Landscape), (Size Paper, Rect Printable)>();
        foreach (int page in pages)
        {
            Size source = sourceSize(page);
            var paper = MatchPaper(supported, source) ?? throw new ArgumentException(
                $"PDF {page + 1}ページ（{source.Width:F1} × {source.Height:F1} mm）に合う用紙が、このプリンターにはありません。\n「ページごとに原本の用紙・向きに合わせる」をオフにして、出力する用紙を選べます。");
            var settings = (PageSettings)printer.DefaultPageSettings.Clone();
            settings.PaperSize = paper;
            settings.Landscape = (source.Width > source.Height) != (paper.Width > paper.Height);
            var key = (paper.RawKind, paper.Width, paper.Height, settings.Landscape);
            if (!geometries.TryGetValue(key, out var geometry))
            {
                geometry = PrinterOutput.GetGeometry(printer, settings);
                double expectedWidth = (settings.Landscape ? paper.Height : paper.Width) * .254;
                double expectedHeight = (settings.Landscape ? paper.Width : paper.Height) * .254;
                if (Math.Abs(geometry.Paper.Width - expectedWidth) > 2 || Math.Abs(geometry.Paper.Height - expectedHeight) > 2)
                    throw new IOException($"プリンターがPDF {page + 1}ページの用紙・向きに対応できません。詳細設定の用紙を確認してください。");
                geometries.Add(key, geometry);
            }
            var sheet = PrintLayout.Build(sourceSize, [page], geometry.Paper, geometry.Printable, options, selection)[0];
            output.Add(new(sheet with { Label = $"PDF {page + 1}ページ" }, settings));
        }
        if (output.Count == 0) throw new ArgumentException("印刷するページがありません。");
        return printer.PrinterSettings.Duplex is Duplex.Vertical or Duplex.Horizontal ? PadDuplex(output) : output;
    }

    internal static List<PrintSheet> PadDuplex(IReadOnlyList<PrintSheet> input)
    {
        var output = new List<PrintSheet>();
        foreach (var current in input)
        {
            // 異なる用紙・向きを同じ紙の表裏へ割り当てない。
            if (output.Count % 2 == 1 && output[^1].Layout.Paper != current.Layout.Paper)
            {
                var front = output[^1];
                output.Add(new(new Sheet(front.Layout.Paper, front.Layout.Printable, [], "用紙・向き変更のため裏面は空白"),
                    (PageSettings)front.Settings.Clone()));
            }
            output.Add(current);
        }
        return output;
    }

    internal static string PaperLabel(PrintSheet sheet)
    {
        string name = sheet.Settings.PaperSize.Kind == PaperKind.Custom
            ? sheet.Settings.PaperSize.PaperName : sheet.Settings.PaperSize.Kind.ToString();
        return $"{name} {(sheet.Layout.Paper.Width > sheet.Layout.Paper.Height ? "横" : "縦")}";
    }

    internal static bool Print(PdfDocument document, PrintDocument printer, IReadOnlyList<PrintSheet> plan)
    {
        int index = 0;
        bool completed = false;
        void Configure(object? sender, QueryPageSettingsEventArgs args)
        {
            if (index >= plan.Count) { args.Cancel = true; return; }
            // PrintPageより前にドライバーへ渡し、プレビューと同じ用紙を使う。
            args.PageSettings = (PageSettings)plan[index].Settings.Clone();
        }
        void Draw(object? sender, PrintPageEventArgs args)
        {
            PrinterOutput.Draw(document, plan[index++].Layout, args);
            args.HasMorePages = index < plan.Count;
        }
        void End(object? sender, PrintEventArgs args) => completed = !args.Cancel && index == plan.Count;
        printer.QueryPageSettings += Configure; printer.PrintPage += Draw; printer.EndPrint += End;
        try { printer.Print(); return completed; }
        finally { printer.QueryPageSettings -= Configure; printer.PrintPage -= Draw; printer.EndPrint -= End; }
    }
}
