using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using RendererDotNet.Models;

namespace RendererDotNet;

/// <summary>
/// Renders 3D models (MD3) with frame interpolation.
/// Uses GLSL 450 with per-vertex position/normal/UV and per-entity transforms.
/// </summary>
public sealed unsafe class Renderer3D : IDisposable
{
    private GL _gl = null!;
    private uint _program;
    private int _mvpLoc;
    private int _modelLoc;
    private int _colorLoc;
    private int _lightDirLoc;
    private int _ambientLightLoc;
    private int _directedLightLoc;
    private int _envMapLoc;
    private int _viewPosLoc;
    private int _fullbrightLoc;

    // Depth-only shader for shadow map rendering
    private uint _depthProgram;
    private int _depthMvpLoc;

    // Reusable buffers for interpolated vertex data
    private uint _vao;
    private uint _vbo;
    private uint _ebo;

    // Temporary CPU buffer for interpolated vertices
    // Per-vertex: x, y, z, nx, ny, nz, u, v = 8 floats
    private const int FLOATS_PER_VERT = 8;
    private float[] _vertBuf = new float[4096 * FLOATS_PER_VERT];

    private const string VertSrc = """
        #version 450 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUV;

        uniform mat4 uMVP;
        uniform mat4 uModel;
        uniform int uEnvMap;
        uniform vec3 uViewPos;

        out vec2 vUV;
        out vec3 vNormal;

        void main() {
            vec4 worldPos = uModel * vec4(aPos, 1.0);
            gl_Position = uMVP * vec4(aPos, 1.0);
            vNormal = mat3(uModel) * aNormal;

            if (uEnvMap != 0) {
                // Q3 environment mapping: reflect view vector off normal
                vec3 viewDir = normalize(worldPos.xyz - uViewPos);
                vec3 n = normalize(vNormal);
                vec3 reflected = reflect(viewDir, n);
                // Map reflected vector to 2D UV (spherical mapping)
                vUV = vec2(0.5 + reflected.x * 0.5, 0.5 - reflected.y * 0.5);
            } else {
                vUV = aUV;
            }
        }
        """;

    private const string FragSrc = """
        #version 450 core
        in vec2 vUV;
        in vec3 vNormal;

        uniform sampler2D uTex;
        uniform vec4 uColor;
        uniform vec3 uLightDir;
        uniform vec3 uAmbientLight;
        uniform vec3 uDirectedLight;
        uniform int uFullbright;

        out vec4 oColor;

        void main() {
            vec4 texColor = texture(uTex, vUV);
            if (uFullbright != 0) {
                oColor = texColor * uColor;
            } else {
                float ndl = max(dot(normalize(vNormal), uLightDir), 0.0);
                vec3 light = uAmbientLight + uDirectedLight * ndl;
                oColor = texColor * uColor * vec4(light, 1.0);
            }
        }
        """;

    private const string DepthVertSrc = """
        #version 450 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMVP;
        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
        }
        """;

    private const string DepthFragSrc = """
        #version 450 core
        void main() { }
        """;

    public void Init(GL gl)
    {
        _gl = gl;
        _program = CreateProgram();
        _mvpLoc = _gl.GetUniformLocation(_program, "uMVP");
        _modelLoc = _gl.GetUniformLocation(_program, "uModel");
        _colorLoc = _gl.GetUniformLocation(_program, "uColor");
        _lightDirLoc = _gl.GetUniformLocation(_program, "uLightDir");
        _ambientLightLoc = _gl.GetUniformLocation(_program, "uAmbientLight");
        _directedLightLoc = _gl.GetUniformLocation(_program, "uDirectedLight");
        _envMapLoc = _gl.GetUniformLocation(_program, "uEnvMap");
        _viewPosLoc = _gl.GetUniformLocation(_program, "uViewPos");
        _fullbrightLoc = _gl.GetUniformLocation(_program, "uFullbright");

        // Depth-only shader for shadow maps
        _depthProgram = CreateProgram(DepthVertSrc, DepthFragSrc);
        _depthMvpLoc = _gl.GetUniformLocation(_depthProgram, "uMVP");

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

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

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Render a single MD3 model surface with frame interpolation.
    /// </summary>
    public void DrawSurface(Md3Surface surface, int frame, int oldFrame, float backlerp,
                            float* mvp, float* modelMatrix, uint textureId,
                            float r, float g, float b, float a,
                            bool envMap = false, float viewX = 0, float viewY = 0, float viewZ = 0,
                            BlendMode blend = default, int cullMode = 0,
                            float ambR = 0.5f, float ambG = 0.5f, float ambB = 0.5f,
                            float dirLightR = 0.5f, float dirLightG = 0.5f, float dirLightB = 0.5f,
                            float lightDirX = 0.57735f, float lightDirY = 0.57735f, float lightDirZ = 0.57735f)
    {
        int numVerts = surface.NumVerts;
        int numTris = surface.NumTriangles;
        if (numVerts == 0 || numTris == 0) return;

        // Clamp frames
        int maxFrame = surface.NumFrames - 1;
        if (frame > maxFrame) frame = maxFrame;
        if (oldFrame > maxFrame) oldFrame = maxFrame;
        if (frame < 0) frame = 0;
        if (oldFrame < 0) oldFrame = 0;

        // Ensure buffer is large enough
        int needed = numVerts * FLOATS_PER_VERT;
        if (_vertBuf.Length < needed)
            _vertBuf = new float[needed];

        float frontlerp = 1.0f - backlerp;
        int frameOff = frame * numVerts * 3;
        int oldOff = oldFrame * numVerts * 3;

        // Interpolate vertices and interleave with normals + UVs
        for (int i = 0; i < numVerts; i++)
        {
            int pi = i * 3;
            int vo = i * FLOATS_PER_VERT;

            // Lerp position
            _vertBuf[vo] = surface.Positions[frameOff + pi] * frontlerp
                         + surface.Positions[oldOff + pi] * backlerp;
            _vertBuf[vo + 1] = surface.Positions[frameOff + pi + 1] * frontlerp
                             + surface.Positions[oldOff + pi + 1] * backlerp;
            _vertBuf[vo + 2] = surface.Positions[frameOff + pi + 2] * frontlerp
                             + surface.Positions[oldOff + pi + 2] * backlerp;

            // Lerp normal
            _vertBuf[vo + 3] = surface.Normals[frameOff + pi] * frontlerp
                             + surface.Normals[oldOff + pi] * backlerp;
            _vertBuf[vo + 4] = surface.Normals[frameOff + pi + 1] * frontlerp
                             + surface.Normals[oldOff + pi + 1] * backlerp;
            _vertBuf[vo + 5] = surface.Normals[frameOff + pi + 2] * frontlerp
                             + surface.Normals[oldOff + pi + 2] * backlerp;

            // UV (shared across frames)
            _vertBuf[vo + 6] = surface.TexCoords[i * 2];
            _vertBuf[vo + 7] = surface.TexCoords[i * 2 + 1];
        }

        _gl.UseProgram(_program);

        // Set uniforms
        _gl.UniformMatrix4(_mvpLoc, 1, false, mvp);
        _gl.UniformMatrix4(_modelLoc, 1, false, modelMatrix);
        _gl.Uniform4(_colorLoc, r, g, b, a);
        _gl.Uniform3(_lightDirLoc, lightDirX, lightDirY, lightDirZ);
        _gl.Uniform3(_ambientLightLoc, ambR, ambG, ambB);
        _gl.Uniform3(_directedLightLoc, dirLightR, dirLightG, dirLightB);
        _gl.Uniform1(_envMapLoc, envMap ? 1 : 0);
        _gl.Uniform3(_viewPosLoc, viewX, viewY, viewZ);

        // Use fullbright (no NDL lighting) for non-opaque blend modes (additive, alpha, etc.)
        bool useBlend = blend.NeedsBlending || a < 0.999f;
        _gl.Uniform1(_fullbrightLoc, useBlend ? 1 : 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, textureId);

        _gl.BindVertexArray(_vao);

        // Upload vertex data
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _vertBuf)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(numVerts * FLOATS_PER_VERT * sizeof(float)), p, BufferUsageARB.StreamDraw);

        // Upload index data
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (int* p = surface.Indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(numTris * 3 * sizeof(int)), p, BufferUsageARB.StreamDraw);

        _gl.Enable(EnableCap.DepthTest);
        // Apply per-shader cull mode: 0=front (Q3 CW default), 1=back, 2=none
        if (cullMode == 2)
        {
            _gl.Disable(EnableCap.CullFace);
        }
        else
        {
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(cullMode == 1 ? TriangleFace.Back : TriangleFace.Front);
        }

        // Enable blending from shader blend mode or entity alpha
        if (blend.NeedsBlending)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc((BlendingFactor)blend.SrcFactor, (BlendingFactor)blend.DstFactor);
            _gl.DepthMask(false);
        }
        else if (a < 0.999f)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
        }
        else
        {
            _gl.Disable(EnableCap.Blend);
            _gl.DepthMask(true);
        }

        _gl.DrawElements(PrimitiveType.Triangles,
            (uint)(numTris * 3), DrawElementsType.UnsignedInt, null);

        // Restore state
        if (blend.NeedsBlending || a < 0.999f)
        {
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        _gl.BindVertexArray(0);
    }

    // Reusable index buffer for IQM surfaces
    private int[] _iqmIdxBuf = new int[4096];
    // Reusable bone matrix buffer for CPU skinning
    private float[] _poseMatBuf = new float[128 * 12];

    /// <summary>
    /// Render an IQM model surface with CPU-side bone skinning.
    /// poseMats are 3x4 row-major matrices (12 floats per joint), already multiplied by inverse bind pose.
    /// </summary>
    public void DrawIqmSurface(IqmSurface surface, IqmModel model,
                                float* poseMats, int numJoints,
                                float* mvp, float* modelMatrix, uint textureId,
                                float r, float g, float b, float a,
                                bool envMap = false, float viewX = 0, float viewY = 0, float viewZ = 0,
                                BlendMode blend = default, int cullMode = 0,
                                float ambR = 0.5f, float ambG = 0.5f, float ambB = 0.5f,
                                float dirLightR = 0.5f, float dirLightG = 0.5f, float dirLightB = 0.5f,
                                float lightDirX = 0.57735f, float lightDirY = 0.57735f, float lightDirZ = 0.57735f)
    {
        int numVerts = surface.NumVertexes;
        int numTris = surface.NumTriangles;
        if (numVerts == 0 || numTris == 0) return;

        int needed = numVerts * FLOATS_PER_VERT;
        if (_vertBuf.Length < needed)
            _vertBuf = new float[needed];

        int firstVert = surface.FirstVertex;

        // CPU-side bone skinning: transform each vertex by weighted blend of bone matrices
        for (int i = 0; i < numVerts; i++)
        {
            int vi = firstVert + i;
            int vo = i * FLOATS_PER_VERT;

            float px = model.Positions[vi * 3];
            float py = model.Positions[vi * 3 + 1];
            float pz = model.Positions[vi * 3 + 2];
            float nx = model.Normals[vi * 3];
            float ny = model.Normals[vi * 3 + 1];
            float nz = model.Normals[vi * 3 + 2];

            float ox = 0, oy = 0, oz = 0;
            float onx = 0, ony = 0, onz = 0;

            // Blend up to 4 bones
            for (int bi = 0; bi < 4; bi++)
            {
                int boneIdx = model.BlendIndexes[vi * 4 + bi];
                float weight = model.BlendWeights[vi * 4 + bi] / 255.0f;
                if (weight <= 0) continue;
                if (boneIdx >= numJoints) continue;

                float* m = poseMats + boneIdx * 12;

                // Transform position: m * [px,py,pz,1]
                ox += weight * (m[0] * px + m[1] * py + m[2] * pz + m[3]);
                oy += weight * (m[4] * px + m[5] * py + m[6] * pz + m[7]);
                oz += weight * (m[8] * px + m[9] * py + m[10] * pz + m[11]);

                // Transform normal: m * [nx,ny,nz,0] (rotation only)
                onx += weight * (m[0] * nx + m[1] * ny + m[2] * nz);
                ony += weight * (m[4] * nx + m[5] * ny + m[6] * nz);
                onz += weight * (m[8] * nx + m[9] * ny + m[10] * nz);
            }

            _vertBuf[vo] = ox;
            _vertBuf[vo + 1] = oy;
            _vertBuf[vo + 2] = oz;

            float nlen = MathF.Sqrt(onx * onx + ony * ony + onz * onz);
            if (nlen > 0.0001f) { onx /= nlen; ony /= nlen; onz /= nlen; }

            _vertBuf[vo + 3] = onx;
            _vertBuf[vo + 4] = ony;
            _vertBuf[vo + 5] = onz;

            _vertBuf[vo + 6] = model.TexCoords[vi * 2];
            _vertBuf[vo + 7] = model.TexCoords[vi * 2 + 1];
        }

        // Build local index buffer (offset from firstTriangle, rebased to 0)
        int firstTri = surface.FirstTriangle;
        int numIdx = numTris * 3;
        if (_iqmIdxBuf.Length < numIdx)
            _iqmIdxBuf = new int[numIdx];

        for (int i = 0; i < numIdx; i++)
            _iqmIdxBuf[i] = model.Triangles[firstTri * 3 + i] - firstVert;

        _gl.UseProgram(_program);

        _gl.UniformMatrix4(_mvpLoc, 1, false, mvp);
        _gl.UniformMatrix4(_modelLoc, 1, false, modelMatrix);
        _gl.Uniform4(_colorLoc, r, g, b, a);
        _gl.Uniform3(_lightDirLoc, lightDirX, lightDirY, lightDirZ);
        _gl.Uniform3(_ambientLightLoc, ambR, ambG, ambB);
        _gl.Uniform3(_directedLightLoc, dirLightR, dirLightG, dirLightB);
        _gl.Uniform1(_envMapLoc, envMap ? 1 : 0);
        _gl.Uniform3(_viewPosLoc, viewX, viewY, viewZ);

        bool useBlend = blend.NeedsBlending || a < 0.999f;
        _gl.Uniform1(_fullbrightLoc, useBlend ? 1 : 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, textureId);

        _gl.BindVertexArray(_vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _vertBuf)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(numVerts * FLOATS_PER_VERT * sizeof(float)), p, BufferUsageARB.StreamDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (int* p = _iqmIdxBuf)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(numIdx * sizeof(int)), p, BufferUsageARB.StreamDraw);

        _gl.Enable(EnableCap.DepthTest);
        // Apply per-shader cull mode: 0=front (Q3 CW default), 1=back, 2=none
        if (cullMode == 2)
        {
            _gl.Disable(EnableCap.CullFace);
        }
        else
        {
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(cullMode == 1 ? TriangleFace.Back : TriangleFace.Front);
        }

        if (blend.NeedsBlending)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc((BlendingFactor)blend.SrcFactor, (BlendingFactor)blend.DstFactor);
            _gl.DepthMask(false);
        }
        else if (a < 0.999f)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
        }
        else
        {
            _gl.Disable(EnableCap.Blend);
            _gl.DepthMask(true);
        }

        _gl.DrawElements(PrimitiveType.Triangles,
            (uint)numIdx, DrawElementsType.UnsignedInt, null);

        if (blend.NeedsBlending || a < 0.999f)
        {
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Render an MD3 surface using only the depth shader (for shadow map generation).
    /// </summary>
    public void DrawSurfaceDepthOnly(Md3Surface surface, int frame, int oldFrame, float backlerp, float* mvp)
    {
        int numVerts = surface.NumVerts;
        int numTris = surface.NumTriangles;
        if (numVerts == 0 || numTris == 0) return;

        int maxFrame = surface.NumFrames - 1;
        if (frame > maxFrame) frame = maxFrame;
        if (oldFrame > maxFrame) oldFrame = maxFrame;
        if (frame < 0) frame = 0;
        if (oldFrame < 0) oldFrame = 0;

        int needed = numVerts * FLOATS_PER_VERT;
        if (_vertBuf.Length < needed)
            _vertBuf = new float[needed];

        float frontlerp = 1.0f - backlerp;
        int frameOff = frame * numVerts * 3;
        int oldOff = oldFrame * numVerts * 3;

        for (int i = 0; i < numVerts; i++)
        {
            int pi = i * 3;
            int vo = i * FLOATS_PER_VERT;
            _vertBuf[vo] = surface.Positions[frameOff + pi] * frontlerp + surface.Positions[oldOff + pi] * backlerp;
            _vertBuf[vo + 1] = surface.Positions[frameOff + pi + 1] * frontlerp + surface.Positions[oldOff + pi + 1] * backlerp;
            _vertBuf[vo + 2] = surface.Positions[frameOff + pi + 2] * frontlerp + surface.Positions[oldOff + pi + 2] * backlerp;
            _vertBuf[vo + 3] = 0; _vertBuf[vo + 4] = 0; _vertBuf[vo + 5] = 0;
            _vertBuf[vo + 6] = 0; _vertBuf[vo + 7] = 0;
        }

        _gl.UseProgram(_depthProgram);
        _gl.UniformMatrix4(_depthMvpLoc, 1, false, mvp);
        _gl.BindVertexArray(_vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _vertBuf)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(numVerts * FLOATS_PER_VERT * sizeof(float)), p, BufferUsageARB.StreamDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (int* p = surface.Indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(numTris * 3 * sizeof(int)), p, BufferUsageARB.StreamDraw);

        _gl.Disable(EnableCap.CullFace);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)(numTris * 3), DrawElementsType.UnsignedInt, null);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Render an IQM surface using only the depth shader (for shadow map generation).
    /// </summary>
    public void DrawIqmSurfaceDepthOnly(IqmSurface surface, IqmModel model,
                                         float* poseMats, int numJoints, float* mvp, float* modelMatrix)
    {
        int numVerts = surface.NumVertexes;
        int numTris = surface.NumTriangles;
        if (numVerts == 0 || numTris == 0) return;

        int needed = numVerts * FLOATS_PER_VERT;
        if (_vertBuf.Length < needed)
            _vertBuf = new float[needed];

        int firstVert = surface.FirstVertex;
        for (int i = 0; i < numVerts; i++)
        {
            int vi = firstVert + i;
            int vo = i * FLOATS_PER_VERT;
            float px = model.Positions[vi * 3], py = model.Positions[vi * 3 + 1], pz = model.Positions[vi * 3 + 2];
            float ox = 0, oy = 0, oz = 0;

            for (int bi = 0; bi < 4; bi++)
            {
                int boneIdx = model.BlendIndexes[vi * 4 + bi];
                float weight = model.BlendWeights[vi * 4 + bi] / 255.0f;
                if (weight <= 0 || boneIdx >= numJoints) continue;
                float* m = poseMats + boneIdx * 12;
                ox += weight * (m[0] * px + m[1] * py + m[2] * pz + m[3]);
                oy += weight * (m[4] * px + m[5] * py + m[6] * pz + m[7]);
                oz += weight * (m[8] * px + m[9] * py + m[10] * pz + m[11]);
            }

            _vertBuf[vo] = ox; _vertBuf[vo + 1] = oy; _vertBuf[vo + 2] = oz;
            _vertBuf[vo + 3] = 0; _vertBuf[vo + 4] = 0; _vertBuf[vo + 5] = 0;
            _vertBuf[vo + 6] = 0; _vertBuf[vo + 7] = 0;
        }

        int firstTri = surface.FirstTriangle;
        int numIdx = numTris * 3;
        if (_iqmIdxBuf.Length < numIdx)
            _iqmIdxBuf = new int[numIdx];
        for (int i = 0; i < numIdx; i++)
            _iqmIdxBuf[i] = model.Triangles[firstTri * 3 + i] - firstVert;

        _gl.UseProgram(_depthProgram);
        _gl.UniformMatrix4(_depthMvpLoc, 1, false, mvp);
        _gl.BindVertexArray(_vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _vertBuf)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(numVerts * FLOATS_PER_VERT * sizeof(float)), p, BufferUsageARB.StreamDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (int* p = _iqmIdxBuf)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(numIdx * sizeof(int)), p, BufferUsageARB.StreamDraw);

        _gl.Disable(EnableCap.CullFace);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)numIdx, DrawElementsType.UnsignedInt, null);
        _gl.BindVertexArray(0);
    }

    private uint CreateProgram()
    {
        return CreateProgram(VertSrc, FragSrc);
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
                $"[.NET] 3D shader link error: {log}\n");
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
                $"[.NET] 3D shader compile error ({type}): {log}\n");
        }
        return s;
    }

    public void Dispose()
    {
        if (_ebo != 0) { _gl.DeleteBuffer(_ebo); _ebo = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_program != 0) { _gl.DeleteProgram(_program); _program = 0; }
        if (_depthProgram != 0) { _gl.DeleteProgram(_depthProgram); _depthProgram = 0; }
    }
}
