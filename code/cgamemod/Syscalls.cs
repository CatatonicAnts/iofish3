using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CGameMod;

/// <summary>
/// Engine syscall wrappers. Provides the same trap_* API as the C cgame.
/// The syscall function pointer is passed from the C cgame at init time.
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
    private static nint Call(nint cmd, nint a0, nint a1, nint a2, nint a3) =>
        _syscall(cmd, a0, a1, a2, a3, 0, 0, 0, 0, 0, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Call(nint cmd, nint a0, nint a1, nint a2, nint a3, nint a4) =>
        _syscall(cmd, a0, a1, a2, a3, a4, 0, 0, 0, 0, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Call(nint cmd, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5) =>
        _syscall(cmd, a0, a1, a2, a3, a4, a5, 0, 0, 0, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Call(nint cmd, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8) =>
        _syscall(cmd, a0, a1, a2, a3, a4, a5, a6, a7, a8, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PassFloat(float f) => BitConverter.SingleToInt32Bits(f);

    // Syscall command IDs (from cg_public.h cgameImport_t)
    private const int CG_PRINT = 0;
    private const int CG_ERROR = 1;
    private const int CG_MILLISECONDS = 2;
    private const int CG_CVAR_REGISTER = 3;
    private const int CG_CVAR_UPDATE = 4;
    private const int CG_CVAR_SET = 5;
    private const int CG_CVAR_VARIABLESTRINGBUFFER = 6;
    private const int CG_ARGC = 7;
    private const int CG_ARGV = 8;
    private const int CG_ARGS = 9;
    private const int CG_R_REGISTERSHADER = 39;
    private const int CG_R_REGISTERSHADERNOMIP = 54;
    private const int CG_R_CLEARSCENE = 40;
    private const int CG_R_ADDREFENTITYTOSCENE = 41;
    private const int CG_R_ADDPOLYTOSCENE = 42;
    private const int CG_R_ADDLIGHTTOSCENE = 43;
    private const int CG_R_RENDERSCENE = 44;
    private const int CG_R_SETCOLOR = 45;
    private const int CG_R_DRAWSTRETCHPIC = 46;
    private const int CG_R_REGISTERFONT = 56;
    private const int CG_R_REGISTERMODEL = 37;
    private const int CG_S_REGISTERSOUND = 34;
    private const int CG_S_STARTLOCALSOUND = 29;
    private const int CG_ADDCOMMAND = 15;
    private const int CG_REMOVECOMMAND = 69;
    private const int CG_SENDCONSOLECOMMAND = 14;

    // --- Print ---
    public static void Print(string msg)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(msg + '\0'))
            Call(CG_PRINT, (nint)p);
    }

    // --- Cvars ---
    public static void CvarSet(string name, string value)
    {
        fixed (byte* pName = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
        fixed (byte* pValue = System.Text.Encoding.UTF8.GetBytes(value + '\0'))
            Call(CG_CVAR_SET, (nint)pName, (nint)pValue);
    }

    public static string CvarGetString(string name)
    {
        byte* buf = stackalloc byte[256];
        fixed (byte* pName = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            Call(CG_CVAR_VARIABLESTRINGBUFFER, (nint)pName, (nint)buf, 256);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    // --- Command args ---
    public static int Argc() => (int)Call(CG_ARGC);

    public static string Argv(int n)
    {
        byte* buf = stackalloc byte[1024];
        Call(CG_ARGV, n, (nint)buf, 1024);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    // --- Time ---
    public static int Milliseconds() => (int)Call(CG_MILLISECONDS);

    // --- Renderer ---
    public static int R_RegisterShader(string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(CG_R_REGISTERSHADER, (nint)p);
    }

    public static int R_RegisterShaderNoMip(string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(CG_R_REGISTERSHADERNOMIP, (nint)p);
    }

    public static void R_SetColor(float r, float g, float b, float a)
    {
        float* color = stackalloc float[4];
        color[0] = r; color[1] = g; color[2] = b; color[3] = a;
        Call(CG_R_SETCOLOR, (nint)color);
    }

    public static void R_SetColor(float* rgba)
    {
        Call(CG_R_SETCOLOR, (nint)rgba);
    }

    public static void R_DrawStretchPic(float x, float y, float w, float h,
        float s1, float t1, float s2, float t2, int shader)
    {
        Call(CG_R_DRAWSTRETCHPIC,
            PassFloat(x), PassFloat(y), PassFloat(w), PassFloat(h),
            PassFloat(s1), PassFloat(t1), PassFloat(s2), PassFloat(t2),
            shader);
    }

    // --- Sound ---
    public static int S_RegisterSound(string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(CG_S_REGISTERSOUND, (nint)p, 0);
    }

    public static void S_StartLocalSound(int sfx, int channel)
    {
        Call(CG_S_STARTLOCALSOUND, sfx, channel);
    }

    // --- Commands ---
    public static void AddCommand(string cmd)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(cmd + '\0'))
            Call(CG_ADDCOMMAND, (nint)p);
    }

    public static void RemoveCommand(string cmd)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(cmd + '\0'))
            Call(CG_REMOVECOMMAND, (nint)p);
    }

    public static void SendConsoleCommand(string cmd)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(cmd + '\0'))
            Call(CG_SENDCONSOLECOMMAND, (nint)p);
    }
}
