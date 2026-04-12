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
    private const int UI_FS_GETFILELIST = 17;
    private const int UI_R_REGISTERMODEL = 18;
    private const int UI_R_REGISTERSKIN = 19;
    private const int UI_R_REGISTERSHADERNOMIP = 20;
    private const int UI_R_CLEARSCENE = 21;
    private const int UI_R_ADDREFENTITYTOSCENE = 22;
    private const int UI_R_ADDLIGHTTOSCENE = 24;
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
    private const int UI_CM_LERPTAG = 29;
    private const int UI_GETCLIENTSTATE = 44;
    private const int UI_GETCONFIGSTRING = 45;
    private const int UI_CVAR_REGISTER = 50;
    private const int UI_R_REGISTERFONT = 55;

    // LAN syscalls
    private const int UI_LAN_GETSERVERCOUNT = 65;
    private const int UI_LAN_GETSERVERADDRESSSTRING = 66;
    private const int UI_LAN_GETSERVERINFO = 67;
    private const int UI_LAN_MARKSERVERVISIBLE = 68;
    private const int UI_LAN_UPDATEVISIBLEPINGS = 69;
    private const int UI_LAN_RESETPINGS = 70;
    private const int UI_LAN_LOADCACHEDSERVERS = 71;
    private const int UI_LAN_SAVECACHEDSERVERS = 72;
    private const int UI_LAN_ADDSERVER = 73;
    private const int UI_LAN_REMOVESERVER = 74;
    private const int UI_LAN_SERVERSTATUS = 82;
    private const int UI_LAN_GETSERVERPING = 83;
    private const int UI_LAN_SERVERISVISIBLE = 84;

    private const int UI_GETCLIPBOARDDATA = 42;

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

    public static int R_RegisterModel(string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(UI_R_REGISTERMODEL, (nint)p);
    }

    public static int R_RegisterSkin(string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(UI_R_REGISTERSKIN, (nint)p);
    }

    public static void R_ClearScene() => Call(UI_R_CLEARSCENE);

    public static void R_AddRefEntityToScene(RefEntity* ent) =>
        Call(UI_R_ADDREFENTITYTOSCENE, (nint)ent);

    public static void R_AddLightToScene(float* org, float intensity, float r, float g, float b) =>
        Call(UI_R_ADDLIGHTTOSCENE, (nint)org, PassFloat(intensity), PassFloat(r), PassFloat(g), PassFloat(b));

    public static void R_RenderScene(RefDef* rd) =>
        Call(UI_R_RENDERSCENE, (nint)rd);

    public static int CM_LerpTag(TagOrientation* tag, int model, int startFrame, int endFrame, float frac, string tagName)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(tagName + '\0'))
            return (int)Call(UI_CM_LERPTAG, (nint)tag, model, startFrame, endFrame, PassFloat(frac), (nint)p);
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

    public static string GetBindingBuf(int keyNum)
    {
        byte[] buf = new byte[256];
        fixed (byte* pBuf = buf)
        {
            Call(UI_KEY_GETBINDINGBUF, keyNum, (nint)pBuf, buf.Length);
        }
        return System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0').Trim();
    }

    private static int KeyNameToNum(string name)
    {
        // Map common key names to Q3 key numbers (from keycodes.h)
        if (name.Length == 1 && name[0] >= 'a' && name[0] <= 'z') return name[0];
        if (name.Length == 1 && name[0] >= '0' && name[0] <= '9') return name[0];
        return name.ToUpperInvariant() switch
        {
            "SPACE" => 32,
            "ENTER" => 13,
            "TAB" => 9,
            "ESCAPE" => 27,
            "BACKSPACE" => 127,
            "UPARROW" => 132,
            "DOWNARROW" => 133,
            "LEFTARROW" => 134,
            "RIGHTARROW" => 135,
            "ALT" => 136,
            "CTRL" => 137,
            "SHIFT" => 138,
            "INS" => 139,
            "DEL" => 140,
            "PGDN" => 141,
            "PGUP" => 142,
            "HOME" => 143,
            "END" => 144,
            "F1" => 145, "F2" => 146, "F3" => 147, "F4" => 148,
            "F5" => 149, "F6" => 150, "F7" => 151, "F8" => 152,
            "F9" => 153, "F10" => 154, "F11" => 155, "F12" => 156,
            "KP_HOME" => 160, "KP_UPARROW" => 161, "KP_PGUP" => 162,
            "KP_LEFTARROW" => 163, "KP_5" => 164, "KP_RIGHTARROW" => 165,
            "KP_END" => 166, "KP_DOWNARROW" => 167, "KP_PGDN" => 168,
            "KP_ENTER" => 169, "KP_INS" => 170, "KP_DEL" => 171,
            "KP_SLASH" => 172, "KP_MINUS" => 173, "KP_PLUS" => 174,
            "KP_STAR" => 176,
            "MOUSE1" => 178,
            "MOUSE2" => 179,
            "MOUSE3" => 180,
            "MOUSE4" => 181,
            "MOUSE5" => 182,
            "MWHEELDOWN" => 183,
            "MWHEELUP" => 184,
            _ when name.Length == 1 && name[0] >= 32 && name[0] < 127 => name[0],
            _ => 0
        };
    }

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

    // --- Filesystem ---
    public static string[] FS_GetFileList(string path, string extension)
    {
        byte[] buf = new byte[8192];
        fixed (byte* pPath = System.Text.Encoding.UTF8.GetBytes(path + '\0'))
        fixed (byte* pExt = System.Text.Encoding.UTF8.GetBytes(extension + '\0'))
        fixed (byte* pBuf = buf)
        {
            int count = (int)Call(UI_FS_GETFILELIST, (nint)pPath, (nint)pExt, (nint)pBuf, 8192);
            if (count <= 0) return [];

            var results = new string[count];
            int offset = 0;
            for (int i = 0; i < count && offset < 8192; i++)
            {
                int start = offset;
                while (offset < 8192 && pBuf[offset] != 0) offset++;
                results[i] = System.Text.Encoding.UTF8.GetString(pBuf + start, offset - start);
                offset++;
            }
            return results;
        }
    }

    // --- LAN Server Browser ---
    public static void LAN_LoadCachedServers() => Call(UI_LAN_LOADCACHEDSERVERS);
    public static void LAN_SaveCachedServers() => Call(UI_LAN_SAVECACHEDSERVERS);

    public static int LAN_GetServerCount(int source) => (int)Call(UI_LAN_GETSERVERCOUNT, source);

    public static string LAN_GetServerAddressString(int source, int index)
    {
        byte* buf = stackalloc byte[256];
        Call(UI_LAN_GETSERVERADDRESSSTRING, source, index, (nint)buf, 256);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    public static string LAN_GetServerInfo(int source, int index)
    {
        byte[] buf = new byte[1024];
        fixed (byte* pBuf = buf)
        {
            Call(UI_LAN_GETSERVERINFO, source, index, (nint)pBuf, 1024);
            return Marshal.PtrToStringUTF8((nint)pBuf) ?? "";
        }
    }

    public static int LAN_GetServerPing(int source, int index) =>
        (int)Call(UI_LAN_GETSERVERPING, source, index);

    public static void LAN_MarkServerVisible(int source, int index, int visible) =>
        Call(UI_LAN_MARKSERVERVISIBLE, source, index, visible);

    public static int LAN_UpdateVisiblePings(int source) =>
        (int)Call(UI_LAN_UPDATEVISIBLEPINGS, source);

    public static void LAN_ResetPings(int source) =>
        Call(UI_LAN_RESETPINGS, source);

    public static int LAN_AddServer(int source, string name, string address)
    {
        fixed (byte* pName = System.Text.Encoding.UTF8.GetBytes(name + '\0'))
        fixed (byte* pAddr = System.Text.Encoding.UTF8.GetBytes(address + '\0'))
            return (int)Call(UI_LAN_ADDSERVER, source, (nint)pName, (nint)pAddr);
    }

    public static void LAN_RemoveServer(int source, string address)
    {
        fixed (byte* pAddr = System.Text.Encoding.UTF8.GetBytes(address + '\0'))
            Call(UI_LAN_REMOVESERVER, source, (nint)pAddr);
    }

    public static int LAN_ServerIsVisible(int source, int index) =>
        (int)Call(UI_LAN_SERVERISVISIBLE, source, index);

    // --- Clipboard ---
    public static string GetClipboardData()
    {
        byte* buf = stackalloc byte[256];
        Call(UI_GETCLIPBOARDDATA, (nint)buf, 256);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    // --- Key name ---
    public static string KeyNumToString(int keyNum)
    {
        byte* buf = stackalloc byte[64];
        Call(UI_KEY_KEYNUMTOSTRINGBUF, keyNum, (nint)buf, 64);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }
}
