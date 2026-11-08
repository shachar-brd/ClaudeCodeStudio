using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeStudioAgent;

// Which Sandboxie box a process runs in, through SbieDll's SbieApi_QueryProcess
// (the API Sandboxie's own Start.exe uses). Keeps the persistent shell in the
// box claude runs in: when only claude.exe is forced into a box, a shell the
// (unboxed) agent starts would run outside it.
internal static class SandboxieBox
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueryProcessFn(IntPtr processId, IntPtr boxName, IntPtr imageName, IntPtr sid, out uint sessionId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    private const string SbieDll = "SbieDll.dll";

    // null when Sandboxie is not installed; Failed when it is but its API could not
    // be loaded (then no process can be vouched for).
    private sealed record Api(QueryProcessFn? Query, bool Failed);

    private static readonly Lazy<Api?> Loaded = new(Load);

    // SbieDll was already in this process before Load(): this process is boxed.
    private static bool _loadedInBox;

    private static Api? Load()
    {
        try
        {
            // Inside a box SbieDll is already loaded into every process.
            var lib = GetModuleHandleW(SbieDll);
            var installed = _loadedInBox = lib != IntPtr.Zero;
            if (lib == IntPtr.Zero)
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                foreach (var dir in new[] { "Sandboxie-Plus", "Sandboxie" })
                {
                    var path = Path.Combine(pf, dir, SbieDll);
                    if (!File.Exists(path)) continue;
                    installed = true;
                    if (NativeLibrary.TryLoad(path, out lib)) break;
                }
            }
            if (!installed) return null;
            if (lib == IntPtr.Zero || !NativeLibrary.TryGetExport(lib, "SbieApi_QueryProcess", out var fn))
                return new Api(null, true);
            return new Api(Marshal.GetDelegateForFunctionPointer<QueryProcessFn>(fn), false);
        }
        catch
        {
            return new Api(null, true);
        }
    }

    // The box name; "" when the process is not in a box (or Sandboxie is not
    // installed); null when it cannot be told.
    public static string? BoxOf(int pid)
    {
        var api = Loaded.Value;
        if (api == null) return "";
        if (api.Query == null) return null;

        // Buffer sizes from SbieApi.h: box 34, image and SID 96 WCHARs.
        IntPtr box = Marshal.AllocHGlobal(34 * 2), image = Marshal.AllocHGlobal(96 * 2), sid = Marshal.AllocHGlobal(96 * 2);
        try
        {
            Marshal.WriteInt16(box, 0);
            var status = api.Query((IntPtr)pid, box, image, sid, out _);
            var name = status == 0 ? Marshal.PtrToStringUni(box) ?? "" : "";
            // A process with SbieDll loaded is in a box even if the query says no;
            // except this one when Load() loaded it to call the API.
            if (name == "" && !(pid == Environment.ProcessId && !_loadedInBox) && HasSbieModule(pid)) return null;
            return name;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(box);
            Marshal.FreeHGlobal(image);
            Marshal.FreeHGlobal(sid);
        }
    }

    private static bool HasSbieModule(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            foreach (ProcessModule m in p.Modules)
                if (string.Equals(m.ModuleName, SbieDll, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    public static string Describe(string? box) => box == null ? "an unknown box" : box == "" ? "no box" : $"box '{box}'";
}
