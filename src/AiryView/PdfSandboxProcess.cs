using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AiryView;

// ネットワーク能力を付与しないAppContainer。失敗しても通常権限では起動しない。
internal sealed class PdfSandboxProcess : IDisposable
{
    private readonly string profile = "AiryView.PdfWorker." + Guid.NewGuid().ToString("N");
    private bool profileCreated;
    private static readonly object ProfileGate = new();
    internal Stream Input { get; private set; } = Stream.Null;
    internal Stream Output { get; private set; } = Stream.Null;
    private Stream error = Stream.Null;
    internal string ErrorText { get; private set; } = "";
    internal int ProcessId { get; private set; }
    internal string Folder { get; }
    private IntPtr job;
    private IntPtr workerProcess;
    private bool disposed;
    internal PdfSandboxProcess()
    {
        IntPtr sid = IntPtr.Zero, profilePath = IntPtr.Zero;
        try
        {
            lock (ProfileGate)
            {
                // 文書・署名処理ごとに別SID。別ワーカーのPDFや証明書も読めない。
                int hr = CreateAppContainerProfile(profile, "AiryView PDF", "Isolated PDF processing", IntPtr.Zero, 0, out sid);
                Marshal.ThrowExceptionForHR(hr); profileCreated = true;
                var securityId = new SecurityIdentifier(sid);
                Marshal.ThrowExceptionForHR(GetAppContainerFolderPath(securityId.Value, out profilePath));
                Folder = System.IO.Path.Combine(Marshal.PtrToStringUni(profilePath)!, "LocalState", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Folder);
                // 新規セッションだけに権限を付け、元の文書・保存先のACLは変えない。
                var directory = new DirectoryInfo(Folder);
                var acl = directory.GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(securityId, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                directory.SetAccessControl(acl);
            }
        }
        finally { if (sid != IntPtr.Zero) FreeSid(sid); if (profilePath != IntPtr.Zero) Marshal.FreeCoTaskMem(profilePath); }
    }
    internal static bool IsAppContainer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return GetTokenInformation(identity.AccessToken.DangerousGetHandle(), 29, out int value, 4, out _) && value != 0;
    }
    internal void Start(string exe, string arguments)
    {
        IntPtr sid = IntPtr.Zero, attributes = IntPtr.Zero, capsMemory = IntPtr.Zero, handlesMemory = IntPtr.Zero, environment = IntPtr.Zero;
        IntPtr childIn = IntPtr.Zero, parentIn = IntPtr.Zero, parentOut = IntPtr.Zero, childOut = IntPtr.Zero, parentError = IntPtr.Zero, childError = IntPtr.Zero;
        ProcessInfo process = default;
        try
        {
            Marshal.ThrowExceptionForHR(DeriveAppContainerSidFromAppContainerName(profile, out sid));
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            Check(CreatePipe(out childIn, out parentIn, ref sa, 0));
            Check(CreatePipe(out parentOut, out childOut, ref sa, 0));
            Check(CreatePipe(out parentError, out childError, ref sa, 0));
            Check(SetHandleInformation(parentIn, 1, 0)); Check(SetHandleInformation(parentOut, 1, 0));
            Check(SetHandleInformation(parentError, 1, 0));
            nuint size = 0; InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal((nint)size);
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
            var caps = new SecurityCapabilities { AppContainerSid = sid };
            capsMemory = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>()); Marshal.StructureToPtr(caps, capsMemory, false);
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20009, capsMemory, (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero));
            handlesMemory = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handlesMemory, childIn); Marshal.WriteIntPtr(handlesMemory, IntPtr.Size, childOut);
            Marshal.WriteIntPtr(handlesMemory, IntPtr.Size * 2, childError);
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handlesMemory, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupInfoEx { Attributes = attributes };
            startup.Startup.Size = Marshal.SizeOf<StartupInfoEx>(); startup.Startup.Flags = 0x100;
            startup.Startup.Input = childIn; startup.Startup.Output = childOut; startup.Startup.Error = childError;
            // 親のAPIキー等の環境変数を隔離先へ引き継がない。
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = windows, ["WINDIR"] = windows, ["PATH"] = System.IO.Path.Combine(windows, "System32"),
                ["TEMP"] = Folder, ["TMP"] = Folder, ["USERPROFILE"] = Folder,
                ["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ["SystemDrive"] = System.IO.Path.GetPathRoot(windows)!.TrimEnd('\\'),
                ["DOTNET_EnableDiagnostics"] = "0", ["COMPlus_EnableDiagnostics"] = "0"
            };
            environment = Marshal.StringToHGlobalUni(string.Join('\0', env.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
            job = CreateJobObject(IntPtr.Zero, null); if (job == IntPtr.Zero) throw new Win32Exception();
            var limits = new JobLimits();
            limits.Basic.LimitFlags = 0x2000 | 0x100 | 0x8; // 親終了時kill、メモリ上限、子プロセス禁止
            limits.Basic.ActiveProcessLimit = 1; limits.ProcessMemoryLimit = (nuint)(2L * 1024 * 1024 * 1024);
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
            Check(CreateProcess(exe, new StringBuilder('"' + exe + "\" " + arguments), IntPtr.Zero, IntPtr.Zero, true,
                0x80000 | 0x08000000 | 0x4 | 0x400, environment, Folder, ref startup, out process));
            Check(AssignProcessToJobObject(job, process.Process));
            ProcessId = process.Id;
            if (ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception();
            workerProcess = process.Process; process.Process = IntPtr.Zero;
            Input = new FileStream(new SafeFileHandle(parentIn, true), FileAccess.Write); parentIn = IntPtr.Zero;
            Output = new FileStream(new SafeFileHandle(parentOut, true), FileAccess.Read); parentOut = IntPtr.Zero;
            error = new FileStream(new SafeFileHandle(parentError, true), FileAccess.Read); parentError = IntPtr.Zero;
            _ = Task.Run(() =>
            {
                try { using var reader = new StreamReader(error, Encoding.UTF8); char[] chars = new char[1024]; int count;
                    while ((count = reader.Read(chars)) > 0) if (ErrorText.Length < 4096) ErrorText += new string(chars, 0, Math.Min(count, 4096 - ErrorText.Length)); }
                catch (IOException) { } catch (ObjectDisposedException) { }
            });
        }
        catch { if (process.Process != IntPtr.Zero) TerminateProcess(process.Process, 1); Dispose(); throw; }
        finally
        {
            foreach (IntPtr h in new[] { childIn, childOut, parentIn, parentOut, childError, parentError, process.Process, process.Thread }) if (h != IntPtr.Zero) CloseHandle(h);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            foreach (IntPtr p in new[] { capsMemory, handlesMemory, environment }) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            if (sid != IntPtr.Zero) FreeSid(sid);
        }
    }
    internal void Kill() { if (job != IntPtr.Zero) TerminateJobObject(job, 1); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Kill();
        if (workerProcess != IntPtr.Zero) { WaitForSingleObject(workerProcess, 2000); CloseHandle(workerProcess); workerProcess = IntPtr.Zero; }
        try { Input.Dispose(); } catch (IOException) { }
        try { Output.Dispose(); } catch (IOException) { }
        try { error.Dispose(); } catch (IOException) { }
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        // 削除するのはこのインスタンスが生成した専用フォルダーだけ。
        try
        {
            if (Directory.Exists(Folder))
                Directory.Delete(Folder, (File.GetAttributes(Folder) & FileAttributes.ReparsePoint) == 0);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (profileCreated) { DeleteAppContainerProfile(profile); profileCreated = false; }
    }
    private static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "PDF隔離プロセスを開始できません。"); }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr AppContainerSid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public int X,Y,Width,Height,XCount,YCount,Fill,Flags; public short Show,ReservedSize; public IntPtr ReservedPointer,Input,Output,Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int Id, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime,JobTime; public uint LimitFlags; public nuint MinWorkingSet,MaxWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemory,PeakJobMemory; }
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name,string display,string description,IntPtr capabilities,uint count,out IntPtr sid);
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name,out IntPtr sid);
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] private static extern int GetAppContainerFolderPath(string sid,out IntPtr path);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool GetTokenInformation(IntPtr token,int type,out int value,int size,out int returned);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CreatePipe(out IntPtr read,out IntPtr write,ref SecurityAttributes sa,uint size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref nuint size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,nuint attribute,IntPtr value,nuint size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CreateProcess(string exe,StringBuilder command,IntPtr processSa,IntPtr threadSa,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfoEx startup,out ProcessInfo process);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr sa,string? name);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job,int type,ref JobLimits limits,uint size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job,uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
}
