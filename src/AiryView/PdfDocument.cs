using System.Runtime.InteropServices;

namespace AiryView;

public readonly record struct PdfTextCharacter(char Character, Rect RelativeBox);

// PDFiumは全ドキュメント間で直列化する。描画中の回転・解放との競合も防ぐ。
public sealed class PdfDocument : IDisposable
{
    private static readonly object Gate = new();
    private static bool initialized;
    private IntPtr handle, formHandle, formInfo, loadedPage;
    private int loadedIndex = -1;
    private Native.GetPageCallback? getPage;
    private Native.GetCurrentPageCallback? getCurrentPage;
    private GCHandle pinned;
    private readonly Dictionary<int, Size> pageSizes = new();
    public string Path { get; }
    internal string? Password { get; }
    public bool HasSignatures { get; }
    public int Count { get; }
    public bool Dirty { get; private set; }
    public bool CanPrint { get; }
    public bool CanEdit { get; }
    public bool CanFill { get; }
    public double PrintPercent { get; set; } = 100;
    public PrintMode LastPrintMode { get; set; } = PrintMode.Scale;

    public PdfDocument(string path, string? password = null)
    {
        Password = password;
        Path = System.IO.Path.GetFullPath(path);
        if (new FileInfo(Path).Length > 512L * 1024 * 1024)
            throw new IOException("この試作版では512MBを超えるPDFは開けません。");
        lock (Gate)
        {
            if (!initialized) { Native.FPDF_InitLibrary(); initialized = true; }
            byte[] bytes = File.ReadAllBytes(Path);
            pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            IntPtr passwordPtr = password == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(password);
            try { handle = Native.FPDF_LoadMemDocument64(pinned.AddrOfPinnedObject(), (UIntPtr)bytes.LongLength, passwordPtr); }
            finally { if (passwordPtr != IntPtr.Zero) Marshal.ZeroFreeCoTaskMemUTF8(passwordPtr); }
            if (handle == IntPtr.Zero)
            {
                uint error = Native.FPDF_GetLastError();
                pinned.Free();
                if (error == 4) throw new PdfPasswordException();
                throw new IOException($"PDFを開けませんでした（エラー {error}）。");
            }
            formInfo = Marshal.AllocHGlobal(1024);
            Marshal.Copy(new byte[1024], 0, formInfo, 1024);
            Marshal.WriteInt32(formInfo, 1);
            getPage = (_, doc, index) => doc == handle && index == loadedIndex ? loadedPage : IntPtr.Zero;
            getCurrentPage = (_, doc) => doc == handle ? loadedPage : IntPtr.Zero;
            Marshal.WriteIntPtr(formInfo, 8 + 8 * 8, Marshal.GetFunctionPointerForDelegate(getPage));
            Marshal.WriteIntPtr(formInfo, 8 + 9 * 8, Marshal.GetFunctionPointerForDelegate(getCurrentPage));
            formHandle = Native.FPDFDOC_InitFormFillEnvironment(handle, formInfo);
            Count = Native.FPDF_GetPageCount(handle);
            HasSignatures = Native.FPDF_GetSignatureCount(handle) > 0;
            uint permissions = Native.FPDF_GetDocPermissions(handle);
            CanPrint = (permissions & 4) != 0 && (permissions & 2048) != 0;
            CanEdit = (permissions & 8) != 0 && !HasSignatures;
            CanFill = ((permissions & 256) != 0 || (permissions & 8) != 0) && !HasSignatures;
            if (Count < 1) { Dispose(); throw new IOException("表示できるページがありません。"); }
        }
    }

    private T WithPage<T>(int index, Func<IntPtr, T> action)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            IntPtr page = Native.FPDF_LoadPage(handle, index);
            if (page == IntPtr.Zero) throw new IOException($"{index + 1}ページを読み込めません。");
            loadedPage = page; loadedIndex = index;
            if (formHandle != IntPtr.Zero) Native.FORM_OnAfterLoadPage(page, formHandle);
            try { return action(page); }
            finally { if (formHandle != IntPtr.Zero) Native.FORM_OnBeforeClosePage(page, formHandle); loadedPage = IntPtr.Zero; loadedIndex = -1; Native.FPDF_ClosePage(page); }
        }
    }

    public Size SizeMm(int index)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (pageSizes.TryGetValue(index, out var cached)) return cached;
            // レイアウト用の寸法取得では本文・画像・フォームを解析しない。
            if (Native.FPDF_GetPageSizeByIndexF(handle, index, out var points) == 0)
                throw new IOException($"{index + 1}ページの寸法を読み込めません。");
            var size = new Size(points.Width * 25.4 / 72, points.Height * 25.4 / 72);
            if (size.Width <= 0 || size.Height <= 0 || !double.IsFinite(size.Width + size.Height)) throw new IOException("ページ寸法が不正です。");
            pageSizes[index] = size;
            return size;
        }
    }

    internal Size LoadedSizeMmForTest(int index) => WithPage(index, page =>
        new Size(Native.FPDF_GetPageWidthF(page) * 25.4 / 72, Native.FPDF_GetPageHeightF(page) * 25.4 / 72));

    public BitmapSource Render(int index, int width, int height, bool lcdText = false) => WithPage(index, page =>
    {
        if (width <= 0 || height <= 0 || (long)width * height > 24_000_000) throw new ArgumentOutOfRangeException(nameof(width));
        IntPtr bitmap = Native.FPDFBitmap_Create(width, height, 0);
        if (bitmap == IntPtr.Zero) throw new IOException("描画用メモリを確保できません。");
        try
        {
            Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            int flags = lcdText ? 1 | 2 : 1;
            Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, flags);
            if (formHandle != IntPtr.Zero) Native.FPDF_FFLDraw(formHandle, bitmap, page, 0, 0, width, height, 0, flags);
            int stride = Native.FPDFBitmap_GetStride(bitmap);
            // ネイティブバッファ解放前にWPF側へコピーする。
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
                Native.FPDFBitmap_GetBuffer(bitmap), stride * height, stride);
            image.Freeze();
            return image;
        }
        finally { Native.FPDFBitmap_Destroy(bitmap); }
    });

    public void DrawToPrinter(IntPtr dc, int index, int x, int y, int width, int height) => WithPage(index, page =>
    {
        Native.FPDF_RenderPage(dc, page, x, y, width, height, 0, 1 | 0x800);
        if (formHandle != IntPtr.Zero && Native.FPDF_GetFormType(handle) != 0)
        {
            // 本文はベクトルのまま。入力欄の見た目だけ透過画像を重ねる。
            double factor = Math.Min(1, Math.Sqrt(12_000_000.0 / Math.Max(1.0, (double)width * height)));
            int w = Math.Max(1, (int)(width * factor)), h = Math.Max(1, (int)(height * factor));
            IntPtr bitmap = Native.FPDFBitmap_Create(w, h, 1);
            if (bitmap == IntPtr.Zero) throw new IOException("入力欄の印刷用画像を作成できません。");
            try
            {
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0);
                Native.FPDF_FFLDraw(formHandle, bitmap, page, 0, 0, w, h, 0, 0x801);
                using var image = new System.Drawing.Bitmap(w, h, Native.FPDFBitmap_GetStride(bitmap), System.Drawing.Imaging.PixelFormat.Format32bppArgb, Native.FPDFBitmap_GetBuffer(bitmap));
                using var graphics = System.Drawing.Graphics.FromHdc(dc);
                graphics.PageUnit = System.Drawing.GraphicsUnit.Pixel;
                graphics.DrawImage(image, new System.Drawing.Rectangle(x, y, width, height));
            }
            finally { Native.FPDFBitmap_Destroy(bitmap); }
        }
        return true;
    });

    public void Rotate(int index, int delta)
    {
        if (!CanEdit) throw new InvalidOperationException("このPDFは編集が制限されています。");
        WithPage(index, page => { Native.FPDFPage_SetRotation(page, (Native.FPDFPage_GetRotation(page) + delta + 4) % 4); pageSizes.Remove(index); return true; });
        Dirty = true;
    }

    public void ResetRotation(int index)
    {
        if (!CanEdit) throw new InvalidOperationException("このPDFは編集が制限されています。");
        WithPage(index, page => { Native.FPDFPage_SetRotation(page, 0); pageSizes.Remove(index); return true; });
        Dirty = true;
    }
    public void SaveCopy(string destination, bool markClean = true)
    {
        if (!CanEdit) throw new InvalidOperationException("このPDFは編集が制限されています。");
        if (string.Equals(System.IO.Path.GetFullPath(destination), Path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("元のPDFを保護するため、別の名前を指定してください。");
        // 完成するまで既存の保存先を書き換えない。
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                Exception? writeError = null;
                Native.WriteBlock callback = (_, data, length) =>
                {
                    try { var bytes = new byte[checked((int)length)]; Marshal.Copy(data, bytes, 0, bytes.Length); stream.Write(bytes); return 1; }
                    catch (Exception ex) { writeError = ex; return 0; }
                };
                var writer = new Native.FileWrite { Version = 1, Callback = callback };
                lock (Gate)
                {
                    if (Native.FPDF_SaveAsCopy(handle, ref writer, 2) == 0)
                        throw new IOException("PDFを保存できませんでした。", writeError);
                }
                GC.KeepAlive(callback);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
            if (markClean) Dirty = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IReadOnlyList<PdfTextCharacter> TextCharacters(int index) => WithPage(index, page =>
    {
        IntPtr text = Native.FPDFText_LoadPage(page);
        if (text == IntPtr.Zero) return (IReadOnlyList<PdfTextCharacter>)Array.Empty<PdfTextCharacter>();
        try
        {
            int count = Native.FPDFText_CountChars(text);
            var result = new List<PdfTextCharacter>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                uint code = Native.FPDFText_GetUnicode(text, i);
                if (code > char.MaxValue) continue;
                Rect box = Rect.Empty;
                if (Native.FPDFText_GetCharBox(text, i, out double left, out double right, out double bottom, out double top) &&
                    Native.FPDF_PageToDevice(page, 0, 0, 10000, 10000, 0, left, top, out int x1, out int y1) != 0 &&
                    Native.FPDF_PageToDevice(page, 0, 0, 10000, 10000, 0, right, bottom, out int x2, out int y2) != 0)
                    box = new Rect(Math.Min(x1, x2) / 10000.0, Math.Min(y1, y2) / 10000.0,
                        Math.Abs(x2 - x1) / 10000.0, Math.Abs(y2 - y1) / 10000.0);
                result.Add(new PdfTextCharacter((char)code, box));
            }
            return result;
        }
        finally { Native.FPDFText_ClosePage(text); }
    });
    public string PageText(int index) => WithPage(index, page =>
    {
        IntPtr text = Native.FPDFText_LoadPage(page);
        if (text == IntPtr.Zero) return "";
        try
        {
            int count = Native.FPDFText_CountChars(text);
            if (count <= 0) return "";
            byte[] buffer = new byte[checked((count + 1) * 2)];
            int written = Native.FPDFText_GetText(text, 0, count, buffer);
            return System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, written - 1) * 2);
        }
        finally { Native.FPDFText_ClosePage(text); }
    });
    public void Dispose()
    {
        lock (Gate)
        {
            if (formHandle != IntPtr.Zero) { Native.FPDFDOC_ExitFormFillEnvironment(formHandle); formHandle = IntPtr.Zero; }
            if (formInfo != IntPtr.Zero) { Marshal.FreeHGlobal(formInfo); formInfo = IntPtr.Zero; }
            if (handle != IntPtr.Zero) { Native.FPDF_CloseDocument(handle); handle = IntPtr.Zero; }
            if (pinned.IsAllocated) pinned.Free();
        }
    }

    private static class Native
    {
        private const string Dll = "pdfium";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr GetPageCallback(IntPtr info, IntPtr doc, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr GetCurrentPageCallback(IntPtr info, IntPtr doc);
        [DllImport(Dll)] internal static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, IntPtr info);
        [DllImport(Dll)] internal static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);
        [DllImport(Dll)] internal static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);
        [DllImport(Dll)] internal static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);
        [DllImport(Dll)] internal static extern void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotation, int flags);
        [DllImport(Dll)] internal static extern int FPDF_GetFormType(IntPtr document);
        [DllImport(Dll)] internal static extern int FPDF_GetSignatureCount(IntPtr document);
        [DllImport(Dll)] internal static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Dll)] internal static extern void FPDFText_ClosePage(IntPtr text);
        [DllImport(Dll)] internal static extern int FPDFText_CountChars(IntPtr text);
        [DllImport(Dll)] internal static extern int FPDFText_GetText(IntPtr text, int start, int count, [Out] byte[] result);
        [DllImport(Dll)] internal static extern uint FPDFText_GetUnicode(IntPtr text, int index);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FPDFText_GetCharBox(IntPtr text, int index, out double left, out double right, out double bottom, out double top);
        [DllImport(Dll)] internal static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
        [DllImport(Dll)] internal static extern void FPDF_InitLibrary();
        [DllImport(Dll)] internal static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, UIntPtr size, IntPtr password);
        [DllImport(Dll)] internal static extern uint FPDF_GetLastError();
        [DllImport(Dll)] internal static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Dll)] internal static extern uint FPDF_GetDocPermissions(IntPtr document);
        [DllImport(Dll)] internal static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Dll)] internal static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll)] internal static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Dll)] internal static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Dll)] internal static extern float FPDF_GetPageHeightF(IntPtr page);
        [StructLayout(LayoutKind.Sequential)] internal struct PageSize { public float Width, Height; }
        [DllImport(Dll)] internal static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int index, out PageSize size);
        [DllImport(Dll)] internal static extern int FPDFPage_GetRotation(IntPtr page);
        [DllImport(Dll)] internal static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
        [DllImport(Dll)] internal static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Dll)] internal static extern void FPDFBitmap_FillRect(IntPtr bitmap, int x, int y, int width, int height, uint color);
        [DllImport(Dll)] internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
        [DllImport(Dll)] internal static extern void FPDF_RenderPage(IntPtr dc, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
        [DllImport(Dll)] internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Dll)] internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Dll)] internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int WriteBlock(IntPtr self, IntPtr data, uint size);
        [StructLayout(LayoutKind.Sequential)] internal struct FileWrite { public int Version; public WriteBlock Callback; }
        [DllImport(Dll)] internal static extern int FPDF_SaveAsCopy(IntPtr document, ref FileWrite writer, uint flags);
    }
}

public sealed class PdfPasswordException : IOException { public PdfPasswordException() : base("PDFのパスワードを入力してください。") { } }
