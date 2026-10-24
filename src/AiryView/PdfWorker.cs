using System.Text.Json;
namespace AiryView;

internal static class PdfWorker
{
    internal static bool IsWorker { get; private set; }
    internal static int Run()
    {
        IsWorker = true;
        if (!PdfSandboxProcess.IsAppContainer()) return 2;
        using Stream input = Console.OpenStandardInput(), output = Console.OpenStandardOutput();
        NativePdfDocument? document = null;
        string folder = Environment.CurrentDirectory;
        try
        {
            while (true)
            {
                byte[] frame;
                try { frame = ReadFrame(input); } catch (EndOfStreamException) { break; }
                try
                {
                    using var json = JsonDocument.Parse(frame);
                    var q = json.RootElement; string op = q.GetProperty("op").GetString()!;
                    object result;
                    if (op == "open")
                    {
                        if (document != null) throw new IOException("文書は既に開かれています。");
                        document = new NativePdfDocument(System.IO.Path.Combine(folder,"input.pdf"),q.GetProperty("password").GetString());
                        result = new { count=document.Count,signed=document.HasSignatures,print=document.CanPrint,edit=document.CanEdit,fill=document.CanFill };
                    }
                    else
                    {
                        if (document == null) throw new IOException("文書が開かれていません。");
                        int index = q.TryGetProperty("index",out var page) ? page.GetInt32() : 0;
                        switch (op)
                        {
                            case "size": var size=document.SizeMm(index); result=new[]{size.Width,size.Height}; break;
                            case "loadedSize": var loaded=document.LoadedSizeMmForTest(index); result=new[]{loaded.Width,loaded.Height}; break;
                            case "render":
                                var bitmap=document.Render(index,q.GetProperty("width").GetInt32(),q.GetProperty("height").GetInt32(),q.GetProperty("lcdText").GetBoolean());
                                byte[] pixels=new byte[checked(bitmap.PixelWidth*bitmap.PixelHeight*4)]; bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0); result=pixels; break;
                            case "text": result=document.PageText(index); break;
                            case "characters": result=document.TextCharacters(index).Select(c=>new { code=(int)c.Character,box=c.RelativeBox.IsEmpty ? null : new[]{c.RelativeBox.X,c.RelativeBox.Y,c.RelativeBox.Width,c.RelativeBox.Height} }).ToArray(); break;
                            case "link": result=document.InternalLinkPageAt(index,new Point(q.GetProperty("x").GetDouble(),q.GetProperty("y").GetDouble())); break;
                            case "rotate": document.Rotate(index,q.GetProperty("delta").GetInt32()); result=true; break;
                            case "reset": document.ResetRotation(index); result=true; break;
                            case "save": document.SaveCopy(System.IO.Path.Combine(folder,"output.pdf")); result=true; break;
                            case "drawings":
                                var drawings=q.GetProperty("pages").EnumerateArray().Select(p=>new PdfDrawingPage(p.GetProperty("Index").GetInt32(),p.GetProperty("Width").GetDouble(),p.GetProperty("Height").GetDouble(),
                                    p.GetProperty("shapes").EnumerateArray().Select(s=>new PdfDrawingShape(PathGeometry.CreateFromGeometry(Geometry.Parse(s.GetProperty("geometry").GetString()!)),(Color)ColorConverter.ConvertFromString(s.GetProperty("color").GetString()!))).ToArray())).ToArray();
                                document.SaveDrawings(System.IO.Path.Combine(folder,"output.pdf"),drawings); result=true; break;
                            case "probe": result=Probe(q.GetProperty("path").GetString()!); break;
                            case "crash": return 3;
                            case "wait": System.Threading.Thread.Sleep(TimeSpan.FromMinutes(2)); result=true; break;
                            default: throw new IOException("不明なPDF操作です。");
                        }
                    }
                    WriteFrame(output,JsonSerializer.SerializeToUtf8Bytes(new {ok=true,result}));
                }
                catch(Exception ex)
                { WriteFrame(output,JsonSerializer.SerializeToUtf8Bytes(new {ok=false,error=ex.Message,password=ex is PdfPasswordException})); }
            }
        }
        finally { document?.Dispose(); }
        return 0;
    }
    private static object Probe(string path)
    {
        bool read=false,write=false,network=false;
        try { using var f=File.OpenRead(path); read=true; } catch { }
        try { using var f=new FileStream(path+".worker-write",FileMode.CreateNew,FileAccess.Write); write=true; } catch { }
        try { using var client=new System.Net.Sockets.TcpClient(); client.ConnectAsync("1.1.1.1",443).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); network=true; } catch { }
        return new {appContainer=PdfSandboxProcess.IsAppContainer(),read,write,network};
    }
    internal static byte[] ReadFrame(Stream stream)
    {
        Span<byte> header=stackalloc byte[4]; stream.ReadExactly(header);
        int size=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if(size<=0 || size>PdfSecurityLimits.MaxFrameBytes) throw new IOException("PDF処理の応答が上限を超えています。");
        byte[] bytes=new byte[size]; stream.ReadExactly(bytes); return bytes;
    }
    internal static void WriteFrame(Stream stream,byte[] bytes)
    {
        if(bytes.Length<=0 || bytes.Length>PdfSecurityLimits.MaxFrameBytes) throw new IOException("PDF処理データが上限を超えています。");
        Span<byte> header=stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        stream.Write(header); stream.Write(bytes); stream.Flush();
    }
}
