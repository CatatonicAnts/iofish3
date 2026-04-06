using Silk.NET.OpenGL;

namespace RendererDotNet;

/// <summary>
/// Batched 2D quad renderer using OpenGL 4.5.
/// Draws textured, colored quads with an orthographic projection.
/// Quads sharing the same texture are batched and drawn in one call.
/// </summary>
public sealed unsafe class Renderer2D : System.IDisposable
{
    private GL _gl = null!;

    private uint _vao;
    private uint _vbo;
    private uint _ebo;
    private uint _program;
    private int _projLoc;
    private uint _whiteTexture;

    // Per-vertex: x, y, u, v, r, g, b, a
    private const int FLOATS_PER_VERTEX = 8;
    private const int MAX_QUADS = 8192;
    private const int VERTS_PER_QUAD = 4;

    private readonly float[] _verts = new float[MAX_QUADS * VERTS_PER_QUAD * FLOATS_PER_VERTEX];
    private int _quadCount;
    private uint _batchTexture;

    private float _r = 1f, _g = 1f, _b = 1f, _a = 1f;
    private int _screenW, _screenH;
    private BlendMode _batchBlend = BlendMode.Alpha;

    public uint WhiteTexture => _whiteTexture;

    private const string VertSrc = """
        #version 450 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec4 aColor;
        uniform mat4 uProj;
        out vec2 vUV;
        out vec4 vColor;
        void main() {
            gl_Position = uProj * vec4(aPos, 0.0, 1.0);
            vUV = aUV;
            vColor = aColor;
        }
        """;

    private const string FragSrc = """
        #version 450 core
        in vec2 vUV;
        in vec4 vColor;
        uniform sampler2D uTex;
        out vec4 oColor;
        void main() {
            oColor = texture(uTex, vUV) * vColor;
        }
        """;

    public void Init(GL gl, int width, int height)
    {
        _gl = gl;
        _screenW = width;
        _screenH = height;

        _program = CreateProgram();
        _projLoc = _gl.GetUniformLocation(_program, "uProj");

        // VAO
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        // VBO — dynamic vertex data
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer,
            (nuint)(_verts.Length * sizeof(float)), null, BufferUsageARB.DynamicDraw);

        // EBO — static index pattern: 0-1-2, 0-2-3 per quad
        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        var idx = new uint[MAX_QUADS * 6];
        for (int i = 0; i < MAX_QUADS; i++)
        {
            uint bv = (uint)(i * 4);
            int bi = i * 6;
            idx[bi] = bv; idx[bi + 1] = bv + 1; idx[bi + 2] = bv + 2;
            idx[bi + 3] = bv; idx[bi + 4] = bv + 2; idx[bi + 5] = bv + 3;
        }
        fixed (uint* p = idx)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(idx.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        uint stride = FLOATS_PER_VERTEX * sizeof(float);
        // Position (vec2)
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        // UV (vec2)
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        // Color (vec4)
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);

        _gl.BindVertexArray(0);

        // 1x1 white texture (fallback for untextured quads)
        _whiteTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _whiteTexture);
        byte[] white = [255, 255, 255, 255];
        fixed (byte* p = white)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

        _batchTexture = _whiteTexture;
    }

    public void SetColor(float r, float g, float b, float a)
    {
        _r = r; _g = g; _b = b; _a = a;
    }

    public void ResetColor()
    {
        _r = 1f; _g = 1f; _b = 1f; _a = 1f;
    }

    public void DrawQuad(float x, float y, float w, float h,
        float s1, float t1, float s2, float t2, uint texture, BlendMode blend = BlendMode.Alpha)
    {
        if (texture == 0) texture = _whiteTexture;
        if ((texture != _batchTexture || blend != _batchBlend) && _quadCount > 0)
            Flush();
        _batchTexture = texture;
        _batchBlend = blend;

        if (_quadCount >= MAX_QUADS)
            Flush();

        int o = _quadCount * VERTS_PER_QUAD * FLOATS_PER_VERTEX;
        float x2 = x + w, y2 = y + h;

        // Top-left
        _verts[o] = x;  _verts[o + 1] = y;
        _verts[o + 2] = s1; _verts[o + 3] = t1;
        _verts[o + 4] = _r; _verts[o + 5] = _g; _verts[o + 6] = _b; _verts[o + 7] = _a;

        // Top-right
        o += 8;
        _verts[o] = x2; _verts[o + 1] = y;
        _verts[o + 2] = s2; _verts[o + 3] = t1;
        _verts[o + 4] = _r; _verts[o + 5] = _g; _verts[o + 6] = _b; _verts[o + 7] = _a;

        // Bottom-right
        o += 8;
        _verts[o] = x2; _verts[o + 1] = y2;
        _verts[o + 2] = s2; _verts[o + 3] = t2;
        _verts[o + 4] = _r; _verts[o + 5] = _g; _verts[o + 6] = _b; _verts[o + 7] = _a;

        // Bottom-left
        o += 8;
        _verts[o] = x;  _verts[o + 1] = y2;
        _verts[o + 2] = s1; _verts[o + 3] = t2;
        _verts[o + 4] = _r; _verts[o + 5] = _g; _verts[o + 6] = _b; _verts[o + 7] = _a;

        _quadCount++;
    }

    public void Flush()
    {
        if (_quadCount == 0) return;

        _gl.UseProgram(_program);
        UploadProjection();

        _gl.BindVertexArray(_vao);

        // Upload only the portion of vertex data we need
        int bytes = _quadCount * VERTS_PER_QUAD * FLOATS_PER_VERTEX * sizeof(float);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _verts)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)bytes, p);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _batchTexture);

        _gl.Enable(EnableCap.Blend);
        switch (_batchBlend)
        {
            case BlendMode.Add:
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
                break;
            case BlendMode.Filter:
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                break;
            case BlendMode.Opaque:
                _gl.Disable(EnableCap.Blend);
                break;
            case BlendMode.Alpha:
            default:
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                break;
        }
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);

        _gl.DrawElements(PrimitiveType.Triangles,
            (uint)(_quadCount * 6), DrawElementsType.UnsignedInt, null);

        _quadCount = 0;
    }

    private void UploadProjection()
    {
        // Ortho: (0,0) top-left → (screenW, screenH) bottom-right
        Span<float> m = stackalloc float[16];
        m.Clear();
        m[0] = 2f / _screenW;
        m[5] = -2f / _screenH;   // flip Y: top = 0
        m[10] = -1f;
        m[12] = -1f;
        m[13] = 1f;
        m[15] = 1f;

        fixed (float* p = m)
            _gl.UniformMatrix4(_projLoc, 1, false, p);
    }

    /// <summary>
    /// Upload RGBA pixel data as a GL texture. Returns the texture ID.
    /// </summary>
    public uint CreateTexture(int width, int height, byte* data, bool clamp = false)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, data);

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        var wrap = clamp ? (int)GLEnum.ClampToEdge : (int)GLEnum.Repeat;
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrap);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrap);

        return tex;
    }

    public void DeleteTexture(uint tex)
    {
        if (tex != 0 && tex != _whiteTexture)
            _gl.DeleteTexture(tex);
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
                $"[.NET] Shader link error: {log}\n");
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
                $"[.NET] Shader compile error ({type}): {log}\n");
        }
        return s;
    }

    public void Dispose()
    {
        if (_whiteTexture != 0) { _gl.DeleteTexture(_whiteTexture); _whiteTexture = 0; }
        if (_ebo != 0) { _gl.DeleteBuffer(_ebo); _ebo = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_program != 0) { _gl.DeleteProgram(_program); _program = 0; }
    }
}
