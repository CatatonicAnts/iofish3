using System;
using Silk.NET.OpenGL;
using RendererDotNet.Interop;

namespace RendererDotNet.World;

/// <summary>
/// Renders a skybox cube using 6 face textures loaded from Q3 sky shader definitions.
/// Suffixes: _rt, _bk, _lf, _ft, _up, _dn (right, back, left, front, up, down).
/// </summary>
public sealed unsafe class SkyboxRenderer : IDisposable
{
    private GL _gl = null!;
    private uint _program;
    private int _mvpLoc;
    private uint _vao;
    private uint _vbo;

    // 6 face textures indexed by: 0=rt, 1=bk, 2=lf, 3=ft, 4=up, 5=dn
    private readonly uint[] _faceTextures = new uint[6];
    private bool _loaded;

    private static readonly string[] Suffixes = ["_rt", "_bk", "_lf", "_ft", "_up", "_dn"];

    // Q3 swaps back(1) and left(2) face textures when rendering.
    // Geometry face i uses texture sky_texorder[i].
    private static readonly int[] SkyTexOrder = [0, 2, 1, 3, 4, 5];

    private const string VertSrc = """
        #version 450 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;
        uniform mat4 uMVP;
        out vec2 vUV;
        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
            // Push skybox to far plane
            gl_Position.z = gl_Position.w;
            vUV = aUV;
        }
        """;

    private const string FragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uTex;
        out vec4 oColor;
        void main() {
            oColor = texture(uTex, vUV);
        }
        """;

    public bool IsLoaded => _loaded;

    public void Init(GL gl)
    {
        _gl = gl;

        uint vs = Compile(ShaderType.VertexShader, VertSrc);
        uint fs = Compile(ShaderType.FragmentShader, FragSrc);
        _program = gl.CreateProgram();
        gl.AttachShader(_program, vs);
        gl.AttachShader(_program, fs);
        gl.LinkProgram(_program);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        _mvpLoc = gl.GetUniformLocation(_program, "uMVP");

        // Build skybox cube: 6 faces × 2 triangles × 3 vertices × 5 floats (xyz + uv)
        Span<float> verts = stackalloc float[6 * 6 * 5];
        BuildCubeVertices(verts);

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = verts)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        uint stride = 5 * sizeof(float);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Load skybox face textures from the given base name.
    /// Tries .tga then .jpg extensions for each face.
    /// </summary>
    public void LoadSkyTextures(string baseName, Renderer2D renderer2D)
    {
        _loaded = false;
        int loadedCount = 0;

        for (int i = 0; i < 6; i++)
        {
            string path = baseName + Suffixes[i];
            var tex = ImageLoader.LoadTextureFromEngineFS(path);

            if (tex == null)
            {
                EngineImports.Printf(EngineImports.PRINT_DEVELOPER,
                    $"[.NET] Skybox face not found: {path}\n");
                continue;
            }

            if (tex.IsDds)
            {
                _faceTextures[i] = renderer2D.CreateCompressedTexture(tex.Dds!, clamp: true);
            }
            else
            {
                fixed (byte* data = tex.Image!.Data)
                {
                    _faceTextures[i] = renderer2D.CreateTexture(
                        tex.Image.Width, tex.Image.Height, data, clamp: true);
                }
            }
            loadedCount++;
        }

        if (loadedCount > 0)
        {
            _loaded = true;
            EngineImports.Printf(EngineImports.PRINT_ALL,
                $"[.NET] Loaded skybox: {baseName} ({loadedCount}/6 faces)\n");
        }
    }

    /// <summary>
    /// Render the skybox centered on the given camera position.
    /// Call this BEFORE rendering world geometry, with depth write disabled.
    /// </summary>
    public void Render(float* viewProjection, float camX, float camY, float camZ)
    {
        if (!_loaded) return;

        // Build a VP matrix that removes translation (sky follows camera)
        // We achieve this by multiplying VP × translate(camPos)
        Span<float> translate = stackalloc float[16];
        translate.Clear();
        translate[0] = 1; translate[5] = 1; translate[10] = 1; translate[15] = 1;
        translate[12] = camX; translate[13] = camY; translate[14] = camZ;

        Span<float> mvp = stackalloc float[16];
        for (int col = 0; col < 4; col++)
        for (int row = 0; row < 4; row++)
        {
            mvp[col * 4 + row] =
                viewProjection[0 * 4 + row] * translate[col * 4 + 0] +
                viewProjection[1 * 4 + row] * translate[col * 4 + 1] +
                viewProjection[2 * 4 + row] * translate[col * 4 + 2] +
                viewProjection[3 * 4 + row] * translate[col * 4 + 3];
        }

        _gl.UseProgram(_program);
        fixed (float* p = mvp)
            _gl.UniformMatrix4(_mvpLoc, 1, false, p);

        _gl.BindVertexArray(_vao);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(false);
        // Depth func <= so skybox at z=w (far plane) passes
        _gl.DepthFunc(DepthFunction.Lequal);

        _gl.ActiveTexture(TextureUnit.Texture0);

        // Draw each face (6 vertices per face = 2 triangles)
        // Use sky_texorder to match Q3's back/left texture swap
        for (int i = 0; i < 6; i++)
        {
            int texIdx = SkyTexOrder[i];
            if (_faceTextures[texIdx] == 0) continue;
            _gl.BindTexture(TextureTarget.Texture2D, _faceTextures[texIdx]);
            _gl.DrawArrays(PrimitiveType.Triangles, i * 6, 6);
        }

        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Build a cube centered at origin. Each face has 2 triangles with UV coords.
    /// Face-to-axis mapping derived from Q3's MakeSkyVec st_to_vec table:
    ///   rt(0)=+X, bk(1)=-X, lf(2)=+Y, ft(3)=-Y, up(4)=+Z, dn(5)=-Z
    /// UV mapping: u = (s+1)/2, v = 1-(t+1)/2 matching Q3's outSt calculation.
    /// </summary>
    private static void BuildCubeVertices(Span<float> v)
    {
        const float S = 4096f;
        int o = 0;

        // AddQuad vertex UV mapping: a(0,1) b(0,0) c(1,0) d(1,1)
        // For each face, corners are computed from Q3's st_to_vec:
        //   UV(0,0)=s=-1,t=1  UV(1,0)=s=1,t=1  UV(0,1)=s=-1,t=-1  UV(1,1)=s=1,t=-1

        // rt (+X): y = -s*S, z = t*S
        AddQuad(v, ref o,
            new(S, S, -S), new(S, S, S), new(S, -S, S), new(S, -S, -S));
        // bk (-X): y = s*S, z = t*S
        AddQuad(v, ref o,
            new(-S, -S, -S), new(-S, -S, S), new(-S, S, S), new(-S, S, -S));
        // lf (+Y): x = s*S, z = t*S
        AddQuad(v, ref o,
            new(-S, S, -S), new(-S, S, S), new(S, S, S), new(S, S, -S));
        // ft (-Y): x = -s*S, z = t*S
        AddQuad(v, ref o,
            new(S, -S, -S), new(S, -S, S), new(-S, -S, S), new(-S, -S, -S));
        // up (+Z): x = -t*S, y = -s*S
        AddQuad(v, ref o,
            new(S, S, S), new(-S, S, S), new(-S, -S, S), new(S, -S, S));
        // dn (-Z): x = t*S, y = -s*S
        AddQuad(v, ref o,
            new(-S, S, -S), new(S, S, -S), new(S, -S, -S), new(-S, -S, -S));
    }

    private static void AddQuad(Span<float> v, ref int o,
        (float x, float y, float z) a, (float x, float y, float z) b,
        (float x, float y, float z) c, (float x, float y, float z) d)
    {
        // Triangle 1: a, b, c
        SetVert(v, ref o, a.x, a.y, a.z, 0, 1);
        SetVert(v, ref o, b.x, b.y, b.z, 0, 0);
        SetVert(v, ref o, c.x, c.y, c.z, 1, 0);
        // Triangle 2: a, c, d
        SetVert(v, ref o, a.x, a.y, a.z, 0, 1);
        SetVert(v, ref o, c.x, c.y, c.z, 1, 0);
        SetVert(v, ref o, d.x, d.y, d.z, 1, 1);
    }

    private static void SetVert(Span<float> v, ref int o, float x, float y, float z, float u, float vv)
    {
        v[o++] = x; v[o++] = y; v[o++] = z;
        v[o++] = u; v[o++] = vv;
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
                $"[.NET] Skybox shader error ({type}): {log}\n");
        }
        return s;
    }

    public void Dispose()
    {
        for (int i = 0; i < 6; i++)
            if (_faceTextures[i] != 0) { _gl.DeleteTexture(_faceTextures[i]); _faceTextures[i] = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_program != 0) { _gl.DeleteProgram(_program); _program = 0; }
    }
}
