namespace AiryView;

// PDFの文字情報は文章ハイライトを使う時だけ取得し、同じドラッグ中は再利用する。
internal static class TextHighlightSelection
{
    internal static Rect[] Select(IReadOnlyList<PdfTextCharacter> characters, Size page, Point start, Point end)
    {
        Rect Box(PdfTextCharacter c) => c.RelativeBox.IsEmpty ? Rect.Empty : new Rect(c.RelativeBox.X * page.Width, c.RelativeBox.Y * page.Height, c.RelativeBox.Width * page.Width, c.RelativeBox.Height * page.Height);
        int At(Point point)
        {
            int found = -1; double best = 20 * 20;
            for (int i = 0; i < characters.Count; i++)
            {
                var box = Box(characters[i]); if (box.IsEmpty || char.IsWhiteSpace(characters[i].Character)) continue;
                double dx = Math.Max(0, Math.Max(box.Left - point.X, point.X - box.Right));
                double dy = Math.Max(0, Math.Max(box.Top - point.Y, point.Y - box.Bottom));
                double distance = dx * dx + dy * dy;
                if (distance < best) { best = distance; found = i; }
            }
            return found;
        }
        int first = At(start), last = At(end); if (first < 0 || last < 0) return [];
        if (first > last) (first, last) = (last, first);
        var rows = new List<Rect>();
        for (int i = first; i <= last && rows.Count < 2000; i++)
        {
            Rect box = Box(characters[i]); if (box.IsEmpty || box.Width <= 0 || box.Height <= 0 || char.IsWhiteSpace(characters[i].Character)) continue;
            box.Inflate(.8, 1);
            if (rows.Count > 0)
            {
                Rect row = rows[^1]; double height = Math.Max(row.Height, box.Height);
                bool sameLine = Math.Abs((row.Top + row.Bottom - box.Top - box.Bottom) / 2) < height * .5;
                bool adjacent = box.Left <= row.Right + height * 2 && box.Right >= row.Left - height * 2;
                if (sameLine && adjacent) { row.Union(box); rows[^1] = row; continue; }
            }
            rows.Add(box);
        }
        return rows.ToArray();
    }
}
