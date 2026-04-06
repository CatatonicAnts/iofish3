using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using RendererDotNet.Interop;

namespace RendererDotNet.World;

/// <summary>
/// Uploads BSP world geometry to the GPU and renders visible surfaces.
/// Vertices are uploaded once at load time. Surfaces are drawn per-frame
/// based on BSP tree visibility.
/// </summary>
public sealed unsafe class BspRenderer : IDisposable
{
    private GL _gl = null!;
    private uint _program;
    private int _mvpLoc;
    private int _colorLoc;
    private int _lightDirLoc;
    private int _useLightmapLoc;
    private int _texDiffuseLoc;
    private int _texLightmapLoc;
    private int _alphaFuncLoc;
    private int _envMapLoc;
    private int _viewPosLoc;
    private int _timeLoc;
    private int _tcModCountLoc;
    private int _tcModLoc;
    private int _rgbGenLoc;
    private int _deformTypeLoc;
    private int _deformParams0Loc;
    private int _deformParams1Loc;
    private int _overbrightScaleLoc;

    private uint _vao;
    private uint _vbo;
    private uint _ebo;

    private BspWorld? _world;
    private uint[] _lightmapTextures = [];

    // Per-vertex: x,y,z, nx,ny,nz, u,v, lmU,lmV, r,g,b,a = 14 floats
    private const int FLOATS_PER_VERT = 14;

    // Visibility tracking to avoid drawing same surface twice per frame
    private int[] _surfaceDrawnFrame = [];
    private int _frameCount;

    // Transparent surfaces deferred to second pass
    private readonly List<(int SurfIdx, BlendMode Blend, int SortKey)> _transparentSurfaces = new(256);

    // Current view position, stored per-frame for tcGen environment
    private float _viewX, _viewY, _viewZ;

    // Frustum planes for culling (6 planes, each: nx, ny, nz, d)
    private readonly float[] _frustum = new float[24]; // 6 * 4

    private const string VertSrc = """
        #version 450 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUV;
        layout(location = 3) in vec2 aLmUV;
        layout(location = 4) in vec4 aColor;

        uniform mat4 uMVP;
        uniform int uEnvMap;
        uniform vec3 uViewPos;
        uniform float uTime;
        // tcMod: up to 4 ops, each encoded as vec4(type, p0, p1, p2)
        uniform int uTcModCount;
        uniform vec4 uTcMod[4];
        // deformVertexes: type(0=wave,1=move), params packed in vec4s
        uniform int uDeformType; // -1=none, 0=wave, 1=move
        uniform vec4 uDeformParams0; // wave: div,func,base,amp  move: x,y,z,func
        uniform vec4 uDeformParams1; // wave: phase,freq,0,0     move: base,amp,phase,freq

        out vec2 vUV;
        out vec2 vLmUV;
        out vec3 vNormal;
        out vec4 vColor;

        float evalWaveFunc(int func, float phase) {
            if (func == 0) return sin(phase * 6.283185);       // sin
            if (func == 1) {                                    // triangle
                float f = fract(phase);
                return f < 0.5 ? f * 4.0 - 1.0 : 3.0 - f * 4.0;
            }
            if (func == 2) return fract(phase) < 0.5 ? 1.0 : -1.0; // square
            if (func == 3) return fract(phase) * 2.0 - 1.0;         // sawtooth
            if (func == 4) return 1.0 - fract(phase) * 2.0;         // inverseSawtooth
            return sin(phase * 6.283185);
        }

        void main() {
            vec3 pos = aPos;

            // Apply deformVertexes
            if (uDeformType == 0) { // wave
                float div = uDeformParams0.x;
                int func = int(uDeformParams0.y);
                float base_ = uDeformParams0.z;
                float amp = uDeformParams0.w;
                float phase = uDeformParams1.x;
                float freq = uDeformParams1.y;
                // Per-vertex phase offset based on position
                float off = (pos.x + pos.y + pos.z) / (div > 0.0 ? div : 100.0);
                float wave = evalWaveFunc(func, phase + off + uTime * freq);
                pos += aNormal * (base_ + wave * amp);
            } else if (uDeformType == 1) { // move
                vec3 dir = uDeformParams0.xyz;
                int func = int(uDeformParams0.w);
                float base_ = uDeformParams1.x;
                float amp = uDeformParams1.y;
                float phase = uDeformParams1.z;
                float freq = uDeformParams1.w;
                float wave = evalWaveFunc(func, phase + uTime * freq);
                pos += dir * (base_ + wave * amp);
            }

            gl_Position = uMVP * vec4(pos, 1.0);
            vLmUV = aLmUV;
            vNormal = aNormal;
            vColor = aColor;

            if (uEnvMap != 0) {
                vec3 viewDir = normalize(pos - uViewPos);
                vec3 n = normalize(aNormal);
                vec3 reflected = reflect(viewDir, n);
                vUV = vec2(0.5 + reflected.x * 0.5, 0.5 - reflected.y * 0.5);
            } else {
                vec2 uv = aUV;

                // Apply tcMod operations in order
                for (int i = 0; i < uTcModCount && i < 4; i++) {
                    int modType = int(uTcMod[i].x);
                    if (modType == 0) { // scroll
                        uv.x += uTcMod[i].y * uTime;
                        uv.y += uTcMod[i].z * uTime;
                    } else if (modType == 1) { // scale
                        uv.x *= uTcMod[i].y;
                        uv.y *= uTcMod[i].z;
                    } else if (modType == 2) { // rotate
                        float angle = radians(uTcMod[i].y * uTime);
                        float s = sin(angle);
                        float c = cos(angle);
                        vec2 centered = uv - vec2(0.5);
                        uv = vec2(centered.x * c - centered.y * s,
                                  centered.x * s + centered.y * c) + vec2(0.5);
                    } else if (modType == 3) { // turb
                        float amp2 = uTcMod[i].z;
                        float phase2 = uTcMod[i].w;
                        float freq2 = uTcMod[i].y;
                        uv.x += amp2 * sin((pos.x + pos.z) * 0.0625 + uTime * freq2 + phase2);
                        uv.y += amp2 * sin((pos.y) * 0.0625 + uTime * freq2 + phase2);
                    }
                }

                vUV = uv;
            }
        }
        """;

    private const string FragSrc = """
        #version 450 core
        in vec2 vUV;
        in vec2 vLmUV;
        in vec3 vNormal;
        in vec4 vColor;

        uniform sampler2D uTexDiffuse;
        uniform sampler2D uTexLightmap;
        uniform vec4 uColor;
        uniform vec3 uLightDir;
        uniform int uUseLightmap;
        uniform float uOverbrightScale;
        uniform int uAlphaFunc; // 0=none, 1=GT0, 2=LT128, 3=GE128
        uniform int uRgbGen;    // 0=identity, 1=vertex, 2=entity, 4=identityLighting

        out vec4 oColor;

        void main() {
            vec4 texColor = texture(uTexDiffuse, vUV);

            // Alpha test — discard fragments based on alpha function
            if (uAlphaFunc == 1 && texColor.a <= 0.0) discard;       // GT0
            else if (uAlphaFunc == 2 && texColor.a >= 0.5) discard;  // LT128
            else if (uAlphaFunc == 3 && texColor.a < 0.5) discard;   // GE128

            if (uUseLightmap != 0) {
                vec4 lmColor = texture(uTexLightmap, vLmUV);
                oColor = texColor * lmColor * uOverbrightScale;
            } else if (uRgbGen == 1) {
                // rgbGen vertex — multiply by vertex color directly
                oColor = texColor * vColor;
            } else {
                // Default or identity — use directional lighting
                float vcSum = vColor.r + vColor.g + vColor.b;
                if (vcSum > 0.01) {
                    oColor = texColor * vColor;
                } else {
                    float ndl = max(dot(normalize(vNormal), uLightDir), 0.0);
                    float light = 0.3 + 0.7 * ndl;
                    oColor = texColor * vec4(vec3(light), 1.0);
                }
            }
            oColor *= uColor;
            oColor.a = texColor.a * uColor.a;
        }
        """;

    public void Init(GL gl)
    {
        _gl = gl;
        _program = CreateProgram();
        _mvpLoc = _gl.GetUniformLocation(_program, "uMVP");
        _colorLoc = _gl.GetUniformLocation(_program, "uColor");
        _lightDirLoc = _gl.GetUniformLocation(_program, "uLightDir");
        _useLightmapLoc = _gl.GetUniformLocation(_program, "uUseLightmap");
        _texDiffuseLoc = _gl.GetUniformLocation(_program, "uTexDiffuse");
        _texLightmapLoc = _gl.GetUniformLocation(_program, "uTexLightmap");
        _alphaFuncLoc = _gl.GetUniformLocation(_program, "uAlphaFunc");
        _envMapLoc = _gl.GetUniformLocation(_program, "uEnvMap");
        _viewPosLoc = _gl.GetUniformLocation(_program, "uViewPos");
        _timeLoc = _gl.GetUniformLocation(_program, "uTime");
        _tcModCountLoc = _gl.GetUniformLocation(_program, "uTcModCount");
        _tcModLoc = _gl.GetUniformLocation(_program, "uTcMod");
        _rgbGenLoc = _gl.GetUniformLocation(_program, "uRgbGen");
        _deformTypeLoc = _gl.GetUniformLocation(_program, "uDeformType");
        _deformParams0Loc = _gl.GetUniformLocation(_program, "uDeformParams0");
        _deformParams1Loc = _gl.GetUniformLocation(_program, "uDeformParams1");
        _overbrightScaleLoc = _gl.GetUniformLocation(_program, "uOverbrightScale");

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();
    }

    /// <summary>
    /// Upload the entire BSP world geometry to the GPU. Called once at map load.
    /// </summary>
    public void LoadWorld(BspWorld world)
    {
        _world = world;
        _frameCount = 0;
        _surfaceDrawnFrame = new int[world.Surfaces.Length];

        UploadGeometry(world);
        UploadLightmaps(world);
    }

    private void UploadGeometry(BspWorld world)
    {
        int numVerts = world.Vertices.Length;
        int numIndices = world.Indices.Length;

        // Build interleaved vertex buffer
        var verts = new float[numVerts * FLOATS_PER_VERT];
        for (int i = 0; i < numVerts; i++)
        {
            ref var v = ref world.Vertices[i];
            int o = i * FLOATS_PER_VERT;
            verts[o] = v.X; verts[o + 1] = v.Y; verts[o + 2] = v.Z;
            verts[o + 3] = v.NX; verts[o + 4] = v.NY; verts[o + 5] = v.NZ;
            verts[o + 6] = v.U; verts[o + 7] = v.V;
            verts[o + 8] = v.LmU; verts[o + 9] = v.LmV;
            verts[o + 10] = v.R / 255f; verts[o + 11] = v.G / 255f;
            verts[o + 12] = v.B / 255f; verts[o + 13] = v.A / 255f;
        }

        _gl.BindVertexArray(_vao);

        // Upload vertex data (static — world doesn't change)
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        // Upload index data
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (int* p = world.Indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(numIndices * sizeof(int)), p, BufferUsageARB.StaticDraw);

        uint stride = FLOATS_PER_VERT * sizeof(float);

        // Position (vec3)
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        // Normal (vec3)
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        // UV (vec2)
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        // Lightmap UV (vec2)
        _gl.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        // Vertex color (vec4)
        _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(10 * sizeof(float)));
        _gl.EnableVertexAttribArray(4);

        _gl.BindVertexArray(0);
    }

    private void UploadLightmaps(BspWorld world)
    {
        _lightmapTextures = new uint[world.Lightmaps.Length];
        for (int i = 0; i < world.Lightmaps.Length; i++)
        {
            uint tex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, tex);

            fixed (byte* p = world.Lightmaps[i].Data)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8,
                    128, 128, 0, PixelFormat.Rgb, PixelType.UnsignedByte, p);
            }

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            _lightmapTextures[i] = tex;
        }
    }

    /// <summary>
    /// Render the world using BSP tree traversal for visibility.
    /// Renders opaque surfaces first, then transparent surfaces in a second pass.
    /// </summary>
    public void Render(float* mvp, float viewX, float viewY, float viewZ,
                       ShaderManager shaders, float timeSec)
    {
        if (_world == null) return;

        _frameCount++;
        _transparentSurfaces.Clear();
        _viewX = viewX;
        _viewY = viewY;
        _viewZ = viewZ;

        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_mvpLoc, 1, false, mvp);
        _gl.Uniform4(_colorLoc, 1f, 1f, 1f, 1f);
        _gl.Uniform3(_lightDirLoc, 0.57735f, 0.57735f, 0.57735f);
        _gl.Uniform1(_texDiffuseLoc, 0);
        _gl.Uniform1(_texLightmapLoc, 1);
        _gl.Uniform1(_envMapLoc, 0);
        _gl.Uniform3(_viewPosLoc, viewX, viewY, viewZ);
        _gl.Uniform1(_timeLoc, timeSec);
        _gl.Uniform1(_tcModCountLoc, 0);
        _gl.Uniform1(_deformTypeLoc, -1);
        _gl.Uniform1(_overbrightScaleLoc, 2.0f);

        ExtractFrustumPlanes(mvp);

        _gl.BindVertexArray(_vao);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Front); // Q3 winding

        // Find camera leaf for PVS
        int leafIdx = _world.FindLeaf(viewX, viewY, viewZ);
        int cameraCluster = -1;
        if (leafIdx >= 0 && leafIdx < _world.Leafs.Length)
            cameraCluster = _world.Leafs[leafIdx].Cluster;

        // Walk BSP tree and draw visible opaque surfaces,
        // collect transparent ones for second pass
        WalkBspTree(0, cameraCluster, shaders);

        // Second pass: draw transparent surfaces with blending enabled
        if (_transparentSurfaces.Count > 0)
        {
            // Sort by sort key for correct layering (decals < seeThrough < banner < blend)
            _transparentSurfaces.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));

            _gl.Enable(EnableCap.Blend);
            _gl.DepthMask(false);

            foreach (var (surfIdx, blend, _) in _transparentSurfaces)
            {
                ref var surf = ref _world.Surfaces[surfIdx];
                _gl.BlendFunc((BlendingFactor)blend.SrcFactor, (BlendingFactor)blend.DstFactor);
                DrawSurfaceGeometry(ref surf, shaders, 0);
            }

            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Render an inline BSP submodel (doors, platforms, etc.) with the given MVP.
    /// The MVP should include the entity's model transform.
    /// </summary>
    public void RenderSubmodel(int submodelIndex, float* mvp, ShaderManager shaders, float timeSec)
    {
        if (_world == null) return;
        if (submodelIndex < 0 || submodelIndex >= _world.Models.Length) return;

        ref var bmodel = ref _world.Models[submodelIndex];

        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_mvpLoc, 1, false, mvp);
        _gl.Uniform4(_colorLoc, 1f, 1f, 1f, 1f);
        _gl.Uniform3(_lightDirLoc, 0.57735f, 0.57735f, 0.57735f);
        _gl.Uniform1(_texDiffuseLoc, 0);
        _gl.Uniform1(_texLightmapLoc, 1);
        _gl.Uniform1(_envMapLoc, 0);
        _gl.Uniform3(_viewPosLoc, _viewX, _viewY, _viewZ);
        _gl.Uniform1(_timeLoc, timeSec);
        _gl.Uniform1(_tcModCountLoc, 0);
        _gl.Uniform1(_deformTypeLoc, -1);
        _gl.Uniform1(_overbrightScaleLoc, 2.0f);

        _gl.BindVertexArray(_vao);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Front);

        // Draw all surfaces in this submodel
        var transparentSubmodelSurfaces = new List<(int SurfIdx, BlendMode Blend)>();

        for (int i = 0; i < bmodel.NumSurfaces; i++)
        {
            int surfIdx = bmodel.FirstSurface + i;
            if (surfIdx < 0 || surfIdx >= _world.Surfaces.Length) continue;

            ref var surf = ref _world.Surfaces[surfIdx];
            if (surf.SurfaceType != SurfaceTypes.MST_PLANAR &&
                surf.SurfaceType != SurfaceTypes.MST_TRIANGLE_SOUP &&
                surf.SurfaceType != SurfaceTypes.MST_PATCH)
                continue;
            if (surf.NumIndices == 0 || surf.NumVertices == 0) continue;

            // Check surface flags
            if (surf.ShaderIndex >= 0 && surf.ShaderIndex < _world.Shaders.Length)
            {
                int sFlags = _world.Shaders[surf.ShaderIndex].SurfaceFlags;
                if ((sFlags & SurfaceFlags.SURF_NODRAW) != 0) continue;
                if ((sFlags & SurfaceFlags.SURF_SKY) != 0) continue;
            }

            // Check blend mode — defer transparent surfaces
            BlendMode blend = shaders.GetBlendMode(surf.ShaderHandle);
            if (blend.NeedsBlending)
            {
                transparentSubmodelSurfaces.Add((surfIdx, blend));
                continue;
            }

            int alphaFunc = shaders.GetAlphaFunc(surf.ShaderHandle);
            DrawSurfaceGeometry(ref surf, shaders, alphaFunc);
        }

        // Draw transparent submodel surfaces
        if (transparentSubmodelSurfaces.Count > 0)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.DepthMask(false);

            foreach (var (surfIdx, blend) in transparentSubmodelSurfaces)
            {
                ref var surf = ref _world.Surfaces[surfIdx];
                _gl.BlendFunc((BlendingFactor)blend.SrcFactor, (BlendingFactor)blend.DstFactor);
                DrawSurfaceGeometry(ref surf, shaders, 0);
            }

            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        _gl.BindVertexArray(0);
    }

    private void WalkBspTree(int nodeIdx, int cameraCluster, ShaderManager shaders)
    {
        if (_world == null) return;

        if (nodeIdx < 0)
        {
            // Leaf node
            int leafIdx = -(nodeIdx + 1);
            if (leafIdx < 0 || leafIdx >= _world.Leafs.Length) return;

            ref var leaf = ref _world.Leafs[leafIdx];

            // PVS check
            if (leaf.Cluster >= 0 && !_world.ClusterVisible(cameraCluster, leaf.Cluster))
                return;

            // Frustum cull leaf bounding box
            if (!BoxInFrustum(leaf.MinX, leaf.MinY, leaf.MinZ,
                              leaf.MaxX, leaf.MaxY, leaf.MaxZ))
                return;

            // Draw all surfaces in this leaf
            for (int i = 0; i < leaf.NumLeafSurfaces; i++)
            {
                int surfIdx = _world.LeafSurfaces[leaf.FirstLeafSurface + i];
                if (surfIdx < 0 || surfIdx >= _world.Surfaces.Length) continue;

                // Skip if already drawn this frame
                if (_surfaceDrawnFrame[surfIdx] == _frameCount) continue;
                _surfaceDrawnFrame[surfIdx] = _frameCount;

                DrawSurface(ref _world.Surfaces[surfIdx], surfIdx, shaders);
            }
            return;
        }

        if (nodeIdx >= _world.Nodes.Length) return;

        ref var node = ref _world.Nodes[nodeIdx];

        // Frustum cull node bounding box
        if (!BoxInFrustum(node.MinX, node.MinY, node.MinZ,
                          node.MaxX, node.MaxY, node.MaxZ))
            return;

        // Traverse both children
        WalkBspTree(node.Child0, cameraCluster, shaders);
        WalkBspTree(node.Child1, cameraCluster, shaders);
    }

    private void DrawSurface(ref BspSurface surf, int surfIdx, ShaderManager shaders)
    {
        // Only draw planar faces, triangle soups, and tessellated patches
        if (surf.SurfaceType != SurfaceTypes.MST_PLANAR &&
            surf.SurfaceType != SurfaceTypes.MST_TRIANGLE_SOUP &&
            surf.SurfaceType != SurfaceTypes.MST_PATCH)
            return;

        if (surf.NumIndices == 0 || surf.NumVertices == 0)
            return;

        // Skip NODRAW and SKY surfaces
        if (_world != null && surf.ShaderIndex >= 0 && surf.ShaderIndex < _world.Shaders.Length)
        {
            int sFlags = _world.Shaders[surf.ShaderIndex].SurfaceFlags;
            if ((sFlags & SurfaceFlags.SURF_NODRAW) != 0)
                return;
            if ((sFlags & SurfaceFlags.SURF_SKY) != 0)
                return;

            // Surfaces with alphaFunc (alpha testing) render in the opaque pass
            // with depth writes, discarding pixels based on alpha test
            int alphaFunc = shaders.GetAlphaFunc(surf.ShaderHandle);
            if (alphaFunc != 0)
            {
                DrawSurfaceGeometry(ref surf, shaders, alphaFunc);
                return;
            }

            // Defer surfaces that need blending to the transparent pass.
            // Check content flags, surfaceparm trans, AND shader blend mode.
            int cFlags = _world.Shaders[surf.ShaderIndex].ContentFlags;
            bool isTrans = (cFlags & CONTENTS_TRANSLUCENT) != 0;

            if (!isTrans)
                isTrans = shaders.IsTransparent(surf.ShaderHandle);

            BlendMode blend = shaders.GetBlendMode(surf.ShaderHandle);
            if (isTrans || blend.NeedsBlending)
            {
                if (blend.NeedsBlending)
                {
                    int sortKey = shaders.GetSortKey(surf.ShaderHandle);
                    if (sortKey == 0) sortKey = isTrans ? 8 : 5; // trans=blend, non-trans=seeThrough
                    _transparentSurfaces.Add((surfIdx, blend, sortKey));
                    return;
                }
            }
        }

        DrawSurfaceGeometry(ref surf, shaders, 0);
    }

    private const int CONTENTS_TRANSLUCENT = 0x20000000;

    private void DrawSurfaceGeometry(ref BspSurface surf, ShaderManager shaders, int alphaFunc)
    {
        // Bind diffuse texture
        uint texId = shaders.GetTextureId(surf.ShaderHandle);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texId);

        // Bind lightmap if available
        bool hasLightmap = surf.LightmapIndex >= 0 && surf.LightmapIndex < _lightmapTextures.Length;
        _gl.Uniform1(_useLightmapLoc, hasLightmap ? 1 : 0);
        if (hasLightmap)
        {
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _lightmapTextures[surf.LightmapIndex]);
        }

        // Set alpha test mode
        _gl.Uniform1(_alphaFuncLoc, alphaFunc);

        // Per-surface rgbGen
        int rgbGen = shaders.GetRgbGen(surf.ShaderHandle);
        _gl.Uniform1(_rgbGenLoc, rgbGen);

        // Per-surface environment mapping
        bool envMap = shaders.GetHasEnvMap(surf.ShaderHandle);
        _gl.Uniform1(_envMapLoc, envMap ? 1 : 0);

        // Per-surface tcMod
        TcMod[]? tcMods = shaders.GetTcMods(surf.ShaderHandle);
        if (tcMods != null && tcMods.Length > 0)
        {
            int count = Math.Min(tcMods.Length, 4);
            _gl.Uniform1(_tcModCountLoc, count);
            for (int i = 0; i < count; i++)
            {
                ref var m = ref tcMods[i];
                float typeF = (float)m.Type;
                switch (m.Type)
                {
                    case TcModType.Scroll:
                        _gl.Uniform4(_tcModLoc + i, 0f, m.Param0, m.Param1, 0f);
                        break;
                    case TcModType.Scale:
                        _gl.Uniform4(_tcModLoc + i, 1f, m.Param0, m.Param1, 0f);
                        break;
                    case TcModType.Rotate:
                        _gl.Uniform4(_tcModLoc + i, 2f, m.Param0, 0f, 0f);
                        break;
                    case TcModType.Turb:
                        _gl.Uniform4(_tcModLoc + i, 3f, m.Param3, m.Param1, m.Param2);
                        break;
                    default:
                        _gl.Uniform4(_tcModLoc + i, -1f, 0f, 0f, 0f);
                        break;
                }
            }
        }
        else
        {
            _gl.Uniform1(_tcModCountLoc, 0);
        }

        // Per-surface cull mode
        int cullMode = shaders.GetCullMode(surf.ShaderHandle);
        if (cullMode == 2) // none/twosided
            _gl.Disable(EnableCap.CullFace);
        else if (cullMode == 1) // back
            _gl.CullFace(TriangleFace.Back);
        // else cullMode == 0 → front (already set as default)

        // Per-surface polygonOffset
        bool polyOffset = shaders.GetPolygonOffset(surf.ShaderHandle);
        if (polyOffset)
        {
            _gl.Enable(EnableCap.PolygonOffsetFill);
            _gl.PolygonOffset(-1f, -1f);
        }

        // Per-surface depth function
        int depthFuncVal = shaders.GetDepthFunc(surf.ShaderHandle);
        if (depthFuncVal == 1)
            _gl.DepthFunc(DepthFunction.Equal);

        // Per-surface deformVertexes
        var deforms = shaders.GetDeforms(surf.ShaderHandle);
        bool hasDeform = false;
        if (deforms != null && deforms.Length > 0)
        {
            // Apply the first wave or move deform (GPU-based)
            for (int d = 0; d < deforms.Length; d++)
            {
                var df = deforms[d];
                if (df.Type == DeformType.Wave)
                {
                    _gl.Uniform1(_deformTypeLoc, 0);
                    _gl.Uniform4(_deformParams0Loc, df.Param0, df.Param1, df.Param2, df.Param3);
                    _gl.Uniform4(_deformParams1Loc, df.Param4, df.Param5, 0f, 0f);
                    hasDeform = true;
                    break;
                }
                else if (df.Type == DeformType.Move)
                {
                    _gl.Uniform1(_deformTypeLoc, 1);
                    _gl.Uniform4(_deformParams0Loc, df.Param0, df.Param1, df.Param2, df.Param4);
                    _gl.Uniform4(_deformParams1Loc, df.Param5, df.Param6, df.Param7, df.Param8);
                    hasDeform = true;
                    break;
                }
            }
        }

        _gl.DrawElementsBaseVertex(PrimitiveType.Triangles,
            (uint)surf.NumIndices, DrawElementsType.UnsignedInt,
            (void*)(surf.FirstIndex * sizeof(int)),
            surf.FirstVertex);

        // Restore default state
        if (cullMode != 0)
        {
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(TriangleFace.Front);
        }
        if (polyOffset)
            _gl.Disable(EnableCap.PolygonOffsetFill);
        if (depthFuncVal != 0)
            _gl.DepthFunc(DepthFunction.Lequal);
        if (hasDeform)
            _gl.Uniform1(_deformTypeLoc, -1);
    }

    /// <summary>
    /// Extract 6 frustum planes from the MVP matrix (column-major).
    /// Each plane: nx, ny, nz, d where nx*x + ny*y + nz*z + d >= 0 means inside.
    /// </summary>
    private void ExtractFrustumPlanes(float* m)
    {
        // Left:   row3 + row0
        SetPlane(0, m[3]+m[0], m[7]+m[4], m[11]+m[8], m[15]+m[12]);
        // Right:  row3 - row0
        SetPlane(1, m[3]-m[0], m[7]-m[4], m[11]-m[8], m[15]-m[12]);
        // Bottom: row3 + row1
        SetPlane(2, m[3]+m[1], m[7]+m[5], m[11]+m[9], m[15]+m[13]);
        // Top:    row3 - row1
        SetPlane(3, m[3]-m[1], m[7]-m[5], m[11]-m[9], m[15]-m[13]);
        // Near:   row3 + row2
        SetPlane(4, m[3]+m[2], m[7]+m[6], m[11]+m[10], m[15]+m[14]);
        // Far:    row3 - row2
        SetPlane(5, m[3]-m[2], m[7]-m[6], m[11]-m[10], m[15]-m[14]);
    }

    private void SetPlane(int idx, float a, float b, float c, float d)
    {
        float len = MathF.Sqrt(a*a + b*b + c*c);
        if (len > 0.0001f)
        {
            float inv = 1.0f / len;
            _frustum[idx*4]   = a * inv;
            _frustum[idx*4+1] = b * inv;
            _frustum[idx*4+2] = c * inv;
            _frustum[idx*4+3] = d * inv;
        }
    }

    /// <summary>
    /// Test if an AABB is at least partially inside the frustum.
    /// Returns true if visible (not fully outside any plane).
    /// </summary>
    private bool BoxInFrustum(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
    {
        for (int i = 0; i < 6; i++)
        {
            float nx = _frustum[i*4], ny = _frustum[i*4+1], nz = _frustum[i*4+2], d = _frustum[i*4+3];

            // Find the corner of the AABB most in the direction of the plane normal (p-vertex)
            float px = nx >= 0 ? maxX : minX;
            float py = ny >= 0 ? maxY : minY;
            float pz = nz >= 0 ? maxZ : minZ;

            if (nx*px + ny*py + nz*pz + d < 0)
                return false; // Entirely outside this plane
        }
        return true;
    }

    private uint CreateProgram()
    {
        uint vs = Compile(ShaderType.VertexShader, VertSrc);
        uint fs = Compile(ShaderType.FragmentShader, FragSrc);

        uint prog = _gl.CreateProgram();
        _gl.AttachShader(prog, vs);
        _gl.AttachShader(prog, fs);
        _gl.LinkProgram(prog);

        _gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetProgramInfoLog(prog);
            EngineImports.Printf(EngineImports.PRINT_ERROR,
                $"[.NET] BSP shader link error: {log}\n");
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
            EngineImports.Printf(EngineImports.PRINT_ERROR,
                $"[.NET] BSP shader compile error ({type}): {log}\n");
        }
        return s;
    }

    public void Dispose()
    {
        foreach (uint tex in _lightmapTextures)
            if (tex != 0) _gl.DeleteTexture(tex);
        _lightmapTextures = [];

        if (_ebo != 0) { _gl.DeleteBuffer(_ebo); _ebo = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_program != 0) { _gl.DeleteProgram(_program); _program = 0; }
        _world = null;
    }
}
