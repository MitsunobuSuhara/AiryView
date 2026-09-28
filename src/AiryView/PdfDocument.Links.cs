using System.Runtime.InteropServices;

namespace AiryView;

public sealed partial class PdfDocument
{
    // 起動時には解析せず、クリック時だけ調べる。外部URI・ファイルは実行しない。
    internal int InternalLinkPageAt(int index, Point relative)
    {
        if (!double.IsFinite(relative.X + relative.Y) || relative.X < 0 || relative.X > 1 || relative.Y < 0 || relative.Y > 1) return -1;
        return WithPage(index, page =>
        {
            const int scale = 1000000;
            if (Native.FPDF_DeviceToPage(page, 0, 0, scale, scale, 0, (int)Math.Round(relative.X * scale), (int)Math.Round(relative.Y * scale), out double x, out double y) == 0) return -1;
            IntPtr link = Native.FPDFLink_GetLinkAtPoint(page, x, y);
            if (link == IntPtr.Zero) return -1;
            IntPtr destination = Native.FPDFLink_GetDest(handle, link);
            if (destination == IntPtr.Zero)
            {
                IntPtr action = Native.FPDFLink_GetAction(link);
                if (action == IntPtr.Zero || Native.FPDFAction_GetType(action) != 1) return -1;
                destination = Native.FPDFAction_GetDest(handle, action);
            }
            if (destination == IntPtr.Zero) return -1;
            int target = Native.FPDFDest_GetDestPageIndex(handle, destination);
            return target >= 0 && target < Count ? target : -1;
        });
    }
    private static partial class Native
    {
        [DllImport(Dll)] internal static extern IntPtr FPDFLink_GetLinkAtPoint(IntPtr page, double x, double y);
        [DllImport(Dll)] internal static extern IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);
        [DllImport(Dll)] internal static extern IntPtr FPDFLink_GetAction(IntPtr link);
        [DllImport(Dll)] internal static extern uint FPDFAction_GetType(IntPtr action);
        [DllImport(Dll)] internal static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);
        [DllImport(Dll)] internal static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr destination);
    }
}
