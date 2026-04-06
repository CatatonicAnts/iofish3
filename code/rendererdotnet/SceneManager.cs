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
    private ModelManager? _models;
    private ShaderManager? _shaders;
    private SkinManager? _skins;
    private Renderer3D? _renderer3D;
    private World.BspRenderer? _bspRenderer;
    private World.BspWorld? _bspWorld;
    private GL? _gl;
    private int _screenW;
    private int _screenH;

    public void Init(ModelManager models, ShaderManager shaders, SkinManager skins,
                     Renderer3D renderer3D, World.BspRenderer bspRenderer,
                     GL gl, int screenW, int screenH)
    {
        _models = models;
        _shaders = shaders;
        _skins = skins;
        _renderer3D = renderer3D;
        _bspRenderer = bspRenderer;
        _gl = gl;
        _screenW = screenW;
        _screenH = screenH;
    }

    public void SetWorld(World.BspWorld? world)
    {
        _bspWorld = world;
    }

    public void ClearScene()
    {
        _entities.Clear();
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
        if (reType != 0) // Only RT_MODEL for now
            return;

        int hModel = *(int*)(entityPtr + 8);
        if (hModel <= 0) return;

        var entity = new SceneEntity
        {
            ModelHandle = hModel,
            Frame = *(int*)(entityPtr + 80),
            OldFrame = *(int*)(entityPtr + 96),
            BackLerp = *(float*)(entityPtr + 100),
            CustomSkin = *(int*)(entityPtr + 108),
            CustomShader = *(int*)(entityPtr + 112),
        };

        // Origin
        float* origin = (float*)(entityPtr + 68);
        entity.OriginX = origin[0];
        entity.OriginY = origin[1];
        entity.OriginZ = origin[2];

        // Axis (3x3 rotation matrix as 3 column vectors)
        float* axis = (float*)(entityPtr + 28);
        for (int i = 0; i < 9; i++)
            entity.Axis[i] = axis[i];

        // shaderRGBA
        byte* rgba = entityPtr + 116;
        entity.R = rgba[0] / 255.0f;
        entity.G = rgba[1] / 255.0f;
        entity.B = rgba[2] / 255.0f;
        entity.A = rgba[3] / 255.0f;

        _entities.Add(entity);
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

        if (width <= 0 || height <= 0) return;

        bool hasWorld = _bspWorld != null && _bspRenderer != null;
        bool hasEntities = _entities.Count > 0;
        if (!hasWorld && !hasEntities) return;

        // Set GL viewport to the refdef rectangle
        // Q3 y=0 is top of screen, OpenGL y=0 is bottom — flip vertically
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

        // Render world geometry (BSP) first
        if (hasWorld)
        {
            fixed (float* vpPtr = vp)
            {
                _bspRenderer!.Render(vpPtr, viewOrg[0], viewOrg[1], viewOrg[2], _shaders);
            }
        }

        // Pre-allocate outside loop to avoid stackalloc warnings
        Span<float> modelMat = stackalloc float[16];
        Span<float> mvp = stackalloc float[16];

        foreach (var ent in _entities)
        {
            var model = _models.GetModel(ent.ModelHandle);
            if (model == null) continue;

            // Build model matrix from entity origin + axis
            BuildModelMatrix(ent, modelMat);

            // MVP = VP * Model
            MatMul(vp, modelMat, mvp);

            foreach (var surface in model.Surfaces)
            {
                // Determine texture: customShader > customSkin > surface default
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
        // We need to transform to OpenGL view space
        float fx = axis[0], fy = axis[1], fz = axis[2]; // forward
        float lx = axis[3], ly = axis[4], lz = axis[5]; // left
        float ux = axis[6], uy = axis[7], uz = axis[8]; // up

        // OpenGL right = -left, OpenGL up = up, OpenGL forward = -forward
        // View matrix rows: right, up, -forward
        m.Clear();
        m[0] = -lx;  m[4] = -ly;  m[8]  = -lz;  m[12] = lx * org[0] + ly * org[1] + lz * org[2];
        m[1] = ux;   m[5] = uy;   m[9]  = uz;    m[13] = -(ux * org[0] + uy * org[1] + uz * org[2]);
        m[2] = -fx;  m[6] = -fy;  m[10] = -fz;   m[14] = fx * org[0] + fy * org[1] + fz * org[2];
        m[3] = 0;    m[7] = 0;    m[11] = 0;     m[15] = 1;
    }

    /// <summary>
    /// Build a perspective projection matrix from field-of-view angles.
    /// </summary>
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

    /// <summary>
    /// Build a model matrix from entity origin and Q3 axis.
    /// </summary>
    private static void BuildModelMatrix(SceneEntity ent, Span<float> m)
    {
        // Q3 axis: axis[0-2] = forward, axis[3-5] = left/right, axis[6-8] = up
        m.Clear();
        m[0] = ent.Axis[0]; m[4] = ent.Axis[3]; m[8]  = ent.Axis[6]; m[12] = ent.OriginX;
        m[1] = ent.Axis[1]; m[5] = ent.Axis[4]; m[9]  = ent.Axis[7]; m[13] = ent.OriginY;
        m[2] = ent.Axis[2]; m[6] = ent.Axis[5]; m[10] = ent.Axis[8]; m[14] = ent.OriginZ;
        m[3] = 0;           m[7] = 0;           m[11] = 0;           m[15] = 1;
    }

    /// <summary>
    /// 4x4 matrix multiply: result = a * b (column-major).
    /// </summary>
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
}

/// <summary>
/// Cached entity data for scene rendering.
/// </summary>
internal unsafe struct SceneEntity
{
    public int ModelHandle;
    public int Frame;
    public int OldFrame;
    public float BackLerp;
    public int CustomSkin;
    public int CustomShader;

    public float OriginX, OriginY, OriginZ;

    // 3x3 axis as flat array: [fwd.x, fwd.y, fwd.z, left.x, left.y, left.z, up.x, up.y, up.z]
    public fixed float Axis[9];

    public float R, G, B, A;
}
