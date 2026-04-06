using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    private const int WIDTH = 1280;
    private const int HEIGHT = 720;

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

        // Always clean up subsystems
        _scene = null;
        _models = null;
        _skins = null;
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

            EngineImports.IN_Init((nint)_window);
        }
        else
        {
            EngineImports.Printf(EngineImports.PRINT_ALL, "[.NET] Reusing existing window\n");
            _sdl!.GLMakeCurrent(_window, _glContext);
        }

        // (Re)initialize subsystems — these are lightweight and safe to recreate
        _renderer2D?.Dispose();
        _renderer2D = new Renderer2D();
        _renderer2D.Init(_gl!, WIDTH, HEIGHT);

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
        _scene.Init(_models, _shaders, _skins, _renderer3D, _bspRenderer, _skyboxRenderer, _gl!, WIDTH, HEIGHT);

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
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _sdl.GLSwapWindow(_window);

        EngineImports.Printf(EngineImports.PRINT_ALL,
            $"[.NET] Window created: {WIDTH}x{HEIGHT}, OpenGL 4.5 Core\n");
        EngineImports.Printf(EngineImports.PRINT_ALL, "[.NET] ----- finished R_Init -----\n");
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
    public static void AddLightToScene(float* org, float intensity, float r, float g, float b) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void AddAdditiveLightToScene(float* org, float intensity, float r, float g, float b) { }

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
        byte* data, int client, int dirty) { }

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

        if (_sdl != null && _window != null)
            _sdl.GLSwapWindow(_window);

        if (frontEndMsec != null) *frontEndMsec = 0;
        if (backEndMsec != null) *backEndMsec = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int MarkFragments(int numPoints, float* points, float* projection,
        int maxPoints, float* pointBuffer, int maxFragments, nint fragmentBuffer)
    {
        if (_bspWorld == null || numPoints < 3 || points == null || projection == null
            || pointBuffer == null || fragmentBuffer == 0 || maxPoints < 3 || maxFragments < 1)
            return 0;

        // Compute midpoint of the input polygon
        float cx = 0, cy = 0, cz = 0;
        for (int i = 0; i < numPoints; i++)
        {
            cx += points[i * 3]; cy += points[i * 3 + 1]; cz += points[i * 3 + 2];
        }
        float invN = 1.0f / numPoints;
        cx *= invN; cy *= invN; cz *= invN;

        // Projection vector
        float px = projection[0], py = projection[1], pz = projection[2];
        float projLen = MathF.Sqrt(px * px + py * py + pz * pz);
        if (projLen < 0.001f) return 0;

        // Find which leaf the midpoint is in
        int leafIdx = _bspWorld.FindLeaf(cx, cy, cz);
        if (leafIdx < 0 || leafIdx >= _bspWorld.Leafs.Length) return 0;

        ref var leaf = ref _bspWorld.Leafs[leafIdx];
        int numFragments = 0;
        int numOutputPoints = 0;
        byte* fragBuf = (byte*)fragmentBuffer;

        // markFragment_t = { int firstPoint, int numPoints } = 8 bytes
        const int FRAG_SIZE = 8;

        for (int i = 0; i < leaf.NumLeafSurfaces && numFragments < maxFragments; i++)
        {
            int surfIdx = _bspWorld.LeafSurfaces[leaf.FirstLeafSurface + i];
            if (surfIdx < 0 || surfIdx >= _bspWorld.Surfaces.Length) continue;

            ref var surf = ref _bspWorld.Surfaces[surfIdx];
            if (surf.SurfaceType != World.SurfaceTypes.MST_PLANAR &&
                surf.SurfaceType != World.SurfaceTypes.MST_TRIANGLE_SOUP)
                continue;
            if (surf.NumVertices < 3) continue;

            // Get surface normal from first vertex
            ref var v0 = ref _bspWorld.Vertices[surf.FirstVertex];
            float snx = v0.NX, sny = v0.NY, snz = v0.NZ;
            float snLen = MathF.Sqrt(snx * snx + sny * sny + snz * snz);
            if (snLen < 0.001f) continue;

            // Must be facing opposite to projection
            float dot = snx * px + sny * py + snz * pz;
            if (dot >= -0.001f) continue;

            // Compute surface plane distance
            float planeDist = snx * v0.X + sny * v0.Y + snz * v0.Z;

            // Project each input point onto the surface plane
            int fragStart = numOutputPoints;
            bool valid = true;

            for (int j = 0; j < numPoints && numOutputPoints + numPoints <= maxPoints; j++)
            {
                float ix = points[j * 3], iy = points[j * 3 + 1], iz = points[j * 3 + 2];
                float d = snx * ix + sny * iy + snz * iz - planeDist;
                float denom = snx * px + sny * py + snz * pz;
                if (MathF.Abs(denom) < 0.001f) { valid = false; break; }

                float t = -d / denom;
                if (t < -0.1f || t > 1.1f) { valid = false; break; }

                pointBuffer[numOutputPoints * 3] = ix + px * t;
                pointBuffer[numOutputPoints * 3 + 1] = iy + py * t;
                pointBuffer[numOutputPoints * 3 + 2] = iz + pz * t;
                numOutputPoints++;
            }

            if (!valid || numOutputPoints - fragStart < 3)
            {
                numOutputPoints = fragStart;
                continue;
            }

            // Write markFragment_t
            *(int*)(fragBuf + numFragments * FRAG_SIZE) = fragStart;
            *(int*)(fragBuf + numFragments * FRAG_SIZE + 4) = numOutputPoints - fragStart;
            numFragments++;
            break; // One fragment per call is sufficient for basic marks
        }

        return numFragments;
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

        // Font data is stored as binary in fonts/fontImage_{pointSize}.dat
        string datFile = $"fonts/fontImage_{pointSize}.dat";
        const int FONT_INFO_SIZE = 20548; // sizeof(fontInfo_t)

        int len = EngineImports.FS_ReadFile(datFile, out byte* data);
        if (len != FONT_INFO_SIZE || data == null)
        {
            if (data != null) EngineImports.FS_FreeFile(data);
            return;
        }

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
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void RemapShader(byte* oldShader, byte* newShader, byte* offsetTime) { }

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
        byte* encodeBuffer, int motionJpeg) { }
}
