using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RendererDotNet.Interop;

namespace RendererDotNet;

public static unsafe class EntryPoint
{
    private const int REF_API_VERSION = 8;

    // Static storage for the export table - must survive the call
    private static RefExport _refExport;
    private static bool _initialized;

    [UnmanagedCallersOnly(EntryPoint = "GetRefAPI", CallConvs = [typeof(CallConvCdecl)])]
    public static RefExport* GetRefAPI(int apiVersion, RefImport* rimp)
    {
        // Store engine imports for use by renderer
        EngineImports.Init(rimp);

        if (apiVersion != REF_API_VERSION)
        {
            EngineImports.Printf(EngineImports.PRINT_ALL,
                $"[.NET] Mismatched REF_API_VERSION: expected {REF_API_VERSION}, got {apiVersion}\n");
            return null;
        }

        // Zero out and populate the export table
        _refExport = default;

        _refExport.Shutdown               = (nint)(delegate* unmanaged[Cdecl]<int, void>)&RendererExports.Shutdown;
        _refExport.BeginRegistration       = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&RendererExports.BeginRegistration;
        _refExport.RegisterModel           = (nint)(delegate* unmanaged[Cdecl]<byte*, int>)&RendererExports.RegisterModel;
        _refExport.RegisterSkin            = (nint)(delegate* unmanaged[Cdecl]<byte*, int>)&RendererExports.RegisterSkin;
        _refExport.RegisterShader          = (nint)(delegate* unmanaged[Cdecl]<byte*, int>)&RendererExports.RegisterShader;
        _refExport.RegisterShaderNoMip     = (nint)(delegate* unmanaged[Cdecl]<byte*, int>)&RendererExports.RegisterShaderNoMip;
        _refExport.LoadWorld               = (nint)(delegate* unmanaged[Cdecl]<byte*, void>)&RendererExports.LoadWorld;
        _refExport.SetWorldVisData         = (nint)(delegate* unmanaged[Cdecl]<byte*, void>)&RendererExports.SetWorldVisData;
        _refExport.EndRegistration         = (nint)(delegate* unmanaged[Cdecl]<void>)&RendererExports.EndRegistration;
        _refExport.ClearScene              = (nint)(delegate* unmanaged[Cdecl]<void>)&RendererExports.ClearScene;
        _refExport.AddRefEntityToScene     = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&RendererExports.AddRefEntityToScene;
        _refExport.AddPolyToScene          = (nint)(delegate* unmanaged[Cdecl]<int, int, nint, int, void>)&RendererExports.AddPolyToScene;
        _refExport.LightForPoint           = (nint)(delegate* unmanaged[Cdecl]<float*, float*, float*, float*, int>)&RendererExports.LightForPoint;
        _refExport.AddLightToScene         = (nint)(delegate* unmanaged[Cdecl]<float*, float, float, float, float, void>)&RendererExports.AddLightToScene;
        _refExport.AddAdditiveLightToScene = (nint)(delegate* unmanaged[Cdecl]<float*, float, float, float, float, void>)&RendererExports.AddAdditiveLightToScene;
        _refExport.RenderScene             = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&RendererExports.RenderScene;
        _refExport.SetColor                = (nint)(delegate* unmanaged[Cdecl]<float*, void>)&RendererExports.SetColor;
        _refExport.DrawStretchPic          = (nint)(delegate* unmanaged[Cdecl]<float, float, float, float, float, float, float, float, int, void>)&RendererExports.DrawStretchPic;
        _refExport.DrawStretchRaw          = (nint)(delegate* unmanaged[Cdecl]<int, int, int, int, int, int, byte*, int, int, void>)&RendererExports.DrawStretchRaw;
        _refExport.UploadCinematic         = (nint)(delegate* unmanaged[Cdecl]<int, int, int, int, byte*, int, int, void>)&RendererExports.UploadCinematic;
        _refExport.BeginFrame              = (nint)(delegate* unmanaged[Cdecl]<int, void>)&RendererExports.BeginFrame;
        _refExport.EndFrame                = (nint)(delegate* unmanaged[Cdecl]<int*, int*, void>)&RendererExports.EndFrame;
        _refExport.MarkFragments           = (nint)(delegate* unmanaged[Cdecl]<int, float*, float*, int, float*, int, nint, int>)&RendererExports.MarkFragments;
        _refExport.LerpTag                 = (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int, float, byte*, int>)&RendererExports.LerpTag;
        _refExport.ModelBounds             = (nint)(delegate* unmanaged[Cdecl]<int, float*, float*, void>)&RendererExports.ModelBounds;
        _refExport.RegisterFont            = (nint)(delegate* unmanaged[Cdecl]<byte*, int, nint, void>)&RendererExports.RegisterFont;
        _refExport.RemapShader             = (nint)(delegate* unmanaged[Cdecl]<byte*, byte*, byte*, void>)&RendererExports.RemapShader;
        _refExport.GetEntityToken          = (nint)(delegate* unmanaged[Cdecl]<byte*, int, int>)&RendererExports.GetEntityToken;
        _refExport.InPVS                   = (nint)(delegate* unmanaged[Cdecl]<float*, float*, int>)&RendererExports.InPVS;
        _refExport.TakeVideoFrame          = (nint)(delegate* unmanaged[Cdecl]<int, int, byte*, byte*, int, void>)&RendererExports.TakeVideoFrame;

        if (!_initialized)
        {
            EngineImports.Printf(EngineImports.PRINT_ALL, "[.NET] Renderer loaded (.NET 9 / NativeAOT)\n");
            _initialized = true;
        }

        return (RefExport*)Unsafe.AsPointer(ref _refExport);
    }
}
