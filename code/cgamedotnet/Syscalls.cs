using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace CGameDotNet;

/// <summary>
/// Type-safe C# wrappers for the Q3 engine syscalls (cgameImport_t).
/// The syscall function pointer is received via dllEntry and stored here.
/// On Windows x64, variadic __cdecl passes all integer-sized args the same
/// as non-variadic, so we can call it as a fixed-arg function pointer.
/// </summary>
public static unsafe class Syscalls
{
    // Syscall function pointer: intptr_t (*)(intptr_t arg, ...)
    // We declare it with 13 nint params (command + up to 12 args) to cover all Q3 syscalls.
    private static delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint> _syscall;

    public static void Init(nint syscallPtr)
    {
        _syscall = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint, nint>)syscallPtr;
    }

    // Helper to call syscall with varying arg counts (unused args zeroed)
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
    private static nint Call(nint cmd, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6) =>
        _syscall(cmd, a0, a1, a2, a3, a4, a5, a6, 0, 0, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Call(nint cmd, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7) =>
        _syscall(cmd, a0, a1, a2, a3, a4, a5, a6, a7, 0, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Call(nint cmd, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8) =>
        _syscall(cmd, a0, a1, a2, a3, a4, a5, a6, a7, a8, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Call(nint cmd, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8, nint a9) =>
        _syscall(cmd, a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, 0, 0);

    // Q3 uses PASSFLOAT to bit-cast float→int for syscalls
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint PassFloat(float f) => BitConverter.SingleToInt32Bits(f);

    // cgameImport_t command IDs (from cg_public.h)
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
    private const int CG_FS_FOPENFILE = 10;
    private const int CG_FS_READ = 11;
    private const int CG_FS_WRITE = 12;
    private const int CG_FS_FCLOSEFILE = 13;
    private const int CG_SENDCONSOLECOMMAND = 14;
    private const int CG_ADDCOMMAND = 15;
    private const int CG_SENDCLIENTCOMMAND = 16;
    private const int CG_UPDATESCREEN = 17;
    private const int CG_CM_LOADMAP = 18;
    private const int CG_CM_NUMINLINEMODELS = 19;
    private const int CG_CM_INLINEMODEL = 20;
    private const int CG_CM_LOADMODEL = 21;
    private const int CG_CM_TEMPBOXMODEL = 22;
    private const int CG_CM_POINTCONTENTS = 23;
    private const int CG_CM_TRANSFORMEDPOINTCONTENTS = 24;
    private const int CG_CM_BOXTRACE = 25;
    private const int CG_CM_TRANSFORMEDBOXTRACE = 26;
    private const int CG_CM_MARKFRAGMENTS = 27;
    private const int CG_S_STARTSOUND = 28;
    private const int CG_S_STARTLOCALSOUND = 29;
    private const int CG_S_CLEARLOOPINGSOUNDS = 30;
    private const int CG_S_ADDLOOPINGSOUND = 31;
    private const int CG_S_UPDATEENTITYPOSITION = 32;
    private const int CG_S_RESPATIALIZE = 33;
    private const int CG_S_REGISTERSOUND = 34;
    private const int CG_S_STARTBACKGROUNDTRACK = 35;
    private const int CG_R_LOADWORLDMAP = 36;
    private const int CG_R_REGISTERMODEL = 37;
    private const int CG_R_REGISTERSKIN = 38;
    private const int CG_R_REGISTERSHADER = 39;
    private const int CG_R_CLEARSCENE = 40;
    private const int CG_R_ADDREFENTITYTOSCENE = 41;
    private const int CG_R_ADDPOLYTOSCENE = 42;
    private const int CG_R_ADDLIGHTTOSCENE = 43;
    private const int CG_R_RENDERSCENE = 44;
    private const int CG_R_SETCOLOR = 45;
    private const int CG_R_DRAWSTRETCHPIC = 46;
    private const int CG_R_MODELBOUNDS = 47;
    private const int CG_R_LERPTAG = 48;
    private const int CG_GETGLCONFIG = 49;
    private const int CG_GETGAMESTATE = 50;
    private const int CG_GETCURRENTSNAPSHOTNUMBER = 51;
    private const int CG_GETSNAPSHOT = 52;
    private const int CG_GETSERVERCOMMAND = 53;
    private const int CG_GETCURRENTCMDNUMBER = 54;
    private const int CG_GETUSERCMD = 55;
    private const int CG_SETUSERCMDVALUE = 56;
    private const int CG_R_REGISTERSHADERNOMIP = 57;
    private const int CG_MEMORY_REMAINING = 58;
    private const int CG_R_REGISTERFONT = 59;
    private const int CG_KEY_ISDOWN = 60;
    private const int CG_KEY_GETCATCHER = 61;
    private const int CG_KEY_SETCATCHER = 62;
    private const int CG_KEY_GETKEY = 63;
    private const int CG_PC_ADD_GLOBAL_DEFINE = 64;
    private const int CG_PC_LOAD_SOURCE = 65;
    private const int CG_PC_FREE_SOURCE = 66;
    private const int CG_PC_READ_TOKEN = 67;
    private const int CG_PC_SOURCE_FILE_AND_LINE = 68;
    private const int CG_S_STOPBACKGROUNDTRACK = 69;
    private const int CG_REAL_TIME = 70;
    private const int CG_SNAPVECTOR = 71;
    private const int CG_REMOVECOMMAND = 72;
    private const int CG_R_LIGHTFORPOINT = 73;
    private const int CG_CIN_PLAYCINEMATIC = 74;
    private const int CG_CIN_STOPCINEMATIC = 75;
    private const int CG_CIN_RUNCINEMATIC = 76;
    private const int CG_CIN_DRAWCINEMATIC = 77;
    private const int CG_CIN_SETEXTENTS = 78;
    private const int CG_R_REMAP_SHADER = 79;
    private const int CG_S_ADDREALLOOPINGSOUND = 80;
    private const int CG_S_STOPLOOPINGSOUND = 81;
    private const int CG_CM_TEMPCAPSULEMODEL = 82;
    private const int CG_CM_CAPSULETRACE = 83;
    private const int CG_CM_TRANSFORMEDCAPSULETRACE = 84;
    private const int CG_R_ADDADDITIVELIGHTTOSCENE = 85;
    private const int CG_GET_ENTITY_TOKEN = 86;
    private const int CG_R_ADDPOLYSTOSCENE = 87;
    private const int CG_R_INPVS = 88;
    private const int CG_FS_SEEK = 89;

    // ── Console / Error ──

    public static void Print(string msg)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(msg + '\0'))
            Call(CG_PRINT, (nint)p);
    }

    public static void Error(string msg)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(msg + '\0'))
            Call(CG_ERROR, (nint)p);
    }

    public static int Milliseconds() => (int)Call(CG_MILLISECONDS);

    // ── Cvars ──

    public static void CvarRegister(void* vmCvar, string varName, string defaultValue, int flags)
    {
        fixed (byte* name = Encoding.UTF8.GetBytes(varName + '\0'))
        fixed (byte* def = Encoding.UTF8.GetBytes(defaultValue + '\0'))
            Call(CG_CVAR_REGISTER, (nint)vmCvar, (nint)name, (nint)def, flags);
    }

    public static void CvarUpdate(void* vmCvar) =>
        Call(CG_CVAR_UPDATE, (nint)vmCvar);

    public static void CvarSet(string varName, string value)
    {
        fixed (byte* name = Encoding.UTF8.GetBytes(varName + '\0'))
        fixed (byte* val = Encoding.UTF8.GetBytes(value + '\0'))
            Call(CG_CVAR_SET, (nint)name, (nint)val);
    }

    public static string CvarGetString(string varName)
    {
        byte* buf = stackalloc byte[1024];
        fixed (byte* name = Encoding.UTF8.GetBytes(varName + '\0'))
            Call(CG_CVAR_VARIABLESTRINGBUFFER, (nint)name, (nint)buf, 1024);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    // ── Command arguments ──

    public static int Argc() => (int)Call(CG_ARGC);

    public static string Argv(int n)
    {
        byte* buf = stackalloc byte[1024];
        Call(CG_ARGV, n, (nint)buf, 1024);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    public static string Args()
    {
        byte* buf = stackalloc byte[1024];
        Call(CG_ARGS, (nint)buf, 1024);
        return Marshal.PtrToStringUTF8((nint)buf) ?? "";
    }

    // ── Filesystem ──

    public static int FOpenFile(string path, int* handle, int mode)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(path + '\0'))
            return (int)Call(CG_FS_FOPENFILE, (nint)p, (nint)handle, mode);
    }

    public static void FRead(void* buffer, int len, int handle) =>
        Call(CG_FS_READ, (nint)buffer, len, handle);

    public static void FWrite(void* buffer, int len, int handle) =>
        Call(CG_FS_WRITE, (nint)buffer, len, handle);

    public static void FCloseFile(int handle) =>
        Call(CG_FS_FCLOSEFILE, handle);

    // ── Console commands ──

    public static void SendConsoleCommand(string text)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(text + '\0'))
            Call(CG_SENDCONSOLECOMMAND, (nint)p);
    }

    public static void AddCommand(string cmdName)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(cmdName + '\0'))
            Call(CG_ADDCOMMAND, (nint)p);
    }

    public static void RemoveCommand(string cmdName)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(cmdName + '\0'))
            Call(CG_REMOVECOMMAND, (nint)p);
    }

    public static void SendClientCommand(string s)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(s + '\0'))
            Call(CG_SENDCLIENTCOMMAND, (nint)p);
    }

    // ── Screen ──

    public static void UpdateScreen() => Call(CG_UPDATESCREEN);

    // ── Collision ──

    public static void CM_LoadMap(string mapname)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(mapname + '\0'))
            Call(CG_CM_LOADMAP, (nint)p);
    }

    public static int CM_NumInlineModels() => (int)Call(CG_CM_NUMINLINEMODELS);
    public static int CM_InlineModel(int index) => (int)Call(CG_CM_INLINEMODEL, index);

    public static int CM_TempBoxModel(float* mins, float* maxs) =>
        (int)Call(CG_CM_TEMPBOXMODEL, (nint)mins, (nint)maxs);

    public static int CM_PointContents(float* point, int model) =>
        (int)Call(CG_CM_POINTCONTENTS, (nint)point, model);

    public static int CM_TransformedPointContents(float* point, int model, float* origin, float* angles) =>
        (int)Call(CG_CM_TRANSFORMEDPOINTCONTENTS, (nint)point, model, (nint)origin, (nint)angles);

    public static void CM_BoxTrace(void* results, float* start, float* end,
                                    float* mins, float* maxs, int model, int brushmask) =>
        Call(CG_CM_BOXTRACE, (nint)results, (nint)start, (nint)end,
            (nint)mins, (nint)maxs, model, brushmask);

    public static void CM_TransformedBoxTrace(void* results, float* start, float* end,
                                               float* mins, float* maxs, int model, int brushmask,
                                               float* origin, float* angles) =>
        Call(CG_CM_TRANSFORMEDBOXTRACE, (nint)results, (nint)start, (nint)end,
            (nint)mins, (nint)maxs, model, brushmask, (nint)origin, (nint)angles);

    public static int CM_MarkFragments(int numPoints, float* points, float* projection,
                                        int maxPoints, float* pointBuffer,
                                        int maxFragments, void* fragmentBuffer) =>
        (int)Call(CG_CM_MARKFRAGMENTS, numPoints, (nint)points, (nint)projection,
            maxPoints, (nint)pointBuffer, maxFragments, (nint)fragmentBuffer);

    // ── Sound ──

    public static void S_StartSound(float* origin, int entityNum, int channel, int sfx) =>
        Call(CG_S_STARTSOUND, (nint)origin, entityNum, channel, sfx);

    public static void S_StartLocalSound(int sfx, int channel) =>
        Call(CG_S_STARTLOCALSOUND, sfx, channel);

    public static void S_ClearLoopingSounds(int killall) =>
        Call(CG_S_CLEARLOOPINGSOUNDS, killall);

    public static void S_AddLoopingSound(int entityNum, float* origin, float* velocity, int sfx) =>
        Call(CG_S_ADDLOOPINGSOUND, entityNum, (nint)origin, (nint)velocity, sfx);

    public static void S_UpdateEntityPosition(int entityNum, float* origin) =>
        Call(CG_S_UPDATEENTITYPOSITION, entityNum, (nint)origin);

    public static void S_Respatialize(int entityNum, float* origin, float* axis, int inwater) =>
        Call(CG_S_RESPATIALIZE, entityNum, (nint)origin, (nint)axis, inwater);

    public static int S_RegisterSound(string sample, int compressed)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(sample + '\0'))
            return (int)Call(CG_S_REGISTERSOUND, (nint)p, compressed);
    }

    public static void S_StartBackgroundTrack(string intro, string loop)
    {
        fixed (byte* pi = Encoding.UTF8.GetBytes(intro + '\0'))
        fixed (byte* pl = Encoding.UTF8.GetBytes(loop + '\0'))
            Call(CG_S_STARTBACKGROUNDTRACK, (nint)pi, (nint)pl);
    }

    public static void S_StopBackgroundTrack() => Call(CG_S_STOPBACKGROUNDTRACK);

    public static void S_StopLoopingSound(int entityNum) =>
        Call(CG_S_STOPLOOPINGSOUND, entityNum);

    // ── Renderer ──

    public static void R_LoadWorldMap(string mapname)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(mapname + '\0'))
            Call(CG_R_LOADWORLDMAP, (nint)p);
    }

    public static int R_RegisterModel(string name)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(CG_R_REGISTERMODEL, (nint)p);
    }

    public static int R_RegisterSkin(string name)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(CG_R_REGISTERSKIN, (nint)p);
    }

    public static int R_RegisterShader(string name)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(CG_R_REGISTERSHADER, (nint)p);
    }

    public static int R_RegisterShaderNoMip(string name)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(name + '\0'))
            return (int)Call(CG_R_REGISTERSHADERNOMIP, (nint)p);
    }

    public static void R_RegisterFont(string fontName, int pointSize, void* font)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(fontName + '\0'))
            Call(CG_R_REGISTERFONT, (nint)p, pointSize, (nint)font);
    }

    public static void R_ClearScene() => Call(CG_R_CLEARSCENE);

    public static void R_AddRefEntityToScene(void* entity) =>
        Call(CG_R_ADDREFENTITYTOSCENE, (nint)entity);

    public static void R_AddPolyToScene(int shader, int numVerts, void* verts) =>
        Call(CG_R_ADDPOLYTOSCENE, shader, numVerts, (nint)verts);

    public static void R_AddLightToScene(float* origin, float intensity, float r, float g, float b) =>
        Call(CG_R_ADDLIGHTTOSCENE, (nint)origin, PassFloat(intensity), PassFloat(r), PassFloat(g), PassFloat(b));

    public static void R_RenderScene(void* refdef) =>
        Call(CG_R_RENDERSCENE, (nint)refdef);

    public static void R_SetColor(float* rgba) =>
        Call(CG_R_SETCOLOR, (nint)rgba);

    public static void R_DrawStretchPic(float x, float y, float w, float h,
                                         float s1, float t1, float s2, float t2, int shader) =>
        Call(CG_R_DRAWSTRETCHPIC, PassFloat(x), PassFloat(y), PassFloat(w), PassFloat(h),
            PassFloat(s1), PassFloat(t1), PassFloat(s2), PassFloat(t2), shader);

    public static void R_ModelBounds(int model, float* mins, float* maxs) =>
        Call(CG_R_MODELBOUNDS, model, (nint)mins, (nint)maxs);

    public static int R_LerpTag(void* tag, int model, int startFrame, int endFrame, float frac, string tagName)
    {
        fixed (byte* p = Encoding.UTF8.GetBytes(tagName + '\0'))
            return (int)Call(CG_R_LERPTAG, (nint)tag, model, startFrame, endFrame, PassFloat(frac), (nint)p);
    }

    public static void R_RemapShader(string oldShader, string newShader, string timeOffset)
    {
        fixed (byte* po = Encoding.UTF8.GetBytes(oldShader + '\0'))
        fixed (byte* pn = Encoding.UTF8.GetBytes(newShader + '\0'))
        fixed (byte* pt = Encoding.UTF8.GetBytes(timeOffset + '\0'))
            Call(CG_R_REMAP_SHADER, (nint)po, (nint)pn, (nint)pt);
    }

    // ── Game state / Snapshots ──

    public static void GetGlconfig(void* glconfig) =>
        Call(CG_GETGLCONFIG, (nint)glconfig);

    public static void GetGameState(void* gamestate) =>
        Call(CG_GETGAMESTATE, (nint)gamestate);

    public static void GetCurrentSnapshotNumber(int* snapshotNumber, int* serverTime) =>
        Call(CG_GETCURRENTSNAPSHOTNUMBER, (nint)snapshotNumber, (nint)serverTime);

    public static bool GetSnapshot(int snapshotNumber, void* snapshot) =>
        Call(CG_GETSNAPSHOT, snapshotNumber, (nint)snapshot) != 0;

    public static bool GetServerCommand(int serverCommandNumber) =>
        Call(CG_GETSERVERCOMMAND, serverCommandNumber) != 0;

    public static int GetCurrentCmdNumber() => (int)Call(CG_GETCURRENTCMDNUMBER);

    public static bool GetUserCmd(int cmdNumber, void* ucmd) =>
        Call(CG_GETUSERCMD, cmdNumber, (nint)ucmd) != 0;

    public static void SetUserCmdValue(int stateValue, float sensitivityScale) =>
        Call(CG_SETUSERCMDVALUE, stateValue, PassFloat(sensitivityScale));

    // ── Keys ──

    public static bool Key_IsDown(int keynum) => Call(CG_KEY_ISDOWN, keynum) != 0;
    public static int Key_GetCatcher() => (int)Call(CG_KEY_GETCATCHER);
    public static void Key_SetCatcher(int catcher) => Call(CG_KEY_SETCATCHER, catcher);

    // ── Entity token ──

    public static bool GetEntityToken(byte* buffer, int bufferSize) =>
        Call(CG_GET_ENTITY_TOKEN, (nint)buffer, bufferSize) != 0;

    // ── Misc ──

    public static int MemoryRemaining() => (int)Call(CG_MEMORY_REMAINING);
}
