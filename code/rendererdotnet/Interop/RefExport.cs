using System.Runtime.InteropServices;

namespace RendererDotNet.Interop;

/// <summary>
/// Mirrors the C refexport_t struct from tr_public.h.
/// These are the functions exported by the renderer module.
/// Each field is a native function pointer (28 total).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct RefExport
{
    public nint Shutdown;               // void (*Shutdown)(qboolean destroyWindow)
    public nint BeginRegistration;      // void (*BeginRegistration)(glconfig_t *config)
    public nint RegisterModel;          // qhandle_t (*RegisterModel)(const char *name)
    public nint RegisterSkin;           // qhandle_t (*RegisterSkin)(const char *name)
    public nint RegisterShader;         // qhandle_t (*RegisterShader)(const char *name)
    public nint RegisterShaderNoMip;    // qhandle_t (*RegisterShaderNoMip)(const char *name)
    public nint LoadWorld;              // void (*LoadWorld)(const char *name)
    public nint SetWorldVisData;        // void (*SetWorldVisData)(const byte *vis)
    public nint EndRegistration;        // void (*EndRegistration)(void)
    public nint ClearScene;             // void (*ClearScene)(void)
    public nint AddRefEntityToScene;    // void (*AddRefEntityToScene)(const refEntity_t *re)
    public nint AddPolyToScene;         // void (*AddPolyToScene)(qhandle_t, int, const polyVert_t*, int)
    public nint LightForPoint;          // int (*LightForPoint)(vec3_t, vec3_t, vec3_t, vec3_t)
    public nint AddLightToScene;        // void (*AddLightToScene)(const vec3_t, float, float, float, float)
    public nint AddAdditiveLightToScene;// void (*AddAdditiveLightToScene)(const vec3_t, float, float, float, float)
    public nint RenderScene;            // void (*RenderScene)(const refdef_t *fd)
    public nint SetColor;               // void (*SetColor)(const float *rgba)
    public nint DrawStretchPic;         // void (*DrawStretchPic)(float x,y,w,h,s1,t1,s2,t2, qhandle_t)
    public nint DrawStretchRaw;         // void (*DrawStretchRaw)(int x,y,w,h,cols,rows, const byte*, int, qboolean)
    public nint UploadCinematic;        // void (*UploadCinematic)(int w,h,cols,rows, const byte*, int, qboolean)
    public nint BeginFrame;             // void (*BeginFrame)(stereoFrame_t)
    public nint EndFrame;               // void (*EndFrame)(int*, int*)
    public nint MarkFragments;          // int (*MarkFragments)(int, const vec3_t*, const vec3_t, int, vec3_t, int, markFragment_t*)
    public nint LerpTag;                // int (*LerpTag)(orientation_t*, qhandle_t, int, int, float, const char*)
    public nint ModelBounds;            // void (*ModelBounds)(qhandle_t, vec3_t, vec3_t)
    public nint RegisterFont;           // void (*RegisterFont)(const char*, int, fontInfo_t*)
    public nint RemapShader;            // void (*RemapShader)(const char*, const char*, const char*)
    public nint GetEntityToken;         // qboolean (*GetEntityToken)(char*, int)
    public nint InPVS;                  // qboolean (*inPVS)(const vec3_t, const vec3_t)
    public nint TakeVideoFrame;         // void (*TakeVideoFrame)(int, int, byte*, byte*, qboolean)
}
