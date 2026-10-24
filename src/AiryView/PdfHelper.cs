using System.Text;
using System.Text.Json;
namespace AiryView;
public static class PdfHelper
{
    public static async Task<JsonElement> RunAsync(Dictionary<string,object?> request)
    {
        string helper=System.IO.Path.Combine(AppContext.BaseDirectory,"Helper","airyview-pdf-helper.exe");
        if(!File.Exists(helper)) throw new IOException("PDF編集エンジンがありません。アプリの更新版を登録してください。");
        if(!request.TryGetValue("operation",out var operation) || operation is not string op || op is not ("inspect" or "write" or "sign" or "verify")) throw new IOException("不明なPDF操作です。");
        if(!request.TryGetValue("source",out var sourceValue) || sourceValue is not string source) throw new IOException("PDFが指定されていません。");
        string? destination=request.GetValueOrDefault("destination") as string;
        if(op is "write" or "sign")
        {
            if(string.IsNullOrEmpty(destination)) throw new IOException("保存先が指定されていません。");
            if(string.Equals(System.IO.Path.GetFullPath(source),System.IO.Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase)) throw new IOException("元のPDFとは別の名前で保存してください。");
        }
        using var sandbox=new PdfSandboxProcess();
        var isolated=new Dictionary<string,object?>(request);
        isolated["source"]=System.IO.Path.Combine(sandbox.Folder,"input.pdf");
        File.WriteAllBytes((string)isolated["source"]!,PdfSecurityLimits.ReadPdf(source));
        string output=System.IO.Path.Combine(sandbox.Folder,"output.pdf");
        isolated["destination"]=output;
        if(op=="sign")
        {
            if(request.GetValueOrDefault("pfx") is not string pfx) throw new IOException("証明書が指定されていません。");
            isolated["pfx"]=System.IO.Path.Combine(sandbox.Folder,"certificate.pfx");
            File.WriteAllBytes((string)isolated["pfx"]!,PdfSecurityLimits.ReadPdf(pfx));
        }
        byte[] payload=JsonSerializer.SerializeToUtf8Bytes(isolated);
        if(payload.Length>8*1024*1024) throw new IOException("PDF編集データが上限を超えています。");
        sandbox.Start(helper,"");
        using var timer=new System.Threading.Timer(_=>sandbox.Kill(),null,TimeSpan.FromMinutes(3),System.Threading.Timeout.InfiniteTimeSpan);
        await sandbox.Input.WriteAsync(payload); sandbox.Input.Close();
        using var response=new MemoryStream(); byte[] buffer=new byte[8192];
        while(true)
        {
            int count=await sandbox.Output.ReadAsync(buffer); if(count==0) break;
            if(response.Length+count>8*1024*1024) { sandbox.Kill(); throw new IOException("PDF処理の応答が上限を超えています。"); }
            response.Write(buffer,0,count);
        }
        try
        {
            using var json=JsonDocument.Parse(response.ToArray());
            if(!json.RootElement.GetProperty("ok").GetBoolean()) throw new IOException(json.RootElement.GetProperty("error").GetString());
            if(op is "write" or "sign") PdfSecurityLimits.Publish(output,destination!);
            return json.RootElement.GetProperty("result").Clone();
        }
        catch(JsonException ex) { throw new IOException("PDFの隔離処理が終了しました。" + sandbox.ErrorText,ex); }
    }
}
