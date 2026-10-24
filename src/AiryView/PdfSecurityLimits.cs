using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace AiryView;

internal static class PdfSecurityLimits
{
    internal const long MaxFileBytes = 128L * 1024 * 1024;
    internal const int MaxPages = 10_000, MaxCharacters = 200_000, MaxFrameBytes = 192 * 1024 * 1024;
    internal static byte[] ReadPdf(string path, string? confinedDirectory = null)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (confinedDirectory != null)
        {
            var actual = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(file.SafeFileHandle, actual, (uint)actual.Capacity, 0);
            string root = "\\\\?\\" + System.IO.Path.GetFullPath(confinedDirectory).TrimEnd('\\') + "\\";
            if (length == 0 || length >= actual.Capacity || !actual.ToString().StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !GetFileInformationByHandle(file.SafeFileHandle, out var info) || info.Links != 1)
                throw new IOException("隔離フォルダー外のファイルは保存できません。");
        }
        if (file.Length <= 0 || file.Length > MaxFileBytes) throw new IOException("PDFは128MB以内にしてください。");
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }
    internal static void Publish(string source, string destination)
    {
        // 隔離先の応答で任意のパスを選ばせない。選択済み保存先にだけ書く。
        byte[] bytes = ReadPdf(source, System.IO.Path.GetDirectoryName(source));
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) { file.Write(bytes); file.Flush(true); } File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
}
