using System.Text.Json;
using System.Windows.Documents;
namespace AiryView;

// 本体は文書を解析せず、必要なデータだけを制限されたワーカーと交換する。
public sealed class PdfDocument : IDisposable
{
    private readonly object gate = new();
    private readonly PdfSandboxProcess sandbox;
    private bool disposed;
    private readonly Dictionary<int, Size> sizes = new();
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
    internal int WorkerIdForTest => sandbox.ProcessId;
    internal string WorkerInputForTest => System.IO.Path.Combine(sandbox.Folder,"input.pdf");
    public PdfDocument(string path, string? password = null)
    {
        Path = System.IO.Path.GetFullPath(path); Password = password;
        byte[] bytes = PdfSecurityLimits.ReadPdf(Path);
        sandbox = new PdfSandboxProcess();
        try
        {
            File.WriteAllBytes(System.IO.Path.Combine(sandbox.Folder, "input.pdf"), bytes);
            sandbox.Start(System.IO.Path.Combine(AppContext.BaseDirectory, "AiryView.exe"), "--pdf-worker");
            var info = Call(new { op = "open", password });
            Count = info.GetProperty("count").GetInt32();
            if (Count < 1 || Count > PdfSecurityLimits.MaxPages) throw new IOException("PDFのページ数が上限を超えています。");
            HasSignatures = info.GetProperty("signed").GetBoolean(); CanPrint = info.GetProperty("print").GetBoolean();
            CanEdit = info.GetProperty("edit").GetBoolean(); CanFill = info.GetProperty("fill").GetBoolean();
        }
        catch { sandbox.Dispose(); throw; }
    }
    private JsonElement Call(object request, int seconds = 60)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                // ハング・異常終了時にも本体を巻き添えにしない。
                using var timer = new System.Threading.Timer(_ => sandbox.Kill(), null, TimeSpan.FromSeconds(seconds), System.Threading.Timeout.InfiniteTimeSpan);
                PdfWorker.WriteFrame(sandbox.Input, JsonSerializer.SerializeToUtf8Bytes(request));
                using var response = JsonDocument.Parse(PdfWorker.ReadFrame(sandbox.Output));
                if (!response.RootElement.GetProperty("ok").GetBoolean())
                {
                    if (response.RootElement.TryGetProperty("password", out var pw) && pw.GetBoolean()) throw new PdfPasswordException();
                    throw new IOException(response.RootElement.GetProperty("error").GetString());
                }
                return response.RootElement.GetProperty("result").Clone();
            }
            catch (Exception ex) when (ex is EndOfStreamException or JsonException)
            { sandbox.Kill(); throw new IOException("PDFの隔離処理が終了しました。ファイルが不正、または処理上限を超えた可能性があります。", ex); }
        }
    }
    private void CheckPage(int index) { if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index)); }
    public Size SizeMm(int index)
    {
        CheckPage(index);
        lock (gate) { ObjectDisposedException.ThrowIf(disposed,this); if (sizes.TryGetValue(index, out var size)) return size; var r = Call(new { op="size", index }); size = new(r[0].GetDouble(), r[1].GetDouble()); if (!double.IsFinite(size.Width+size.Height) || size.Width<=0 || size.Height<=0) throw new IOException("ページ寸法が不正です。"); sizes[index]=size; return size; }
    }
    internal Size LoadedSizeMmForTest(int index) { CheckPage(index); var r=Call(new { op="loadedSize",index }); return new(r[0].GetDouble(),r[1].GetDouble()); }
    public BitmapSource Render(int index,int width,int height,bool lcdText=false)
    {
        CheckPage(index); if(width<=0 || height<=0 || (long)width*height>24_000_000) throw new ArgumentOutOfRangeException(nameof(width));
        var r=Call(new { op="render",index,width,height,lcdText }); byte[] pixels=r.GetBytesFromBase64();
        if(pixels.Length!=checked(width*height*4)) throw new IOException("PDF描画応答のサイズが不正です。");
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgr32,null,pixels,width*4); image.Freeze(); return image;
    }
    public void DrawToPrinter(IntPtr dc,int index,int x,int y,int width,int height)
    {
        CheckPage(index); if (!CanPrint) throw new IOException("印刷が制限されています。");
        if(width<=0 || height<=0 || width>100000 || height>100000) throw new IOException("印刷サイズが不正です。");
        double factor=Math.Min(1,Math.Sqrt(24_000_000.0/((double)width*height)));
        int w=Math.Max(1,(int)(width*factor)), h=Math.Max(1,(int)(height*factor));
        var rendered=Render(index,w,h); byte[] pixels=new byte[checked(w*h*4)]; rendered.CopyPixels(pixels,w*4,0);
        var pinned=System.Runtime.InteropServices.GCHandle.Alloc(pixels,System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var bitmap=new System.Drawing.Bitmap(w,h,w*4,System.Drawing.Imaging.PixelFormat.Format32bppRgb,pinned.AddrOfPinnedObject());
            using var graphics=System.Drawing.Graphics.FromHdc(dc); graphics.PageUnit=System.Drawing.GraphicsUnit.Pixel;
            graphics.DrawImage(bitmap,new System.Drawing.Rectangle(x,y,width,height));
        }
        finally { pinned.Free(); }
    }

    public void Rotate(int index,int delta) { CheckPage(index); Call(new { op="rotate",index,delta }); sizes.Remove(index); Dirty=true; }
    public void ResetRotation(int index) { CheckPage(index); Call(new { op="reset",index }); sizes.Remove(index); Dirty=true; }
    private string OutputPath => System.IO.Path.Combine(sandbox.Folder,"output.pdf");
    private void CheckDestination(string destination)
    {
        if(!CanEdit) throw new InvalidOperationException("このPDFは編集が制限されています。");
        if(string.Equals(System.IO.Path.GetFullPath(destination),Path,StringComparison.OrdinalIgnoreCase)) throw new IOException("元のPDFとは別の名前で保存してください。");
    }
    public void SaveCopy(string destination,bool markClean=true)
    {
        CheckDestination(destination);
        lock(gate) { Call(new { op="save" }); PdfSecurityLimits.Publish(OutputPath,destination); if(markClean) Dirty=false; }
    }
    internal void SaveDrawings(string destination,IReadOnlyList<PdfDrawingPage> drawings)
    {
        CheckDestination(destination);
        foreach (var drawing in drawings) CheckPage(drawing.Index);
        if(drawings.Count>PdfSecurityLimits.MaxPages || drawings.Any(p=>p.Shapes.Length>10000)) throw new IOException("書き込み数が上限を超えています。");
        var pages=drawings.Select(p=>new { p.Index,p.Width,p.Height,shapes=p.Shapes.Select(s=>new { geometry=s.Geometry.ToString(System.Globalization.CultureInfo.InvariantCulture),color=s.Color.ToString() }).ToArray() }).ToArray();
        lock(gate) { Call(new { op="drawings",pages }); PdfSecurityLimits.Publish(OutputPath,destination); }
    }
    public string PageText(int index) { CheckPage(index); string text=Call(new { op="text",index }).GetString() ?? ""; if(text.Length>PdfSecurityLimits.MaxCharacters*2) throw new IOException("ページの文字数が上限を超えています。"); return text; }
    public IReadOnlyList<PdfTextCharacter> TextCharacters(int index)
    {
        CheckPage(index); var r=Call(new { op="characters",index }); if(r.GetArrayLength()>PdfSecurityLimits.MaxCharacters) throw new IOException("ページの文字数が上限を超えています。");
        return r.EnumerateArray().Select(c=>new PdfTextCharacter((char)c.GetProperty("code").GetInt32(),c.GetProperty("box").ValueKind==JsonValueKind.Null ? Rect.Empty : new Rect(c.GetProperty("box")[0].GetDouble(),c.GetProperty("box")[1].GetDouble(),c.GetProperty("box")[2].GetDouble(),c.GetProperty("box")[3].GetDouble()))).ToArray();
    }
    internal int InternalLinkPageAt(int index,Point relative)
    {
        if (!double.IsFinite(relative.X + relative.Y) || relative.X < 0 || relative.X > 1 || relative.Y < 0 || relative.Y > 1) return -1;
        CheckPage(index); return Call(new { op="link",index,x=relative.X,y=relative.Y }).GetInt32();
    }
    internal JsonElement ProbeForTest(string path) => Call(new { op="probe",path });
    internal void CrashForTest() => Call(new { op="crash" });
    internal void TimeoutForTest() => Call(new { op="wait" }, 1);
    public void Dispose() { lock(gate) { if(disposed) return; disposed=true; sandbox.Dispose(); } }
}
