using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using RendererDotNet.Models;
using RendererDotNet.World;

namespace RendererDotNet;

/// <summary>
/// Projected shadow mapping (pshadows): renders entity silhouettes from light perspective
/// into depth maps, then applies shadows to the scene via a screen-space fullscreen pass.
/// Port of GL2's pshadow system (tr_main.c R_RenderPshadowMaps + pshadow_fp.glsl).
/// </summary>
internal sealed unsafe class ShadowMapper : IDisposable
{
    private const int MAX_CALC_PSHADOWS = 64;
    private const int MAX_DRAWN_PSHADOWS = 16;
    private const int PSHADOW_MAP_SIZE = 512;

    private const int RF_FIRST_PERSON = 0x0004;
    private const int RF_THIRD_PERSON = 0x0008;
    private const int RF_NOSHADOW = 0x0040;
    private const int RT_MODEL = 0;

    private GL _gl = null!;
    private bool _enabled;
    private float _pshadowDist;

    // Shadow depth FBOs
    private uint[] _shadowFbos = new uint[MAX_DRAWN_PSHADOWS];
    private uint[] _shadowDepthTexs = new uint[MAX_DRAWN_PSHADOWS];

    // Shadow application shader
    private uint _applyProgram;
    private int _uSceneDepthLoc;
    private int _uShadowMapLoc;
    private int _uInvVPLoc;
    private int _uShadowVPLoc;
    private int _uLightOriginLoc;
    private int _uLightRadiusLoc;

    // Fullscreen quad
    private uint _quadVao, _quadVbo;

    // Per-frame shadow data
    private PShadow[] _shadows = new PShadow[MAX_CALC_PSHADOWS];
    private int _numShadows;
    private float[][] _shadowVPs = new float[MAX_DRAWN_PSHADOWS][];

    private struct PShadow
    {
        public int NumEntities;
        public int Ent0, Ent1, Ent2, Ent3, Ent4, Ent5, Ent6, Ent7;

        public float ViewOriginX, ViewOriginY, ViewOriginZ;
        public float ViewRadius;
        public float LightRadius;
        public float LightOriginX, LightOriginY, LightOriginZ;

        // Light view axes
        public float FwdX, FwdY, FwdZ;
        public float RightX, RightY, RightZ;
        public float UpX, UpY, UpZ;

        // Per-entity origins and radii (for merging)
        public float EO0X, EO0Y, EO0Z, ER0;
        public float EO1X, EO1Y, EO1Z, ER1;
        public float EO2X, EO2Y, EO2Z, ER2;
        public float EO3X, EO3Y, EO3Z, ER3;
        public float EO4X, EO4Y, EO4Z, ER4;
        public float EO5X, EO5Y, EO5Z, ER5;
        public float EO6X, EO6Y, EO6Z, ER6;
        public float EO7X, EO7Y, EO7Z, ER7;

        public float Sort;

        public int GetEntityIndex(int i) => i switch
        {
            0 => Ent0, 1 => Ent1, 2 => Ent2, 3 => Ent3,
            4 => Ent4, 5 => Ent5, 6 => Ent6, 7 => Ent7,
            _ => -1
        };
        public void SetEntityIndex(int i, int val) { switch (i) { case 0: Ent0 = val; break; case 1: Ent1 = val; break; case 2: Ent2 = val; break; case 3: Ent3 = val; break; case 4: Ent4 = val; break; case 5: Ent5 = val; break; case 6: Ent6 = val; break; case 7: Ent7 = val; break; } }

        public void GetEntityOrigin(int i, out float x, out float y, out float z)
        {
            switch (i)
            {
                case 0: x = EO0X; y = EO0Y; z = EO0Z; return;
                case 1: x = EO1X; y = EO1Y; z = EO1Z; return;
                case 2: x = EO2X; y = EO2Y; z = EO2Z; return;
                case 3: x = EO3X; y = EO3Y; z = EO3Z; return;
                case 4: x = EO4X; y = EO4Y; z = EO4Z; return;
                case 5: x = EO5X; y = EO5Y; z = EO5Z; return;
                case 6: x = EO6X; y = EO6Y; z = EO6Z; return;
                case 7: x = EO7X; y = EO7Y; z = EO7Z; return;
                default: x = y = z = 0; return;
            }
        }
        public void SetEntityOrigin(int i, float x, float y, float z)
        {
            switch (i)
            {
                case 0: EO0X = x; EO0Y = y; EO0Z = z; return;
                case 1: EO1X = x; EO1Y = y; EO1Z = z; return;
                case 2: EO2X = x; EO2Y = y; EO2Z = z; return;
                case 3: EO3X = x; EO3Y = y; EO3Z = z; return;
                case 4: EO4X = x; EO4Y = y; EO4Z = z; return;
                case 5: EO5X = x; EO5Y = y; EO5Z = z; return;
                case 6: EO6X = x; EO6Y = y; EO6Z = z; return;
                case 7: EO7X = x; EO7Y = y; EO7Z = z; return;
            }
        }
        public float GetEntityRadius(int i) => i switch
        {
            0 => ER0, 1 => ER1, 2 => ER2, 3 => ER3,
            4 => ER4, 5 => ER5, 6 => ER6, 7 => ER7,
            _ => 0
        };
        public void SetEntityRadius(int i, float r)
        {
            switch (i)
            {
                case 0: ER0 = r; break; case 1: ER1 = r; break;
                case 2: ER2 = r; break; case 3: ER3 = r; break;
                case 4: ER4 = r; break; case 5: ER5 = r; break;
                case 6: ER6 = r; break; case 7: ER7 = r; break;
            }
        }
    }

    public bool IsEnabled => _enabled;

    #region GLSL Shaders

    private const string ApplyVertSrc = """
        #version 450 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        out vec2 vUV;
        void main() {
            vUV = aUV;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    private const string ApplyFragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uSceneDepth;
        uniform sampler2D uShadowMap;
        uniform mat4 uInvVP;
        uniform mat4 uShadowVP;
        uniform vec3 uLightOrigin;
        uniform float uLightRadius;
        out vec4 oColor;

        void main() {
            float depth = texture(uSceneDepth, vUV).r;
            if (depth >= 0.9999) discard;

            // Reconstruct world position from depth
            vec4 clipPos = vec4(vUV * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
            vec4 wp4 = uInvVP * clipPos;
            vec3 worldPos = wp4.xyz / wp4.w;

            // Project into shadow clip space (ortho: w ≈ 1)
            vec4 sc = uShadowVP * vec4(worldPos, 1.0);
            vec2 st = sc.xy * 0.5 + 0.5;
            float currentZ = sc.z * 0.5 + 0.5;

            // Bounds check
            if (st.x < 0.0 || st.x > 1.0 || st.y < 0.0 || st.y > 1.0) discard;
            if (currentZ < 0.0 || currentZ > 1.0) discard;

            // PCF 3x3 soft shadow
            float texelSize = 1.0 / 512.0;
            float bias = 0.005;
            float shadow = 0.0;
            for (int x = -1; x <= 1; x++) {
                for (int y = -1; y <= 1; y++) {
                    float pcfZ = texture(uShadowMap, st + vec2(x, y) * texelSize).r;
                    shadow += (pcfZ + bias < currentZ) ? 1.0 : 0.0;
                }
            }
            shadow /= 9.0;
            if (shadow < 0.01) discard;

            // Distance fade
            float dist = length(worldPos - uLightOrigin);
            float fade = 1.0 - smoothstep(uLightRadius * 0.6, uLightRadius, dist);

            // Edge softening near shadow map boundaries
            float edgeFade = smoothstep(0.0, 0.05, min(st.x, 1.0 - st.x))
                           * smoothstep(0.0, 0.05, min(st.y, 1.0 - st.y));

            float alpha = shadow * 0.5 * fade * edgeFade;
            if (alpha < 0.001) discard;

            oColor = vec4(0.0, 0.0, 0.0, alpha);
        }
        """;

    #endregion

    public void Init(GL gl)
    {
        _gl = gl;

        Interop.EngineImports.Cvar_Get("r_shadows", "0", 0x01);
        Interop.EngineImports.Cvar_Get("r_pshadowDist", "128", 0);
        int rShadows = Interop.EngineImports.Cvar_VariableIntegerValue("r_shadows");
        _enabled = rShadows >= 1;

        if (!_enabled) return;

        _pshadowDist = 128.0f;
        int psDistInt = Interop.EngineImports.Cvar_VariableIntegerValue("r_pshadowDist");
        if (psDistInt > 0) _pshadowDist = psDistInt;

        // Create shadow depth FBOs
        for (int i = 0; i < MAX_DRAWN_PSHADOWS; i++)
        {
            _shadowDepthTexs[i] = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _shadowDepthTexs[i]);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24,
                PSHADOW_MAP_SIZE, PSHADOW_MAP_SIZE, 0,
                Silk.NET.OpenGL.PixelFormat.DepthComponent, Silk.NET.OpenGL.PixelType.Float, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToBorder);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToBorder);
            float[] borderColor = [1f, 1f, 1f, 1f];
            fixed (float* bc = borderColor)
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, bc);

            _shadowFbos[i] = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbos[i]);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D,
                _shadowDepthTexs[i], 0);
            gl.DrawBuffer(DrawBufferMode.None);
            gl.ReadBuffer(ReadBufferMode.None);
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Shadow application shader
        _applyProgram = CreateProgram(ApplyVertSrc, ApplyFragSrc);
        _uSceneDepthLoc = gl.GetUniformLocation(_applyProgram, "uSceneDepth");
        _uShadowMapLoc = gl.GetUniformLocation(_applyProgram, "uShadowMap");
        _uInvVPLoc = gl.GetUniformLocation(_applyProgram, "uInvVP");
        _uShadowVPLoc = gl.GetUniformLocation(_applyProgram, "uShadowVP");
        _uLightOriginLoc = gl.GetUniformLocation(_applyProgram, "uLightOrigin");
        _uLightRadiusLoc = gl.GetUniformLocation(_applyProgram, "uLightRadius");
        gl.UseProgram(_applyProgram);
        gl.Uniform1(_uSceneDepthLoc, 0);
        gl.Uniform1(_uShadowMapLoc, 1);
        gl.UseProgram(0);

        CreateQuadVao();

        for (int i = 0; i < MAX_DRAWN_PSHADOWS; i++)
            _shadowVPs[i] = new float[16];

        Interop.EngineImports.Printf(Interop.EngineImports.PRINT_ALL,
            $"[.NET] Shadow mapping initialized ({MAX_DRAWN_PSHADOWS}x {PSHADOW_MAP_SIZE}px)\n");
    }

    /// <summary>
    /// Collect shadow-casting entities, sort by priority, merge overlapping, compute light info.
    /// </summary>
    public void CollectShadows(List<SceneEntity> entities, ModelManager models,
                                BspWorld? bspData, float* viewOrg, float* viewFwd)
    {
        _numShadows = 0;
        if (!_enabled || entities.Count == 0) return;

        // Pass 1: Collect candidate shadows
        int numCalc = 0;
        for (int i = 0; i < entities.Count && numCalc < MAX_CALC_PSHADOWS; i++)
        {
            var ent = entities[i];

            if ((ent.Renderfx & (RF_FIRST_PERSON | RF_NOSHADOW)) != 0) continue;
            if (ent.ReType != RT_MODEL) continue;
            if (models.IsBspModel(ent.ModelHandle)) continue;

            float radius = GetEntityRadius(ent, models);
            if (radius <= 0) continue;

            // Cull entities behind the viewer
            float dx = ent.OriginX - viewOrg[0];
            float dy = ent.OriginY - viewOrg[1];
            float dz = ent.OriginZ - viewOrg[2];
            float viewDot = dx * viewFwd[0] + dy * viewFwd[1] + dz * viewFwd[2];
            if (viewDot < -_pshadowDist) continue;

            ref var shadow = ref _shadows[numCalc];
            shadow = default;
            shadow.NumEntities = 1;
            shadow.SetEntityIndex(0, i);
            shadow.ViewRadius = radius;
            shadow.LightRadius = _pshadowDist;
            shadow.ViewOriginX = ent.OriginX;
            shadow.ViewOriginY = ent.OriginY;
            shadow.ViewOriginZ = ent.OriginZ;
            shadow.SetEntityOrigin(0, ent.OriginX, ent.OriginY, ent.OriginZ);
            shadow.SetEntityRadius(0, radius);
            shadow.Sort = (dx * dx + dy * dy + dz * dz) / (radius * radius);

            // Insertion sort by distance/radius ratio
            int j;
            for (j = 0; j < numCalc; j++)
            {
                if (_shadows[j].Sort > shadow.Sort)
                {
                    // Shift and insert
                    var temp = shadow;
                    for (int k = numCalc; k > j; k--)
                        _shadows[k] = _shadows[k - 1];
                    _shadows[j] = temp;
                    break;
                }
            }
            numCalc++;
        }

        // Pass 2: Merge overlapping shadows
        for (int i = 0; i < numCalc; i++)
        {
            ref var ps1 = ref _shadows[i];
            for (int j = i + 1; j < numCalc; j++)
            {
                if (ps1.NumEntities >= 8) break;

                ref var ps2 = ref _shadows[j];
                bool touch = false;

                if (SpheresIntersect(ps1.ViewOriginX, ps1.ViewOriginY, ps1.ViewOriginZ, ps1.ViewRadius,
                                     ps2.ViewOriginX, ps2.ViewOriginY, ps2.ViewOriginZ, ps2.ViewRadius))
                {
                    for (int k = 0; k < ps1.NumEntities; k++)
                    {
                        ps1.GetEntityOrigin(k, out float eox, out float eoy, out float eoz);
                        float er = ps1.GetEntityRadius(k);
                        if (SpheresIntersect(eox, eoy, eoz, er,
                                           ps2.ViewOriginX, ps2.ViewOriginY, ps2.ViewOriginZ, ps2.ViewRadius))
                        {
                            touch = true;
                            break;
                        }
                    }
                }

                if (touch)
                {
                    BoundingSphereOfSpheres(
                        ps1.ViewOriginX, ps1.ViewOriginY, ps1.ViewOriginZ, ps1.ViewRadius,
                        ps2.ViewOriginX, ps2.ViewOriginY, ps2.ViewOriginZ, ps2.ViewRadius,
                        out float nx, out float ny, out float nz, out float nr);
                    ps1.ViewOriginX = nx; ps1.ViewOriginY = ny; ps1.ViewOriginZ = nz;
                    ps1.ViewRadius = nr;

                    int idx = ps1.NumEntities;
                    ps1.SetEntityIndex(idx, ps2.GetEntityIndex(0));
                    ps1.SetEntityOrigin(idx, ps2.ViewOriginX, ps2.ViewOriginY, ps2.ViewOriginZ);
                    ps1.SetEntityRadius(idx, ps2.ViewRadius);
                    ps1.NumEntities++;

                    // Remove ps2
                    for (int k = j; k < numCalc - 1; k++)
                        _shadows[k] = _shadows[k + 1];
                    j--;
                    numCalc--;
                }
            }
        }

        // Cap to max drawn
        _numShadows = Math.Min(numCalc, MAX_DRAWN_PSHADOWS);

        // Pass 3: Compute light direction and axes for each shadow
        for (int i = 0; i < _numShadows; i++)
        {
            ref var s = ref _shadows[i];

            // Sample light grid for light direction
            float ldX = 0.57735f, ldY = 0.57735f, ldZ = 0.57735f;
            if (bspData?.LightGridData != null)
            {
                bspData.SampleLightGrid(s.ViewOriginX, s.ViewOriginY, s.ViewOriginZ,
                    out _, out _, out _, out _, out _, out _, out ldX, out ldY, out ldZ);

                float ldLen = ldX * ldX + ldY * ldY + ldZ * ldZ;
                if (ldLen < 0.9f)
                {
                    ldX = 0; ldY = 0; ldZ = 1;
                }
            }

            if (s.ViewRadius * 3.0f > s.LightRadius)
                s.LightRadius = s.ViewRadius * 3.0f;

            // Light origin = shadow origin + viewRadius * lightDir
            s.LightOriginX = s.ViewOriginX + s.ViewRadius * ldX;
            s.LightOriginY = s.ViewOriginY + s.ViewRadius * ldY;
            s.LightOriginZ = s.ViewOriginZ + s.ViewRadius * ldZ;

            // Forward = -lightDir (light looks toward entities)
            s.FwdX = -ldX; s.FwdY = -ldY; s.FwdZ = -ldZ;

            // Compute right and up axes
            float upX = 0, upY = 0, upZ = -1;
            float dot = upX * s.FwdX + upY * s.FwdY + upZ * s.FwdZ;
            if (MathF.Abs(dot) > 0.9f)
            {
                upX = -1; upY = 0; upZ = 0;
            }

            // Right = cross(forward, up)
            s.RightX = s.FwdY * upZ - s.FwdZ * upY;
            s.RightY = s.FwdZ * upX - s.FwdX * upZ;
            s.RightZ = s.FwdX * upY - s.FwdY * upX;
            float rLen = MathF.Sqrt(s.RightX * s.RightX + s.RightY * s.RightY + s.RightZ * s.RightZ);
            if (rLen > 0.0001f) { s.RightX /= rLen; s.RightY /= rLen; s.RightZ /= rLen; }

            // Up = cross(forward, right)
            s.UpX = s.FwdY * s.RightZ - s.FwdZ * s.RightY;
            s.UpY = s.FwdZ * s.RightX - s.FwdX * s.RightZ;
            s.UpZ = s.FwdX * s.RightY - s.FwdY * s.RightX;
        }
    }

    /// <summary>
    /// Render entity silhouettes into shadow depth maps from each light's perspective.
    /// </summary>
    public void RenderShadowMaps(Renderer3D renderer, List<SceneEntity> entities,
                                  ModelManager models)
    {
        if (!_enabled || _numShadows == 0) return;

        Span<float> shadowView = stackalloc float[16];
        Span<float> shadowProj = stackalloc float[16];
        Span<float> shadowVP = stackalloc float[16];
        Span<float> modelMat = stackalloc float[16];
        Span<float> mvp = stackalloc float[16];
        Span<float> iqmPoseMats = stackalloc float[128 * 12];

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        _gl.ColorMask(false, false, false, false);
        _gl.Disable(EnableCap.Blend);

        for (int i = 0; i < _numShadows; i++)
        {
            ref var s = ref _shadows[i];

            // Bind shadow FBO
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbos[i]);
            _gl.Viewport(0, 0, PSHADOW_MAP_SIZE, PSHADOW_MAP_SIZE);
            _gl.Clear(ClearBufferMask.DepthBufferBit);

            // Build shadow view matrix
            BuildShadowViewMatrix(
                s.LightOriginX, s.LightOriginY, s.LightOriginZ,
                s.FwdX, s.FwdY, s.FwdZ,
                s.RightX, s.RightY, s.RightZ,
                s.UpX, s.UpY, s.UpZ,
                shadowView);

            // Build orthographic projection
            float vr = s.ViewRadius;
            float near = 1.0f;
            float far = s.LightRadius;
            BuildOrthoProjection(-vr, vr, -vr, vr, near, far, shadowProj);

            // Shadow VP
            MatMul(shadowProj, shadowView, shadowVP);

            // Store for application pass
            for (int j = 0; j < 16; j++)
                _shadowVPs[i][j] = shadowVP[j];

            // Render each entity in this shadow
            for (int ei = 0; ei < s.NumEntities; ei++)
            {
                int entIdx = s.GetEntityIndex(ei);
                if (entIdx < 0 || entIdx >= entities.Count) continue;
                var ent = entities[entIdx];

                var model = models.GetModel(ent.ModelHandle);
                var iqmModel = model == null ? models.GetIqmModel(ent.ModelHandle) : null;
                if (model == null && iqmModel == null) continue;

                BuildModelMatrix(ent, modelMat);
                MatMul(shadowVP, modelMat, mvp);

                if (iqmModel != null)
                {
                    int numJoints = iqmModel.NumJoints;
                    var poseMats = iqmPoseMats[..(numJoints * 12)];
                    IqmLoader.ComputePoseMatrices(iqmModel, ent.Frame, ent.OldFrame, ent.BackLerp, poseMats);

                    foreach (var surface in iqmModel.Surfaces)
                    {
                        fixed (float* mvpPtr = mvp)
                        fixed (float* modelPtr = modelMat)
                        fixed (float* posePtr = poseMats)
                        {
                            renderer.DrawIqmSurfaceDepthOnly(surface, iqmModel, posePtr, numJoints, mvpPtr, modelPtr);
                        }
                    }
                }
                else if (model != null)
                {
                    foreach (var surface in model.Surfaces)
                    {
                        fixed (float* mvpPtr = mvp)
                        {
                            renderer.DrawSurfaceDepthOnly(surface, ent.Frame, ent.OldFrame, ent.BackLerp, mvpPtr);
                        }
                    }
                }
            }
        }

        _gl.ColorMask(true, true, true, true);
    }

    /// <summary>
    /// Apply shadow darkening to the scene via fullscreen passes (one per shadow).
    /// Must be called while the scene FBO is bound, after all opaque/entity rendering.
    /// </summary>
    public void ApplyShadows(uint sceneDepthTex, ReadOnlySpan<float> invVP,
                              int viewportX, int viewportY, int viewportW, int viewportH)
    {
        if (!_enabled || _numShadows == 0) return;

        _gl.UseProgram(_applyProgram);
        _gl.BindVertexArray(_quadVao);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);
        _gl.Viewport(viewportX, viewportY, (uint)viewportW, (uint)viewportH);

        fixed (float* invVPPtr = invVP)
            _gl.UniformMatrix4(_uInvVPLoc, 1, false, invVPPtr);

        for (int i = 0; i < _numShadows; i++)
        {
            ref var s = ref _shadows[i];

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, sceneDepthTex);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _shadowDepthTexs[i]);

            fixed (float* svp = _shadowVPs[i])
                _gl.UniformMatrix4(_uShadowVPLoc, 1, false, svp);

            _gl.Uniform3(_uLightOriginLoc, s.LightOriginX, s.LightOriginY, s.LightOriginZ);
            _gl.Uniform1(_uLightRadiusLoc, s.LightRadius);

            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        // Restore state
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
    }

    #region Helpers

    private float GetEntityRadius(SceneEntity ent, ModelManager models)
    {
        var model = models.GetModel(ent.ModelHandle);
        if (model != null)
        {
            int frame = Math.Clamp(ent.Frame, 0, model.Frames.Length - 1);
            return model.Frames[frame].Radius;
        }

        var iqm = models.GetIqmModel(ent.ModelHandle);
        if (iqm?.Bounds != null && iqm.Bounds.Length >= (ent.Frame + 1) * 6)
        {
            int bo = ent.Frame * 6;
            float dx = iqm.Bounds[bo + 3] - iqm.Bounds[bo];
            float dy = iqm.Bounds[bo + 4] - iqm.Bounds[bo + 1];
            float dz = iqm.Bounds[bo + 5] - iqm.Bounds[bo + 2];
            return 0.5f * MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        return 0;
    }

    private static bool SpheresIntersect(float ax, float ay, float az, float ar,
                                          float bx, float by, float bz, float br)
    {
        float dx = ax - bx, dy = ay - by, dz = az - bz;
        float dist2 = dx * dx + dy * dy + dz * dz;
        float radSum = ar + br;
        return dist2 < radSum * radSum;
    }

    private static void BoundingSphereOfSpheres(float ax, float ay, float az, float ar,
                                                 float bx, float by, float bz, float br,
                                                 out float cx, out float cy, out float cz, out float cr)
    {
        float dx = bx - ax, dy = by - ay, dz = bz - az;
        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        if (dist + br <= ar) { cx = ax; cy = ay; cz = az; cr = ar; return; }
        if (dist + ar <= br) { cx = bx; cy = by; cz = bz; cr = br; return; }

        cr = (dist + ar + br) * 0.5f;
        float t = (cr - ar) / (dist > 0.0001f ? dist : 1.0f);
        cx = ax + dx * t;
        cy = ay + dy * t;
        cz = az + dz * t;
    }

    private static void BuildShadowViewMatrix(
        float ox, float oy, float oz,
        float fx, float fy, float fz,
        float rx, float ry, float rz,
        float ux, float uy, float uz,
        Span<float> m)
    {
        // Standard OpenGL view matrix with explicit right/up/forward
        m.Clear();
        m[0] = rx;  m[4] = ry;  m[8]  = rz;   m[12] = -(rx * ox + ry * oy + rz * oz);
        m[1] = ux;  m[5] = uy;  m[9]  = uz;   m[13] = -(ux * ox + uy * oy + uz * oz);
        m[2] = -fx; m[6] = -fy; m[10] = -fz;  m[14] = fx * ox + fy * oy + fz * oz;
        m[3] = 0;   m[7] = 0;   m[11] = 0;    m[15] = 1;
    }

    private static void BuildOrthoProjection(float left, float right, float bottom, float top,
                                              float near, float far, Span<float> m)
    {
        m.Clear();
        m[0] = 2.0f / (right - left);
        m[5] = 2.0f / (top - bottom);
        m[10] = -2.0f / (far - near);
        m[12] = -(right + left) / (right - left);
        m[13] = -(top + bottom) / (top - bottom);
        m[14] = -(far + near) / (far - near);
        m[15] = 1.0f;
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
            for (int row = 0; row < 4; row++)
                result[col * 4 + row] =
                    a[0 * 4 + row] * b[col * 4 + 0] +
                    a[1 * 4 + row] * b[col * 4 + 1] +
                    a[2 * 4 + row] * b[col * 4 + 2] +
                    a[3 * 4 + row] * b[col * 4 + 3];
    }

    /// <summary>
    /// Invert a 4x4 column-major matrix. Returns false if singular.
    /// </summary>
    public static bool InvertMatrix(ReadOnlySpan<float> m, Span<float> inv)
    {
        float a00 = m[0], a01 = m[1], a02 = m[2], a03 = m[3];
        float a10 = m[4], a11 = m[5], a12 = m[6], a13 = m[7];
        float a20 = m[8], a21 = m[9], a22 = m[10], a23 = m[11];
        float a30 = m[12], a31 = m[13], a32 = m[14], a33 = m[15];

        float b00 = a00 * a11 - a01 * a10;
        float b01 = a00 * a12 - a02 * a10;
        float b02 = a00 * a13 - a03 * a10;
        float b03 = a01 * a12 - a02 * a11;
        float b04 = a01 * a13 - a03 * a11;
        float b05 = a02 * a13 - a03 * a12;
        float b06 = a20 * a31 - a21 * a30;
        float b07 = a20 * a32 - a22 * a30;
        float b08 = a20 * a33 - a23 * a30;
        float b09 = a21 * a32 - a22 * a31;
        float b10 = a21 * a33 - a23 * a31;
        float b11 = a22 * a33 - a23 * a32;

        float det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
        if (MathF.Abs(det) < 1e-10f)
        {
            inv.Clear();
            return false;
        }

        float invDet = 1.0f / det;
        inv[0] = (a11 * b11 - a12 * b10 + a13 * b09) * invDet;
        inv[1] = (a02 * b10 - a01 * b11 - a03 * b09) * invDet;
        inv[2] = (a31 * b05 - a32 * b04 + a33 * b03) * invDet;
        inv[3] = (a22 * b04 - a21 * b05 - a23 * b03) * invDet;
        inv[4] = (a12 * b08 - a10 * b11 - a13 * b07) * invDet;
        inv[5] = (a00 * b11 - a02 * b08 + a03 * b07) * invDet;
        inv[6] = (a32 * b02 - a30 * b05 - a33 * b01) * invDet;
        inv[7] = (a20 * b05 - a22 * b02 + a23 * b01) * invDet;
        inv[8] = (a10 * b10 - a11 * b08 + a13 * b06) * invDet;
        inv[9] = (a01 * b08 - a00 * b10 - a03 * b06) * invDet;
        inv[10] = (a30 * b04 - a31 * b02 + a33 * b00) * invDet;
        inv[11] = (a21 * b02 - a20 * b04 - a23 * b00) * invDet;
        inv[12] = (a11 * b07 - a10 * b09 - a12 * b06) * invDet;
        inv[13] = (a00 * b09 - a01 * b07 + a02 * b06) * invDet;
        inv[14] = (a31 * b01 - a30 * b03 - a32 * b00) * invDet;
        inv[15] = (a20 * b03 - a21 * b01 + a22 * b00) * invDet;
        return true;
    }

    private void CreateQuadVao()
    {
        float[] verts =
        [
            -1f, -1f, 0f, 0f,
             1f, -1f, 1f, 0f,
            -1f,  1f, 0f, 1f,
             1f,  1f, 1f, 1f
        ];

        _quadVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_quadVao);

        _quadVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        uint stride = 4 * sizeof(float);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, null);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);

        _gl.BindVertexArray(0);
    }

    private uint CreateProgram(string vertSrc, string fragSrc)
    {
        uint vs = Compile(ShaderType.VertexShader, vertSrc);
        uint fs = Compile(ShaderType.FragmentShader, fragSrc);

        uint prog = _gl.CreateProgram();
        _gl.AttachShader(prog, vs);
        _gl.AttachShader(prog, fs);
        _gl.LinkProgram(prog);

        _gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetProgramInfoLog(prog);
            Interop.EngineImports.Printf(Interop.EngineImports.PRINT_ERROR,
                $"[.NET] Shadow program link error: {log}\n");
        }

        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return prog;
    }

    private uint Compile(ShaderType type, string src)
    {
        uint s = _gl.CreateShader(type);
        _gl.ShaderSource(s, src);
        _gl.CompileShader(s);
        _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetShaderInfoLog(s);
            Interop.EngineImports.Printf(Interop.EngineImports.PRINT_ERROR,
                $"[.NET] Shadow shader compile error ({type}): {log}\n");
        }
        return s;
    }

    #endregion

    public void Dispose()
    {
        if (_applyProgram != 0) _gl.DeleteProgram(_applyProgram);
        for (int i = 0; i < MAX_DRAWN_PSHADOWS; i++)
        {
            if (_shadowFbos[i] != 0) _gl.DeleteFramebuffer(_shadowFbos[i]);
            if (_shadowDepthTexs[i] != 0) _gl.DeleteTexture(_shadowDepthTexs[i]);
        }
        if (_quadVao != 0) _gl.DeleteVertexArray(_quadVao);
        if (_quadVbo != 0) _gl.DeleteBuffer(_quadVbo);
    }
}
