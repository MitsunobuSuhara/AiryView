using System.Globalization;

namespace AiryView;

internal sealed record TextSegment(string Text, Color Color, double Size, string FontId, bool Bold);
internal sealed record EditMark(string Kind, Point Start, Point End, string Text, Color Color, double Size, string FontId = "MS Gothic", bool Bold = false, Point[]? StrokePoints = null, Rect[]? HighlightBoxes = null, TextSegment[]? TextSegments = null);
internal sealed record EditFrame(BitmapSource? Image, double Width, double Height, EditMark[] Marks);

// 編集中だけ存在する履歴。閲覧用のBitmapSourceは変更しない。
internal sealed class VisualEditModel
{
    private readonly List<EditFrame> undo = [], redo = [];
    private EditFrame saved;
    internal EditFrame Frame { get; private set; }
    internal bool Dirty => !ReferenceEquals(Frame, saved);
    internal bool CanUndo => undo.Count > 0;
    internal bool CanRedo => redo.Count > 0;
    internal VisualEditModel(BitmapSource? image, double width, double height)
    { Frame = saved = new(image, width, height, []); }
    internal void MarkSaved() => saved = Frame;
    private void Change(EditFrame frame)
    {
        undo.Add(Frame); redo.Clear(); Frame = frame;
        // 大きな画像の回転・切抜きを繰り返しても履歴を無制限に保持しない。
        while (undo.Count > 30 || undo.Count > 1 && undo.Select(f => f.Image).Append(Frame.Image).Where(i => i != null).Distinct().Sum(i => (long)i!.PixelWidth * i.PixelHeight) > 32_000_000)
            undo.RemoveAt(0);
    }
    internal void Add(EditMark mark)
    {
        if (Frame.Marks.Length >= 500) throw new IOException("1ページの書き込みは500個までです。");
        if (!double.IsFinite(mark.Size) || mark.Size <= 0 || mark.Size > 1000) throw new IOException("文字・線の大きさを確認してください。");
        Change(Frame with { Marks = [.. Frame.Marks, mark] });
    }
    internal void Replace(int index, EditMark? mark)
    {
        if (index < 0 || index >= Frame.Marks.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (mark == Frame.Marks[index]) return;
        var marks = Frame.Marks.ToList();
        if (mark == null) marks.RemoveAt(index); else marks[index] = mark;
        Change(Frame with { Marks = marks.ToArray() });
    }
    internal void Undo() { if (CanUndo) { redo.Add(Frame); Frame = undo[^1]; undo.RemoveAt(undo.Count - 1); } }
    internal void Redo() { if (CanRedo) { undo.Add(Frame); Frame = redo[^1]; redo.RemoveAt(redo.Count - 1); } }
    internal static void ValidateDimensions(int width, int height)
    {
        if (width < 1 || height < 1 || width > 32768 || height > 32768 || (long)width * height > 32_000_000)
            throw new IOException("編集画像は縦横1～32768px、合計3200万画素以内にしてください。");
    }
    internal BitmapSource Render(bool white = false, int? targetWidth = null, int? targetHeight = null)
    {
        int width = targetWidth ?? (int)Math.Round(Frame.Width), height = targetHeight ?? (int)Math.Round(Frame.Height);
        ValidateDimensions(width, height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (white) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.PushTransform(new ScaleTransform(width / Frame.Width, height / Frame.Height));
            EditDrawing.Draw(dc, Frame.Image, Frame.Width, Frame.Height, Frame.Marks);
            dc.Pop();
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }
    internal void Rotate(int quarterTurns)
    {
        var source = Render();
        var rotated = new TransformedBitmap(source, new RotateTransform(quarterTurns * 90)); rotated.Freeze();
        Change(new(rotated, rotated.PixelWidth, rotated.PixelHeight, []));
    }
    internal void Crop(Rect rectangle)
    {
        rectangle.Intersect(new Rect(0, 0, Frame.Width, Frame.Height));
        if (rectangle.IsEmpty || rectangle.Width < 1 || rectangle.Height < 1) throw new IOException("切り抜く範囲をドラッグしてください。");
        int x = (int)Math.Floor(rectangle.Left), y = (int)Math.Floor(rectangle.Top);
        int right = Math.Min((int)Frame.Width, (int)Math.Ceiling(rectangle.Right));
        int bottom = Math.Min((int)Frame.Height, (int)Math.Ceiling(rectangle.Bottom));
        var cropped = new CroppedBitmap(Render(), new Int32Rect(x, y, right - x, bottom - y)); cropped.Freeze();
        Change(new(cropped, cropped.PixelWidth, cropped.PixelHeight, []));
    }
    internal void Resize(int width, int height)
    {
        var resized = Render(targetWidth: width, targetHeight: height);
        Change(new(resized, width, height, []));
    }
    internal void SaveImage(string path, string source)
    {
        if (string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            throw new IOException("元の画像とは別の名前を指定してください。");
        bool jpeg = System.IO.Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || System.IO.Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        if (!jpeg && !System.IO.Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new IOException("保存形式はPNGまたはJPEGを選んでください。");
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(white: jpeg)));
        string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, ".airy-edit-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) encoder.Save(stream);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        MarkSaved();
    }
}

internal static class EditDrawing
{
    internal static IEnumerable<(PathGeometry Geometry, Color Color)> TextShapes(EditMark mark)
    {
        if (mark.TextSegments is not { Length: > 0 })
        {
            var plain = new FormattedText(mark.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                EditorFonts.Typeface(mark.FontId, mark.Bold), mark.Size, Brushes.Black, 1) { LineHeight = mark.Size * 1.3 };
            var geometry = plain.BuildGeometry(mark.Start).GetFlattenedPathGeometry(.02, ToleranceType.Absolute);
            geometry.Freeze(); yield return (geometry, mark.Color);
            yield break;
        }
        double x = mark.Start.X, y = mark.Start.Y, lineHeight = mark.Size * 1.3;
        foreach (var segment in mark.TextSegments)
        {
            string[] lines = segment.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) { y += lineHeight; x = mark.Start.X; lineHeight = mark.Size * 1.3; }
                if (lines[i].Length == 0) continue;
                var text = new FormattedText(lines[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    EditorFonts.Typeface(segment.FontId, segment.Bold), segment.Size, Brushes.Black, 1);
                var geometry = text.BuildGeometry(new Point(x, y)).GetFlattenedPathGeometry(.02, ToleranceType.Absolute);
                geometry.Freeze(); yield return (geometry, segment.Color);
                x += text.WidthIncludingTrailingWhitespace;
                lineHeight = Math.Max(lineHeight, segment.Size * 1.3);
            }
        }
    }
    internal static Color Ink(EditMark mark) => mark.Kind.StartsWith("highlight", StringComparison.Ordinal) ? Color.FromArgb(96, mark.Color.R, mark.Color.G, mark.Color.B) : mark.Color;
    // 画面とPDFで同じ輪郭を使う。棒は矢じりの底で止め、先端へ重ねない。
    private static Geometry ShapeGeometry(EditMark mark)
    {
        if (mark.Kind == "highlight-text" && mark.HighlightBoxes is not { Length: > 0 }) return Geometry.Empty;
        if (mark.HighlightBoxes is { Length: > 0 } boxes)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (Rect box in boxes) group.Children.Add(new RectangleGeometry(box));
            return group;
        }
        if (mark.StrokePoints is { Length: > 0 } points)
        {
            var path = new StreamGeometry();
            using (var dc = path.Open()) { dc.BeginFigure(points[0], false, false); dc.PolyLineTo(points.Skip(1).ToArray(), true, false); }
            return path.GetWidenedPathGeometry(new Pen(Brushes.Black, mark.Size) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        }
        if (mark.Kind == "highlight") return new RectangleGeometry(new Rect(mark.Start, mark.End));
        var pen = new Pen(Brushes.Black, mark.Size) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (mark.Kind == "rectangle") return new RectangleGeometry(new Rect(mark.Start, mark.End)).GetWidenedPathGeometry(pen);
        if (mark.Kind == "ellipse") return new EllipseGeometry(new Rect(mark.Start, mark.End)).GetWidenedPathGeometry(pen);
        Vector direction = mark.End - mark.Start;
        double distance = direction.Length;
        if (mark.Kind != "arrow" || distance < .01)
            return new LineGeometry(mark.Start, mark.End).GetWidenedPathGeometry(pen);
        direction.Normalize();
        double length = Math.Min(distance, Math.Max(8, mark.Size * 4));
        Point back = mark.End - direction * length;
        Vector across = new(-direction.Y * length * .45, direction.X * length * .45);
        var head = new StreamGeometry();
        using (var context = head.Open()) { context.BeginFigure(mark.End, true, true); context.LineTo(back + across, true, false); context.LineTo(back - across, true, false); }
        head.Freeze();
        // ごく短い矢印では矢じりだけにし、丸い始端が先端を追い越すのを防ぐ。
        if (distance - length <= .01) return head;
        pen.EndLineCap = PenLineCap.Flat;
        Geometry shaft = new LineGeometry(mark.Start, back).GetWidenedPathGeometry(pen);
        return Geometry.Combine(shaft, head, GeometryCombineMode.Union, null);
    }
    internal static PathGeometry Outline(EditMark mark, double width, double height)
    {
        Geometry geometry;
        if (mark.Kind == "text")
        {
            var group = new GeometryGroup();
            foreach (var shape in TextShapes(mark)) group.Children.Add(shape.Geometry);
            geometry = group;
        }
        else geometry = ShapeGeometry(mark);
        var clipped = Geometry.Combine(geometry, new RectangleGeometry(new Rect(0, 0, width, height)), GeometryCombineMode.Intersect, null);
        var result = clipped.GetFlattenedPathGeometry(.02, ToleranceType.Absolute);
        result.Freeze(); return result;
    }
    internal static void Draw(DrawingContext dc, BitmapSource? image, double width, double height, IEnumerable<EditMark> marks)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        if (image != null) dc.DrawImage(image, new Rect(0, 0, width, height));
        foreach (var mark in marks) DrawMark(dc, mark);
        dc.Pop();
    }
    internal static void DrawMark(DrawingContext dc, EditMark mark)
    {
        var brush = new SolidColorBrush(Ink(mark)); brush.Freeze();
        if (mark.Kind == "text")
        {
            if (mark.TextSegments is not { Length: > 0 })
            {
                var text = new FormattedText(mark.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    EditorFonts.Typeface(mark.FontId, mark.Bold), mark.Size, brush, 1) { LineHeight = mark.Size * 1.3 };
                dc.DrawText(text, mark.Start); return;
            }
            foreach (var (geometry, color) in TextShapes(mark))
                dc.DrawGeometry(new SolidColorBrush(color), null, geometry);
            return;
        }
        dc.DrawGeometry(brush, null, ShapeGeometry(mark));
    }
}
