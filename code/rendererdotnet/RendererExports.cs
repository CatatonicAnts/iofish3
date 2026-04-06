using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RendererDotNet.Interop;
using Silk.NET.OpenGL;
using Silk.NET.SDL;

namespace RendererDotNet;

/// <summary>
/// Stub implementations for all 30 refexport_t functions.
/// Uses Silk.NET for SDL2 windowing and OpenGL 4.5 rendering.
/// </summary>
public static unsafe class RendererExports
{
    private const int GLCONFIG_SIZE = 11332;

    // glconfig_t field offsets (from tr_types.h)
    private const int GLC_RENDERER_STRING = 0;
    private const int GLC_VENDOR_STRING = 1024;
    private const int GLC_VERSION_STRING = 2048;
    private const int GLC_MAX_TEXTURE_SIZE = 11264;
    private const int GLC_NUM_TEXTURE_UNITS = 11268;
    private const int GLC_COLOR_BITS = 11272;
    private const int GLC_DEPTH_BITS = 11276;
    private const int GLC_STENCIL_BITS = 11280;
    private const int GLC_VID_WIDTH = 11304;
    private const int GLC_VID_HEIGHT = 11308;
    private const int GLC_WINDOW_ASPECT = 11312;
    private const int GLC_DISPLAY_FREQUENCY = 11316;

    private static Sdl? _sdl;
    private static GL? _gl;
    private static Silk.NET.SDL.Window* _window;
    private static void* _glContext;

    private const int WIDTH = 1280;
    private const int HEIGHT = 720;

    internal static GL? Gl => _gl;
    internal static Sdl? Sdl => _sdl;
    internal static Silk.NET.SDL.Window* Window => _window;

    private static void WriteString(byte* dest, string value, int maxLen)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        int len = System.Math.Min(bytes.Length, maxLen - 1);
        fixed (byte* src = bytes)
        {
            System.Buffer.MemoryCopy(src, dest, maxLen, len);
        }
        dest[len] = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void Shutdown(int destroyWindow)
    {
        EngineImports.Printf(EngineImports.PRINT_ALL, "[.NET] Renderer shutdown\n");

        if (destroyWindow != 0)
        {
            _gl?.Dispose();
            _gl = null;

            if (_glContext != null)
            {
                _sdl?.GLDeleteContext(_glContext);
                _glContext = null;
            }
            if (_window != null)
            {
                _sdl?.DestroyWindow(_window);
                _window = null;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void BeginRegistration(nint config)
    {
        EngineImports.Printf(EngineImports.PRINT_ALL, "[.NET] ----- R_Init (.NET Renderer) -----\n");

        _sdl = Sdl.GetApi();
        _sdl.Init(Sdl.InitVideo);

        _sdl.GLSetAttribute(GLattr.ContextMajorVersion, 4);
        _sdl.GLSetAttribute(GLattr.ContextMinorVersion, 5);
        _sdl.GLSetAttribute(GLattr.ContextProfileMask, (int)GLprofile.Core);
        _sdl.GLSetAttribute(GLattr.RedSize, 8);
        _sdl.GLSetAttribute(GLattr.GreenSize, 8);
        _sdl.GLSetAttribute(GLattr.BlueSize, 8);
        _sdl.GLSetAttribute(GLattr.AlphaSize, 8);
        _sdl.GLSetAttribute(GLattr.DepthSize, 24);
        _sdl.GLSetAttribute(GLattr.StencilSize, 8);
        _sdl.GLSetAttribute(GLattr.Doublebuffer, 1);

        _window = _sdl.CreateWindow(
            "ioquake3 (.NET Renderer)",
            Sdl.WindowposCentered, Sdl.WindowposCentered,
            WIDTH, HEIGHT,
            (uint)(WindowFlags.Opengl | WindowFlags.Shown));

        if (_window == null)
        {
            EngineImports.Printf(EngineImports.PRINT_ERROR,
                "[.NET] SDL_CreateWindow failed\n");
            return;
        }

        _glContext = _sdl.GLCreateContext(_window);
        if (_glContext == null)
        {
            EngineImports.Printf(EngineImports.PRINT_ERROR,
                "[.NET] SDL_GL_CreateContext failed\n");
            _sdl.DestroyWindow(_window);
            _window = null;
            return;
        }

        _sdl.GLMakeCurrent(_window, _glContext);

        _gl = GL.GetApi(new SdlGLContext(_sdl));

        // Fill glconfig_t so the engine doesn't crash
        byte* cfg = (byte*)config;
        NativeMemory.Clear(cfg, GLCONFIG_SIZE);

        WriteString(cfg + GLC_RENDERER_STRING, ".NET NativeAOT Renderer (OpenGL 4.5)", 1024);
        WriteString(cfg + GLC_VENDOR_STRING, "iofish3", 1024);
        WriteString(cfg + GLC_VERSION_STRING, "4.5.0 (.NET 9)", 1024);

        *(int*)(cfg + GLC_MAX_TEXTURE_SIZE) = 16384;
        *(int*)(cfg + GLC_NUM_TEXTURE_UNITS) = 8;
        *(int*)(cfg + GLC_COLOR_BITS) = 32;
        *(int*)(cfg + GLC_DEPTH_BITS) = 24;
        *(int*)(cfg + GLC_STENCIL_BITS) = 8;
        *(int*)(cfg + GLC_VID_WIDTH) = WIDTH;
        *(int*)(cfg + GLC_VID_HEIGHT) = HEIGHT;
        *(float*)(cfg + GLC_WINDOW_ASPECT) = (float)WIDTH / HEIGHT;
        *(int*)(cfg + GLC_DISPLAY_FREQUENCY) = 60;

        EngineImports.IN_Init((nint)_window);

        _gl.Viewport(0, 0, (uint)WIDTH, (uint)HEIGHT);
        _gl.ClearColor(0.1f, 0.1f, 0.2f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _sdl.GLSwapWindow(_window);

        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] Window created: {WIDTH}x{HEIGHT}, OpenGL 4.5 Core\n");
        EngineImports.Printf(EngineImports.PRINT_ALL, "[.NET] ----- finished R_Init -----\n");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int RegisterModel(byte* name) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int RegisterSkin(byte* name) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int RegisterShader(byte* name) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int RegisterShaderNoMip(byte* name) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void LoadWorld(byte* name) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void SetWorldVisData(byte* vis) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void EndRegistration() { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void ClearScene() { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void AddRefEntityToScene(nint re) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void AddPolyToScene(int hShader, int numVerts, nint verts, int num) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int LightForPoint(float* point, float* ambientLight, float* directedLight, float* lightDir) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void AddLightToScene(float* org, float intensity, float r, float g, float b) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void AddAdditiveLightToScene(float* org, float intensity, float r, float g, float b) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void RenderScene(nint fd) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void SetColor(float* rgba) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void DrawStretchPic(float x, float y, float w, float h,
        float s1, float t1, float s2, float t2, int hShader) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void DrawStretchRaw(int x, int y, int w, int h,
        int cols, int rows, byte* data, int client, int dirty) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void UploadCinematic(int w, int h, int cols, int rows,
        byte* data, int client, int dirty) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void BeginFrame(int stereoFrame)
    {
        if (_gl != null)
        {
            _gl.ClearColor(0.1f, 0.1f, 0.2f, 1.0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void EndFrame(int* frontEndMsec, int* backEndMsec)
    {
        if (_sdl != null && _window != null)
        {
            _sdl.GLSwapWindow(_window);
        }

        if (frontEndMsec != null) *frontEndMsec = 0;
        if (backEndMsec != null) *backEndMsec = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int MarkFragments(int numPoints, float* points, float* projection,
        int maxPoints, float* pointBuffer, int maxFragments, nint fragmentBuffer) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int LerpTag(nint tag, int model, int startFrame, int endFrame,
        float frac, byte* tagName) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void ModelBounds(int model, float* mins, float* maxs) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void RegisterFont(byte* fontName, int pointSize, nint font) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void RemapShader(byte* oldShader, byte* newShader, byte* offsetTime) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetEntityToken(byte* buffer, int size) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int InPVS(float* p1, float* p2) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void TakeVideoFrame(int h, int w, byte* captureBuffer,
        byte* encodeBuffer, int motionJpeg) { }
}
