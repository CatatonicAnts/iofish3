using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RendererDotNet.Fonts;
using RendererDotNet.Interop;
using Silk.NET.OpenGL;
using Silk.NET.SDL;

namespace RendererDotNet;

/// <summary>
/// Implementations for all 30 refexport_t functions.
/// Uses Silk.NET for SDL2 windowing and OpenGL 4.5 rendering.
/// 2D rendering (menus, console, HUD) is handled by Renderer2D.
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

    private static Renderer2D? _renderer2D;
    private static Renderer3D? _renderer3D;
    private static ShaderManager? _shaders;
    private static SkinManager? _skins;
    private static Models.ModelManager? _models;
    private static SceneManager? _scene;
    private static World.BspRenderer? _bspRenderer;
    private static World.BspWorld? _bspWorld;
    private static World.SkyboxRenderer? _skyboxRenderer;
    private static PostProcess? _postProcess;

    // Scratch textures for cinematic playback (up to 16 simultaneous)
    private const int MAX_SCRATCH_IMAGES = 16;
    private static readonly uint[] _scratchTextures = new uint[MAX_SCRATCH_IMAGES];
    private static readonly int[] _scratchWidths = new int[MAX_SCRATCH_IMAGES];
    private static readonly int[] _scratchHeights = new int[MAX_SCRATCH_IMAGES];

    private const int WIDTH = 1280;  // fallback only
    private const int HEIGHT = 720;   // fallback only
    private static int _currentWidth = WIDTH;
    private static int _currentHeight = HEIGHT;

    private static int _screenshotCount;
    private static bool _screenshotPending;

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

        // Shut down FreeType
        FontRenderer.Shutdown();

        // Always clean up subsystems
        _scene?.DestroyPortalFbo();
        _scene = null;
        _models = null;
        _skins = null;
        _postProcess?.Dispose();
        _postProcess = null;
        _bspRenderer?.Dispose();
        _bspRenderer = null;
        _bspWorld = null;
        _skyboxRenderer?.Dispose();
        _skyboxRenderer = null;
        _renderer3D?.Dispose();
        _renderer3D = null;
        _renderer2D?.Dispose();
        _renderer2D = null;
        _shaders = null;

        // Clean up scratch textures
        if (_gl != null)
        {
            for (int i = 0; i < MAX_SCRATCH_IMAGES; i++)
            {
                if (_scratchTextures[i] != 0)
                {
                    _gl.DeleteTexture(_scratchTextures[i]);
                    _scratchTextures[i] = 0;
                    _scratchWidths[i] = 0;
                    _scratchHeights[i] = 0;
                }
            }
        }

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

        // Read resolution from cvars (set by engine on resize or from config)
        EngineImports.Cvar_Get("r_customwidth", "1280", 0x01 | 0x02);  // ARCHIVE | LATCH
        EngineImports.Cvar_Get("r_customheight", "720", 0x01 | 0x02);
        EngineImports.Cvar_Get("r_mode", "-1", 0x01 | 0x02);
        int rMode = EngineImports.Cvar_VariableIntegerValue("r_mode");
        if (rMode == -1)
        {
            _currentWidth = EngineImports.Cvar_VariableIntegerValue("r_customwidth");
            _currentHeight = EngineImports.Cvar_VariableIntegerValue("r_customheight");
        }
        else if (rMode == -2)
        {
            // Desktop resolution — use fallback for now, SDL will report actual size
            _currentWidth = WIDTH;
            _currentHeight = HEIGHT;
        }
        else
        {
            _currentWidth = WIDTH;
            _currentHeight = HEIGHT;
        }
        if (_currentWidth < 320) _currentWidth = 320;
        if (_currentHeight < 240) _currentHeight = 240;

        // Only create SDL window + GL context if we don't already have one
        if (_window == null)
        {
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
                _currentWidth, _currentHeight,
                (uint)(WindowFlags.Opengl | WindowFlags.Shown | WindowFlags.Resizable));

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

            EngineImports.IN_Init((nint)_window);
        }
        else
        {
            EngineImports.Printf(EngineImports.PRINT_ALL, "[.NET] Reusing existing window\n");
            _sdl!.GLMakeCurrent(_window, _glContext);
            _sdl.SetWindowSize(_window, _currentWidth, _currentHeight);
        }

        // (Re)initialize subsystems — these are lightweight and safe to recreate
        _renderer2D?.Dispose();
        _renderer2D = new Renderer2D();
        _renderer2D.Init(_gl!, _currentWidth, _currentHeight);

        _shaders = new ShaderManager();
        _shaders.WhiteTexture = _renderer2D.WhiteTexture;
        _shaders.SetRenderer(_renderer2D);
        _shaders.LoadShaderScripts();

        _skins = new SkinManager();
        _skins.SetShaderManager(_shaders);

        _renderer3D?.Dispose();
        _renderer3D = new Renderer3D();
        _renderer3D.Init(_gl!);

        _models = new Models.ModelManager();
        _models.SetShaderManager(_shaders);

        _bspRenderer?.Dispose();
        _bspRenderer = new World.BspRenderer();
        _bspRenderer.Init(_gl!);
        _bspWorld = null;

        _skyboxRenderer?.Dispose();
        _skyboxRenderer = new World.SkyboxRenderer();
        _skyboxRenderer.Init(_gl!);

        _scene = new SceneManager();
        _scene.Init(_models, _shaders, _skins, _renderer3D, _bspRenderer, _skyboxRenderer, _gl!, _currentWidth, _currentHeight);

        // Initialize post-processing (bloom)
        _postProcess?.Dispose();
        _postProcess = new PostProcess();
        _postProcess.Init(_gl!, _currentWidth, _currentHeight);
        _scene.SetPostProcess(_postProcess);
        _scene.InitShadowMapper(_gl!);

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
        *(int*)(cfg + GLC_VID_WIDTH) = _currentWidth;
        *(int*)(cfg + GLC_VID_HEIGHT) = _currentHeight;
        *(float*)(cfg + GLC_WINDOW_ASPECT) = (float)_currentWidth / _currentHeight;
        *(int*)(cfg + GLC_DISPLAY_FREQUENCY) = 60;

        EngineImports.IN_Init((nint)_window);

        _gl.Viewport(0, 0, (uint)_currentWidth, (uint)_currentHeight);
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _sdl.GLSwapWindow(_window);

        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] Window created: {_currentWidth}x{_currentHeight}, OpenGL 4.5 Core\n");

        // Initialize FreeType for TrueType font rendering
        FontRenderer.Init();

        EngineImports.Printf(EngineImports.PRINT_ALL, "[.NET] ----- finished R_Init -----\n");

        // Register screenshot console command
        EngineImports.Cmd_AddCommand("screenshot", &ScreenshotCmd);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ScreenshotCmd()
    {
        _screenshotPending = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int RegisterModel(byte* name)
    {
        return _models?.Register(name) ?? 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int RegisterSkin(byte* name)
    {
        return _skins?.Register(name) ?? 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int RegisterShader(byte* name)
    {
        return _shaders?.Register(name) ?? 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int RegisterShaderNoMip(byte* name)
    {
        return _shaders?.Register(name) ?? 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void LoadWorld(byte* name)
    {
        if (name == null || _bspRenderer == null || _shaders == null) return;

        string mapName = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)name) ?? "";
        if (string.IsNullOrEmpty(mapName)) return;

        EngineImports.Printf(EngineImports.PRINT_ALL, $"[.NET] Loading world: {mapName}\n");

        _entityTokenPos = 0;

        _bspWorld = World.BspLoader.LoadFromEngineFS(mapName);
        if (_bspWorld == null) return;

        // Register all BSP shader names with the shader manager
        for (int i = 0; i < _bspWorld.Surfaces.Length; i++)
        {
            ref var surf = ref _bspWorld.Surfaces[i];
            if (surf.ShaderIndex >= 0 && surf.ShaderIndex < _bspWorld.Shaders.Length)
            {
                string shaderName = _bspWorld.Shaders[surf.ShaderIndex].Name;
                if (!string.IsNullOrEmpty(shaderName))
                    surf.ShaderHandle = _shaders.Register(shaderName);
            }
        }

        _bspRenderer.LoadWorld(_bspWorld);
        _scene?.SetWorld(_bspWorld);
        _models?.SetBspWorld(_bspWorld);

        // Resolve fog parameters from shader scripts
        if (_bspWorld.Fogs.Length > 0)
        {
            var parser = _shaders.GetScriptParser();
            if (parser != null)
            {
                for (int i = 0; i < _bspWorld.Fogs.Length; i++)
                {
                    ref var fog = ref _bspWorld.Fogs[i];
                    var def = parser.GetShaderDef(fog.ShaderName);
                    if (def != null && def.HasFogParms)
                    {
                        fog.ColorR = def.FogColorR;
                        fog.ColorG = def.FogColorG;
                        fog.ColorB = def.FogColorB;
                        fog.DepthForOpaque = def.FogDepthForOpaque > 1 ? def.FogDepthForOpaque : 300f;
                        fog.TcScale = 1f / (fog.DepthForOpaque * 8f);
                    }
                }
            }
            EngineImports.Printf(EngineImports.PRINT_ALL,
                $"[.NET] Loaded {_bspWorld.Fogs.Length} fog volumes\n");
        }

        // Load skybox textures from shader scripts
        if (_skyboxRenderer != null && _renderer2D != null)
        {
            // Find sky shader from BSP shader entries
            string? skyBoxName = null;
            var parser = _shaders.GetScriptParser();
            if (parser != null)
            {
                foreach (var shader in _bspWorld.Shaders)
                {
                    if ((shader.SurfaceFlags & World.SurfaceFlags.SURF_SKY) != 0)
                    {
                        var def = parser.GetShaderDef(shader.Name);
                        if (def?.SkyBox != null)
                        {
                            skyBoxName = def.SkyBox;
                            break;
                        }
                    }
                }
                // Fallback: search all parsed shaders for any skyparms
                skyBoxName ??= parser.FindSkyBoxName();
            }

            if (skyBoxName != null)
                _skyboxRenderer.LoadSkyTextures(skyBoxName, _renderer2D);
        }

        // Load cubemap reflections after world geometry is ready
        if (_renderer2D != null)
            _bspRenderer.LoadCubemaps(_renderer2D);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void SetWorldVisData(byte* vis) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void EndRegistration() { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void ClearScene()
    {
        _scene?.ClearScene();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void AddRefEntityToScene(nint re)
    {
        _scene?.AddRefEntity((byte*)re);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void AddPolyToScene(int hShader, int numVerts, nint verts, int num)
    {
        _scene?.AddPoly(hShader, numVerts, (byte*)verts, num);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int LightForPoint(float* point, float* ambientLight, float* directedLight, float* lightDir) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void AddLightToScene(float* org, float intensity, float r, float g, float b)
    {
        _scene?.AddLight(org[0], org[1], org[2], intensity, r, g, b, false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void AddAdditiveLightToScene(float* org, float intensity, float r, float g, float b)
    {
        _scene?.AddLight(org[0], org[1], org[2], intensity, r, g, b, true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void RenderScene(nint fd)
    {
        // Flush pending 2D draws before switching to 3D
        _renderer2D?.Flush();
        _scene?.RenderScene((byte*)fd);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void SetColor(float* rgba)
    {
        if (_renderer2D == null) return;

        if (rgba != null)
            _renderer2D.SetColor(rgba[0], rgba[1], rgba[2], rgba[3]);
        else
            _renderer2D.ResetColor();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void DrawStretchPic(float x, float y, float w, float h,
        float s1, float t1, float s2, float t2, int hShader)
    {
        if (_renderer2D == null || _shaders == null) return;

        uint tex = _shaders.GetTextureId(hShader);
        BlendMode blend = _shaders.GetBlendMode(hShader);
        // 2D UI elements (text, buttons, HUD) always need alpha blending
        // for transparency. The shader blend mode is for 3D surface sorting;
        // for 2D, treat opaque as alpha-blended since textures have alpha channels.
        if (blend.IsOpaque)
            blend = BlendMode.Alpha;
        _renderer2D.DrawQuad(x, y, w, h, s1, t1, s2, t2, tex, blend);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void DrawStretchRaw(int x, int y, int w, int h,
        int cols, int rows, byte* data, int client, int dirty)
    {
        if (_renderer2D == null || _gl == null || data == null || cols <= 0 || rows <= 0)
            return;

        // Upload raw RGBA data as a temporary texture and draw it
        uint tex = _renderer2D.CreateTexture(cols, rows, data, clamp: true);
        if (tex == 0) return;

        _renderer2D.DrawQuad(x, y, w, h, 0, 0, 1, 1, tex, BlendMode.Opaque);
        _renderer2D.Flush();

        // Delete the temporary texture
        _gl.DeleteTexture(tex);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void UploadCinematic(int w, int h, int cols, int rows,
        byte* data, int client, int dirty)
    {
        if (_gl == null || client < 0 || client >= MAX_SCRATCH_IMAGES || data == null)
            return;

        // Create or resize scratch texture
        if (_scratchTextures[client] == 0)
            _scratchTextures[client] = _gl.GenTexture();

        _gl.BindTexture(TextureTarget.Texture2D, _scratchTextures[client]);

        if (cols != _scratchWidths[client] || rows != _scratchHeights[client])
        {
            // Size changed — re-upload entire texture
            _scratchWidths[client] = cols;
            _scratchHeights[client] = rows;
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
                (uint)cols, (uint)rows, 0,
                Silk.NET.OpenGL.PixelFormat.Rgba,
                Silk.NET.OpenGL.PixelType.UnsignedByte, data);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        }
        else if (dirty != 0)
        {
            // Same size but data changed — sub-image update
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                (uint)cols, (uint)rows,
                Silk.NET.OpenGL.PixelFormat.Rgba,
                Silk.NET.OpenGL.PixelType.UnsignedByte, data);
        }

        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>Get the GL texture ID for a cinematic scratch image.</summary>
    public static uint GetScratchTexture(int handle)
    {
        if (handle < 0 || handle >= MAX_SCRATCH_IMAGES) return 0;
        return _scratchTextures[handle];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void BeginFrame(int stereoFrame)
    {
        if (_gl == null) return;

        _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _renderer2D?.ResetColor();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void EndFrame(int* frontEndMsec, int* backEndMsec)
    {
        // Flush any pending 2D draw calls
        _renderer2D?.Flush();

        // Capture screenshot before swap (front buffer is complete)
        if (_screenshotPending && _gl != null)
        {
            _screenshotPending = false;
            TakeScreenshot();
        }

        if (_sdl != null && _window != null)
            _sdl.GLSwapWindow(_window);

        if (frontEndMsec != null) *frontEndMsec = 0;
        if (backEndMsec != null) *backEndMsec = 0;
    }

    /// <summary>
    /// Capture the current framebuffer as a TGA screenshot.
    /// </summary>
    private static void TakeScreenshot()
    {
        if (_gl == null) return;

        // Find next available filename
        string fileName;
        do
        {
            fileName = $"screenshots/shot{_screenshotCount:D4}.tga";
            _screenshotCount++;
        } while (_screenshotCount < 10000);

        int w = _currentWidth, h = _currentHeight;
        int pixelSize = w * h * 3;
        byte[] pixels = new byte[pixelSize];

        fixed (byte* p = pixels)
        {
            _gl.ReadPixels(0, 0, (uint)w, (uint)h,
                Silk.NET.OpenGL.PixelFormat.Rgb, Silk.NET.OpenGL.PixelType.UnsignedByte, p);
        }

        // Build TGA file (18-byte header + pixel data)
        int tgaSize = 18 + pixelSize;
        byte[] tga = new byte[tgaSize];

        // TGA header: uncompressed RGB
        tga[2] = 2;            // image type: uncompressed true-color
        tga[12] = (byte)(w & 0xFF);
        tga[13] = (byte)(w >> 8);
        tga[14] = (byte)(h & 0xFF);
        tga[15] = (byte)(h >> 8);
        tga[16] = 24;          // bits per pixel
        tga[17] = 0;           // image descriptor

        // Copy pixels, converting RGB to BGR (TGA format)
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int srcIdx = (y * w + x) * 3;
                int dstIdx = 18 + (y * w + x) * 3;
                tga[dstIdx + 0] = pixels[srcIdx + 2]; // B
                tga[dstIdx + 1] = pixels[srcIdx + 1]; // G
                tga[dstIdx + 2] = pixels[srcIdx + 0]; // R
            }
        }

        fixed (byte* tgaPtr = tga)
        {
            EngineImports.FS_WriteFile(fileName, tgaPtr, tgaSize);
        }

        EngineImports.Printf(EngineImports.PRINT_ALL, $"[.NET] Screenshot: {fileName}\n");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int MarkFragments(int numPoints, float* points, float* projection,
        int maxPoints, float* pointBuffer, int maxFragments, nint fragmentBuffer)
    {
        if (_bspWorld == null || numPoints < 3 || numPoints > MAX_VERTS_ON_POLY
            || points == null || projection == null
            || pointBuffer == null || fragmentBuffer == 0 || maxPoints < 3 || maxFragments < 1)
            return 0;

        float px = projection[0], py = projection[1], pz = projection[2];
        float projLen = MathF.Sqrt(px * px + py * py + pz * pz);
        if (projLen < 0.001f) return 0;
        float pdx = px / projLen, pdy = py / projLen, pdz = pz / projLen;

        // Build bounding box around input polygon + projected endpoints
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < numPoints; i++)
        {
            float x = points[i * 3], y = points[i * 3 + 1], z = points[i * 3 + 2];
            ExpandBounds(ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ, x, y, z);
            ExpandBounds(ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ,
                x + px, y + py, z + pz);
            // Also extend a bit behind the hit surface
            ExpandBounds(ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ,
                x - 20 * pdx, y - 20 * pdy, z - 20 * pdz);
        }

        // Build clipping planes from polygon edges + projection direction (Q3 R_MarkFragments)
        // numPoints edge planes + 2 near/far planes
        int numPlanes = numPoints + 2;
        Span<float> planeNormals = stackalloc float[numPlanes * 3];
        Span<float> planeDists = stackalloc float[numPlanes];

        for (int i = 0; i < numPoints; i++)
        {
            int next = (i + 1) % numPoints;
            // v1 = points[next] - points[i]
            float v1x = points[next * 3] - points[i * 3];
            float v1y = points[next * 3 + 1] - points[i * 3 + 1];
            float v1z = points[next * 3 + 2] - points[i * 3 + 2];
            // v2 = points[i] - (points[i] + projection)
            float v2x = -px, v2y = -py, v2z = -pz;
            // normal = cross(v1, v2)
            float nx = v1y * v2z - v1z * v2y;
            float ny = v1z * v2x - v1x * v2z;
            float nz = v1x * v2y - v1y * v2x;
            float nLen = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nLen > 0.001f) { nx /= nLen; ny /= nLen; nz /= nLen; }
            planeNormals[i * 3] = nx;
            planeNormals[i * 3 + 1] = ny;
            planeNormals[i * 3 + 2] = nz;
            planeDists[i] = nx * points[i * 3] + ny * points[i * 3 + 1] + nz * points[i * 3 + 2];
        }

        // Near clipping plane (along projection direction)
        planeNormals[numPoints * 3] = pdx;
        planeNormals[numPoints * 3 + 1] = pdy;
        planeNormals[numPoints * 3 + 2] = pdz;
        planeDists[numPoints] = pdx * points[0] + pdy * points[1] + pdz * points[2] - 32;

        // Far clipping plane (opposite direction)
        planeNormals[(numPoints + 1) * 3] = -pdx;
        planeNormals[(numPoints + 1) * 3 + 1] = -pdy;
        planeNormals[(numPoints + 1) * 3 + 2] = -pdz;
        planeDists[numPoints + 1] = -pdx * points[0] - pdy * points[1] - pdz * points[2] - 20;

        // Find surfaces in the bounding box via BSP traversal
        Span<int> surfList = stackalloc int[64];
        int numSurfs = 0;
        BoxSurfaces(_bspWorld, 0, minX, minY, minZ, maxX, maxY, maxZ,
            surfList, ref numSurfs, pdx, pdy, pdz);

        int returnedFragments = 0;
        int returnedPoints = 0;
        byte* fragBuf = (byte*)fragmentBuffer;
        const int FRAG_SIZE = 8;

        // Clip buffers for ping-pong clipping
        Span<float> clipA = stackalloc float[MAX_VERTS_ON_POLY * 3];
        Span<float> clipB = stackalloc float[MAX_VERTS_ON_POLY * 3];

        for (int si = 0; si < numSurfs && returnedFragments < maxFragments; si++)
        {
            int surfIdx = surfList[si];
            ref var surf = ref _bspWorld.Surfaces[surfIdx];

            // Skip surfaces with NOMARKS-type flags
            if (surf.ShaderIndex >= 0 && surf.ShaderIndex < _bspWorld.Shaders.Length)
            {
                int sf = _bspWorld.Shaders[surf.ShaderIndex].SurfaceFlags;
                if ((sf & (0x10 | 0x20)) != 0) continue; // SURF_NOIMPACT | SURF_NOMARKS
            }

            if (surf.SurfaceType == World.SurfaceTypes.MST_PLANAR ||
                surf.SurfaceType == World.SurfaceTypes.MST_TRIANGLE_SOUP)
            {
                // Process each triangle of the surface
                for (int k = 0; k < surf.NumIndices && returnedFragments < maxFragments; k += 3)
                {
                    int i0 = _bspWorld.Indices[surf.FirstIndex + k];
                    int i1 = _bspWorld.Indices[surf.FirstIndex + k + 1];
                    int i2 = _bspWorld.Indices[surf.FirstIndex + k + 2];
                    ref var va = ref _bspWorld.Vertices[surf.FirstVertex + i0];
                    ref var vb = ref _bspWorld.Vertices[surf.FirstVertex + i1];
                    ref var vc = ref _bspWorld.Vertices[surf.FirstVertex + i2];

                    // Check triangle normal faces opposite to projection
                    float e1x = vb.X - va.X, e1y = vb.Y - va.Y, e1z = vb.Z - va.Z;
                    float e2x = vc.X - va.X, e2y = vc.Y - va.Y, e2z = vc.Z - va.Z;
                    float tnx = e1y * e2z - e1z * e2y;
                    float tny = e1z * e2x - e1x * e2z;
                    float tnz = e1x * e2y - e1y * e2x;
                    if (tnx * pdx + tny * pdy + tnz * pdz > -0.1f) continue;

                    // Copy triangle to clip buffer A
                    clipA[0] = va.X; clipA[1] = va.Y; clipA[2] = va.Z;
                    clipA[3] = vb.X; clipA[4] = vb.Y; clipA[5] = vb.Z;
                    clipA[6] = vc.X; clipA[7] = vc.Y; clipA[8] = vc.Z;
                    int numClip = 3;

                    // Clip against all bounding planes (ping-pong)
                    bool useA = true;
                    for (int pi = 0; pi < numPlanes && numClip >= 3; pi++)
                    {
                        var src = useA ? clipA : clipB;
                        var dst = useA ? clipB : clipA;
                        numClip = ChopPolyBehindPlane(src, numClip, dst,
                            planeNormals[pi * 3], planeNormals[pi * 3 + 1], planeNormals[pi * 3 + 2],
                            planeDists[pi], 0.5f);
                        useA = !useA;
                    }

                    if (numClip < 3) continue;
                    if (returnedPoints + numClip > maxPoints) continue;

                    var final = useA ? clipA : clipB;

                    // Write fragment output
                    *(int*)(fragBuf + returnedFragments * FRAG_SIZE) = returnedPoints;
                    *(int*)(fragBuf + returnedFragments * FRAG_SIZE + 4) = numClip;
                    returnedFragments++;

                    for (int j = 0; j < numClip; j++)
                    {
                        pointBuffer[returnedPoints * 3] = final[j * 3];
                        pointBuffer[returnedPoints * 3 + 1] = final[j * 3 + 1];
                        pointBuffer[returnedPoints * 3 + 2] = final[j * 3 + 2];
                        returnedPoints++;
                    }
                }
            }
            else if (surf.SurfaceType == World.SurfaceTypes.MST_PATCH)
            {
                // Patches: process tessellated triangles
                for (int k = 0; k < surf.NumIndices && returnedFragments < maxFragments; k += 3)
                {
                    int i0 = _bspWorld.Indices[surf.FirstIndex + k];
                    int i1 = _bspWorld.Indices[surf.FirstIndex + k + 1];
                    int i2 = _bspWorld.Indices[surf.FirstIndex + k + 2];
                    ref var va = ref _bspWorld.Vertices[surf.FirstVertex + i0];
                    ref var vb = ref _bspWorld.Vertices[surf.FirstVertex + i1];
                    ref var vc = ref _bspWorld.Vertices[surf.FirstVertex + i2];

                    float e1x = vb.X - va.X, e1y = vb.Y - va.Y, e1z = vb.Z - va.Z;
                    float e2x = vc.X - va.X, e2y = vc.Y - va.Y, e2z = vc.Z - va.Z;
                    float tnx = e1y * e2z - e1z * e2y;
                    float tny = e1z * e2x - e1x * e2z;
                    float tnz = e1x * e2y - e1y * e2x;
                    if (tnx * pdx + tny * pdy + tnz * pdz > -0.05f) continue;

                    clipA[0] = va.X; clipA[1] = va.Y; clipA[2] = va.Z;
                    clipA[3] = vb.X; clipA[4] = vb.Y; clipA[5] = vb.Z;
                    clipA[6] = vc.X; clipA[7] = vc.Y; clipA[8] = vc.Z;
                    int numClip = 3;

                    bool useA = true;
                    for (int pi = 0; pi < numPlanes && numClip >= 3; pi++)
                    {
                        var src = useA ? clipA : clipB;
                        var dst = useA ? clipB : clipA;
                        numClip = ChopPolyBehindPlane(src, numClip, dst,
                            planeNormals[pi * 3], planeNormals[pi * 3 + 1], planeNormals[pi * 3 + 2],
                            planeDists[pi], 0.5f);
                        useA = !useA;
                    }

                    if (numClip < 3) continue;
                    if (returnedPoints + numClip > maxPoints) continue;

                    var final = useA ? clipA : clipB;
                    *(int*)(fragBuf + returnedFragments * FRAG_SIZE) = returnedPoints;
                    *(int*)(fragBuf + returnedFragments * FRAG_SIZE + 4) = numClip;
                    returnedFragments++;

                    for (int j = 0; j < numClip; j++)
                    {
                        pointBuffer[returnedPoints * 3] = final[j * 3];
                        pointBuffer[returnedPoints * 3 + 1] = final[j * 3 + 1];
                        pointBuffer[returnedPoints * 3 + 2] = final[j * 3 + 2];
                        returnedPoints++;
                    }
                }
            }
        }

        return returnedFragments;
    }

    private const int MAX_VERTS_ON_POLY = 64;

    /// <summary>
    /// Clip a convex polygon behind a plane using Sutherland-Hodgman algorithm.
    /// Matches Q3's R_ChopPolyBehindPlane. "Behind" = dot product &lt; dist.
    /// </summary>
    private static int ChopPolyBehindPlane(Span<float> inPts, int numIn, Span<float> outPts,
        float nx, float ny, float nz, float dist, float epsilon)
    {
        const int SIDE_FRONT = 0, SIDE_BACK = 1, SIDE_ON = 2;
        Span<float> dists = stackalloc float[numIn + 1];
        Span<int> sides = stackalloc int[numIn + 1];

        for (int i = 0; i < numIn; i++)
        {
            float d = nx * inPts[i * 3] + ny * inPts[i * 3 + 1] + nz * inPts[i * 3 + 2] - dist;
            dists[i] = d;
            sides[i] = d > epsilon ? SIDE_FRONT : (d < -epsilon ? SIDE_BACK : SIDE_ON);
        }
        dists[numIn] = dists[0];
        sides[numIn] = sides[0];

        int numOut = 0;
        for (int i = 0; i < numIn; i++)
        {
            if (sides[i] == SIDE_BACK || sides[i] == SIDE_ON)
            {
                outPts[numOut * 3] = inPts[i * 3];
                outPts[numOut * 3 + 1] = inPts[i * 3 + 1];
                outPts[numOut * 3 + 2] = inPts[i * 3 + 2];
                numOut++;
                if (numOut >= MAX_VERTS_ON_POLY) return numOut;
            }

            if (sides[i + 1] == SIDE_ON || sides[i + 1] == sides[i])
                continue;

            // Generate split point
            int next = (i + 1) % numIn;
            float d = dists[i] - dists[next];
            float frac = d == 0 ? 0 : dists[i] / d;

            outPts[numOut * 3] = inPts[i * 3] + frac * (inPts[next * 3] - inPts[i * 3]);
            outPts[numOut * 3 + 1] = inPts[i * 3 + 1] + frac * (inPts[next * 3 + 1] - inPts[i * 3 + 1]);
            outPts[numOut * 3 + 2] = inPts[i * 3 + 2] + frac * (inPts[next * 3 + 2] - inPts[i * 3 + 2]);
            numOut++;
            if (numOut >= MAX_VERTS_ON_POLY) return numOut;
        }

        return numOut;
    }

    /// <summary>
    /// Traverse BSP tree to find surfaces within a bounding box.
    /// Matches Q3's R_BoxSurfaces_r.
    /// </summary>
    private static void BoxSurfaces(World.BspWorld world, int nodeIdx,
        float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
        Span<int> list, ref int count, float pdx, float pdy, float pdz)
    {
        while (nodeIdx >= 0 && nodeIdx < world.Nodes.Length)
        {
            ref var node = ref world.Nodes[nodeIdx];
            ref var plane = ref world.Planes[node.PlaneIndex];

            // BoxOnPlaneSide: determine which side(s) of the plane the box is on
            int side = BoxOnPlaneSide(minX, minY, minZ, maxX, maxY, maxZ, ref plane);
            if (side == 1)
                nodeIdx = node.Child0;
            else if (side == 2)
                nodeIdx = node.Child1;
            else
            {
                BoxSurfaces(world, node.Child0, minX, minY, minZ, maxX, maxY, maxZ,
                    list, ref count, pdx, pdy, pdz);
                nodeIdx = node.Child1;
            }
        }

        // Reached a leaf
        int leafIdx = -(nodeIdx + 1);
        if (leafIdx < 0 || leafIdx >= world.Leafs.Length) return;
        ref var leaf = ref world.Leafs[leafIdx];

        for (int i = 0; i < leaf.NumLeafSurfaces && count < list.Length; i++)
        {
            int surfIdx = world.LeafSurfaces[leaf.FirstLeafSurface + i];
            if (surfIdx < 0 || surfIdx >= world.Surfaces.Length) continue;

            // Avoid duplicates (simple linear scan — lists are small)
            bool dup = false;
            for (int j = 0; j < count; j++)
                if (list[j] == surfIdx) { dup = true; break; }
            if (dup) continue;

            ref var surf = ref world.Surfaces[surfIdx];
            if (surf.SurfaceType != World.SurfaceTypes.MST_PLANAR &&
                surf.SurfaceType != World.SurfaceTypes.MST_TRIANGLE_SOUP &&
                surf.SurfaceType != World.SurfaceTypes.MST_PATCH)
                continue;
            if (surf.NumVertices < 3 || surf.NumIndices < 3) continue;

            list[count++] = surfIdx;
        }
    }

    /// <summary>
    /// Determine which side of a plane an AABB is on.
    /// Returns 1=front, 2=back, 3=both.
    /// </summary>
    private static int BoxOnPlaneSide(float minX, float minY, float minZ,
        float maxX, float maxY, float maxZ, ref World.BspPlane plane)
    {
        float dMin = plane.NormalX * (plane.NormalX >= 0 ? minX : maxX)
                   + plane.NormalY * (plane.NormalY >= 0 ? minY : maxY)
                   + plane.NormalZ * (plane.NormalZ >= 0 ? minZ : maxZ)
                   - plane.Dist;
        float dMax = plane.NormalX * (plane.NormalX >= 0 ? maxX : minX)
                   + plane.NormalY * (plane.NormalY >= 0 ? maxY : minY)
                   + plane.NormalZ * (plane.NormalZ >= 0 ? maxZ : minZ)
                   - plane.Dist;

        if (dMin >= 0) return 1;
        if (dMax < 0) return 2;
        return 3;
    }

    private static void ExpandBounds(ref float minX, ref float minY, ref float minZ,
        ref float maxX, ref float maxY, ref float maxZ,
        float x, float y, float z)
    {
        if (x < minX) minX = x; if (x > maxX) maxX = x;
        if (y < minY) minY = y; if (y > maxY) maxY = y;
        if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int LerpTag(nint tag, int model, int startFrame, int endFrame,
        float frac, byte* tagName)
    {
        return _models?.LerpTag(tag, model, startFrame, endFrame, frac, tagName) ?? 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void ModelBounds(int model, float* mins, float* maxs)
    {
        _models?.GetBounds(model, mins, maxs);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void RegisterFont(byte* fontName, int pointSize, nint font)
    {
        if (fontName == null || font == 0 || _shaders == null) return;
        if (pointSize <= 0) pointSize = 12;

        string fontNameStr = System.Runtime.InteropServices.Marshal
            .PtrToStringUTF8((nint)fontName) ?? "";

        // Font data is stored as binary in fonts/fontImage_{pointSize}.dat
        string datFile = $"fonts/fontImage_{pointSize}.dat";
        const int FONT_INFO_SIZE = 20548; // sizeof(fontInfo_t)

        int len = EngineImports.FS_ReadFile(datFile, out byte* data);
        if (len == FONT_INFO_SIZE && data != null)
        {
            byte* dst = (byte*)font;

            // Binary layout matches struct layout on little-endian (x64): direct copy
            System.Buffer.MemoryCopy(data, dst, FONT_INFO_SIZE, FONT_INFO_SIZE);
            EngineImports.FS_FreeFile(data);

            // Fix up glyph shader handles — register each font atlas page
            const int GLYPH_SIZE = 80;
            const int GLYPH_HANDLE_OFF = 44;
            const int GLYPH_SHADER_OFF = 48;
            for (int i = 0; i < 256; i++)
            {
                int off = i * GLYPH_SIZE;
                string shaderName = System.Runtime.InteropServices.Marshal
                    .PtrToStringAnsi((nint)(dst + off + GLYPH_SHADER_OFF)) ?? "";
                if (shaderName.Length > 0)
                    *(int*)(dst + off + GLYPH_HANDLE_OFF) = _shaders.Register(shaderName);
            }

            // Overwrite name field with the dat filename
            WriteString(dst + 20484, datFile, 64);
            return;
        }

        if (data != null) EngineImports.FS_FreeFile(data);

        // No .dat file — try FreeType rendering from TrueType font
        if (_renderer2D != null &&
            FontRenderer.RenderFont(fontNameStr, pointSize, (byte*)font, _shaders, _renderer2D))
        {
            return;
        }

        EngineImports.Printf(EngineImports.PRINT_WARNING,
            $"[.NET] RegisterFont: No .dat file and FreeType failed for '{fontNameStr}' at {pointSize}pt\n");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void RemapShader(byte* oldShader, byte* newShader, byte* offsetTime)
    {
        if (oldShader == null || newShader == null || _shaders == null) return;

        string oldName = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)oldShader) ?? "";
        string newName = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)newShader) ?? "";

        if (!string.IsNullOrEmpty(oldName) && !string.IsNullOrEmpty(newName))
            _shaders.RemapShader(oldName, newName);
    }

    private static int _entityTokenPos;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetEntityToken(byte* buffer, int size)
    {
        if (buffer == null || size <= 0 || _bspWorld == null)
            return 0;

        string entityStr = _bspWorld.EntityString;
        if (string.IsNullOrEmpty(entityStr) || _entityTokenPos >= entityStr.Length)
        {
            buffer[0] = 0;
            return 0;
        }

        // Skip whitespace
        while (_entityTokenPos < entityStr.Length && char.IsWhiteSpace(entityStr[_entityTokenPos]))
            _entityTokenPos++;

        if (_entityTokenPos >= entityStr.Length)
        {
            buffer[0] = 0;
            return 0;
        }

        // Read a token (either a quoted string or brace-delimited / whitespace-delimited)
        int start = _entityTokenPos;
        int len;

        if (entityStr[_entityTokenPos] == '"')
        {
            _entityTokenPos++;
            start = _entityTokenPos;
            while (_entityTokenPos < entityStr.Length && entityStr[_entityTokenPos] != '"')
                _entityTokenPos++;
            len = _entityTokenPos - start;
            if (_entityTokenPos < entityStr.Length) _entityTokenPos++; // skip closing quote
        }
        else if (entityStr[_entityTokenPos] == '{' || entityStr[_entityTokenPos] == '}')
        {
            len = 1;
            _entityTokenPos++;
        }
        else
        {
            while (_entityTokenPos < entityStr.Length && !char.IsWhiteSpace(entityStr[_entityTokenPos])
                   && entityStr[_entityTokenPos] != '{' && entityStr[_entityTokenPos] != '}')
                _entityTokenPos++;
            len = _entityTokenPos - start;
        }

        if (len <= 0)
        {
            buffer[0] = 0;
            return 0;
        }

        int copyLen = Math.Min(len, size - 1);
        var bytes = System.Text.Encoding.UTF8.GetBytes(entityStr.Substring(start, copyLen));
        fixed (byte* src = bytes)
            NativeMemory.Copy(src, buffer, (nuint)bytes.Length);
        buffer[bytes.Length] = 0;

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int InPVS(float* p1, float* p2)
    {
        if (_bspWorld == null || p1 == null || p2 == null)
            return 1; // If no world, assume visible

        int leaf1 = _bspWorld.FindLeaf(p1[0], p1[1], p1[2]);
        int leaf2 = _bspWorld.FindLeaf(p2[0], p2[1], p2[2]);

        if (leaf1 < 0 || leaf1 >= _bspWorld.Leafs.Length ||
            leaf2 < 0 || leaf2 >= _bspWorld.Leafs.Length)
            return 1;

        int cluster1 = _bspWorld.Leafs[leaf1].Cluster;
        int cluster2 = _bspWorld.Leafs[leaf2].Cluster;

        return _bspWorld.ClusterVisible(cluster1, cluster2) ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void TakeVideoFrame(int h, int w, byte* captureBuffer,
        byte* encodeBuffer, int motionJpeg)
    {
        if (_gl == null) return;

        const int AVI_LINE_PADDING = 4;

        // Read framebuffer as RGBA (4 bytes per pixel)
        _gl.ReadPixels(0, 0, (uint)w, (uint)h,
            Silk.NET.OpenGL.PixelFormat.Rgba,
            Silk.NET.OpenGL.PixelType.UnsignedByte,
            captureBuffer);

        // Convert RGBA → BGR with AVI line padding, write to encodeBuffer
        int aviLineLen = w * 3;
        int aviPadWidth = (aviLineLen + AVI_LINE_PADDING - 1) & ~(AVI_LINE_PADDING - 1);
        int aviPadLen = aviPadWidth - aviLineLen;

        byte* src = captureBuffer;
        byte* dst = encodeBuffer;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                *dst++ = src[2]; // B
                *dst++ = src[1]; // G
                *dst++ = src[0]; // R
                src += 4;        // skip RGBA
            }
            for (int p = 0; p < aviPadLen; p++)
                *dst++ = 0;
        }

        if (EngineImports.IsInitialized)
            EngineImports.WriteAVIVideoFrame(encodeBuffer, aviPadWidth * h);
    }
}
