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
    private readonly List<DLight> _dlights = new(32);
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
    private PostProcess? _postProcess;

    // GL resources for sprite/poly rendering
    private uint _spriteVao;
    private uint _spriteVbo;
    private uint _spriteEbo;
    private uint _spriteProgram;
    private int _spriteMvpLoc;
    private int _spriteTexLoc;

    private const int RF_THIRD_PERSON = 0x0002;
    private const int RF_FIRST_PERSON = 0x0004;
    private const int RF_DEPTHHACK = 0x0008;
    private const int RF_LIGHTING_ORIGIN = 0x0080;
    private const int RDF_NOWORLDMODEL = 0x0001;

    // Entity types
    private const int RT_MODEL = 0;
    private const int RT_POLY = 1;
    private const int RT_SPRITE = 2;
    private const int RT_BEAM = 3;
    private const int RT_RAIL_CORE = 4;
    private const int RT_RAIL_RINGS = 5;
    private const int RT_LIGHTNING = 6;
    private const int RT_PORTALSURFACE = 7;

    // Portal rendering state
    private uint _portalFbo;
    private uint _portalColorTex;
    private uint _portalDepthRbo;
    private int _portalTexW;
    private int _portalTexH;
    private bool _isPortalPass;  // Prevent recursion

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

    public void SetPostProcess(PostProcess? pp)
    {
        _postProcess = pp;
    }

    public void ClearScene()
    {
        _entities.Clear();
        _polys.Clear();
        _dlights.Clear();
    }

    private const int MAX_DLIGHTS = 32;

    public void AddLight(float x, float y, float z, float radius, float r, float g, float b, bool additive)
    {
        if (_dlights.Count >= MAX_DLIGHTS || radius <= 0) return;
        _dlights.Add(new DLight
        {
            OriginX = x, OriginY = y, OriginZ = z,
            R = r, G = g, B = b,
            Radius = radius,
            Additive = additive
        });
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

            // Read lightingOrigin for RF_LIGHTING_ORIGIN
            float* lightOrigin = (float*)(entityPtr + 12);
            if ((renderfx & RF_LIGHTING_ORIGIN) != 0)
            {
                entity.LightOriginX = lightOrigin[0];
                entity.LightOriginY = lightOrigin[1];
                entity.LightOriginZ = lightOrigin[2];
            }
            else
            {
                entity.LightOriginX = origin[0];
                entity.LightOriginY = origin[1];
                entity.LightOriginZ = origin[2];
            }

            float* axis = (float*)(entityPtr + 28);
            for (int i = 0; i < 9; i++)
                entity.Axis[i] = axis[i];

            byte* rgba = entityPtr + 116;
            // Q3 convention: all-zero RGBA = uninitialized → default to white.
            // If RGB is set but alpha is 0, treat alpha as fully opaque
            // (alpha=0 typically means "not explicitly set" from memset).
            if (rgba[0] == 0 && rgba[1] == 0 && rgba[2] == 0 && rgba[3] == 0)
            {
                entity.R = 1.0f;
                entity.G = 1.0f;
                entity.B = 1.0f;
                entity.A = 1.0f;
            }
            else
            {
                entity.R = rgba[0] / 255.0f;
                entity.G = rgba[1] / 255.0f;
                entity.B = rgba[2] / 255.0f;
                entity.A = rgba[3] == 0 ? 1.0f : rgba[3] / 255.0f;
            }

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
            };

            if (rgba[0] == 0 && rgba[1] == 0 && rgba[2] == 0 && rgba[3] == 0)
            {
                entity.R = 1.0f; entity.G = 1.0f; entity.B = 1.0f; entity.A = 1.0f;
            }
            else
            {
                entity.R = rgba[0] / 255.0f;
                entity.G = rgba[1] / 255.0f;
                entity.B = rgba[2] / 255.0f;
                entity.A = rgba[3] == 0 ? 1.0f : rgba[3] / 255.0f;
            }

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
            };
            if (rgba[0] == 0 && rgba[1] == 0 && rgba[2] == 0 && rgba[3] == 0)
            {
                entity.R = 1.0f; entity.G = 1.0f; entity.B = 1.0f; entity.A = 1.0f;
            }
            else
            {
                entity.R = rgba[0] / 255.0f;
                entity.G = rgba[1] / 255.0f;
                entity.B = rgba[2] / 255.0f;
                entity.A = rgba[3] == 0 ? 1.0f : rgba[3] / 255.0f;
            }
            if (entity.Radius <= 0) entity.Radius = 2.0f;

            _entities.Add(entity);
        }
        else if (reType == RT_PORTALSURFACE)
        {
            // Portal surface entity: origin = portal position, oldorigin = camera target
            float* origin = (float*)(entityPtr + 68);
            float* oldorigin = (float*)(entityPtr + 84);
            float* axis = (float*)(entityPtr + 28);

            var entity = new SceneEntity
            {
                ReType = reType,
                Renderfx = renderfx,
                OriginX = origin[0],
                OriginY = origin[1],
                OriginZ = origin[2],
                OldOriginX = oldorigin[0],
                OldOriginY = oldorigin[1],
                OldOriginZ = oldorigin[2],
                Frame = *(int*)(entityPtr + 80),
                OldFrame = *(int*)(entityPtr + 96),
                CustomSkin = *(int*)(entityPtr + 104), // skinNum for rotation
            };
            for (int i = 0; i < 9; i++)
                entity.Axis[i] = axis[i];

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
        int time = *(int*)(refdefPtr + 72);
        int rdflags = *(int*)(refdefPtr + 76);

        if (width <= 0 || height <= 0) return;

        // Update shader manager time for animated textures
        if (_shaders != null)
            _shaders.CurrentTimeMs = time;

        bool noWorldModel = (rdflags & RDF_NOWORLDMODEL) != 0;
        bool hasWorld = !noWorldModel && _bspWorld != null && _bspRenderer != null;
        bool hasEntities = _entities.Count > 0;
        bool hasPolys = _polys.Count > 0;
        if (!hasWorld && !hasEntities && !hasPolys) return;

        // Portal/mirror rendering pass (before main scene)
        uint portalTexture = 0;
        if (hasWorld && !_isPortalPass)
        {
            portalTexture = TryRenderPortal(viewOrg, viewAxis, fovX, fovY,
                x, y, width, height, time, rdflags);
            if (portalTexture != 0)
                _bspRenderer!.SetPortalTexture(portalTexture);
            else
                _bspRenderer!.SetPortalTexture(0);
        }

        // Bind scene FBO for post-processing (only for world scenes)
        bool usePostProcess = hasWorld && _postProcess != null && _postProcess.IsEnabled;
        if (usePostProcess)
        {
            _postProcess!.BindSceneFbo();
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        }

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
                _bspRenderer!.Render(vpPtr, viewOrg[0], viewOrg[1], viewOrg[2], _shaders, time / 1000.0f,
                    _dlights);
            }
        }

        // Render entities
        Span<float> modelMat = stackalloc float[16];
        Span<float> mvp = stackalloc float[16];
        Span<float> iqmPoseMats = stackalloc float[128 * 12]; // max 128 joints for IQM

        foreach (var ent in _entities)
        {
            // Skip RF_THIRD_PERSON entities (player's own model)
            if ((ent.Renderfx & RF_THIRD_PERSON) != 0)
                continue;

            // RF_DEPTHHACK: draw weapon models in front using reduced depth range
            bool depthHack = (ent.Renderfx & RF_DEPTHHACK) != 0;
            if (depthHack)
                _gl.DepthRange(0.0, 0.3);

            if (ent.ReType == RT_MODEL)
            {
                // Check if this is an inline BSP model (doors, platforms, etc.)
                if (_models.IsBspModel(ent.ModelHandle) && _bspRenderer != null)
                {
                    int bspIdx = _models.GetBspModelIndex(ent.ModelHandle);
                    BuildModelMatrix(ent, modelMat);
                    MatMul(vp, modelMat, mvp);
                    fixed (float* mvpPtr = mvp)
                    {
                        _bspRenderer.RenderSubmodel(bspIdx, mvpPtr, _shaders, time / 1000.0f);
                    }
                    continue;
                }

                var model = _models.GetModel(ent.ModelHandle);
                var iqmModel = model == null ? _models.GetIqmModel(ent.ModelHandle) : null;
                if (model == null && iqmModel == null) continue;

                BuildModelMatrix(ent, modelMat);
                MatMul(vp, modelMat, mvp);

                // Sample light grid for entity lighting
                float ambR = 0.5f, ambG = 0.5f, ambB = 0.5f;
                float dLightR = 0.5f, dLightG = 0.5f, dLightB = 0.5f;
                float ldX = 0.57735f, ldY = 0.57735f, ldZ = 0.57735f;
                if (hasWorld && _bspWorld!.LightGridData != null)
                {
                    _bspWorld.SampleLightGrid(
                        ent.LightOriginX, ent.LightOriginY, ent.LightOriginZ,
                        out ambR, out ambG, out ambB,
                        out dLightR, out dLightG, out dLightB,
                        out ldX, out ldY, out ldZ);
                    // Transform light direction to entity-local space
                    float tlx = ent.Axis[0] * ldX + ent.Axis[1] * ldY + ent.Axis[2] * ldZ;
                    float tly = ent.Axis[3] * ldX + ent.Axis[4] * ldY + ent.Axis[5] * ldZ;
                    float tlz = ent.Axis[6] * ldX + ent.Axis[7] * ldY + ent.Axis[8] * ldZ;
                    ldX = tlx; ldY = tly; ldZ = tlz;
                }

                if (iqmModel != null)
                {
                    // IQM model — compute bone pose matrices and render with CPU skinning
                    int numJoints = iqmModel.NumJoints;
                    var poseMats = iqmPoseMats[..(numJoints * 12)];
                    IqmLoader.ComputePoseMatrices(iqmModel, ent.Frame, ent.OldFrame, ent.BackLerp, poseMats);

                    foreach (var surface in iqmModel.Surfaces)
                    {
                        int shaderHandle = ResolveIqmSurfaceShader(ent, surface);
                        uint texId = _shaders.GetTextureId(shaderHandle);
                        bool envMap = _shaders.GetHasEnvMap(shaderHandle);
                        BlendMode blend = _shaders.GetBlendMode(shaderHandle);
                        int cullMode = _shaders.GetCullMode(shaderHandle);

                        fixed (float* mvpPtr = mvp)
                        fixed (float* modelPtr = modelMat)
                        fixed (float* posePtr = poseMats)
                        {
                            _renderer3D.DrawIqmSurface(surface, iqmModel, posePtr, numJoints,
                                mvpPtr, modelPtr, texId, ent.R, ent.G, ent.B, ent.A,
                                envMap, viewOrg[0], viewOrg[1], viewOrg[2], blend, cullMode,
                                ambR, ambG, ambB, dLightR, dLightG, dLightB, ldX, ldY, ldZ);
                        }
                    }
                }
                else
                {

                foreach (var surface in model!.Surfaces)
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
                    bool envMap = _shaders.GetHasEnvMap(shaderHandle);
                    BlendMode blend = _shaders.GetBlendMode(shaderHandle);
                    int cullMode = _shaders.GetCullMode(shaderHandle);

                    float r = ent.R;
                    float g = ent.G;
                    float b = ent.B;
                    float a = ent.A;

                    fixed (float* mvpPtr = mvp)
                    fixed (float* modelPtr = modelMat)
                    {
                        _renderer3D.DrawSurface(surface, ent.Frame, ent.OldFrame, ent.BackLerp,
                            mvpPtr, modelPtr, texId, r, g, b, a,
                            envMap, viewOrg[0], viewOrg[1], viewOrg[2], blend, cullMode,
                            ambR, ambG, ambB, dLightR, dLightG, dLightB, ldX, ldY, ldZ);
                    }
                }

                } // end MD3 path
            }
            else if (ent.ReType == RT_SPRITE)
            {
                DrawSprite(ent, viewAxis, vp);
            }
            else if (ent.ReType is RT_BEAM or RT_LIGHTNING or RT_RAIL_CORE or RT_RAIL_RINGS)
            {
                DrawBeam(ent, viewOrg, vp);
            }

            if (depthHack)
                _gl.DepthRange(0.0, 1.0);
        }

        // Render scene polys (decals, trails, effects)
        if (hasPolys)
        {
            DrawPolys(vp);
        }

        // Apply post-processing (bloom) and blit to default framebuffer
        if (usePostProcess)
        {
            _postProcess!.ApplyAndBlit();
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

    private int ResolveIqmSurfaceShader(SceneEntity ent, IqmSurface surface)
    {
        if (ent.CustomShader > 0)
            return ent.CustomShader;
        if (ent.CustomSkin > 0 && _skins != null)
        {
            int skinSh = _skins.GetSurfaceShader(ent.CustomSkin, surface.Name);
            if (skinSh > 0) return skinSh;
        }
        return surface.ShaderHandle;
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

    #region Portal / Mirror Rendering

    /// <summary>Ensure the portal FBO exists and matches the given dimensions.</summary>
    private void EnsurePortalFbo(int width, int height)
    {
        if (_gl == null) return;
        if (_portalFbo != 0 && _portalTexW == width && _portalTexH == height)
            return;

        DestroyPortalFbo();

        _portalFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _portalFbo);

        _portalColorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _portalColorTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)width, (uint)height, 0,
            Silk.NET.OpenGL.PixelFormat.Rgba, Silk.NET.OpenGL.PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _portalColorTex, 0);

        _portalDepthRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _portalDepthRbo);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
            (uint)width, (uint)height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _portalDepthRbo);

        _portalTexW = width;
        _portalTexH = height;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void DestroyPortalFbo()
    {
        if (_gl == null) return;
        if (_portalColorTex != 0) { _gl.DeleteTexture(_portalColorTex); _portalColorTex = 0; }
        if (_portalDepthRbo != 0) { _gl.DeleteRenderbuffer(_portalDepthRbo); _portalDepthRbo = 0; }
        if (_portalFbo != 0) { _gl.DeleteFramebuffer(_portalFbo); _portalFbo = 0; }
        _portalTexW = 0;
        _portalTexH = 0;
    }

    /// <summary>
    /// Attempt to render a portal/mirror scene. Returns the portal texture ID,
    /// or 0 if portal rendering was not possible.
    /// </summary>
    private uint TryRenderPortal(
        float* viewOrg, float* viewAxis,
        float fovX, float fovY,
        int x, int y, int width, int height,
        int time, int rdflags)
    {
        if (_isPortalPass || _bspRenderer == null || _bspWorld == null || _gl == null || _shaders == null)
            return 0;

        // Get portal surface info from BspRenderer
        if (!_bspRenderer.GetPortalSurface(out float pnX, out float pnY, out float pnZ, out float pDist,
                out float pcX, out float pcY, out float pcZ))
            return 0;

        // Find matching RT_PORTALSURFACE entity
        bool isMirror = false;
        float camX = 0, camY = 0, camZ = 0;

        bool foundEntity = false;
        foreach (var ent in _entities)
        {
            if (ent.ReType != RT_PORTALSURFACE) continue;

            // Check distance from entity to portal plane
            float d = pnX * ent.OriginX + pnY * ent.OriginY + pnZ * ent.OriginZ - pDist;
            if (d > 64 || d < -64) continue;

            foundEntity = true;

            // Mirror: origin == oldorigin
            if (ent.OriginX == ent.OldOriginX &&
                ent.OriginY == ent.OldOriginY &&
                ent.OriginZ == ent.OldOriginZ)
            {
                isMirror = true;
            }
            else
            {
                // Portal: camera at oldorigin
                camX = ent.OldOriginX;
                camY = ent.OldOriginY;
                camZ = ent.OldOriginZ;
            }
            break;
        }

        if (!foundEntity)
        {
            // No portal entity found — treat as mirror (common case for q3dm0)
            isMirror = true;
        }

        // Compute mirrored/portal view
        Span<float> newViewOrg = stackalloc float[3];
        Span<float> newViewAxis = stackalloc float[9];

        if (isMirror)
        {
            // Reflect camera position across the portal plane
            float dot = viewOrg[0] * pnX + viewOrg[1] * pnY + viewOrg[2] * pnZ - pDist;
            newViewOrg[0] = viewOrg[0] - 2 * dot * pnX;
            newViewOrg[1] = viewOrg[1] - 2 * dot * pnY;
            newViewOrg[2] = viewOrg[2] - 2 * dot * pnZ;

            // Reflect each view axis across the portal plane
            for (int a = 0; a < 3; a++)
            {
                float ax = viewAxis[a * 3 + 0];
                float ay = viewAxis[a * 3 + 1];
                float az = viewAxis[a * 3 + 2];
                float d = ax * pnX + ay * pnY + az * pnZ;
                newViewAxis[a * 3 + 0] = ax - 2 * d * pnX;
                newViewAxis[a * 3 + 1] = ay - 2 * d * pnY;
                newViewAxis[a * 3 + 2] = az - 2 * d * pnZ;
            }
        }
        else
        {
            // Portal: use camera position and reflect view axes
            // Build surface coordinate frame
            float s0x = pnX, s0y = pnY, s0z = pnZ; // surface normal
            // Perpendicular vector
            float s1x, s1y, s1z;
            if (MathF.Abs(s0z) > 0.9f)
            {
                s1x = 1; s1y = 0; s1z = 0;
            }
            else
            {
                s1x = 0; s1y = 0; s1z = 1;
            }
            // s1 = s1 - (s1·s0)*s0
            float ds = s1x * s0x + s1y * s0y + s1z * s0z;
            s1x -= ds * s0x; s1y -= ds * s0y; s1z -= ds * s0z;
            float len = MathF.Sqrt(s1x * s1x + s1y * s1y + s1z * s1z);
            if (len > 0.001f) { s1x /= len; s1y /= len; s1z /= len; }
            // s2 = cross(s0, s1)
            float s2x = s0y * s1z - s0z * s1y;
            float s2y = s0z * s1x - s0x * s1z;
            float s2z = s0x * s1y - s0y * s1x;

            newViewOrg[0] = camX;
            newViewOrg[1] = camY;
            newViewOrg[2] = camZ;

            // Mirror each view axis through surface → camera
            for (int a = 0; a < 3; a++)
            {
                float ax = viewAxis[a * 3 + 0];
                float ay = viewAxis[a * 3 + 1];
                float az = viewAxis[a * 3 + 2];
                float d0 = ax * s0x + ay * s0y + az * s0z;
                float d1 = ax * s1x + ay * s1y + az * s1z;
                float d2 = ax * s2x + ay * s2y + az * s2z;
                // Negate d0 component (camera faces opposite direction through portal)
                newViewAxis[a * 3 + 0] = -d0 * s0x + d1 * s1x + d2 * s2x;
                newViewAxis[a * 3 + 1] = -d0 * s0y + d1 * s1y + d2 * s2y;
                newViewAxis[a * 3 + 2] = -d0 * s0z + d1 * s1z + d2 * s2z;
            }
        }

        // Create/resize portal FBO
        EnsurePortalFbo(width, height);
        if (_portalFbo == 0) return 0;

        // Render portal scene to FBO
        _isPortalPass = true;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _portalFbo);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(0, 0, (uint)width, (uint)height);
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Build portal view+projection matrices
        Span<float> pView = stackalloc float[16];
        Span<float> pProj = stackalloc float[16];
        Span<float> pVp = stackalloc float[16];

        fixed (float* newOrgPtr = newViewOrg)
        fixed (float* newAxisPtr = newViewAxis)
        {
            BuildViewMatrix(newOrgPtr, newAxisPtr, pView);
        }
        BuildPerspective(fovX, fovY, 1.0f, 8192.0f, pProj);
        MatMul(pProj, pView, pVp);

        float timeSec = time / 1000.0f;

        // Mirror: flip face culling
        if (isMirror)
            _gl.FrontFace(FrontFaceDirection.CW);

        // Render skybox
        if (_skyboxRenderer != null && _skyboxRenderer.IsLoaded)
        {
            fixed (float* vpPtr = pVp)
                _skyboxRenderer.Render(vpPtr, newViewOrg[0], newViewOrg[1], newViewOrg[2]);
        }

        // Render world BSP (skip portal surfaces in portal pass)
        fixed (float* vpPtr = pVp)
        {
            _bspRenderer.Render(vpPtr, newViewOrg[0], newViewOrg[1], newViewOrg[2],
                _shaders, timeSec, _dlights);
        }

        // Render entities (skip portal surface entities and RF_THIRD_PERSON)
        Span<float> modelMat = stackalloc float[16];
        Span<float> mvp = stackalloc float[16];
        Span<float> iqmPoseMats2 = stackalloc float[128 * 12];

        foreach (var ent in _entities)
        {
            if (ent.ReType == RT_PORTALSURFACE) continue;
            if ((ent.Renderfx & RF_THIRD_PERSON) != 0) continue;

            bool depthHack = (ent.Renderfx & RF_DEPTHHACK) != 0;
            if (depthHack)
                _gl.DepthRange(0.0, 0.3);

            if (ent.ReType == RT_MODEL && _models != null)
            {
                if (_models.IsBspModel(ent.ModelHandle))
                {
                    int bspIdx = _models.GetBspModelIndex(ent.ModelHandle);
                    BuildModelMatrix(ent, modelMat);
                    MatMul(pVp, modelMat, mvp);
                    fixed (float* mvpPtr = mvp)
                        _bspRenderer.RenderSubmodel(bspIdx, mvpPtr, _shaders, timeSec);
                    continue;
                }

                var model = _models.GetModel(ent.ModelHandle);
                var iqmModel = model == null ? _models.GetIqmModel(ent.ModelHandle) : null;
                if (model == null && iqmModel == null) continue;

                BuildModelMatrix(ent, modelMat);
                MatMul(pVp, modelMat, mvp);

                float ambR = 0.5f, ambG = 0.5f, ambB = 0.5f;
                float dLR = 0.5f, dLG = 0.5f, dLB = 0.5f;
                float ldX = 0.577f, ldY = 0.577f, ldZ = 0.577f;
                if (_bspWorld.LightGridData != null)
                {
                    _bspWorld.SampleLightGrid(
                        ent.LightOriginX, ent.LightOriginY, ent.LightOriginZ,
                        out ambR, out ambG, out ambB,
                        out dLR, out dLG, out dLB,
                        out ldX, out ldY, out ldZ);
                    float tlx = ent.Axis[0] * ldX + ent.Axis[1] * ldY + ent.Axis[2] * ldZ;
                    float tly = ent.Axis[3] * ldX + ent.Axis[4] * ldY + ent.Axis[5] * ldZ;
                    float tlz = ent.Axis[6] * ldX + ent.Axis[7] * ldY + ent.Axis[8] * ldZ;
                    ldX = tlx; ldY = tly; ldZ = tlz;
                }

                if (iqmModel != null)
                {
                    int numJoints = iqmModel.NumJoints;
                    var poseMats = iqmPoseMats2[..(numJoints * 12)];
                    IqmLoader.ComputePoseMatrices(iqmModel, ent.Frame, ent.OldFrame, ent.BackLerp, poseMats);

                    foreach (var surface in iqmModel.Surfaces)
                    {
                        int shaderHandle = ResolveIqmSurfaceShader(ent, surface);
                        uint texId = _shaders.GetTextureId(shaderHandle);
                        bool envMap = _shaders.GetHasEnvMap(shaderHandle);
                        BlendMode blend = _shaders.GetBlendMode(shaderHandle);
                        int cullMode = _shaders.GetCullMode(shaderHandle);

                        fixed (float* mvpPtr = mvp)
                        fixed (float* modelPtr = modelMat)
                        fixed (float* posePtr = poseMats)
                        {
                            _renderer3D!.DrawIqmSurface(surface, iqmModel, posePtr, numJoints,
                                mvpPtr, modelPtr, texId, ent.R, ent.G, ent.B, ent.A,
                                envMap, newViewOrg[0], newViewOrg[1], newViewOrg[2], blend, cullMode,
                                ambR, ambG, ambB, dLR, dLG, dLB, ldX, ldY, ldZ);
                        }
                    }
                }
                else
                {

                foreach (var surface in model!.Surfaces)
                {
                    int shaderHandle = surface.ShaderHandle;
                    if (ent.CustomShader > 0) shaderHandle = ent.CustomShader;
                    else if (ent.CustomSkin > 0 && _skins != null)
                    {
                        int skinSh = _skins.GetSurfaceShader(ent.CustomSkin, surface.Name);
                        if (skinSh > 0) shaderHandle = skinSh;
                    }
                    uint texId = _shaders.GetTextureId(shaderHandle);
                    bool envMap = _shaders.GetHasEnvMap(shaderHandle);
                    BlendMode blend = _shaders.GetBlendMode(shaderHandle);
                    int cullMode = _shaders.GetCullMode(shaderHandle);

                    fixed (float* mvpPtr = mvp)
                    fixed (float* modelPtr = modelMat)
                    {
                        _renderer3D!.DrawSurface(surface, ent.Frame, ent.OldFrame, ent.BackLerp,
                            mvpPtr, modelPtr, texId, ent.R, ent.G, ent.B, ent.A,
                            envMap, newViewOrg[0], newViewOrg[1], newViewOrg[2], blend, cullMode,
                            ambR, ambG, ambB, dLR, dLG, dLB, ldX, ldY, ldZ);
                    }
                }

                } // end MD3 path
            }
            else if (ent.ReType == RT_SPRITE)
            {
                fixed (float* vpPtr = pVp)
                fixed (float* axPtr = newViewAxis)
                {
                    DrawSprite(ent, axPtr, new ReadOnlySpan<float>(vpPtr, 16));
                }
            }

            if (depthHack)
                _gl.DepthRange(0.0, 1.0);
        }

        // Restore state
        if (isMirror)
            _gl.FrontFace(FrontFaceDirection.Ccw);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _isPortalPass = false;

        return _portalColorTex;
    }

    #endregion
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
    public float LightOriginX, LightOriginY, LightOriginZ;
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

/// <summary>
/// Dynamic light for per-frame illumination (rockets, plasma, etc.)
/// </summary>
public struct DLight
{
    public float OriginX, OriginY, OriginZ;
    public float R, G, B;
    public float Radius;
    public bool Additive;
}
