using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace AiryView;

public static class DesktopInstaller
{
    public static string Install()
    {
        string source = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        string dll = System.IO.Path.Combine(source, "AiryView.dll");
        if (!File.Exists(dll) || !File.Exists(System.IO.Path.Combine(source, "coreclr.dll")))
            throw new IOException(".NET同梱の実行用フォルダから登録してください。");
        if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new IOException("Program Filesへの登録には管理者権限が必要です。");
        string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AiryView");
        string target = NormalizeDirectoryCase(root);
        Directory.CreateDirectory(target);
        if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            // コピー前に既存ファイルを確認し、起動中の版への途中までの上書きを防ぐ。
            foreach (string existing in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                string incoming = System.IO.Path.Combine(source, System.IO.Path.GetRelativePath(target, existing));
                if (!File.Exists(incoming) || SHA256.HashData(File.ReadAllBytes(existing)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(incoming)))) continue;
                try { using var check = File.Open(existing, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { throw new IOException("AiryViewを閉じてから、もう一度セットアップしてください。"); }
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = System.IO.Path.GetRelativePath(source, file);
                string destination = System.IO.Path.GetFullPath(System.IO.Path.Combine(target, relative));
                if (!destination.StartsWith(target + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("コピー先がアプリのフォルダ外です。");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                if (File.Exists(destination) && SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(destination)))) continue;
                File.Copy(file, destination, true);
            }
        }
        foreach (string name in new[] { "AiryView.exe", "AiryView.dll", "AiryView.deps.json", "AiryView.runtimeconfig.json", "AiryView.pdb", "AiryView.ico" }) NormalizeFileCase(target, name);
        string exe = ResolveShellPath(System.IO.Path.Combine(target, "AiryView.exe"));
        target = System.IO.Path.GetDirectoryName(exe)!;
        // 元の版は残す。更新中や実行中のファイルを削除しない。
        RemoveOwnShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        RemoveOwnShortcut(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        CreateShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Programs), exe, target);
        string pinnedFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        // 既にピン留めされた自分のリンクのみ更新する。新たなピン留めは行わない。
        if (new[] { "AiryView.lnk", "AiryReader.lnk", "airyPDF.lnk" }.Any(name => File.Exists(System.IO.Path.Combine(pinnedFolder, name))))
            CreateShortcut(pinnedFolder, exe, target);
        foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs), pinnedFolder }) RemoveLegacyShortcut(folder);
        RegisterApplication(exe, target);
        RemoveLegacyInstall(source);
        return exe;
    }
    private static string NormalizeDirectoryCase(string desired)
    {
        string parent = System.IO.Path.GetDirectoryName(desired)!;
        string? existing = Directory.EnumerateDirectories(parent).FirstOrDefault(path =>
            string.Equals(System.IO.Path.GetFileName(path), System.IO.Path.GetFileName(desired), StringComparison.OrdinalIgnoreCase));
        if (existing == null || string.Equals(existing, desired, StringComparison.Ordinal)) return desired;
        string temporary = System.IO.Path.Combine(parent, ".AiryView-case-" + Guid.NewGuid().ToString("N"));
        Directory.Move(existing, temporary);
        Directory.Move(temporary, desired);
        return desired;
    }
    private static void NormalizeFileCase(string folder, string desiredName)
    {
        string desired = System.IO.Path.Combine(folder, desiredName);
        string? existing = Directory.EnumerateFiles(folder).FirstOrDefault(path =>
            string.Equals(System.IO.Path.GetFileName(path), desiredName, StringComparison.OrdinalIgnoreCase));
        if (existing == null || string.Equals(existing, desired, StringComparison.Ordinal)) return;
        string temporary = System.IO.Path.Combine(folder, ".AiryView-case-" + Guid.NewGuid().ToString("N"));
        File.Move(existing, temporary);
        File.Move(temporary, desired);
    }
    private static void RemoveLegacyInstall(string source)
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach ((string Folder, string Exe) legacy in new[] { ("AiryReader", "AiryReader.exe"), ("airyPDF", "airyPDF.exe") })
        {
            string folder = System.IO.Path.Combine(programFiles, legacy.Folder);
            if (!Directory.Exists(folder) || source.StartsWith(folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(System.IO.Path.Combine(folder, legacy.Exe)) || !File.Exists(System.IO.Path.Combine(folder, "coreclr.dll"))) continue;
            if (Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)) continue;
            Directory.Delete(folder, true);
        }
    }    private static void RegisterApplication(string exe, string folder)
    {
        string[] documentExtensions = [".pdf", ".md", ".markdown", ".txt"];
        string[] imageExtensions = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".ico", ".webp", ".svg"];
        string[] supportedExtensions = [.. documentExtensions, .. imageExtensions];
        string[] retiredExtensions = [".html", ".htm"];
        using var app = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\AiryView.exe");
        app.SetValue("FriendlyAppName", "AiryView");
        using (var icon = app.CreateSubKey("DefaultIcon")) icon.SetValue("", IconLocation(exe));
        using (var command = app.CreateSubKey(@"shell\open\command")) command.SetValue("", "\"" + exe + "\" \"%1\"");
        using (var types = app.CreateSubKey("SupportedTypes"))
        {
            foreach (string extension in supportedExtensions) types.SetValue(extension, "");
            foreach (string extension in retiredExtensions) types.DeleteValue(extension, false);
        }

        // Windows 10/11の「既定のアプリ」にAiryViewと対応形式を表示する正式な登録。
        using (var capabilities = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\AiryView\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", "AiryView");
            capabilities.SetValue("ApplicationDescription", "軽量ドキュメントビューア・正確なPDF印刷");
            capabilities.SetValue("ApplicationIcon", IconLocation(exe));
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (string extension in supportedExtensions) associations.SetValue(extension, ProgId(extension));
            foreach (string extension in retiredExtensions) associations.DeleteValue(extension, false);
        }
        using (var registered = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue("AiryView", @"Software\AiryView\Capabilities");
        // 拡張子ごとのProgIDに分け、Windowsの既定アプリ選択で各形式を独立して選べるようにする。
        RegisterProgId("AiryView.Document", "AiryViewで開く文書", exe);
        RegisterProgId("AiryView.Image", "AiryViewで開く画像", exe);
        foreach (string extension in supportedExtensions)
        {
            string progId = ProgId(extension);
            RegisterProgId(progId, documentExtensions.Contains(extension) ? "AiryViewで開く文書" : "AiryViewで開く画像", exe);
            using var openWith = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids");
            openWith.SetValue(progId, Array.Empty<byte>(), Microsoft.Win32.RegistryValueKind.None);
            openWith.DeleteValue("AiryView.Document", false);
            openWith.DeleteValue("AiryView.Image", false);
        }
        foreach (string extension in retiredExtensions)
            using (var openWith = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", true))
                openWith?.DeleteValue("AiryView.Document", false);

        // 旧版の登録を除き、Windowsの候補をAiryViewへ一本化する。
        RemoveLegacyRegistrations(supportedExtensions);
        using var uninstall = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AiryView");
        uninstall.SetValue("DisplayName", "AiryView");
        uninstall.SetValue("DisplayVersion", "2.0.15");
        uninstall.SetValue("Publisher", "AiryView");
        uninstall.SetValue("InstallLocation", folder);
        uninstall.SetValue("DisplayIcon", IconLocation(exe));
        uninstall.SetValue("UninstallString", "\"" + exe + "\" --unregister");
        uninstall.SetValue("NoModify", 1); uninstall.SetValue("NoRepair", 1);
        SHChangeNotify(0x08000000, 0, null!, IntPtr.Zero);
    }
    private static void RemoveLegacyRegistrations(IEnumerable<string> supportedExtensions)
    {
        foreach (string oldName in new[] { "AiryReader", "airyPDF" })
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\" + oldName + ".exe", false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + oldName + ".Document", false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + oldName + ".Image", false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + oldName + @"\Capabilities", false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + oldName, false);
            using (var registered = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) registered?.DeleteValue(oldName, false);
            foreach (string extension in supportedExtensions)
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + oldName + extension, false);
                using var openWith = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", true);
                openWith?.DeleteValue(oldName + extension, false);
                openWith?.DeleteValue(oldName + ".Document", false);
                openWith?.DeleteValue(oldName + ".Image", false);
            }
        }
    }
    private static string ProgId(string extension) => "AiryView" + extension.ToLowerInvariant();
    private static void RegisterProgId(string progId, string description, string exe)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId);
        key.SetValue("", description);
        key.SetValue("FriendlyTypeName", description);
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", IconLocation(exe));
        using (var command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", "\"" + exe + "\" \"%1\"");
    }
    public static void Unregister()
    {
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\AiryView.exe", false);
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AiryView.Document", false);
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AiryView.Image", false);
        foreach (string extension in new[] { ".pdf", ".md", ".markdown", ".txt", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".ico", ".webp", ".svg" })
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId(extension), false);
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\AiryView\Capabilities", false);
        using (var registered = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) registered?.DeleteValue("AiryView", false);
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AiryView", false);
        foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar") })
        {
            string link = System.IO.Path.Combine(folder, "AiryView.lnk");
            if (!File.Exists(link)) continue;
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            object? shortcutObject = null;
            try { dynamic shortcut = shell.CreateShortcut(link); shortcutObject = shortcut;
                if (IsOwnInstall((string)shortcut.TargetPath)) { File.Delete(link); SHChangeNotify(0x4, 0x1005, link, IntPtr.Zero); } }
            finally { if (shortcutObject != null) Marshal.FinalReleaseComObject(shortcutObject); Marshal.FinalReleaseComObject(shell); }
        }
        // 実行ファイルとユーザー設定は復旧用に残す。
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path, uint length, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int change, uint flags, string path, IntPtr unused);
    internal static string ResolveShellPath(string path)
    {
        // パッケージ環境ではLocalAppDataへの書込みが転送される。Explorerからも読める実パスを登録する。
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new System.Text.StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("アイコンと起動先の実際の場所を確認できません。");
        string resolved = buffer.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + resolved[8..];
        return resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
    }
    internal static bool IsOwnInstall(string path)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string packages = System.IO.Path.Combine(local, "Packages") + System.IO.Path.DirectorySeparatorChar;
        string file = System.IO.Path.GetFileName(path);
        bool knownExe = new[] { "AiryView.exe", "AiryReader.exe", "airyPDF.exe" }.Contains(file, StringComparer.OrdinalIgnoreCase);
        bool knownFolder = new[]
        {
            System.IO.Path.Combine(programFiles, "AiryView"), System.IO.Path.Combine(programFiles, "AiryReader"), System.IO.Path.Combine(programFiles, "airyPDF"),
            System.IO.Path.Combine(local, "Programs", "AiryView"), System.IO.Path.Combine(local, "Programs", "AiryReader"), System.IO.Path.Combine(local, "Programs", "airyPDF")
        }.Any(folder => path.StartsWith(folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        bool knownPackage = path.StartsWith(packages, StringComparison.OrdinalIgnoreCase) &&
            (path.Contains("\\LocalCache\\Local\\Programs\\AiryView\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\LocalCache\\Local\\Programs\\AiryReader\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\LocalCache\\Local\\Programs\\airyPDF\\", StringComparison.OrdinalIgnoreCase));
        return knownExe && (knownFolder || knownPackage);
    }    private static void RemoveLegacyShortcut(string folder)
    {
        foreach (string name in new[] { "AiryReader.lnk", "airyPDF.lnk" })
        {
            string path = System.IO.Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")!; dynamic shell = Activator.CreateInstance(shellType)!; object? item = null;
            try { dynamic shortcut = shell.CreateShortcut(path); item = shortcut; if (IsOwnInstall((string)shortcut.TargetPath)) { File.Delete(path); SHChangeNotify(0x4, 0x1005, path, IntPtr.Zero); } }
            finally { if (item != null) Marshal.FinalReleaseComObject(item); Marshal.FinalReleaseComObject(shell); }
        }
    }    private static void RemoveOwnShortcut(string folder)
    {
        string path = System.IO.Path.Combine(folder, "AiryView.lnk");
        if (!File.Exists(path)) return;
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        object? shortcutObject = null;
        try
        {
            dynamic shortcut = shell.CreateShortcut(path); shortcutObject = shortcut;
            if (IsOwnInstall((string)shortcut.TargetPath))
            {
                File.Delete(path);
                SHChangeNotify(0x4, 0x1005, path, IntPtr.Zero);
            }
        }
        finally
        {
            if (shortcutObject != null) Marshal.FinalReleaseComObject(shortcutObject);
            Marshal.FinalReleaseComObject(shell);
        }
    }
    private static string IconLocation(string exe) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe)!, "AiryView.ico") + ",0";
    private static void CreateShortcut(string folder, string exe, string working)
    {
        Directory.CreateDirectory(folder);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("ショートカットを作成できません。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        object? shortcutObject = null;
        try
        {
            string path = System.IO.Path.Combine(folder, "AiryView.lnk");
            dynamic shortcut = shell.CreateShortcut(path); shortcutObject = shortcut;
            if (File.Exists(path) && !string.IsNullOrEmpty((string)shortcut.TargetPath) && !IsOwnInstall((string)shortcut.TargetPath))
                throw new IOException("既存のAiryViewショートカットが別の場所を指しています。上書きせず停止しました。");
            shortcut.TargetPath = exe; shortcut.WorkingDirectory = working; shortcut.IconLocation = IconLocation(exe);
            shortcut.Description = "AiryView — 軽量ドキュメントビューア・正確なPDF印刷";
            shortcut.Save();
            NormalizeFileCase(folder, "AiryView.lnk");
            path = System.IO.Path.Combine(folder, "AiryView.lnk");
            SHChangeNotify(0x2000, 0x1005, path, IntPtr.Zero);
        }
        finally
        {
            if (shortcutObject != null) Marshal.FinalReleaseComObject(shortcutObject);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
