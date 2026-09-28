using System.Runtime.InteropServices;

namespace AiryView;

internal sealed record PdfDrawingPage(int Index, double Width, double Height, PdfDrawingShape[] Shapes);
internal sealed record PdfDrawingShape(PathGeometry Geometry, Color Color);

public sealed partial class PdfDocument
{
    // 閲覧用ドキュメントには触れず、回転を含む現在の状態を複製して保存する。
    internal void SaveDrawings(string destination, IReadOnlyList<PdfDrawingPage> drawings)
    {
        if (!CanEdit) throw new IOException("署名または編集制限のあるPDFには書き込めません。");
        if (string.Equals(System.IO.Path.GetFullPath(destination), Path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("原本とは別の名前で保存してください。");
        string snapshot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "airy-edit-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            SaveCopy(snapshot, false);
            using var copy = new PdfDocument(snapshot, Password);
            foreach (var drawing in drawings) copy.AddDrawing(drawing);
            copy.SaveCopy(destination);
        }
        finally { if (File.Exists(snapshot)) File.Delete(snapshot); }
    }

    private void AddDrawing(PdfDrawingPage drawing) => WithPage(drawing.Index, page =>
    {
        // PDFium自身の座標変換を使い、CropBoxの原点とページ回転を反映する。
        const int grid = 1000000;
        Check(Native.FPDF_DeviceToPage(page, 0, 0, grid, grid, 0, 0, 0, out double x0, out double y0));
        Check(Native.FPDF_DeviceToPage(page, 0, 0, grid, grid, 0, grid, 0, out double xx, out double yx));
        Check(Native.FPDF_DeviceToPage(page, 0, 0, grid, grid, 0, 0, grid, out double xy, out double yy));
        foreach (var shape in drawing.Shapes)
        {
            IntPtr path = IntPtr.Zero;
            try
            {
                foreach (var figure in shape.Geometry.Figures)
                {
                    if (path == IntPtr.Zero)
                    {
                        path = Native.FPDFPageObj_CreateNewPath((float)figure.StartPoint.X, (float)figure.StartPoint.Y);
                        if (path == IntPtr.Zero) throw new IOException("書き込み用の領域を確保できません。");
                    }
                    else Check(Native.FPDFPath_MoveTo(path, (float)figure.StartPoint.X, (float)figure.StartPoint.Y));
                    foreach (var segment in figure.Segments)
                    {
                        if (segment is PolyLineSegment poly)
                            foreach (Point point in poly.Points) Check(Native.FPDFPath_LineTo(path, (float)point.X, (float)point.Y));
                        else if (segment is LineSegment line)
                            Check(Native.FPDFPath_LineTo(path, (float)line.Point.X, (float)line.Point.Y));
                        else throw new IOException("書き込みの図形を変換できません。");
                    }
                    if (figure.IsClosed) Check(Native.FPDFPath_Close(path));
                }
                if (path == IntPtr.Zero) continue;
                var color = shape.Color;
                Check(Native.FPDFPageObj_SetFillColor(path, color.R, color.G, color.B, color.A));
                Check(Native.FPDFPath_SetDrawMode(path, shape.Geometry.FillRule == FillRule.EvenOdd ? 1 : 2, 0));
                Native.FPDFPageObj_Transform(path, (xx - x0) / drawing.Width, (yx - y0) / drawing.Width,
                    (xy - x0) / drawing.Height, (yy - y0) / drawing.Height, x0, y0);
                int before = Native.FPDFPage_CountObjects(page);
                Native.FPDFPage_InsertObject(page, path);
                if (Native.FPDFPage_CountObjects(page) != before + 1) throw new IOException("書き込みを追加できません。");
                path = IntPtr.Zero; // 所有権はページへ移る。
            }
            finally { if (path != IntPtr.Zero) Native.FPDFPageObj_Destroy(path); }
        }
        Check(Native.FPDFPage_GenerateContent(page));
        return true;
    });

    private static void Check(int result)
    { if (result == 0) throw new IOException("PDFへの書き込みに失敗しました。"); }

    private static partial class Native
    {
        [DllImport(Dll)] internal static extern int FPDF_DeviceToPage(IntPtr page, int x, int y, int width, int height, int rotation, int dx, int dy, out double px, out double py);
        [DllImport(Dll)] internal static extern IntPtr FPDFPageObj_CreateNewPath(float x, float y);
        [DllImport(Dll)] internal static extern int FPDFPath_MoveTo(IntPtr path, float x, float y);
        [DllImport(Dll)] internal static extern int FPDFPath_LineTo(IntPtr path, float x, float y);
        [DllImport(Dll)] internal static extern int FPDFPath_Close(IntPtr path);
        [DllImport(Dll)] internal static extern int FPDFPath_SetDrawMode(IntPtr path, int fill, int stroke);
        [DllImport(Dll)] internal static extern int FPDFPageObj_SetFillColor(IntPtr path, uint red, uint green, uint blue, uint alpha);
        [DllImport(Dll)] internal static extern void FPDFPageObj_Transform(IntPtr path, double a, double b, double c, double d, double e, double f);
        [DllImport(Dll)] internal static extern void FPDFPageObj_Destroy(IntPtr path);
        [DllImport(Dll)] internal static extern int FPDFPage_CountObjects(IntPtr page);
        [DllImport(Dll)] internal static extern void FPDFPage_InsertObject(IntPtr page, IntPtr path);
        [DllImport(Dll)] internal static extern int FPDFPage_GenerateContent(IntPtr page);
    }
}
