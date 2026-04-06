using System;
using Silk.NET.OpenGL;

namespace RendererDotNet;

/// <summary>
/// Post-processing pipeline: renders 3D scene to an FBO, applies bloom
/// (bright extract → downsample → Gaussian blur → additive composite),
/// then blits result to the default framebuffer for 2D overlay.
/// </summary>
public sealed unsafe class PostProcess : IDisposable
{
    private GL _gl = null!;
    private int _width, _height;
    private bool _enabled;

    // Main scene FBO (full resolution)
    private uint _sceneFbo;
    private uint _sceneColorTex;
    private uint _sceneDepthRbo;

    // Bloom pipeline: half-res bright extract, quarter-res ping-pong blur
    private uint _brightFbo, _brightTex;
    private uint _blurFboA, _blurTexA;
    private uint _blurFboB, _blurTexB;
    private int _halfW, _halfH;
    private int _quarterW, _quarterH;

    // Shader programs
    private uint _brightProgram;   // bright-pass extraction
    private uint _blurHProgram;    // horizontal Gaussian blur
    private uint _blurVProgram;    // vertical Gaussian blur
    private uint _compositeProgram; // combine scene + bloom
    private uint _blitProgram;     // simple texture blit (for downsample)

    // Fullscreen quad VAO
    private uint _quadVao, _quadVbo;

    // Uniform locations
    private int _brightThresholdLoc;
    private int _blurHTexelSizeLoc;
    private int _blurVTexelSizeLoc;
    private int _compSceneLoc, _compBloomLoc, _compStrengthLoc;

    // Bloom settings
    private const float BloomThreshold = 0.7f;
    private const float BloomStrength = 0.35f;

    #region GLSL Shaders

    private const string QuadVertSrc = """
        #version 450 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        out vec2 vUV;
        void main() {
            vUV = aUV;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    private const string BrightFragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uScene;
        uniform float uThreshold;
        out vec4 oColor;
        void main() {
            vec3 color = texture(uScene, vUV).rgb;
            float lum = dot(color, vec3(0.2126, 0.7152, 0.0722));
            // Soft knee: smoothstep around threshold
            float contrib = smoothstep(uThreshold, uThreshold + 0.3, lum);
            oColor = vec4(color * contrib, 1.0);
        }
        """;

    private const string BlurHFragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uTex;
        uniform float uTexelSize; // 1.0 / texture width
        out vec4 oColor;
        void main() {
            // 9-tap Gaussian blur (sigma ~2.5)
            float weights[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);
            vec3 result = texture(uTex, vUV).rgb * weights[0];
            for (int i = 1; i < 5; i++) {
                float off = float(i) * uTexelSize;
                result += texture(uTex, vUV + vec2(off, 0.0)).rgb * weights[i];
                result += texture(uTex, vUV - vec2(off, 0.0)).rgb * weights[i];
            }
            oColor = vec4(result, 1.0);
        }
        """;

    private const string BlurVFragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uTex;
        uniform float uTexelSize; // 1.0 / texture height
        out vec4 oColor;
        void main() {
            float weights[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);
            vec3 result = texture(uTex, vUV).rgb * weights[0];
            for (int i = 1; i < 5; i++) {
                float off = float(i) * uTexelSize;
                result += texture(uTex, vUV + vec2(0.0, off)).rgb * weights[i];
                result += texture(uTex, vUV - vec2(0.0, off)).rgb * weights[i];
            }
            oColor = vec4(result, 1.0);
        }
        """;

    private const string CompositeFragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uScene;
        uniform sampler2D uBloom;
        uniform float uBloomStrength;
        out vec4 oColor;
        void main() {
            vec3 scene = texture(uScene, vUV).rgb;
            vec3 bloom = texture(uBloom, vUV).rgb;
            oColor = vec4(scene + bloom * uBloomStrength, 1.0);
        }
        """;

    private const string BlitFragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uTex;
        out vec4 oColor;
        void main() {
            oColor = texture(uTex, vUV);
        }
        """;

    #endregion

    public bool IsEnabled => _enabled;

    public void Init(GL gl, int width, int height)
    {
        _gl = gl;
        _width = width;
        _height = height;
        _halfW = width / 2;
        _halfH = height / 2;
        _quarterW = width / 4;
        _quarterH = height / 4;

        // Check r_bloom cvar (default on)
        Interop.EngineImports.Cvar_Get("r_bloom", "1", 0x01); // CVAR_ARCHIVE
        int bloomVal = Interop.EngineImports.Cvar_VariableIntegerValue("r_bloom");
        if (bloomVal == 0)
        {
            _enabled = false;
            Interop.EngineImports.Printf(Interop.EngineImports.PRINT_ALL,
                "[.NET] Post-processing disabled (r_bloom 0)\n");
            return;
        }

        // Create fullscreen quad
        CreateQuadVao();

        // Create FBOs
        CreateSceneFbo();
        _brightFbo = CreateColorFbo(_halfW, _halfH, out _brightTex);
        _blurFboA = CreateColorFbo(_quarterW, _quarterH, out _blurTexA);
        _blurFboB = CreateColorFbo(_quarterW, _quarterH, out _blurTexB);

        // Create shader programs
        _brightProgram = CreateProgram(QuadVertSrc, BrightFragSrc);
        _brightThresholdLoc = _gl.GetUniformLocation(_brightProgram, "uThreshold");
        int brightSceneLoc = _gl.GetUniformLocation(_brightProgram, "uScene");
        _gl.UseProgram(_brightProgram);
        _gl.Uniform1(brightSceneLoc, 0);

        _blurHProgram = CreateProgram(QuadVertSrc, BlurHFragSrc);
        _blurHTexelSizeLoc = _gl.GetUniformLocation(_blurHProgram, "uTexelSize");
        int blurHTex = _gl.GetUniformLocation(_blurHProgram, "uTex");
        _gl.UseProgram(_blurHProgram);
        _gl.Uniform1(blurHTex, 0);

        _blurVProgram = CreateProgram(QuadVertSrc, BlurVFragSrc);
        _blurVTexelSizeLoc = _gl.GetUniformLocation(_blurVProgram, "uTexelSize");
        int blurVTex = _gl.GetUniformLocation(_blurVProgram, "uTex");
        _gl.UseProgram(_blurVProgram);
        _gl.Uniform1(blurVTex, 0);

        _compositeProgram = CreateProgram(QuadVertSrc, CompositeFragSrc);
        _compSceneLoc = _gl.GetUniformLocation(_compositeProgram, "uScene");
        _compBloomLoc = _gl.GetUniformLocation(_compositeProgram, "uBloom");
        _compStrengthLoc = _gl.GetUniformLocation(_compositeProgram, "uBloomStrength");
        _gl.UseProgram(_compositeProgram);
        _gl.Uniform1(_compSceneLoc, 0);
        _gl.Uniform1(_compBloomLoc, 1);

        _blitProgram = CreateProgram(QuadVertSrc, BlitFragSrc);
        _gl.UseProgram(_blitProgram);
        _gl.Uniform1(_gl.GetUniformLocation(_blitProgram, "uTex"), 0);

        _gl.UseProgram(0);
        _enabled = true;

        Interop.EngineImports.Printf(Interop.EngineImports.PRINT_ALL,
            "[.NET] Post-processing initialized (bloom enabled)\n");
    }

    /// <summary>Bind the scene FBO so all 3D rendering goes to our texture.</summary>
    public void BindSceneFbo()
    {
        if (!_enabled) return;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
    }

    /// <summary>Unbind the scene FBO and apply bloom post-processing.</summary>
    public void ApplyAndBlit()
    {
        if (!_enabled) return;

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(_quadVao);

        // Step 1: Bright-pass extract to half-res FBO
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _brightFbo);
        _gl.Viewport(0, 0, (uint)_halfW, (uint)_halfH);
        _gl.UseProgram(_brightProgram);
        _gl.Uniform1(_brightThresholdLoc, BloomThreshold);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // Step 2: Downsample bright to quarter-res via blit
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFboA);
        _gl.Viewport(0, 0, (uint)_quarterW, (uint)_quarterH);
        _gl.UseProgram(_blitProgram);
        _gl.BindTexture(TextureTarget.Texture2D, _brightTex);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // Step 3: Horizontal Gaussian blur (A → B)
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFboB);
        _gl.UseProgram(_blurHProgram);
        _gl.Uniform1(_blurHTexelSizeLoc, 1.0f / _quarterW);
        _gl.BindTexture(TextureTarget.Texture2D, _blurTexA);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // Step 4: Vertical Gaussian blur (B → A)
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFboA);
        _gl.UseProgram(_blurVProgram);
        _gl.Uniform1(_blurVTexelSizeLoc, 1.0f / _quarterH);
        _gl.BindTexture(TextureTarget.Texture2D, _blurTexB);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // Optional: second blur pass for smoother bloom
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFboB);
        _gl.UseProgram(_blurHProgram);
        _gl.BindTexture(TextureTarget.Texture2D, _blurTexA);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFboA);
        _gl.UseProgram(_blurVProgram);
        _gl.BindTexture(TextureTarget.Texture2D, _blurTexB);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // Step 5: Composite — scene + bloom → default framebuffer
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.UseProgram(_compositeProgram);
        _gl.Uniform1(_compStrengthLoc, BloomStrength);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _blurTexA);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // Restore state for 2D rendering
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
    }

    public void Dispose()
    {
        if (_compositeProgram != 0) _gl.DeleteProgram(_compositeProgram);
        if (_blitProgram != 0) _gl.DeleteProgram(_blitProgram);
        if (_blurVProgram != 0) _gl.DeleteProgram(_blurVProgram);
        if (_blurHProgram != 0) _gl.DeleteProgram(_blurHProgram);
        if (_brightProgram != 0) _gl.DeleteProgram(_brightProgram);

        DeleteFbo(ref _blurFboB, ref _blurTexB, 0);
        DeleteFbo(ref _blurFboA, ref _blurTexA, 0);
        DeleteFbo(ref _brightFbo, ref _brightTex, 0);
        DeleteFbo(ref _sceneFbo, ref _sceneColorTex, _sceneDepthRbo);
        _sceneDepthRbo = 0;

        if (_quadVbo != 0) { _gl.DeleteBuffer(_quadVbo); _quadVbo = 0; }
        if (_quadVao != 0) { _gl.DeleteVertexArray(_quadVao); _quadVao = 0; }

        _enabled = false;
    }

    #region Setup helpers

    private void CreateSceneFbo()
    {
        _sceneFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);

        // Color attachment: RGBA8 (could upgrade to RGBA16F for HDR later)
        _sceneColorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)_width, (uint)_height, 0, Silk.NET.OpenGL.PixelFormat.Rgba, Silk.NET.OpenGL.PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _sceneColorTex, 0);

        // Depth attachment: renderbuffer
        _sceneDepthRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _sceneDepthRbo);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
            (uint)_width, (uint)_height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _sceneDepthRbo);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            Interop.EngineImports.Printf(Interop.EngineImports.PRINT_WARNING,
                $"[.NET] WARNING: Scene FBO incomplete: {status}\n");
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private uint CreateColorFbo(int w, int h, out uint colorTex)
    {
        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

        colorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, colorTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)w, (uint)h, 0, Silk.NET.OpenGL.PixelFormat.Rgba, Silk.NET.OpenGL.PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, colorTex, 0);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return fbo;
    }

    private void DeleteFbo(ref uint fbo, ref uint tex, uint rbo)
    {
        if (rbo != 0) _gl.DeleteRenderbuffer(rbo);
        if (tex != 0) { _gl.DeleteTexture(tex); tex = 0; }
        if (fbo != 0) { _gl.DeleteFramebuffer(fbo); fbo = 0; }
    }

    private void CreateQuadVao()
    {
        // Fullscreen triangle strip: position (xy) + uv
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
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)),
                p, BufferUsageARB.StaticDraw);

        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float),
            (void*)(2 * sizeof(float)));
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
                $"[.NET] PostProcess program link error: {log}\n");
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
                $"[.NET] PostProcess shader compile error: {log}\n");
        }
        return s;
    }

    #endregion
}
