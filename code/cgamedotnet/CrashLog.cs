using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CGameDotNet;

/// <summary>
/// Crash debugging for NativeAOT. Writes breadcrumb-style logs to a file
/// so we can trace exactly where fail-fast crashes occur.
/// NativeAOT fail-fast bypasses try-catch — this is the only way to debug them.
/// </summary>
public static class CrashLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Quake3", "baseq3", "cgame_crash.log");

    private static StreamWriter? _writer;
    private static int _breadcrumbId;
    private static bool _enabled = true;

    [DllImport("kernel32.dll")]
    private static extern nint SetUnhandledExceptionFilter(nint lpTopLevelExceptionFilter);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int NativeCrashHandler(nint exceptionInfo)
    {
        try
        {
            // EXCEPTION_POINTERS: first field is EXCEPTION_RECORD*, second is CONTEXT*
            nint exceptionRecord = *(nint*)exceptionInfo;
            // EXCEPTION_RECORD: ExceptionCode (uint), ExceptionFlags (uint), *ExceptionRecord, ExceptionAddress
            uint code = *(uint*)exceptionRecord;
            nint address = *(nint*)(exceptionRecord + 16); // offset 16 on x64

            _writer?.WriteLine($"[{Environment.TickCount64}] NATIVE CRASH: code=0x{code:X8} address=0x{address:X16}");
            _writer?.Flush();
        }
        catch { }
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }

    /// <summary>
    /// Initialize crash logging. Call once at DLL load.
    /// Also installs a global unhandled exception handler.
    /// </summary>
    public static unsafe void Init()
    {
        try
        {
            string? dir = Path.GetDirectoryName(LogPath);
            if (dir != null) Directory.CreateDirectory(dir);

            _writer = new StreamWriter(LogPath, append: false) { AutoFlush = true };
            _writer.WriteLine($"=== cgame_dotnet crash log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            _writer.WriteLine($"PID: {Environment.ProcessId}");
            _writer.WriteLine();

            // Install native SEH filter to catch access violations etc.
            SetUnhandledExceptionFilter((nint)(delegate* unmanaged[Stdcall]<nint, int>)&NativeCrashHandler);

            // Catch unhandled managed exceptions before they become fail-fast
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                Log($"UNHANDLED EXCEPTION (isTerminating={args.IsTerminating}):");
                Log(ex?.ToString() ?? args.ExceptionObject?.ToString() ?? "null");
            };
        }
        catch
        {
            _enabled = false;
        }
    }

    /// <summary>
    /// Write a message to the crash log.
    /// </summary>
    public static void Log(string message)
    {
        if (!_enabled) return;
        try { _writer?.WriteLine($"[{Environment.TickCount64}] {message}"); }
        catch { /* don't crash the crash logger */ }
    }

    /// <summary>
    /// Drop a numbered breadcrumb. Call at the start of key operations.
    /// If the game crashes, the last breadcrumb tells you where.
    /// </summary>
    public static int Breadcrumb(
        string label,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (!_enabled) return 0;
        int id = ++_breadcrumbId;
        string shortFile = Path.GetFileName(file);
        try { _writer?.WriteLine($"[{Environment.TickCount64}] #{id} {label}  ({shortFile}:{line})"); }
        catch { }
        return id;
    }

    /// <summary>
    /// Log an exception with full stack trace.
    /// </summary>
    public static void LogException(string context, Exception ex)
    {
        if (!_enabled) return;
        try
        {
            _writer?.WriteLine($"[{Environment.TickCount64}] EXCEPTION in {context}:");
            _writer?.WriteLine(ex.ToString());
            _writer?.WriteLine();
        }
        catch { }
    }

    /// <summary>
    /// Flush and close the log. Called on shutdown.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            _writer?.WriteLine($"[{Environment.TickCount64}] === CLEAN SHUTDOWN ===");
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
        catch { }
    }
}
