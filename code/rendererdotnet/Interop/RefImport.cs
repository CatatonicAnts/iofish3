using System.Runtime.InteropServices;

namespace RendererDotNet.Interop;

/// <summary>
/// Mirrors the C refimport_t struct from tr_public.h.
/// These are the functions imported by the renderer from the engine.
/// Each field is a native function pointer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RefImport
{
    public nint Printf;                 // void (QDECL *Printf)(int printLevel, const char *fmt, ...)
    public nint Error;                  // void (QDECL *Error)(int errorLevel, const char *fmt, ...)
    public nint Milliseconds;           // int (*Milliseconds)(void)
    public nint Hunk_Alloc;             // void *(*Hunk_Alloc)(int size, ha_pref pref)
    public nint Hunk_AllocateTempMemory;// void *(*Hunk_AllocateTempMemory)(int size)
    public nint Hunk_FreeTempMemory;    // void (*Hunk_FreeTempMemory)(void *block)
    public nint Malloc;                 // void *(*Malloc)(int bytes)
    public nint Free;                   // void (*Free)(void *buf)
    public nint Cvar_Get;               // cvar_t *(*Cvar_Get)(const char*, const char*, int)
    public nint Cvar_Set;               // void (*Cvar_Set)(const char*, const char*)
    public nint Cvar_SetValue;          // void (*Cvar_SetValue)(const char*, float)
    public nint Cvar_CheckRange;        // void (*Cvar_CheckRange)(cvar_t*, float, float, qboolean)
    public nint Cvar_SetDescription;    // void (*Cvar_SetDescription)(cvar_t*, const char*)
    public nint Cvar_VariableIntegerValue; // int (*Cvar_VariableIntegerValue)(const char*)
    public nint Cmd_AddCommand;         // void (*Cmd_AddCommand)(const char*, void(*cmd)(void))
    public nint Cmd_RemoveCommand;      // void (*Cmd_RemoveCommand)(const char*)
    public nint Cmd_Argc;               // int (*Cmd_Argc)(void)
    public nint Cmd_Argv;               // char *(*Cmd_Argv)(int)
    public nint Cmd_ExecuteText;        // void (*Cmd_ExecuteText)(int, const char*)
    public nint CM_ClusterPVS;          // byte *(*CM_ClusterPVS)(int)
    public nint CM_DrawDebugSurface;    // void (*CM_DrawDebugSurface)(void (*drawPoly)(...))
    public nint FS_FileIsInPAK;         // int (*FS_FileIsInPAK)(const char*, int*)
    public nint FS_ReadFile;            // long (*FS_ReadFile)(const char*, void**)
    public nint FS_FreeFile;            // void (*FS_FreeFile)(void*)
    public nint FS_ListFiles;           // char **(*FS_ListFiles)(const char*, const char*, int*)
    public nint FS_FreeFileList;        // void (*FS_FreeFileList)(char**)
    public nint FS_WriteFile;           // void (*FS_WriteFile)(const char*, const void*, int)
    public nint FS_FileExists;          // qboolean (*FS_FileExists)(const char*)
    public nint CIN_UploadCinematic;    // void (*CIN_UploadCinematic)(int)
    public nint CIN_PlayCinematic;      // int (*CIN_PlayCinematic)(const char*, int, int, int, int, int)
    public nint CIN_RunCinematic;       // e_status (*CIN_RunCinematic)(int)
    public nint CL_WriteAVIVideoFrame;  // void (*CL_WriteAVIVideoFrame)(const byte*, int)
    public nint IN_Init;                // void (*IN_Init)(void*)
    public nint IN_Shutdown;            // void (*IN_Shutdown)(void)
    public nint IN_Restart;             // void (*IN_Restart)(void)
    public nint ftol;                   // long (*ftol)(float)
    public nint Sys_SetEnv;             // void (*Sys_SetEnv)(const char*, const char*)
    public nint Sys_GLimpSafeInit;      // void (*Sys_GLimpSafeInit)(void)
    public nint Sys_GLimpInit;          // void (*Sys_GLimpInit)(void)
    public nint Sys_LowPhysicalMemory;  // qboolean (*Sys_LowPhysicalMemory)(void)
}
