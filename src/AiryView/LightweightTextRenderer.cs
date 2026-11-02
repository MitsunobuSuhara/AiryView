using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Documents;

namespace AiryView;

public static class LightweightTextRenderer
{
   public static FlowDocument Build(string markdown)
    {
        var doc = NewDocument();
        var paragraph = new List<string>(); var list = new List();
        void FlushParagraph() { if (paragraph.Count == 0) return; var p = new Paragraph { Margin = new Thickness(0, 0, 0, 14) }; AddInline(p.Inlines, string.Join(" ", paragraph)); doc.Blocks.Add(p); paragraph.Clear(); }
        void FlushList() { if (list.ListItems.Count == 0) return; list.MarkerStyle = TextMarkerStyle.Disc; list.Margin = new Thickness(20, 0, 0, 14); doc.Blocks.Add(list); list = new List(); }
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); FlushList(); continue; }
            var heading = Regex.Match(line, "^(#{1,3})\\s+(.+)$");
            if (heading.Success) { FlushParagraph(); FlushList(); int level = heading.Groups[1].Value.Length; var p = new Paragraph { FontWeight = FontWeights.Bold, FontSize = level == 1 ? 30 : level == 2 ? 23 : 18, Margin = new Thickness(0, level == 1 ? 8 : 22, 0, 12), KeepWithNext = true }; AddInline(p.Inlines, heading.Groups[2].Value); doc.Blocks.Add(p); continue; }
            if (Regex.IsMatch(line, "^(-{3,}|\\*{3,})$")) { FlushParagraph(); FlushList(); doc.Blocks.Add(new BlockUIContainer(new Separator()) { Margin = new Thickness(0, 12, 0, 18) }); continue; }
            var bullet = Regex.Match(line, "^[-*]\\s+(.+)$");
            if (bullet.Success) { FlushParagraph(); var p = new Paragraph(); AddInline(p.Inlines, bullet.Groups[1].Value); list.ListItems.Add(new ListItem(p)); continue; }
            if (line.StartsWith("> ")) { FlushParagraph(); FlushList(); var p = new Paragraph { Margin = new Thickness(18, 4, 10, 16), Padding = new Thickness(16, 10, 12, 10) }; p.SetResourceReference(TextElement.BackgroundProperty, "AiryQuoteBackground"); AddInline(p.Inlines, line[2..]); doc.Blocks.Add(p); continue; }
            paragraph.Add(line);
        }
        FlushParagraph(); FlushList(); return doc;
    }
    public static FlowDocument BuildPlain(string text)
    {
        var doc = NewDocument();
        var paragraph = new Paragraph { FontFamily = new FontFamily("Cascadia Mono, Consolas, Yu Gothic UI"), Margin = new Thickness(0) };
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++) { paragraph.Inlines.Add(new Run(lines[i])); if (i + 1 < lines.Length) paragraph.Inlines.Add(new LineBreak()); }
        doc.Blocks.Add(paragraph); return doc;
    }
    private static FlowDocument NewDocument() => new()
    {
        PagePadding = new Thickness(54, 42, 54, 60), FontFamily = new FontFamily("Yu Gothic UI"), FontSize = 16,
        Foreground = new SolidColorBrush(Color.FromRgb(32, 38, 46)), LineHeight = 27, ColumnWidth = 760
    };
    private static void AddInline(InlineCollection target, string text)
    {
        int at = 0;
        foreach (Match match in Regex.Matches(text, "\\*\\*(.+?)\\*\\*")) { if (match.Index > at) target.Add(new Run(WebUtility.HtmlDecode(text[at..match.Index]))); target.Add(new Bold(new Run(WebUtility.HtmlDecode(match.Groups[1].Value)))); at = match.Index + match.Length; }
        if (at < text.Length) target.Add(new Run(WebUtility.HtmlDecode(text[at..])));
    }
}
