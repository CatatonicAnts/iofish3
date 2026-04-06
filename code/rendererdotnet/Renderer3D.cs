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

        out vec2 vUV;
        out vec3 vNormal;

        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
            vUV = aUV;
            vNormal = mat3(uModel) * aNormal;
        }
        """;

    private const string FragSrc = """
        #version 450 core
        in vec2 vUV;
        in vec3 vNormal;

        uniform sampler2D uTex;
        uniform vec4 uColor;
        uniform vec3 uLightDir;

        out vec4 oColor;

        void main() {
            vec4 texColor = texture(uTex, vUV);
            // Simple directional lighting
            float ndl = max(dot(normalize(vNormal), uLightDir), 0.0);
            float light = 0.3 + 0.7 * ndl;
            oColor = texColor * uColor * vec4(vec3(light), 1.0);
        }
        """;

    public void Init(GL gl)
    {
        _gl = gl;
        _program = CreateProgram();
        _mvpLoc = _gl.GetUniformLocation(_program, "uMVP");
        _modelLoc = _gl.GetUniformLocation(_program, "uModel");
        _colorLoc = _gl.GetUniformLocation(_program, "uColor");
        _lightDirLoc = _gl.GetUniformLocation(_program, "uLightDir");

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
                            float r, float g, float b, float a)
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
        // Default light direction (down from upper-right-front)
        _gl.Uniform3(_lightDirLoc, 0.57735f, 0.57735f, 0.57735f);

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
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Front); // Q3 uses clockwise winding
        _gl.Disable(EnableCap.Blend);

        _gl.DrawElements(PrimitiveType.Triangles,
            (uint)(numTris * 3), DrawElementsType.UnsignedInt, null);

        _gl.BindVertexArray(0);
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
    }
}
