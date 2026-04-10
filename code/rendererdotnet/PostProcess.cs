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
    private bool _hdrEnabled;
    private bool _bloomEnabled;
    private bool _ssaoEnabled;

    // Main scene FBO (full resolution)
    private uint _sceneFbo;
    private uint _sceneColorTex;
    private uint _sceneDepthTex; // depth texture (for SSAO sampling)

    // Bloom pipeline: half-res bright extract, quarter-res ping-pong blur
    private uint _brightFbo, _brightTex;
    private uint _blurFboA, _blurTexA;
    private uint _blurFboB, _blurTexB;
    private int _halfW, _halfH;
    private int _quarterW, _quarterH;

    // HDR: luminance pyramid for auto-exposure
    private uint[] _lumFbos = Array.Empty<uint>();
    private uint[] _lumTexs = Array.Empty<uint>();
    private int _lumLevels;
    private uint _exposureFbo, _exposureTex;     // current smoothed exposure (1x1)
    private uint _prevExposureFbo, _prevExposureTex; // previous frame's exposure
    private int _frameCount;
    private bool _autoExposure;

    // Shader programs
    private uint _brightProgram;   // bright-pass extraction
    private uint _blurHProgram;    // horizontal Gaussian blur
    private uint _blurVProgram;    // vertical Gaussian blur
    private uint _compositeProgram; // combine scene + bloom + tonemap
    private uint _blitProgram;     // simple texture blit (for downsample)
    private uint _lumInitProgram;  // initial log-luminance from HDR scene
    private uint _lumBlendProgram; // temporal exposure blending
    private uint _ssaoProgram;     // SSAO computation
    private uint _ssaoBlurProgram; // bilateral blur for SSAO

    // Fullscreen quad VAO
    private uint _quadVao, _quadVbo;

    // Uniform locations
    private int _brightThresholdLoc;
    private int _blurHTexelSizeLoc;
    private int _blurVTexelSizeLoc;
    private int _compSceneLoc, _compBloomLoc, _compStrengthLoc;
    private int _compHdrLoc, _compExposureTexLoc, _compUseSsaoLoc;
    private int _lumBlendFactorLoc;

    // SSAO
    private uint _ssaoFbo, _ssaoTex;           // half-res SSAO result
    private uint _ssaoBlurFbo, _ssaoBlurTex;   // blurred SSAO
    private int _ssaoViewInfoLoc;              // zFar/zNear, zFar, 1/width, 1/height
    private int _ssaoBlurTexelLoc;
    private int _ssaoBlurZFarDivZNearLoc;

    // Bloom settings
    private const float BloomThreshold = 0.85f;
    private const float BloomStrength = 0.15f;
    private const float ExposureBlendFactor = 0.03f;

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
        uniform sampler2D uExposure;
        uniform sampler2D uSsao;
        uniform float uBloomStrength;
        uniform int uHdr;
        uniform int uUseSsao;

        // Filmic tonemapping (Uncharted 2 / Hable)
        vec3 FilmicTonemap(vec3 x) {
            const float SS  = 0.22; // Shoulder Strength
            const float LS  = 0.30; // Linear Strength
            const float LA  = 0.10; // Linear Angle
            const float TS  = 0.20; // Toe Strength
            const float TAN = 0.01; // Toe Angle Numerator
            const float TAD = 0.30; // Toe Angle Denominator
            return ((x*(SS*x+LA*LS)+TS*TAN)/(x*(SS*x+LS)+TS*TAD)) - TAN/TAD;
        }

        out vec4 oColor;
        void main() {
            vec3 scene = texture(uScene, vUV).rgb;
            vec3 bloom = texture(uBloom, vUV).rgb;
            vec3 color = scene + bloom * uBloomStrength;

            // Apply SSAO (multiplicative darkening)
            if (uUseSsao != 0) {
                float ao = texture(uSsao, vUV).r;
                color *= ao;
            }

            if (uHdr == 1) {
                // Read auto-exposure from 1x1 texture
                float avgLogLum = texture(uExposure, vec2(0.5)).r;
                // Decode from [0,1] back to log space [-10,+10]
                float logLum = (avgLogLum - 0.5) * 20.0;
                float exposure = pow(2.0, -logLum);
                color *= exposure;

                // Apply filmic tonemapping
                float W = 11.2; // Linear White
                vec3 mapped = FilmicTonemap(color) / FilmicTonemap(vec3(W));

                // Gamma correction (linear → sRGB)
                color = pow(mapped, vec3(1.0 / 2.2));
            }

            oColor = vec4(color, 1.0);
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

    // Compute log-luminance from HDR scene (initial pass)
    private const string LumInitFragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uScene;
        out vec4 oColor;
        void main() {
            vec3 color = texture(uScene, vUV).rgb;
            float lum = dot(color, vec3(0.2126, 0.7152, 0.0722));
            // Encode log luminance to [0,1] range: log2 in [-10,10] → [0,1]
            float logLum = clamp(log2(max(lum, 0.00001)), -10.0, 10.0);
            float encoded = logLum * 0.05 + 0.5;
            oColor = vec4(encoded, encoded, encoded, 1.0);
        }
        """;

    // Temporal blend between previous and current exposure
    private const string LumBlendFragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uTex;
        uniform float uBlendFactor;
        out vec4 oColor;
        void main() {
            // Just output current luminance with alpha for blending
            float lum = texture(uTex, vec2(0.5)).r;
            oColor = vec4(lum, lum, lum, uBlendFactor);
        }
        """;

    // SSAO: depth-based ambient occlusion (ported from GL2's ssao_fp.glsl)
    // Uses normalized depth, slope-based self-occlusion prevention, and proper range checks.
    private const string SsaoFragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uDepth;
        uniform vec4 uViewInfo; // x=zFar/zNear, y=zFar, z=1/width, w=1/height
        out vec4 oColor;

        // Returns normalized linear depth in [zNear/zFar, 1.0] range
        float getLinearDepth(vec2 tex) {
            float d = texture(uDepth, tex).r;
            return 1.0 / mix(uViewInfo.x, 1.0, d);
        }

        float random(vec2 p) {
            const vec2 r = vec2(23.1406926327792690, 2.6651441426902251);
            return mod(123456789.0, 1e-7 + 256.0 * dot(p, r));
        }

        mat2 randomRotation(vec2 p) {
            float r = random(p);
            float sr = sin(r), cr = cos(r);
            return mat2(cr, sr, -sr, cr);
        }

        #define NUM_SAMPLES 9

        void main() {
            float depth = texture(uDepth, vUV).r;
            if (depth >= 0.9999) { oColor = vec4(1.0); return; }

            vec2 poissonDisc[9] = vec2[](
                vec2(-0.7055767, 0.196515),    vec2(0.3524343, -0.7791386),
                vec2(0.2391056, 0.9189604),    vec2(-0.07580382, -0.09224417),
                vec2(0.5784913, -0.002528916), vec2(0.192888, 0.4064181),
                vec2(-0.6335801, -0.5247476),  vec2(-0.5579782, 0.7491854),
                vec2(0.7320465, 0.6317794)
            );

            float zFarDivZNear = uViewInfo.x;
            float zFar = uViewInfo.y;
            vec2 scale = uViewInfo.wz; // height, width (matching GL2 convention)

            float sampleZ = getLinearDepth(vUV);
            float scaleZ = zFarDivZNear * sampleZ;

            // Surface slope detection to prevent self-occlusion on angled surfaces
            vec2 slope = vec2(dFdx(sampleZ), dFdy(sampleZ)) / vec2(dFdx(vUV.x), dFdy(vUV.y));
            if (length(slope) * zFar > 5000.0) {
                oColor = vec4(1.0);
                return;
            }

            vec2 offsetScale = scale * 1024.0 / scaleZ;
            mat2 rmat = randomRotation(vUV);

            float invZFar = 1.0 / zFar;
            float zLimit = 20.0 * invZFar;
            float result = 0.0;

            for (int i = 0; i < NUM_SAMPLES; i++) {
                vec2 offset = rmat * poissonDisc[i] * offsetScale;
                float sampleDiff = getLinearDepth(vUV + offset) - sampleZ;

                bool s1 = abs(sampleDiff) > zLimit;
                bool s2 = sampleDiff + invZFar > dot(slope, offset);
                result += float(s1 || s2);
            }

            result /= float(NUM_SAMPLES);
            oColor = vec4(vec3(result), 1.0);
        }
        """;

    // Bilateral blur for SSAO (depth-aware to preserve edges)
    private const string SsaoBlurFragSrc = """
        #version 450 core
        in vec2 vUV;
        uniform sampler2D uTex;
        uniform sampler2D uDepth;
        uniform vec2 uTexelSize;
        uniform float uZFarDivZNear;
        out vec4 oColor;

        float getLinearDepth(vec2 tex) {
            float d = texture(uDepth, tex).r;
            return 1.0 / mix(uZFarDivZNear, 1.0, d);
        }

        void main() {
            float result = 0.0;
            float totalWeight = 0.0;
            float centerZ = getLinearDepth(vUV);

            for (int x = -2; x <= 2; x++) {
                for (int y = -2; y <= 2; y++) {
                    vec2 offset = vec2(float(x), float(y)) * uTexelSize;
                    float sampleAO = texture(uTex, vUV + offset).r;
                    float sampleZ = getLinearDepth(vUV + offset);

                    float depthDiff = abs(centerZ - sampleZ);
                    float w = exp(-depthDiff * 10.0 / max(centerZ, 0.001));
                    result += sampleAO * w;
                    totalWeight += w;
                }
            }

            oColor = vec4(vec3(result / max(totalWeight, 0.001)), 1.0);
        }
        """;

    #endregion

    public bool IsEnabled => _enabled;
    public uint SceneDepthTex => _sceneDepthTex;
    public uint SceneFbo => _sceneFbo;

    public void Init(GL gl, int width, int height)
    {
        _gl = gl;
        _width = width;
        _height = height;
        _halfW = width / 2;
        _halfH = height / 2;
        _quarterW = width / 4;
        _quarterH = height / 4;
        _frameCount = 0;

        // Check cvars
        Interop.EngineImports.Cvar_Get("r_bloom", "1", 0x01); // CVAR_ARCHIVE
        Interop.EngineImports.Cvar_Get("r_hdr", "0", 0x01);   // CVAR_ARCHIVE
        Interop.EngineImports.Cvar_Get("r_autoExposure", "0", 0x01);
        Interop.EngineImports.Cvar_Get("r_cameraExposure", "0", 0);
        Interop.EngineImports.Cvar_Get("r_ssao", "0", 0x01);  // CVAR_ARCHIVE

        _bloomEnabled = Interop.EngineImports.Cvar_VariableIntegerValue("r_bloom") != 0;
        _hdrEnabled = Interop.EngineImports.Cvar_VariableIntegerValue("r_hdr") != 0;
        _autoExposure = Interop.EngineImports.Cvar_VariableIntegerValue("r_autoExposure") != 0;
        _ssaoEnabled = Interop.EngineImports.Cvar_VariableIntegerValue("r_ssao") != 0;

        int rShadows = Interop.EngineImports.Cvar_VariableIntegerValue("r_shadows");
        bool shadowsEnabled = rShadows >= 1;

        if (!_bloomEnabled && !_hdrEnabled && !_ssaoEnabled && !shadowsEnabled)
        {
            _enabled = false;
            Interop.EngineImports.Printf(Interop.EngineImports.PRINT_ALL,
                "[.NET] Post-processing disabled (r_bloom 0, r_hdr 0, r_ssao 0, r_shadows 0)\n");
            return;
        }

        // Create fullscreen quad
        CreateQuadVao();

        // Create FBOs (HDR uses RGBA16F, LDR uses RGBA8)
        CreateSceneFbo();

        if (_bloomEnabled)
        {
            _brightFbo = CreateColorFbo(_halfW, _halfH, out _brightTex);
            _blurFboA = CreateColorFbo(_quarterW, _quarterH, out _blurTexA);
            _blurFboB = CreateColorFbo(_quarterW, _quarterH, out _blurTexB);
        }

        if (_hdrEnabled)
        {
            CreateLuminancePyramid();
        }

        if (_ssaoEnabled)
        {
            _ssaoFbo = CreateColorFbo(_halfW, _halfH, out _ssaoTex);
            _ssaoBlurFbo = CreateColorFbo(_halfW, _halfH, out _ssaoBlurTex);
        }

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
        _compHdrLoc = _gl.GetUniformLocation(_compositeProgram, "uHdr");
        _compExposureTexLoc = _gl.GetUniformLocation(_compositeProgram, "uExposure");
        int compSsaoLoc = _gl.GetUniformLocation(_compositeProgram, "uSsao");
        int compUseSsaoLoc = _gl.GetUniformLocation(_compositeProgram, "uUseSsao");
        _gl.UseProgram(_compositeProgram);
        _gl.Uniform1(_compSceneLoc, 0);
        _gl.Uniform1(_compBloomLoc, 1);
        _gl.Uniform1(_compExposureTexLoc, 2);
        _gl.Uniform1(compSsaoLoc, 3);
        _compUseSsaoLoc = compUseSsaoLoc;

        _blitProgram = CreateProgram(QuadVertSrc, BlitFragSrc);
        _gl.UseProgram(_blitProgram);
        _gl.Uniform1(_gl.GetUniformLocation(_blitProgram, "uTex"), 0);

        if (_hdrEnabled)
        {
            _lumInitProgram = CreateProgram(QuadVertSrc, LumInitFragSrc);
            _gl.UseProgram(_lumInitProgram);
            _gl.Uniform1(_gl.GetUniformLocation(_lumInitProgram, "uScene"), 0);

            _lumBlendProgram = CreateProgram(QuadVertSrc, LumBlendFragSrc);
            _lumBlendFactorLoc = _gl.GetUniformLocation(_lumBlendProgram, "uBlendFactor");
            _gl.UseProgram(_lumBlendProgram);
            _gl.Uniform1(_gl.GetUniformLocation(_lumBlendProgram, "uTex"), 0);
        }

        if (_ssaoEnabled)
        {
            _ssaoProgram = CreateProgram(QuadVertSrc, SsaoFragSrc);
            _ssaoViewInfoLoc = _gl.GetUniformLocation(_ssaoProgram, "uViewInfo");
            _gl.UseProgram(_ssaoProgram);
            _gl.Uniform1(_gl.GetUniformLocation(_ssaoProgram, "uDepth"), 0);

            _ssaoBlurProgram = CreateProgram(QuadVertSrc, SsaoBlurFragSrc);
            _ssaoBlurTexelLoc = _gl.GetUniformLocation(_ssaoBlurProgram, "uTexelSize");
            _ssaoBlurZFarDivZNearLoc = _gl.GetUniformLocation(_ssaoBlurProgram, "uZFarDivZNear");
            _gl.UseProgram(_ssaoBlurProgram);
            _gl.Uniform1(_gl.GetUniformLocation(_ssaoBlurProgram, "uTex"), 0);
            _gl.Uniform1(_gl.GetUniformLocation(_ssaoBlurProgram, "uDepth"), 1);
        }

        _gl.UseProgram(0);
        _enabled = true;

        string features = "";
        if (_hdrEnabled) features += "HDR";
        if (_bloomEnabled) features += (features.Length > 0 ? " + " : "") + "bloom";
        if (_ssaoEnabled) features += (features.Length > 0 ? " + " : "") + "SSAO";
        if (features.Length == 0) features = "basic FBO";
        Interop.EngineImports.Printf(Interop.EngineImports.PRINT_ALL,
            $"[.NET] Post-processing initialized ({features})\n");
    }

    /// <summary>Bind the scene FBO so all 3D rendering goes to our texture.</summary>
    public void BindSceneFbo()
    {
        if (!_enabled) return;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
    }

    /// <summary>Unbind the scene FBO and apply post-processing (bloom + HDR tonemapping).</summary>
    public void ApplyAndBlit()
    {
        if (!_enabled) return;
        _frameCount++;

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.BindVertexArray(_quadVao);

        // Bloom passes
        uint bloomTex = 0;
        if (_bloomEnabled)
        {
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

            // Second blur pass for smoother bloom
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFboB);
            _gl.UseProgram(_blurHProgram);
            _gl.BindTexture(TextureTarget.Texture2D, _blurTexA);
            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFboA);
            _gl.UseProgram(_blurVProgram);
            _gl.BindTexture(TextureTarget.Texture2D, _blurTexB);
            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            bloomTex = _blurTexA;
        }

        // HDR: compute auto-exposure via luminance pyramid
        if (_hdrEnabled && _autoExposure)
        {
            ComputeAutoExposure();
        }

        // SSAO passes
        uint ssaoResult = 0;
        if (_ssaoEnabled)
        {
            _gl.ActiveTexture(TextureUnit.Texture0);

            // Pass 1: Compute raw SSAO at half resolution
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _ssaoFbo);
            _gl.Viewport(0, 0, (uint)_halfW, (uint)_halfH);
            _gl.UseProgram(_ssaoProgram);
            // GL2 convention: x=zFar/zNear, y=zFar, z=1/width, w=1/height
            _gl.Uniform4(_ssaoViewInfoLoc, 8192.0f / 1.0f, 8192.0f, 1.0f / _halfW, 1.0f / _halfH);
            _gl.BindTexture(TextureTarget.Texture2D, _sceneDepthTex);
            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            // Pass 2: Bilateral blur (depth-aware)
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _ssaoBlurFbo);
            _gl.UseProgram(_ssaoBlurProgram);
            _gl.Uniform2(_ssaoBlurTexelLoc, 1.0f / _halfW, 1.0f / _halfH);
            _gl.Uniform1(_ssaoBlurZFarDivZNearLoc, 8192.0f / 1.0f);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _ssaoTex);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _sceneDepthTex);
            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            ssaoResult = _ssaoBlurTex;
        }

        // Final composite — scene + bloom + SSAO → default framebuffer (with optional tonemapping)
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.UseProgram(_compositeProgram);
        _gl.Uniform1(_compStrengthLoc, _bloomEnabled ? BloomStrength : 0.0f);
        _gl.Uniform1(_compHdrLoc, _hdrEnabled ? 1 : 0);
        _gl.Uniform1(_compUseSsaoLoc, _ssaoEnabled ? 1 : 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, bloomTex != 0 ? bloomTex : _sceneColorTex);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, _hdrEnabled ? _exposureTex : 0);
        _gl.ActiveTexture(TextureUnit.Texture3);
        _gl.BindTexture(TextureTarget.Texture2D, ssaoResult);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // Restore state for 2D rendering
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
    }

    /// <summary>Compute auto-exposure via luminance pyramid downsampling.</summary>
    private void ComputeAutoExposure()
    {
        _gl.ActiveTexture(TextureUnit.Texture0);

        // Step 1: Extract log-luminance from scene to first pyramid level
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _lumFbos[0]);
        int size = 1 << (_lumLevels - 1); // largest level
        _gl.Viewport(0, 0, (uint)size, (uint)size);
        _gl.UseProgram(_lumInitProgram);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // Step 2: Downsample pyramid to 1x1 via blit (linear filtering averages)
        _gl.UseProgram(_blitProgram);
        for (int i = 1; i < _lumLevels; i++)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _lumFbos[i]);
            size >>= 1;
            _gl.Viewport(0, 0, (uint)size, (uint)size);
            _gl.BindTexture(TextureTarget.Texture2D, _lumTexs[i - 1]);
            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        // Step 3: Temporal blend — smoothly adapt exposure
        // Swap exposure FBOs
        (_exposureFbo, _prevExposureFbo) = (_prevExposureFbo, _exposureFbo);
        (_exposureTex, _prevExposureTex) = (_prevExposureTex, _exposureTex);

        // Blit previous exposure first as base
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _exposureFbo);
        _gl.Viewport(0, 0, 1, 1);
        _gl.UseProgram(_blitProgram);
        _gl.BindTexture(TextureTarget.Texture2D, _prevExposureTex);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // Blend in current luminance
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.UseProgram(_lumBlendProgram);
        _gl.Uniform1(_lumBlendFactorLoc, ExposureBlendFactor);
        _gl.BindTexture(TextureTarget.Texture2D, _lumTexs[_lumLevels - 1]); // 1x1 current
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        if (_compositeProgram != 0) _gl.DeleteProgram(_compositeProgram);
        if (_blitProgram != 0) _gl.DeleteProgram(_blitProgram);
        if (_blurVProgram != 0) _gl.DeleteProgram(_blurVProgram);
        if (_blurHProgram != 0) _gl.DeleteProgram(_blurHProgram);
        if (_brightProgram != 0) _gl.DeleteProgram(_brightProgram);
        if (_lumInitProgram != 0) _gl.DeleteProgram(_lumInitProgram);
        if (_lumBlendProgram != 0) _gl.DeleteProgram(_lumBlendProgram);

        if (_ssaoProgram != 0) _gl.DeleteProgram(_ssaoProgram);
        if (_ssaoBlurProgram != 0) _gl.DeleteProgram(_ssaoBlurProgram);

        DeleteFbo(ref _ssaoBlurFbo, ref _ssaoBlurTex, 0);
        DeleteFbo(ref _ssaoFbo, ref _ssaoTex, 0);
        DeleteFbo(ref _blurFboB, ref _blurTexB, 0);
        DeleteFbo(ref _blurFboA, ref _blurTexA, 0);
        DeleteFbo(ref _brightFbo, ref _brightTex, 0);

        // Scene FBO: depth is a texture, not a renderbuffer
        if (_sceneDepthTex != 0) { _gl.DeleteTexture(_sceneDepthTex); _sceneDepthTex = 0; }
        DeleteFbo(ref _sceneFbo, ref _sceneColorTex, 0);

        // Clean up HDR luminance pyramid
        for (int i = 0; i < _lumLevels; i++)
        {
            DeleteFbo(ref _lumFbos[i], ref _lumTexs[i], 0);
        }
        _lumFbos = Array.Empty<uint>();
        _lumTexs = Array.Empty<uint>();
        _lumLevels = 0;
        DeleteFbo(ref _exposureFbo, ref _exposureTex, 0);
        DeleteFbo(ref _prevExposureFbo, ref _prevExposureTex, 0);

        if (_quadVbo != 0) { _gl.DeleteBuffer(_quadVbo); _quadVbo = 0; }
        if (_quadVao != 0) { _gl.DeleteVertexArray(_quadVao); _quadVao = 0; }

        _enabled = false;
    }

    #region Setup helpers

    private void CreateSceneFbo()
    {
        _sceneFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);

        // HDR: RGBA16F, LDR: RGBA8
        _sceneColorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        if (_hdrEnabled)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f,
                (uint)_width, (uint)_height, 0, Silk.NET.OpenGL.PixelFormat.Rgba, Silk.NET.OpenGL.PixelType.Float, null);
        }
        else
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)_width, (uint)_height, 0, Silk.NET.OpenGL.PixelFormat.Rgba, Silk.NET.OpenGL.PixelType.UnsignedByte, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _sceneColorTex, 0);

        // Depth attachment: texture (so SSAO can sample it)
        _sceneDepthTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _sceneDepthTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24,
            (uint)_width, (uint)_height, 0, Silk.NET.OpenGL.PixelFormat.DepthComponent,
            Silk.NET.OpenGL.PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _sceneDepthTex, 0);

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

    private void CreateLuminancePyramid()
    {
        // Compute number of levels needed to get from 256 down to 1 (256→128→64→32→16→8→4→2→1 = 9 levels)
        _lumLevels = 9; // 256x256 → 1x1
        _lumFbos = new uint[_lumLevels];
        _lumTexs = new uint[_lumLevels];

        int size = 256;
        for (int i = 0; i < _lumLevels; i++)
        {
            _lumFbos[i] = CreateColorFbo(size, size, out _lumTexs[i]);
            size >>= 1;
            if (size < 1) size = 1;
        }

        // Two 1x1 FBOs for temporal exposure blending (current + previous)
        _exposureFbo = CreateColorFbo(1, 1, out _exposureTex);
        _prevExposureFbo = CreateColorFbo(1, 1, out _prevExposureTex);

        // Initialize exposure textures to mid-gray (0.5 = log2(1.0) = neutral exposure)
        float midGray = 0.5f;
        _gl.BindTexture(TextureTarget.Texture2D, _exposureTex);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 1, 1,
            Silk.NET.OpenGL.PixelFormat.Red, Silk.NET.OpenGL.PixelType.Float, &midGray);
        _gl.BindTexture(TextureTarget.Texture2D, _prevExposureTex);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 1, 1,
            Silk.NET.OpenGL.PixelFormat.Red, Silk.NET.OpenGL.PixelType.Float, &midGray);
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
