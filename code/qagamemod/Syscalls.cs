using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QGameMod;

/// <summary>
/// Engine syscall wrappers. Provides the same trap_* API as the C game module.
/// The syscall function pointer is passed from the C game module at init time.
/// </summary>
public static unsafe class Syscalls
{
    private static delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint> _syscall;

    internal static void Init(nint syscallPtr)
    {
        _syscall = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)syscallPtr;
    }

    internal static bool IsInitialized => _syscall != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Call(nint cmd) =>
        _syscall(cmd, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Call(nint cmd, nint a0) =>
        _syscall(cmd, a0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Call(nint cmd, nint a0, nint a1) =>
        _syscall(cmd, a0, a1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Call(nint cmd, nint a0, nint a1, nint a2) =>
        _syscall(cmd, a0, a1, a2, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PassFloat(float f) => BitConverter.SingleToInt32Bits(f);

    // Syscall command IDs (from g_public.h gameImport_t)
    private const int G_PRINT = 0;
    private const int G_ERROR = 1;
    private const int G_MILLISECONDS = 2;
    private const int G_CVAR_REGISTER = 3;
    private const int G_CVAR_UPDATE = 4;
    private const int G_CVAR_SET = 5;
    private const int G_CVAR_VARIABLE_INTEGER_VALUE = 6;
    private const int G_CVAR_VARIABLE_STRING_BUFFER = 7;
    private const int G_ARGC = 8;
    private const int G_ARGV = 9;
    private const int G_SEND_CONSOLE_COMMAND = 14;
    private const int G_SEND_SERVER_COMMAND = 17;

    // --- Print ---
    public static void Print(string msg)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(msg + '\0'))
            Call(G_PRINT, (nint)p);
    }

    // --- Cvars ---
    public static void CvarSet(string name, string value)
    {
        fixed (byte* pName = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
        fixed (byte* pValue = System.Text.Encoding.UTF8.GetBytes(value + '\0'))
            Call(G_CVAR_SET, (nint)pName, (nint)pValue);
    }

    public static string CvarGetString(string name)
    {
        byte* buf = stackalloc byte[256];
        fixed (byte* pName = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            Call(G_CVAR_VARIABLE_STRING_BUFFER, (nint)pName, (nint)buf, 256);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    public static int CvarGetInteger(string name)
    {
        fixed (byte* pName = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(G_CVAR_VARIABLE_INTEGER_VALUE, (nint)pName);
    }

    // --- Command args ---
    public static int Argc() => (int)Call(G_ARGC);

    public static string Argv(int n)
    {
        byte* buf = stackalloc byte[1024];
        Call(G_ARGV, n, (nint)buf, 1024);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    // --- Time ---
    public static int Milliseconds() => (int)Call(G_MILLISECONDS);

    // --- Console/Server commands ---
    public static void SendConsoleCommand(string cmd)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(cmd + '\0'))
            Call(G_SEND_CONSOLE_COMMAND, (nint)p);
    }

    /// <summary>
    /// Send a command to a specific client, or -1 for all clients.
    /// The command string is typically "print \"message\n\"" or "chat \"message\"".
    /// </summary>
    public static void SendServerCommand(int clientNum, string cmd)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(cmd + '\0'))
            Call(G_SEND_SERVER_COMMAND, clientNum, (nint)p);
    }
}
