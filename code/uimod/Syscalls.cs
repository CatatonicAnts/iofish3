using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UiMod;

/// <summary>
/// Engine syscall wrappers for the UI module.
/// Uses the same syscall function pointer that the C UI DLL receives via dllEntry.
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
    private static nint Call(nint cmd, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8) =>
        _syscall(cmd, a0, a1, a2, a3, a4, a5, a6, a7, a8, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PassFloat(float f) => BitConverter.SingleToInt32Bits(f);

    // Syscall IDs (from ui_public.h uiImport_t)
    private const int UI_ERROR = 0;
    private const int UI_PRINT = 1;
    private const int UI_MILLISECONDS = 2;
    private const int UI_CVAR_SET = 3;
    private const int UI_CVAR_VARIABLEVALUE = 4;
    private const int UI_CVAR_VARIABLESTRINGBUFFER = 5;
    private const int UI_CVAR_SETVALUE = 6;
    private const int UI_ARGC = 10;
    private const int UI_ARGV = 11;
    private const int UI_CMD_EXECUTETEXT = 12;
    private const int UI_R_REGISTERMODEL = 18;
    private const int UI_R_REGISTERSHADERNOMIP = 20;
    private const int UI_R_CLEARSCENE = 21;
    private const int UI_R_ADDREFENTITYTOSCENE = 22;
    private const int UI_R_RENDERSCENE = 25;
    private const int UI_R_SETCOLOR = 26;
    private const int UI_R_DRAWSTRETCHPIC = 27;
    private const int UI_S_REGISTERSOUND = 31;
    private const int UI_S_STARTLOCALSOUND = 32;
    private const int UI_KEY_KEYNUMTOSTRINGBUF = 33;
    private const int UI_KEY_GETBINDINGBUF = 34;
    private const int UI_KEY_SETBINDING = 35;
    private const int UI_KEY_ISDOWN = 36;
    private const int UI_KEY_GETCATCHER = 40;
    private const int UI_KEY_SETCATCHER = 41;
    private const int UI_GETGLCONFIG = 43;
    private const int UI_GETCLIENTSTATE = 44;
    private const int UI_GETCONFIGSTRING = 45;
    private const int UI_CVAR_REGISTER = 50;
    private const int UI_R_REGISTERFONT = 55;

    // Exec types
    public const int EXEC_NOW = 0;
    public const int EXEC_INSERT = 1;
    public const int EXEC_APPEND = 2;

    // --- Print ---
    public static void Print(string msg)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(msg + '\0'))
            Call(UI_PRINT, (nint)p);
    }

    // --- Milliseconds ---
    public static int Milliseconds() => (int)Call(UI_MILLISECONDS);

    // --- Cvars ---
    public static void CvarSet(string name, string value)
    {
        fixed (byte* pName = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
        fixed (byte* pValue = System.Text.Encoding.UTF8.GetBytes(value + '\0'))
            Call(UI_CVAR_SET, (nint)pName, (nint)pValue);
    }

    public static string CvarGetString(string name)
    {
        byte* buf = stackalloc byte[256];
        fixed (byte* pName = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            Call(UI_CVAR_VARIABLESTRINGBUFFER, (nint)pName, (nint)buf, 256);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    public static float CvarGetValue(string name)
    {
        fixed (byte* pName = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            return BitConverter.Int32BitsToSingle((int)Call(UI_CVAR_VARIABLEVALUE, (nint)pName));
    }

    // --- Commands ---
    public static int Argc() => (int)Call(UI_ARGC);

    public static string Argv(int n)
    {
        byte* buf = stackalloc byte[1024];
        Call(UI_ARGV, n, (nint)buf, 1024);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    public static void ExecuteCommand(string cmd, int execType = EXEC_APPEND)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(cmd + '\0'))
            Call(UI_CMD_EXECUTETEXT, execType, (nint)p);
    }

    // --- Renderer ---
    public static int R_RegisterShaderNoMip(string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(UI_R_REGISTERSHADERNOMIP, (nint)p);
    }

    public static void R_SetColor(float r, float g, float b, float a)
    {
        float* color = stackalloc float[4] { r, g, b, a };
        Call(UI_R_SETCOLOR, (nint)color);
    }

    public static void R_ClearColor()
    {
        Call(UI_R_SETCOLOR, 0);
    }

    public static void R_DrawStretchPic(float x, float y, float w, float h,
        float s1, float t1, float s2, float t2, int shader)
    {
        Call(UI_R_DRAWSTRETCHPIC,
            PassFloat(x), PassFloat(y), PassFloat(w), PassFloat(h),
            PassFloat(s1), PassFloat(t1), PassFloat(s2), PassFloat(t2),
            shader);
    }

    // --- Sound ---
    public static int S_RegisterSound(string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(UI_S_REGISTERSOUND, (nint)p, 0);
    }

    public static void S_StartLocalSound(int sfx, int channel)
    {
        Call(UI_S_STARTLOCALSOUND, sfx, channel);
    }

    // --- Key ---
    public static int Key_GetCatcher() => (int)Call(UI_KEY_GETCATCHER);

    public static void Key_SetCatcher(int catcher)
    {
        Call(UI_KEY_SETCATCHER, catcher);
    }

    public static bool Key_IsDown(int key) => Call(UI_KEY_ISDOWN, key) != 0;

    // --- Config ---
    public static string GetConfigString(int index)
    {
        byte* buf = stackalloc byte[1024];
        Call(UI_GETCONFIGSTRING, index, (nint)buf, 1024);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    /// <summary>
    /// Read glconfig_t from the engine and extract vidWidth/vidHeight.
    /// glconfig_t is ~11332 bytes; vidWidth at offset 11304, vidHeight at 11308.
    /// </summary>
    public static (int width, int height) GetGlConfig()
    {
        const int GLCONFIG_SIZE = 11340;
        const int VID_WIDTH_OFFSET = 11304;
        const int VID_HEIGHT_OFFSET = 11308;

        byte[] buf = new byte[GLCONFIG_SIZE];
        fixed (byte* p = buf)
        {
            Call(UI_GETGLCONFIG, (nint)p);
            int w = *(int*)(p + VID_WIDTH_OFFSET);
            int h = *(int*)(p + VID_HEIGHT_OFFSET);
            return (w, h);
        }
    }
}
