using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using RendererDotNet.Models;
using RendererDotNet.Interop;

namespace RendererDotNet;

/// <summary>
/// Manages the 3D scene: accumulates entities between ClearScene and RenderScene,
/// then renders them all with proper view/projection transforms.
/// </summary>
public sealed unsafe class SceneManager
{
    private readonly List<SceneEntity> _entities = new(256);
    private readonly List<ScenePoly> _polys = new(512);
    private ModelManager? _models;
    private ShaderManager? _shaders;
    private SkinManager? _skins;
    private Renderer3D? _renderer3D;
    private World.BspRenderer? _bspRenderer;
    private World.SkyboxRenderer? _skyboxRenderer;
    private World.BspWorld? _bspWorld;
    private GL? _gl;
    private int _screenW;
    private int _screenH;

    // GL resources for sprite/poly rendering
    private uint _spriteVao;
    private uint _spriteVbo;
    private uint _spriteEbo;
    private uint _spriteProgram;
    private int _spriteMvpLoc;
    private int _spriteTexLoc;

    private const int RF_THIRD_PERSON = 0x0002;
    private const int RDF_NOWORLDMODEL = 0x0001;

    // Entity types
    private const int RT_MODEL = 0;
    private const int RT_POLY = 1;
    private const int RT_SPRITE = 2;
    private const int RT_BEAM = 3;
    private const int RT_RAIL_CORE = 4;
    private const int RT_RAIL_RINGS = 5;
    private const int RT_LIGHTNING = 6;

    public void Init(ModelManager models, ShaderManager shaders, SkinManager skins,
                     Renderer3D renderer3D, World.BspRenderer bspRenderer,
                     World.SkyboxRenderer skyboxRenderer,
                     GL gl, int screenW, int screenH)
    {
        _models = models;
        _shaders = shaders;
        _skins = skins;
        _renderer3D = renderer3D;
        _bspRenderer = bspRenderer;
        _skyboxRenderer = skyboxRenderer;
        _gl = gl;
        _screenW = screenW;
        _screenH = screenH;

        InitSpriteRenderer(gl);
    }

    public void SetWorld(World.BspWorld? world)
    {
        _bspWorld = world;
    }

    public void ClearScene()
    {
        _entities.Clear();
        _polys.Clear();
    }

    /// <summary>
    /// Add a ref entity to the current scene. The entity data is copied.
    /// refEntity_t layout (from tr_types.h):
    ///   0: int reType
    ///   4: int renderfx
    ///   8: int hModel
    ///  12: float[3] lightingOrigin
    ///  24: float shadowPlane
    ///  28: float[3][3] axis (3 column vectors)
    ///  64: int nonNormalizedAxes
    ///  68: float[3] origin
    ///  80: int frame
    ///  84: float[3] oldorigin
    ///  96: int oldframe
    /// 100: float backlerp
    /// 104: int skinNum
    /// 108: int customSkin
    /// 112: int customShader
    /// 116: byte[4] shaderRGBA
    /// 120: float[2] shaderTexCoord
    /// 128: float shaderTime
    /// 132: float radius
    /// 136: float rotation
    /// </summary>
    public void AddRefEntity(byte* entityPtr)
    {
        if (entityPtr == null) return;

        int reType = *(int*)(entityPtr + 0);
        int renderfx = *(int*)(entityPtr + 4);

        if (reType == RT_MODEL)
        {
            int hModel = *(int*)(entityPtr + 8);
            if (hModel <= 0) return;

            var entity = new SceneEntity
            {
                ReType = reType,
                Renderfx = renderfx,
                ModelHandle = hModel,
                Frame = *(int*)(entityPtr + 80),
                OldFrame = *(int*)(entityPtr + 96),
                BackLerp = *(float*)(entityPtr + 100),
                CustomSkin = *(int*)(entityPtr + 108),
                CustomShader = *(int*)(entityPtr + 112),
            };

            float* origin = (float*)(entityPtr + 68);
            entity.OriginX = origin[0];
            entity.OriginY = origin[1];
            entity.OriginZ = origin[2];

            float* axis = (float*)(entityPtr + 28);
            for (int i = 0; i < 9; i++)
                entity.Axis[i] = axis[i];

            byte* rgba = entityPtr + 116;
            entity.R = rgba[0] / 255.0f;
            entity.G = rgba[1] / 255.0f;
            entity.B = rgba[2] / 255.0f;
            entity.A = rgba[3] / 255.0f;

            _entities.Add(entity);
        }
        else if (reType == RT_SPRITE)
        {
            int customShader = *(int*)(entityPtr + 112);
            if (customShader <= 0) return;

            float* origin = (float*)(entityPtr + 68);
            byte* rgba = entityPtr + 116;

            var entity = new SceneEntity
            {
                ReType = reType,
                Renderfx = renderfx,
                CustomShader = customShader,
                OriginX = origin[0],
                OriginY = origin[1],
                OriginZ = origin[2],
                Radius = *(float*)(entityPtr + 132),
                Rotation = *(float*)(entityPtr + 136),
                R = rgba[0] / 255.0f,
                G = rgba[1] / 255.0f,
                B = rgba[2] / 255.0f,
                A = rgba[3] / 255.0f,
            };

            _entities.Add(entity);
        }
        else if (reType is RT_BEAM or RT_LIGHTNING or RT_RAIL_CORE or RT_RAIL_RINGS)
        {
            int customShader = *(int*)(entityPtr + 112);
            if (customShader <= 0) return;

            float* origin = (float*)(entityPtr + 68);
            float* oldorigin = (float*)(entityPtr + 84);
            byte* rgba = entityPtr + 116;

            var entity = new SceneEntity
            {
                ReType = reType,
                Renderfx = renderfx,
                CustomShader = customShader,
                OriginX = origin[0],
                OriginY = origin[1],
                OriginZ = origin[2],
                OldOriginX = oldorigin[0],
                OldOriginY = oldorigin[1],
                OldOriginZ = oldorigin[2],
                Radius = *(float*)(entityPtr + 132),
                R = rgba[0] / 255.0f,
                G = rgba[1] / 255.0f,
                B = rgba[2] / 255.0f,
                A = rgba[3] / 255.0f,
            };
            if (entity.Radius <= 0) entity.Radius = 2.0f;

            _entities.Add(entity);
        }
    }

    /// <summary>
    /// Add world-space polygons (used for decals, particle trails, blood, etc.)
    /// polyVert_t: { float xyz[3]; float st[2]; byte modulate[4]; } = 24 bytes
    /// </summary>
    public void AddPoly(int hShader, int numVerts, byte* vertsPtr, int numPolys)
    {
        if (hShader <= 0 || numVerts < 3 || vertsPtr == null) return;

        for (int p = 0; p < numPolys; p++)
        {
            var poly = new ScenePoly
            {
                ShaderHandle = hShader,
                Verts = new PolyVert[numVerts],
            };

            byte* src = vertsPtr + p * numVerts * 24;
            for (int v = 0; v < numVerts; v++)
            {
                float* f = (float*)(src + v * 24);
                byte* mod = src + v * 24 + 20;
                poly.Verts[v] = new PolyVert
                {
                    X = f[0], Y = f[1], Z = f[2],
                    U = f[3], V = f[4],
                    R = mod[0] / 255.0f,
                    G = mod[1] / 255.0f,
                    B = mod[2] / 255.0f,
                    A = mod[3] / 255.0f,
                };
            }

            _polys.Add(poly);
        }
    }

    /// <summary>
    /// Render all accumulated entities with the given view parameters.
    /// refdef_t layout (from tr_types.h):
    ///   0: int x
    ///   4: int y
    ///   8: int width
    ///  12: int height
    ///  16: float fov_x
    ///  20: float fov_y
    ///  24: float[3] vieworg
    ///  36: float[3][3] viewaxis
    ///  72: int time
    ///  76: int rdflags
    /// </summary>
    public void RenderScene(byte* refdefPtr)
    {
        if (refdefPtr == null || _renderer3D == null || _models == null || _shaders == null || _gl == null)
            return;

        int x = *(int*)(refdefPtr + 0);
        int y = *(int*)(refdefPtr + 4);
        int width = *(int*)(refdefPtr + 8);
        int height = *(int*)(refdefPtr + 12);
        float fovX = *(float*)(refdefPtr + 16);
        float fovY = *(float*)(refdefPtr + 20);
        float* viewOrg = (float*)(refdefPtr + 24);
        float* viewAxis = (float*)(refdefPtr + 36);
        int rdflags = *(int*)(refdefPtr + 76);

        if (width <= 0 || height <= 0) return;

        bool noWorldModel = (rdflags & RDF_NOWORLDMODEL) != 0;
        bool hasWorld = !noWorldModel && _bspWorld != null && _bspRenderer != null;
        bool hasEntities = _entities.Count > 0;
        bool hasPolys = _polys.Count > 0;
        if (!hasWorld && !hasEntities && !hasPolys) return;

        // Set GL viewport to the refdef rectangle
        int glY = _screenH - (y + height);
        _gl.Viewport(x, glY, (uint)width, (uint)height);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(x, glY, (uint)width, (uint)height);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.Clear(ClearBufferMask.DepthBufferBit);

        // Build view matrix from vieworg + viewaxis
        Span<float> view = stackalloc float[16];
        BuildViewMatrix(viewOrg, viewAxis, view);

        // Build perspective projection matrix
        Span<float> proj = stackalloc float[16];
        BuildPerspective(fovX, fovY, 1.0f, 8192.0f, proj);

        // VP = Projection * View
        Span<float> vp = stackalloc float[16];
        MatMul(proj, view, vp);

        // Render skybox before world geometry
        if (hasWorld && _skyboxRenderer != null && _skyboxRenderer.IsLoaded)
        {
            fixed (float* vpPtr = vp)
            {
                _skyboxRenderer.Render(vpPtr, viewOrg[0], viewOrg[1], viewOrg[2]);
            }
        }

        // Render world geometry (BSP)
        if (hasWorld)
        {
            fixed (float* vpPtr = vp)
            {
                _bspRenderer!.Render(vpPtr, viewOrg[0], viewOrg[1], viewOrg[2], _shaders);
            }
        }

        // Render entities
        Span<float> modelMat = stackalloc float[16];
        Span<float> mvp = stackalloc float[16];

        foreach (var ent in _entities)
        {
            // Skip RF_THIRD_PERSON entities (player's own model)
            if ((ent.Renderfx & RF_THIRD_PERSON) != 0)
                continue;

            if (ent.ReType == RT_MODEL)
            {
                var model = _models.GetModel(ent.ModelHandle);
                if (model == null) continue;

                BuildModelMatrix(ent, modelMat);
                MatMul(vp, modelMat, mvp);

                foreach (var surface in model.Surfaces)
                {
                    int shaderHandle;
                    if (ent.CustomShader > 0)
                    {
                        shaderHandle = ent.CustomShader;
                    }
                    else if (ent.CustomSkin > 0 && _skins != null)
                    {
                        int skinSh = _skins.GetSurfaceShader(ent.CustomSkin, surface.Name);
                        shaderHandle = skinSh > 0 ? skinSh : surface.ShaderHandle;
                    }
                    else
                    {
                        shaderHandle = surface.ShaderHandle;
                    }
                    uint texId = _shaders.GetTextureId(shaderHandle);

                    float r = ent.R > 0 ? ent.R : 1.0f;
                    float g = ent.G > 0 ? ent.G : 1.0f;
                    float b = ent.B > 0 ? ent.B : 1.0f;
                    float a = ent.A > 0 ? ent.A : 1.0f;

                    fixed (float* mvpPtr = mvp)
                    fixed (float* modelPtr = modelMat)
                    {
                        _renderer3D.DrawSurface(surface, ent.Frame, ent.OldFrame, ent.BackLerp,
                            mvpPtr, modelPtr, texId, r, g, b, a);
                    }
                }
            }
            else if (ent.ReType == RT_SPRITE)
            {
                DrawSprite(ent, viewAxis, vp);
            }
            else if (ent.ReType is RT_BEAM or RT_LIGHTNING or RT_RAIL_CORE or RT_RAIL_RINGS)
            {
                DrawBeam(ent, viewOrg, vp);
            }
        }

        // Render scene polys (decals, trails, effects)
        if (hasPolys)
        {
            DrawPolys(vp);
        }

        // Restore full-screen viewport for 2D rendering
        _gl.Viewport(0, 0, (uint)_screenW, (uint)_screenH);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.DepthTest);
    }
    /// Q3 coordinate system: X=forward, Y=left, Z=up.
    /// OpenGL: X=right, Y=up, Z=-forward.
    /// </summary>
    private static void BuildViewMatrix(float* org, float* axis, Span<float> m)
    {
        // Q3 viewaxis: [0-2] = forward, [3-5] = left, [6-8] = up
        float fx = axis[0], fy = axis[1], fz = axis[2]; // forward
        float lx = axis[3], ly = axis[4], lz = axis[5]; // left
        float ux = axis[6], uy = axis[7], uz = axis[8]; // up

        // OpenGL right = -left, OpenGL up = up, OpenGL forward = -forward
        m.Clear();
        m[0] = -lx;  m[4] = -ly;  m[8]  = -lz;  m[12] = lx * org[0] + ly * org[1] + lz * org[2];
        m[1] = ux;   m[5] = uy;   m[9]  = uz;    m[13] = -(ux * org[0] + uy * org[1] + uz * org[2]);
        m[2] = -fx;  m[6] = -fy;  m[10] = -fz;   m[14] = fx * org[0] + fy * org[1] + fz * org[2];
        m[3] = 0;    m[7] = 0;    m[11] = 0;     m[15] = 1;
    }

    private static void BuildPerspective(float fovX, float fovY, float near, float far, Span<float> m)
    {
        float xmax = near * MathF.Tan(fovX * MathF.PI / 360.0f);
        float ymax = near * MathF.Tan(fovY * MathF.PI / 360.0f);

        m.Clear();
        m[0] = near / xmax;
        m[5] = near / ymax;
        m[10] = -(far + near) / (far - near);
        m[11] = -1.0f;
        m[14] = -(2.0f * far * near) / (far - near);
    }

    private static void BuildModelMatrix(SceneEntity ent, Span<float> m)
    {
        m.Clear();
        m[0] = ent.Axis[0]; m[4] = ent.Axis[3]; m[8]  = ent.Axis[6]; m[12] = ent.OriginX;
        m[1] = ent.Axis[1]; m[5] = ent.Axis[4]; m[9]  = ent.Axis[7]; m[13] = ent.OriginY;
        m[2] = ent.Axis[2]; m[6] = ent.Axis[5]; m[10] = ent.Axis[8]; m[14] = ent.OriginZ;
        m[3] = 0;           m[7] = 0;           m[11] = 0;           m[15] = 1;
    }

    private static void MatMul(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> result)
    {
        for (int col = 0; col < 4; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                result[col * 4 + row] =
                    a[0 * 4 + row] * b[col * 4 + 0] +
                    a[1 * 4 + row] * b[col * 4 + 1] +
                    a[2 * 4 + row] * b[col * 4 + 2] +
                    a[3 * 4 + row] * b[col * 4 + 3];
            }
        }
    }

    // --- Sprite and Poly rendering ---

    private const string SpriteVertSrc = """
        #version 450 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec4 aColor;

        uniform mat4 uMVP;

        out vec2 vUV;
        out vec4 vColor;

        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
            vUV = aUV;
            vColor = aColor;
        }
        """;

    private const string SpriteFragSrc = """
        #version 450 core
        in vec2 vUV;
        in vec4 vColor;

        uniform sampler2D uTex;

        out vec4 oColor;

        void main() {
            vec4 texColor = texture(uTex, vUV);
            oColor = texColor * vColor;
            if (oColor.a < 0.01) discard;
        }
        """;

    private void InitSpriteRenderer(GL gl)
    {
        // Compile shader
        uint vs = CompileShader(gl, ShaderType.VertexShader, SpriteVertSrc);
        uint fs = CompileShader(gl, ShaderType.FragmentShader, SpriteFragSrc);
        _spriteProgram = gl.CreateProgram();
        gl.AttachShader(_spriteProgram, vs);
        gl.AttachShader(_spriteProgram, fs);
        gl.LinkProgram(_spriteProgram);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        _spriteMvpLoc = gl.GetUniformLocation(_spriteProgram, "uMVP");
        _spriteTexLoc = gl.GetUniformLocation(_spriteProgram, "uTex");

        _spriteVao = gl.GenVertexArray();
        _spriteVbo = gl.GenBuffer();
        _spriteEbo = gl.GenBuffer();

        gl.BindVertexArray(_spriteVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _spriteVbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _spriteEbo);

        // Per vertex: x,y,z, u,v, r,g,b,a = 9 floats
        uint stride = 9 * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        gl.EnableVertexAttribArray(2);

        gl.BindVertexArray(0);
    }

    private static uint CompileShader(GL gl, ShaderType type, string src)
    {
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        return s;
    }

    /// <summary>
    /// Draw a camera-facing billboard sprite.
    /// </summary>
    private void DrawSprite(SceneEntity ent, float* viewAxis, ReadOnlySpan<float> vp)
    {
        if (_gl == null || _shaders == null) return;

        float radius = ent.Radius;
        if (radius <= 0) return;

        uint texId = _shaders.GetTextureId(ent.CustomShader);

        // Q3 viewaxis: [3-5] = left, [6-8] = up
        float lx = viewAxis[3], ly = viewAxis[4], lz = viewAxis[5];
        float ux = viewAxis[6], uy = viewAxis[7], uz = viewAxis[8];

        // Apply rotation if any
        if (ent.Rotation != 0)
        {
            float ang = MathF.PI * ent.Rotation / 180.0f;
            float c = MathF.Cos(ang), s = MathF.Sin(ang);
            float nlx = c * lx - s * ux, nly = c * ly - s * uy, nlz = c * lz - s * uz;
            float nux = s * lx + c * ux, nuy = s * ly + c * uy, nuz = s * lz + c * uz;
            lx = nlx; ly = nly; lz = nlz;
            ux = nux; uy = nuy; uz = nuz;
        }

        float ox = ent.OriginX, oy = ent.OriginY, oz = ent.OriginZ;

        // 4 corners: -left-up, +left-up, +left+up, -left+up
        Span<float> verts = stackalloc float[4 * 9];
        SetSpriteVert(verts, 0, ox - lx * radius - ux * radius,
                                oy - ly * radius - uy * radius,
                                oz - lz * radius - uz * radius,
                                0, 1, ent.R, ent.G, ent.B, ent.A);
        SetSpriteVert(verts, 1, ox + lx * radius - ux * radius,
                                oy + ly * radius - uy * radius,
                                oz + lz * radius - uz * radius,
                                1, 1, ent.R, ent.G, ent.B, ent.A);
        SetSpriteVert(verts, 2, ox + lx * radius + ux * radius,
                                oy + ly * radius + uy * radius,
                                oz + lz * radius + uz * radius,
                                1, 0, ent.R, ent.G, ent.B, ent.A);
        SetSpriteVert(verts, 3, ox - lx * radius + ux * radius,
                                oy - ly * radius + uy * radius,
                                oz - lz * radius + uz * radius,
                                0, 0, ent.R, ent.G, ent.B, ent.A);

        Span<uint> indices = stackalloc uint[] { 0, 1, 2, 0, 2, 3 };

        _gl.UseProgram(_spriteProgram);
        fixed (float* vpPtr = vp)
            _gl.UniformMatrix4(_spriteMvpLoc, 1, false, vpPtr);
        _gl.Uniform1(_spriteTexLoc, 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texId);

        _gl.BindVertexArray(_spriteVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _spriteVbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _spriteEbo);
        fixed (uint* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StreamDraw);

        _gl.Enable(EnableCap.Blend);
        BlendMode spriteBlend = _shaders.GetBlendMode(ent.CustomShader);
        // Sprites should never be fully opaque — force at least alpha blending
        if (spriteBlend.IsOpaque)
            spriteBlend = BlendMode.Alpha;
        _gl.BlendFunc((BlendingFactor)spriteBlend.SrcFactor, (BlendingFactor)spriteBlend.DstFactor);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);

        _gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, null);

        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }

    private static void SetSpriteVert(Span<float> buf, int idx,
        float x, float y, float z, float u, float v,
        float r, float g, float b, float a)
    {
        int o = idx * 9;
        buf[o] = x; buf[o+1] = y; buf[o+2] = z;
        buf[o+3] = u; buf[o+4] = v;
        buf[o+5] = r; buf[o+6] = g; buf[o+7] = b; buf[o+8] = a;
    }

    /// <summary>
    /// Draw a beam/lightning/rail effect as a billboarded quad strip from origin to oldorigin.
    /// </summary>
    private void DrawBeam(SceneEntity ent, float* viewOrg, ReadOnlySpan<float> vp)
    {
        if (_gl == null || _shaders == null) return;

        float radius = ent.Radius;
        if (radius <= 0) radius = 2.0f;

        // Direction from origin to oldorigin
        float dx = ent.OldOriginX - ent.OriginX;
        float dy = ent.OldOriginY - ent.OriginY;
        float dz = ent.OldOriginZ - ent.OriginZ;
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 0.001f) return;

        float invLen = 1.0f / len;
        dx *= invLen; dy *= invLen; dz *= invLen;

        // View direction from midpoint to camera
        float midX = (ent.OriginX + ent.OldOriginX) * 0.5f;
        float midY = (ent.OriginY + ent.OldOriginY) * 0.5f;
        float midZ = (ent.OriginZ + ent.OldOriginZ) * 0.5f;

        float vx = viewOrg[0] - midX;
        float vy = viewOrg[1] - midY;
        float vz = viewOrg[2] - midZ;

        // Cross product of beam direction with view direction → perpendicular offset
        float px = dy * vz - dz * vy;
        float py = dz * vx - dx * vz;
        float pz = dx * vy - dy * vx;
        float plen = MathF.Sqrt(px * px + py * py + pz * pz);
        if (plen < 0.001f) return;

        float pInv = radius / plen;
        px *= pInv; py *= pInv; pz *= pInv;

        // 4 vertices forming a quad
        float numSegU = len / 128.0f; // Tile texture along length
        Span<float> verts = stackalloc float[4 * 9];
        SetSpriteVert(verts, 0,
            ent.OriginX + px, ent.OriginY + py, ent.OriginZ + pz,
            0, 0, ent.R, ent.G, ent.B, ent.A);
        SetSpriteVert(verts, 1,
            ent.OriginX - px, ent.OriginY - py, ent.OriginZ - pz,
            0, 1, ent.R, ent.G, ent.B, ent.A);
        SetSpriteVert(verts, 2,
            ent.OldOriginX - px, ent.OldOriginY - py, ent.OldOriginZ - pz,
            numSegU, 1, ent.R, ent.G, ent.B, ent.A);
        SetSpriteVert(verts, 3,
            ent.OldOriginX + px, ent.OldOriginY + py, ent.OldOriginZ + pz,
            numSegU, 0, ent.R, ent.G, ent.B, ent.A);

        Span<uint> indices = stackalloc uint[] { 0, 1, 2, 0, 2, 3 };

        uint texId = _shaders.GetTextureId(ent.CustomShader);

        _gl.UseProgram(_spriteProgram);
        fixed (float* vpPtr = vp)
            _gl.UniformMatrix4(_spriteMvpLoc, 1, false, vpPtr);
        _gl.Uniform1(_spriteTexLoc, 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texId);

        _gl.BindVertexArray(_spriteVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _spriteVbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _spriteEbo);
        fixed (uint* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StreamDraw);

        _gl.Enable(EnableCap.Blend);
        BlendMode beamBlend = _shaders.GetBlendMode(ent.CustomShader);
        _gl.BlendFunc((BlendingFactor)beamBlend.SrcFactor, (BlendingFactor)beamBlend.DstFactor);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);

        _gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, null);

        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Render all accumulated scene polys as triangle fans.
    /// </summary>
    private void DrawPolys(ReadOnlySpan<float> vp)
    {
        if (_gl == null || _shaders == null || _polys.Count == 0) return;

        _gl.UseProgram(_spriteProgram);
        fixed (float* vpPtr = vp)
            _gl.UniformMatrix4(_spriteMvpLoc, 1, false, vpPtr);
        _gl.Uniform1(_spriteTexLoc, 0);

        _gl.BindVertexArray(_spriteVao);

        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);

        // Max poly verts is typically 4-10, pre-allocate outside loop
        const int MAX_POLY_VERTS = 64;
        Span<float> polyVertBuf = stackalloc float[MAX_POLY_VERTS * 9];
        Span<uint> polyIdxBuf = stackalloc uint[(MAX_POLY_VERTS - 2) * 3];

        foreach (var poly in _polys)
        {
            int nv = poly.Verts.Length;
            if (nv < 3 || nv > MAX_POLY_VERTS) continue;

            uint texId = _shaders.GetTextureId(poly.ShaderHandle);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, texId);

            // Apply per-shader blend mode (polys should always blend)
            BlendMode polyBlend = _shaders.GetBlendMode(poly.ShaderHandle);
            if (polyBlend.IsOpaque)
                polyBlend = BlendMode.Alpha;
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc((BlendingFactor)polyBlend.SrcFactor, (BlendingFactor)polyBlend.DstFactor);

            // Build triangle fan vertices
            var verts = polyVertBuf[..(nv * 9)];
            for (int i = 0; i < nv; i++)
            {
                ref var pv = ref poly.Verts[i];
                int o = i * 9;
                verts[o] = pv.X; verts[o+1] = pv.Y; verts[o+2] = pv.Z;
                verts[o+3] = pv.U; verts[o+4] = pv.V;
                verts[o+5] = pv.R; verts[o+6] = pv.G; verts[o+7] = pv.B; verts[o+8] = pv.A;
            }

            // Triangle fan indices
            int numTris = nv - 2;
            var indices = polyIdxBuf[..(numTris * 3)];
            for (int i = 0; i < numTris; i++)
            {
                indices[i * 3] = 0;
                indices[i * 3 + 1] = (uint)(i + 1);
                indices[i * 3 + 2] = (uint)(i + 2);
            }

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _spriteVbo);
            fixed (float* p = verts)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(nv * 9 * sizeof(float)), p, BufferUsageARB.StreamDraw);
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _spriteEbo);
            fixed (uint* p = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(numTris * 3 * sizeof(uint)), p, BufferUsageARB.StreamDraw);

            _gl.DrawElements(PrimitiveType.Triangles, (uint)(numTris * 3), DrawElementsType.UnsignedInt, null);
        }

        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }
}

/// <summary>
/// Cached entity data for scene rendering.
/// </summary>
internal unsafe struct SceneEntity
{
    public int ReType;
    public int Renderfx;
    public int ModelHandle;
    public int Frame;
    public int OldFrame;
    public float BackLerp;
    public int CustomSkin;
    public int CustomShader;

    public float OriginX, OriginY, OriginZ;
    public float OldOriginX, OldOriginY, OldOriginZ;
    public fixed float Axis[9];
    public float R, G, B, A;

    // Sprite-specific
    public float Radius;
    public float Rotation;
}

internal struct PolyVert
{
    public float X, Y, Z;
    public float U, V;
    public float R, G, B, A;
}

internal struct ScenePoly
{
    public int ShaderHandle;
    public PolyVert[] Verts;
}
