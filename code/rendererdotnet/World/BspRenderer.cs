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
    private readonly List<(int SurfIdx, BlendMode Blend)> _transparentSurfaces = new(256);

    private const string VertSrc = """
        #version 450 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUV;
        layout(location = 3) in vec2 aLmUV;
        layout(location = 4) in vec4 aColor;

        uniform mat4 uMVP;

        out vec2 vUV;
        out vec2 vLmUV;
        out vec3 vNormal;
        out vec4 vColor;

        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
            vUV = aUV;
            vLmUV = aLmUV;
            vNormal = aNormal;
            vColor = aColor;
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

        out vec4 oColor;

        void main() {
            vec4 texColor = texture(uTexDiffuse, vUV);
            if (uUseLightmap != 0) {
                vec4 lmColor = texture(uTexLightmap, vLmUV);
                // Lightmaps are pre-multiplied, scale up for Q3-style overbright
                oColor = texColor * lmColor * 2.0 * uColor;
            } else {
                // Fallback: simple directional lighting
                float ndl = max(dot(normalize(vNormal), uLightDir), 0.0);
                float light = 0.3 + 0.7 * ndl;
                oColor = texColor * uColor * vec4(vec3(light), 1.0);
            }
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
                       ShaderManager shaders)
    {
        if (_world == null) return;

        _frameCount++;
        _transparentSurfaces.Clear();

        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_mvpLoc, 1, false, mvp);
        _gl.Uniform4(_colorLoc, 1f, 1f, 1f, 1f);
        _gl.Uniform3(_lightDirLoc, 0.57735f, 0.57735f, 0.57735f);
        _gl.Uniform1(_texDiffuseLoc, 0);
        _gl.Uniform1(_texLightmapLoc, 1);

        _gl.BindVertexArray(_vao);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
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
            _gl.Enable(EnableCap.Blend);
            _gl.DepthMask(false);

            foreach (var (surfIdx, blend) in _transparentSurfaces)
            {
                ref var surf = ref _world.Surfaces[surfIdx];
                _gl.BlendFunc((BlendingFactor)blend.SrcFactor, (BlendingFactor)blend.DstFactor);
                DrawSurfaceGeometry(ref surf, shaders);
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

        // Traverse both children (front first would be ideal but both works)
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

            // Only defer surfaces that are ACTUALLY transparent
            // (CONTENTS_TRANSLUCENT flag or surfaceparm trans in shader script)
            int cFlags = _world.Shaders[surf.ShaderIndex].ContentFlags;
            bool isTrans = (cFlags & CONTENTS_TRANSLUCENT) != 0;

            // Also check shader script for surfaceparm trans
            if (!isTrans)
                isTrans = shaders.IsTransparent(surf.ShaderHandle);

            if (isTrans)
            {
                BlendMode blend = shaders.GetBlendMode(surf.ShaderHandle);
                if (blend.NeedsBlending)
                {
                    _transparentSurfaces.Add((surfIdx, blend));
                    return;
                }
            }
        }

        DrawSurfaceGeometry(ref surf, shaders);
    }

    private const int CONTENTS_TRANSLUCENT = 0x20000000;

    private void DrawSurfaceGeometry(ref BspSurface surf, ShaderManager shaders)
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

        _gl.DrawElementsBaseVertex(PrimitiveType.Triangles,
            (uint)surf.NumIndices, DrawElementsType.UnsignedInt,
            (void*)(surf.FirstIndex * sizeof(int)),
            surf.FirstVertex);
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
