using System.Runtime.InteropServices;

namespace CGameDotNet;

/// <summary>
/// NativeAOT exports for the Q3 cgame DLL interface.
/// The engine calls dllEntry once at load, then vmMain for all cgame commands.
/// </summary>
public static unsafe class CGameExports
{
    // cgameExport_t commands (from cg_public.h)
    private const int CG_INIT = 0;
    private const int CG_SHUTDOWN = 1;
    private const int CG_CONSOLE_COMMAND = 2;
    private const int CG_DRAW_ACTIVE_FRAME = 3;
    private const int CG_CROSSHAIR_PLAYER = 4;
    private const int CG_LAST_ATTACKER = 5;
    private const int CG_KEY_EVENT = 6;
    private const int CG_MOUSE_EVENT = 7;
    private const int CG_EVENT_HANDLING = 8;

    /// <summary>
    /// Called once at DLL load. Receives the engine's syscall function pointer.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "dllEntry")]
    public static void DllEntry(nint syscallPtr)
    {
        CrashLog.Init();
        CrashLog.Breadcrumb("dllEntry");
        Syscalls.Init(syscallPtr);
        Syscalls.Print("[.NET cgame] dllEntry called\n");
    }

    /// <summary>
    /// Main entry point called by the engine for all cgame commands.
    /// Signature: intptr_t vmMain(int command, int arg0..arg11)
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "vmMain")]
    public static nint VmMain(int command, int arg0, int arg1, int arg2, int arg3,
                              int arg4, int arg5, int arg6, int arg7,
                              int arg8, int arg9, int arg10, int arg11)
    {
        try
        {
            CrashLog.Breadcrumb($"vmMain cmd={command}");
            nint result = command switch
            {
                CG_INIT => Init(arg0, arg1, arg2),
                CG_SHUTDOWN => Shutdown(),
                CG_CONSOLE_COMMAND => ConsoleCommand(),
                CG_DRAW_ACTIVE_FRAME => DrawActiveFrame(arg0, arg1, arg2),
                CG_CROSSHAIR_PLAYER => CrosshairPlayer(),
                CG_LAST_ATTACKER => LastAttacker(),
                CG_KEY_EVENT => KeyEvent(arg0, arg1),
                CG_MOUSE_EVENT => MouseEvent(arg0, arg1),
                CG_EVENT_HANDLING => EventHandling(arg0),
                _ => 0,
            };
            CrashLog.Breadcrumb($"vmMain cmd={command} done");
            return result;
        }
        catch (Exception ex)
        {
            CrashLog.LogException($"vmMain({command})", ex);
            Syscalls.Print($"[.NET cgame] Exception in vmMain({command}): {ex.Message}\n{ex.StackTrace}\n");
            return 0;
        }
    }

    private static nint Init(int serverMessageNum, int serverCommandSequence, int clientNum)
    {
        CrashLog.Breadcrumb("CG_INIT begin");
        Syscalls.Print($"[.NET cgame] CG_Init: serverMsg={serverMessageNum}, cmdSeq={serverCommandSequence}, client={clientNum}\n");
        CGame.Init(serverMessageNum, serverCommandSequence, clientNum);
        CrashLog.Breadcrumb("CG_INIT CGame.Init returned");
        Syscalls.Print("[.NET cgame] DEBUG: CGame.Init returned OK\n");
        CrashLog.Breadcrumb("CG_INIT complete");
        return 0;
    }

    private static nint Shutdown()
    {
        Syscalls.Print("[.NET cgame] CG_Shutdown\n");
        CGame.Shutdown();
        CrashLog.Shutdown();
        return 0;
    }

    private static nint ConsoleCommand()
    {
        return CGame.ConsoleCommand() ? 1 : 0;
    }

    private static nint DrawActiveFrame(int serverTime, int stereoView, int demoPlayback)
    {
        CrashLog.Breadcrumb($"DAF t={serverTime}");
        CGame.DrawActiveFrame(serverTime, stereoView, demoPlayback != 0);
        return 0;
    }

    private static nint CrosshairPlayer()
    {
        return CGame.CrosshairPlayer();
    }

    private static nint LastAttacker()
    {
        return CGame.LastAttacker();
    }

    private static nint KeyEvent(int key, int down)
    {
        CGame.KeyEvent(key, down != 0);
        return 0;
    }

    private static nint MouseEvent(int dx, int dy)
    {
        CGame.MouseEvent(dx, dy);
        return 0;
    }

    private static nint EventHandling(int type)
    {
        CGame.EventHandling(type);
        return 0;
    }
}
