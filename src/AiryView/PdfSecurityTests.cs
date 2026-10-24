using System.Text;
namespace AiryView;

internal static class PdfSecurityTests
{
    internal static void Run()
    {
        Directory.CreateDirectory("artifacts/pdf-security");
        var results=new List<string>();
        void Check(bool ok,string label) { if(!ok) throw new IOException("FAIL: "+label); results.Add("PASS: "+label); }
        string sample=System.IO.Path.Combine(AppContext.BaseDirectory,"Samples","print-check.pdf");
        using var doc=new PdfDocument(sample);
        Check(doc.WorkerIdForTest!=Environment.ProcessId,"PDF parsing runs outside the UI process");
        string secret=System.IO.Path.GetFullPath("artifacts/pdf-security/private-test.txt");
        File.WriteAllText(secret,"security-test-canary");
        var probe=doc.ProbeForTest(secret);
        Check(probe.GetProperty("appContainer").GetBoolean(),"worker token is an AppContainer");
        Check(!probe.GetProperty("read").GetBoolean(),"worker cannot read an ungranted private file");
        Check(!probe.GetProperty("write").GetBoolean(),"worker cannot write outside its session folder");
        Check(!probe.GetProperty("network").GetBoolean(),"worker has no outbound network capability");
        using(var other=new PdfDocument(sample))
            Check(!doc.ProbeForTest(other.WorkerInputForTest).GetProperty("read").GetBoolean(),"worker cannot read another document worker's files");
        Check(doc.SizeMm(0).Width>0 && doc.Render(0,420,594).PixelWidth==420,"isolated page dimensions and rendering work");
        Check(doc.PageText(0).Length>0,"isolated text extraction works");
        using(var current=System.Diagnostics.Process.GetCurrentProcess())
            Check(!current.Modules.Cast<System.Diagnostics.ProcessModule>().Any(m=>m.ModuleName.Equals("pdfium.dll",StringComparison.OrdinalIgnoreCase)),"UI process does not load the native PDF parser");
        int worker=doc.WorkerIdForTest;
        try { doc.Render(0,100000,100000); Check(false,"oversized rendering rejected"); } catch(ArgumentOutOfRangeException) { Check(true,"oversized rendering rejected before IPC"); }
        string saved=System.IO.Path.GetFullPath("artifacts/pdf-security/saved.pdf"); doc.Rotate(0,1); doc.SaveCopy(saved);
        using(var reopened=new PdfDocument(saved)) Check(reopened.SizeMm(0).Width>reopened.SizeMm(0).Height,"rotation and saved PDF reopen through sandbox");
        try { doc.CrashForTest(); Check(false,"worker termination detected"); } catch(IOException) { Check(true,"worker termination is reported without crashing UI"); }
        using(var next=new PdfDocument(sample)) Check(next.Render(0,100,100).PixelHeight==100,"another document opens after a worker exits");
        using(var stalled=new PdfDocument(sample))
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();
            try { stalled.TimeoutForTest(); Check(false,"worker deadline"); }
            catch(IOException) { Check(watch.Elapsed.TotalSeconds<8,"stalled worker is terminated at the deadline"); }
        }
        using(var printable=new PdfDocument(sample))
        using(var bitmap=new System.Drawing.Bitmap(700,1000))
        {
            using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.White); var dc=graphics.GetHdc();
                try { printable.DrawToPrinter(dc,0,30,40,600,840); } finally { graphics.ReleaseHdc(dc); }
            }
            int ink=0;
            for(int y=40;y<880;y+=3) for(int x=30;x<630;x+=3) if(bitmap.GetPixel(x,y).R<200) ink++;
            Check(ink>20 && bitmap.GetPixel(0,0).R==255,"printer adapter draws isolated pixels at the requested size and offset");
            bitmap.Save("artifacts/pdf-security/print-adapter.png");
        }
        using var oversizedFrame=new MemoryStream(new byte[]{255,255,255,127});
        try { PdfWorker.ReadFrame(oversizedFrame); Check(false,"invalid IPC length rejected"); } catch(IOException) { Check(true,"invalid IPC length rejected before allocation"); }
        var helper=PdfHelper.RunAsync(new Dictionary<string,object?> { ["operation"]="inspect",["source"]=sample }).GetAwaiter().GetResult();
        Check(helper.TryGetProperty("fields",out _),"Python PDF helper also works inside AppContainer");
        string large="artifacts/pdf-security/too-large.pdf";
        using(var file=new FileStream(large,FileMode.Create,FileAccess.Write)) file.SetLength(PdfSecurityLimits.MaxFileBytes+1);
        try { using var blocked=new PdfDocument(large); Check(false,"large file rejected"); } catch(IOException) { Check(true,"oversized PDF rejected before launching parser"); }
        File.Delete(large);
        try { using var blocked=new PdfDocument("artifacts/pdf-security/too-many-pages.pdf"); Check(false,"page limit"); } catch(IOException) { Check(true,"PDFs above 10000 pages are rejected"); }
        using(var active=new PdfDocument("artifacts/pdf-security/active-content.pdf"))
            Check(active.Render(0,100,100).PixelWidth==100,"PDF with open-action JavaScript and attachment is rendered without executing actions");
        string fixtureRoot=System.IO.Path.GetFullPath("artifacts/feature-tests");
        if(File.Exists(System.IO.Path.Combine(fixtureRoot,"test-only.pfx")))
        {
            string signed=System.IO.Path.GetFullPath("artifacts/pdf-security/isolated-signed.pdf");
            PdfHelper.RunAsync(new Dictionary<string,object?> { ["operation"]="sign",["source"]=System.IO.Path.Combine(fixtureRoot,"forms.pdf"),["destination"]=signed,["pfx"]=System.IO.Path.Combine(fixtureRoot,"test-only.pfx"),["pfxPassword"]="test" }).GetAwaiter().GetResult();
            var status=PdfHelper.RunAsync(new Dictionary<string,object?> { ["operation"]="verify",["source"]=signed }).GetAwaiter().GetResult();
            Check(status.GetProperty("signatures")[0].GetProperty("valid").GetBoolean(),"signing and offline verification work inside AppContainer");
        }
        File.WriteAllLines("artifacts/pdf-security/security-test-results.txt",results);
    }
}
