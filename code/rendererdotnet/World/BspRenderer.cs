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
    private int _tcModExtraLoc;
    private int _rgbGenLoc;
    private int _alphaGenModeLoc;
    private int _rgbWaveLoc;
    private int _rgbWavePhaseLoc;
    private int _alphaWaveLoc;
    private int _alphaWavePhaseLoc;
    private int _constColorLoc;
    private int _constAlphaLoc;
    private int _deformTypeLoc;
    private int _deformParams0Loc;
    private int _deformParams1Loc;
    private int _overbrightScaleLoc;
    private int _useLmUVLoc;
    private int _greyscaleLoc;
    private int _texNormalMapLoc;
    private int _texSpecularMapLoc;
    private int _useNormalMapLoc;
    private int _useSpecularMapLoc;
    private int _useParallaxLoc;
    private int _parallaxScaleLoc;
    private int _texDeluxeMapLoc;
    private int _useDeluxeMapLoc;
    private int _portalMapLoc;
    private int _screenSizeLoc;
    private int _usePBRLoc;
    private int _texCubeMapLoc;
    private int _useCubeMapLoc;
    private int _cubeMapInfoLoc;
    private float _greyscaleValue; // 0.0=color, 1.0=fully greyscale

    private uint _vao;
    private uint _vbo;
    private uint _ebo;

    private BspWorld? _world;
    private uint[] _lightmapTextures = [];
    private uint[] _deluxeMapTextures = [];
    private bool _hasDeluxeMapping;
    private uint[] _cubemapTextures = [];     // OpenGL cubemap texture handles

    // Per-vertex: x,y,z, nx,ny,nz, u,v, lmU,lmV, r,g,b,a, tx,ty,tz,ts = 18 floats
    private const int FLOATS_PER_VERT = 18;

    // Visibility tracking to avoid drawing same surface twice per frame
    private int[] _surfaceDrawnFrame = [];
    private int _frameCount;

    // Transparent surfaces deferred to second pass
    private readonly List<(int SurfIdx, BlendMode Blend, int SortKey)> _transparentSurfaces = new(256);

    // Deferred opaque surfaces for shader-sorted batching
    private readonly List<(int SurfIdx, int ShaderHandle, int LmIdx, int AlphaFunc)> _deferredOpaque = new(4096);

    // Multi-draw batch buffers (reused per frame)
    private int[] _batchCounts = new int[512];
    private nint[] _batchOffsets = new nint[512];
    private int[] _batchBaseVerts = new int[512];

    // Current frame time for multi-stage animation
    private float _currentTimeSec;

    // Current view position, stored per-frame for tcGen environment
    private float _viewX, _viewY, _viewZ;
    // View axes for autosprite billboarding
    private readonly float[] _viewRight = new float[3];
    private readonly float[] _viewUp = new float[3];

    // Frustum planes for culling (6 planes, each: nx, ny, nz, d)
    private readonly float[] _frustum = new float[24]; // 6 * 4

    // Dynamic lighting
    private uint _dlightProgram;
    private int _dlMvpLoc;
    private int _dlLightOriginLoc;
    private int _dlLightColorLoc;
    private int _dlLightRadiusLoc;
    private uint _dlightTexture; // radial falloff texture

    // Fog rendering
    private uint _fogProgram;
    private int _fogMvpLoc;
    private int _fogColorLoc;
    private int _fogDistanceLoc;  // fog distance vector
    private int _fogDepthLoc;     // fog depth vector
    private int _fogEyeTLoc;      // eye position relative to fog surface

    // Flare rendering
    private uint _flareProgram;
    private int _flareColorLoc;
    private int _flarePosLoc;     // screen-space position
    private int _flareSizeLoc;    // screen-space size
    private uint _flareVao;
    private uint _flareVbo;
    private uint _flareTexture;   // radial glow texture
    private readonly List<(float X, float Y, float Z, float R, float G, float B, float NX, float NY, float NZ)> _visibleFlares = new(64);

    // Surfaces drawn this frame (for dlight reuse)
    private readonly List<int> _visibleSurfaceIndices = new(4096);

    // Autosprite deform: dynamic VBO for CPU-side billboarding
    private uint _autospVao, _autospVbo, _autospEbo;
    private float[] _autospVerts = new float[4096]; // reusable per-frame
    private uint[] _autospIndices = new uint[2048];

    // Portal rendering: collected portal surface info and per-surface textures
    private readonly List<int> _portalSurfIndices = new();  // All visible portal surface indices
    private readonly Dictionary<int, uint> _portalTextures = new();  // surfIdx → texture ID

    /// <summary>When true, frustum culling is skipped (debug for portal pass).</summary>
    public bool SkipFrustumCulling { get; set; }

    /// <summary>Number of deferred opaque surfaces after the last Render() call.</summary>
    public int LastDeferredOpaqueCount { get; private set; }
    /// <summary>Number of transparent surfaces after the last Render() call.</summary>
    public int LastTransparentCount { get; private set; }

    private const string DlightVertSrc = """
        #version 450 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;

        uniform mat4 uMVP;
        uniform vec3 uLightOrigin;
        uniform float uLightRadius;

        out vec2 vDlightUV;
        out float vDlightMod;

        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);

            // Project light onto surface
            vec3 dist = uLightOrigin - aPos;
            float scale = 1.0 / uLightRadius;

            // UV coords from XY distance (projected texture)
            vDlightUV = dist.xy * scale * 0.5 + vec2(0.5);

            // Modulate by surface normal facing and Z distance
            float ndl = step(0.0, dot(normalize(dist), normalize(aNormal)));
            float zFade = clamp(2.0 * (1.0 - abs(dist.z) * scale), 0.0, 1.0);
            vDlightMod = ndl * zFade;
        }
        """;

    private const string DlightFragSrc = """
        #version 450 core
        in vec2 vDlightUV;
        in float vDlightMod;

        uniform sampler2D uDlightTex;
        uniform vec3 uLightColor;

        out vec4 oColor;

        void main() {
            if (vDlightMod <= 0.0) discard;
            vec4 dlTex = texture(uDlightTex, vDlightUV);
            oColor = vec4(uLightColor * dlTex.r * vDlightMod, 1.0);
        }
        """;

    private const string FogVertSrc = """
        #version 450 core
        layout(location = 0) in vec3 aPos;

        uniform mat4 uMVP;
        uniform vec4 uFogDistance;
        uniform vec4 uFogDepth;
        uniform float uFogEyeT;

        out float vFogFactor;

        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);

            // Compute fog density from distance and depth
            float s = dot(vec4(aPos, 1.0), uFogDistance) * 8.0;
            float t = dot(vec4(aPos, 1.0), uFogDepth);

            float eyeOutside = step(0.0, -uFogEyeT);
            float fogged = step(eyeOutside, t);
            t += 1e-6;
            t *= fogged / (t - uFogEyeT * eyeOutside);

            vFogFactor = s * t;
        }
        """;

    private const string FogFragSrc = """
        #version 450 core
        in float vFogFactor;

        uniform vec4 uFogColor;

        out vec4 oColor;

        void main() {
            float alpha = sqrt(clamp(vFogFactor, 0.0, 1.0));
            if (alpha < 0.004) discard;
            oColor = vec4(uFogColor.rgb, alpha);
        }
        """;

    private const string FlareVertSrc = """
        #version 450 core
        layout(location = 0) in vec2 aPos; // [-1,1] quad corners

        uniform vec2 uFlarePos;   // screen-space center (NDC)
        uniform vec2 uFlareSize;  // half-size in NDC

        out vec2 vUV;

        void main() {
            vUV = aPos * 0.5 + vec2(0.5);
            gl_Position = vec4(uFlarePos + aPos * uFlareSize, 0.0, 1.0);
        }
        """;

    private const string FlareFragSrc = """
        #version 450 core
        in vec2 vUV;

        uniform vec3 uFlareColor;
        uniform sampler2D uFlareTex;

        out vec4 oColor;

        void main() {
            float intensity = texture(uFlareTex, vUV).r;
            if (intensity < 0.01) discard;
            oColor = vec4(uFlareColor * intensity, intensity);
        }
        """;

    private const string VertSrc = """
        #version 450 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUV;
        layout(location = 3) in vec2 aLmUV;
        layout(location = 4) in vec4 aColor;
        layout(location = 5) in vec4 aTangent;

        uniform mat4 uMVP;
        uniform int uEnvMap;
        uniform vec3 uViewPos;
        uniform float uTime;
        // tcMod: up to 4 ops, each encoded as vec4(type, p0, p1, p2)
        uniform int uTcModCount;
        uniform vec4 uTcMod[4];
        uniform vec4 uTcModExtra[4]; // extra params for stretch (phase,freq) and transform (m11,t0,t1)
        // deformVertexes: type(0=wave,1=move,2=bulge,3=normal), params packed in vec4s
        uniform int uDeformType; // -1=none, 0=wave, 1=move, 2=bulge, 3=normal
        uniform vec4 uDeformParams0; // wave: div,func,base,amp  move: x,y,z,func
        uniform vec4 uDeformParams1; // wave: phase,freq,0,0     move: base,amp,phase,freq
        uniform int uUseNormalMap;

        out vec2 vUV;
        out vec2 vLmUV;
        out vec3 vNormal;
        out vec4 vColor;
        out vec3 vWorldPos;
        out mat3 vTBN;

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
            vec3 deformedNormal = aNormal;

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
            } else if (uDeformType == 2) { // bulge
                float bulgeWidth = uDeformParams0.x;
                float bulgeHeight = uDeformParams0.y;
                float bulgeSpeed = uDeformParams0.z;
                float bulgePhase = aUV.x * bulgeWidth;
                float wave = sin(bulgePhase + uTime * bulgeSpeed);
                pos += aNormal * (wave * bulgeHeight);
            } else if (uDeformType == 3) { // normal
                float normAmp = uDeformParams0.x;
                float normFreq = uDeformParams0.y;
                float px = sin(pos.x * 0.01 + uTime * normFreq) * normAmp;
                float py = sin(pos.y * 0.01 + uTime * normFreq * 1.1) * normAmp;
                float pz = sin(pos.z * 0.01 + uTime * normFreq * 0.9) * normAmp;
                deformedNormal = normalize(aNormal + vec3(px, py, pz));
            }

            gl_Position = uMVP * vec4(pos, 1.0);
            vLmUV = aLmUV;
            vNormal = deformedNormal;
            vColor = aColor;
            vWorldPos = pos;

            // Compute TBN matrix for normal mapping
            if (uUseNormalMap != 0) {
                vec3 N = normalize(aNormal);
                vec3 T = normalize(aTangent.xyz);
                T = normalize(T - dot(T, N) * N); // re-orthogonalize
                vec3 B = cross(N, T) * aTangent.w;
                vTBN = mat3(T, B, N);
            } else {
                vTBN = mat3(1.0);
            }

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
                    } else if (modType == 4) { // stretch
                        int func = int(uTcMod[i].y);
                        float base_ = uTcMod[i].z;
                        float amp = uTcMod[i].w;
                        // phase and freq packed in second vec4 via uTcModExtra
                        float phase = uTcModExtra[i].x;
                        float freq = uTcModExtra[i].y;
                        float wave = base_ + amp * evalWaveFunc(func, phase + uTime * freq);
                        if (abs(wave) < 0.001) wave = 0.001;
                        float invWave = 1.0 / wave;
                        uv = vec2(0.5) + (uv - vec2(0.5)) * invWave;
                    } else if (modType == 5) { // transform
                        float m00 = uTcMod[i].y;
                        float m01 = uTcMod[i].z;
                        float m10 = uTcMod[i].w;
                        float m11 = uTcModExtra[i].x;
                        float t0 = uTcModExtra[i].y;
                        float t1 = uTcModExtra[i].z;
                        vec2 newUV;
                        newUV.x = uv.x * m00 + uv.y * m10 + t0;
                        newUV.y = uv.x * m01 + uv.y * m11 + t1;
                        uv = newUV;
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
        in vec3 vWorldPos;
        in mat3 vTBN;

        uniform sampler2D uTexDiffuse;
        uniform sampler2D uTexLightmap;
        uniform sampler2D uTexNormalMap;
        uniform sampler2D uTexSpecularMap;
        uniform sampler2D uTexDeluxeMap;
        uniform vec4 uColor;
        uniform vec3 uLightDir;
        uniform vec3 uViewPos;
        uniform int uUseLightmap;
        uniform float uOverbrightScale;
        uniform int uAlphaFunc; // 0=none, 1=GT0, 2=LT128, 3=GE128
        uniform int uRgbGen;    // 0=identity, 1=vertex, 2=entity, 3=wave, 4=identityLighting, 5=const, 6=oneMinusVertex, 7=lightingDiffuse, 8=NDL fallback
        uniform int uAlphaGenMode; // 0=identity, 1=vertex, 2=entity, 3=wave, 4=const
        uniform vec4 uRgbWave;  // x=func, y=base, z=amp, w=freq
        uniform float uRgbWavePhase;
        uniform vec4 uAlphaWave; // x=func, y=base, z=amp, w=freq
        uniform float uAlphaWavePhase;
        uniform vec3 uConstColor; // rgbGen const color
        uniform float uConstAlpha; // alphaGen const value
        uniform int uUseLmUV;   // 1=sample diffuse with lightmap UVs (for multi-stage lightmap)
        uniform float uGreyscale; // 0.0=color, 1.0=fully greyscale
        uniform int uUseNormalMap;
        uniform int uUseSpecularMap;
        uniform int uUseParallax;
        uniform float uParallaxScale; // height scale (default 0.05)
        uniform int uUseDeluxeMap;
        uniform int uPortalMap;   // 1=use screen-space UVs from gl_FragCoord
        uniform vec2 uScreenSize; // viewport size for portal mapping
        uniform int uUsePBR;      // 1=metallic/roughness PBR workflow
        uniform float uTime;
        uniform samplerCube uTexCubeMap;
        uniform int uUseCubeMap;       // 1=cubemap reflection enabled for this surface
        uniform vec4 uCubeMapInfo;     // xyz = cubemapOrigin - viewOrigin, w = 1/parallaxRadius

        out vec4 oColor;

        float evalWave(int func, float phase) {
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

        // Environment BRDF approximation for cubemap reflections
        vec3 EnvironmentBRDF(float roughness, float NE, vec3 specular) {
            float v = 1.0 - max(roughness, NE);
            v *= v * v;
            return vec3(v) + specular;
        }

        // Steep parallax mapping: 16-step linear search + interpolation
        vec2 ParallaxOffset(vec2 texCoords, vec3 viewDirTS) {
            const int numSteps = 16;
            float layerDepth = 1.0 / float(numSteps);
            float currentDepth = 0.0;
            vec2 deltaUV = viewDirTS.xy * uParallaxScale / max(viewDirTS.z, 0.1);
            deltaUV /= float(numSteps);

            vec2 currentUV = texCoords;
            float heightValue = 1.0 - texture(uTexNormalMap, currentUV).a;

            float prevDepth = 0.0;
            float prevHeight = heightValue;
            vec2 prevUV = currentUV;

            for (int i = 0; i < numSteps; i++) {
                currentDepth += layerDepth;
                currentUV -= deltaUV;
                heightValue = 1.0 - texture(uTexNormalMap, currentUV).a;

                if (currentDepth >= heightValue) {
                    // Interpolate between previous and current
                    float w = (prevDepth - prevHeight) / 
                              ((prevDepth - prevHeight) - (currentDepth - heightValue));
                    return mix(prevUV, currentUV, w) - texCoords;
                }
                prevDepth = currentDepth;
                prevHeight = heightValue;
                prevUV = currentUV;
            }
            return currentUV - texCoords;
        }

        void main() {
            // Portal map: sample texture using screen-space coordinates
            if (uPortalMap != 0) {
                vec2 screenUV = gl_FragCoord.xy / uScreenSize;
                oColor = texture(uTexDiffuse, screenUV);
                return;
            }

            vec2 uv = vUV;

            // Parallax offset (modifies UVs before all sampling)
            if (uUseParallax != 0 && uUseNormalMap != 0) {
                vec3 viewDir = normalize(uViewPos - vWorldPos);
                vec3 viewDirTS = normalize(transpose(vTBN) * viewDir);
                uv += ParallaxOffset(uv, viewDirTS);
            }

            vec2 sampleUV = uUseLmUV != 0 ? vLmUV : uv;
            vec4 texColor = texture(uTexDiffuse, sampleUV);

            // Alpha test — discard fragments based on alpha function
            if (uAlphaFunc == 1 && texColor.a <= 0.0) discard;       // GT0
            else if (uAlphaFunc == 2 && texColor.a >= 0.5) discard;  // LT128
            else if (uAlphaFunc == 3 && texColor.a < 0.5) discard;   // GE128

            // Compute shading normal (from normal map or interpolated)
            vec3 N = normalize(vNormal);
            if (uUseNormalMap != 0) {
                vec3 mapN = texture(uTexNormalMap, uv).rgb * 2.0 - 1.0;
                // Reconstruct Z if stored as two-component
                if (abs(mapN.z) < 0.01) {
                    mapN.z = sqrt(max(0.0, 1.0 - mapN.x*mapN.x - mapN.y*mapN.y));
                }
                N = normalize(vTBN * mapN);
            }

            // PBR: convert diffuse from sRGB to linear
            if (uUsePBR != 0) {
                texColor.rgb *= texColor.rgb;
            }

            if (uUseLightmap != 0) {
                vec4 lmColor = texture(uTexLightmap, vLmUV);
                // PBR: convert lightmap to linear
                if (uUsePBR != 0) lmColor.rgb *= lmColor.rgb;

                oColor = texColor * lmColor * uOverbrightScale;

                // Deluxe map: per-pixel light direction for enhanced lighting
                if (uUseDeluxeMap != 0 && uUseNormalMap != 0) {
                    vec3 deluxeDir = normalize(texture(uTexDeluxeMap, vLmUV).rgb * 2.0 - 1.0);
                    float ndl = max(dot(N, deluxeDir), 0.0);
                    // Blend between lightmap-only and deluxe-enhanced
                    float lmLuma = dot(lmColor.rgb, vec3(0.299, 0.587, 0.114));
                    if (lmLuma > 0.01) {
                        float ratio = ndl / max(lmLuma, 0.01);
                        oColor.rgb = texColor.rgb * lmColor.rgb * ratio * uOverbrightScale;
                    }
                }
            } else if (uRgbGen == 1) {
                // rgbGen vertex — multiply by vertex color directly
                oColor = texColor * vColor;
            } else if (uRgbGen == 3) {
                // rgbGen wave — animated color from wave function
                int func = int(uRgbWave.x);
                float base_ = uRgbWave.y;
                float amp = uRgbWave.z;
                float freq = uRgbWave.w;
                float wave = clamp(base_ + amp * evalWave(func, uRgbWavePhase + uTime * freq), 0.0, 1.0);
                oColor = texColor * vec4(vec3(wave), 1.0);
            } else if (uRgbGen == 5) {
                // rgbGen const — constant color
                oColor = texColor * vec4(uConstColor, 1.0);
            } else if (uRgbGen == 6) {
                // rgbGen oneMinusVertex
                oColor = texColor * (vec4(1.0) - vColor);
            } else if (uRgbGen == 7) {
                // rgbGen lightingDiffuse — NDL from light direction
                float ndl = max(dot(N, uLightDir), 0.0);
                float light = 0.3 + 0.7 * ndl;
                oColor = texColor * vec4(vec3(light), 1.0);
            } else if (uRgbGen == 8) {
                // NDL fallback (single-stage path with vertex color check)
                float vcSum = vColor.r + vColor.g + vColor.b;
                if (vcSum > 0.01) {
                    oColor = texColor * vColor;
                } else {
                    float ndl = max(dot(N, uLightDir), 0.0);
                    float light = 0.3 + 0.7 * ndl;
                    oColor = texColor * vec4(vec3(light), 1.0);
                }
            } else {
                // rgbGen identity (0) or identityLighting (4) — pass through
                oColor = texColor;
            }

            // Apply alphaGen
            if (uAlphaGenMode == 1) {
                oColor.a = texColor.a * vColor.a;
            } else if (uAlphaGenMode == 2) {
                oColor.a = texColor.a * uColor.a;
            } else if (uAlphaGenMode == 3) {
                int afunc = int(uAlphaWave.x);
                float abase = uAlphaWave.y;
                float aamp = uAlphaWave.z;
                float afreq = uAlphaWave.w;
                oColor.a = texColor.a * clamp(abase + aamp * evalWave(afunc, uAlphaWavePhase + uTime * afreq), 0.0, 1.0);
            } else if (uAlphaGenMode == 4) {
                oColor.a = texColor.a * uConstAlpha;
            }

            // Specular / PBR lighting
            if (uUseSpecularMap != 0) {
                vec3 V = normalize(uViewPos - vWorldPos);
                vec3 L = uLightDir;
                if (uUseDeluxeMap != 0) {
                    L = normalize(texture(uTexDeluxeMap, vLmUV).rgb * 2.0 - 1.0);
                }
                vec3 H = normalize(L + V);
                float NdotH = max(dot(N, H), 0.0);
                float NdotL = max(dot(N, L), 0.0);
                float EdotH = max(dot(V, H), 0.0);
                float NdotV = max(dot(N, V), 0.0);

                vec4 specSample = texture(uTexSpecularMap, uv);

                if (uUsePBR != 0) {
                    // PBR metallic/roughness workflow
                    float gloss = specSample.r;
                    float metal = specSample.g;

                    // Fresnel F0: dielectric=0.04, metal=albedo
                    vec3 F0 = metal * texColor.rgb + vec3(0.04 - 0.04 * metal);

                    // Remove metallic from diffuse
                    oColor.rgb *= (1.0 - metal);

                    // Roughness from gloss (exponential mapping)
                    float roughness = exp2(-3.0 * gloss);
                    roughness = max(roughness, 0.04);

                    // Microfacet specular BRDF (GGX/Trowbridge-Reitz)
                    float rr = roughness * roughness;
                    float rrrr = rr * rr;
                    float d = (NdotH * NdotH) * (rrrr - 1.0) + 1.0;
                    float v = (EdotH * EdotH) * (roughness + 0.5) + 0.0001;
                    vec3 spec = F0 * (rrrr / (4.0 * d * d * v));

                    oColor.rgb += spec * NdotL;
                } else {
                    // Standard Blinn-Phong specular
                    float spec = pow(NdotH, 16.0);
                    oColor.rgb += specSample.rgb * spec * 0.3;
                }
            }

            oColor *= uColor;
            oColor.a = texColor.a * uColor.a;

            // PBR: convert linear back to sRGB
            if (uUsePBR != 0) {
                oColor.rgb = sqrt(max(oColor.rgb, vec3(0.0)));
            }

            // Cubemap reflections (parallax-corrected)
            if (uUseCubeMap != 0) {
                vec3 V = normalize(uViewPos - vWorldPos);
                vec3 N2 = normalize(vNormal);
                float NE = max(dot(N2, V), 0.0);

                float roughness = 0.5;
                vec3 specColor = vec3(0.04);
                if (uUseSpecularMap != 0) {
                    vec4 specSamp = texture(uTexSpecularMap, vUV);
                    if (uUsePBR != 0) {
                        roughness = exp2(-3.0 * specSamp.r);
                        specColor = specSamp.g * texColor.rgb + vec3(0.04 - 0.04 * specSamp.g);
                    } else {
                        roughness = 1.0 - specSamp.r * 0.5;
                        specColor = specSamp.rgb * 0.3 + vec3(0.04);
                    }
                }

                vec3 reflectance = EnvironmentBRDF(roughness, NE, specColor);
                vec3 R = reflect(-V, N2);

                // Parallax correction
                vec3 parallax = uCubeMapInfo.xyz + uCubeMapInfo.w * (-V);
                float mipLevel = 7.0 * roughness;
                vec3 cubeLightColor = textureLod(uTexCubeMap, R + parallax, mipLevel).rgb;

                if (uUsePBR != 0)
                    cubeLightColor *= cubeLightColor; // sRGB to linear

                oColor.rgb += cubeLightColor * reflectance;
            }

            // Greyscale desaturation
            if (uGreyscale > 0.0) {
                float luma = dot(oColor.rgb, vec3(0.299, 0.587, 0.114));
                oColor.rgb = mix(oColor.rgb, vec3(luma), uGreyscale);
            }
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
        _tcModExtraLoc = _gl.GetUniformLocation(_program, "uTcModExtra");
        _rgbGenLoc = _gl.GetUniformLocation(_program, "uRgbGen");
        _alphaGenModeLoc = _gl.GetUniformLocation(_program, "uAlphaGenMode");
        _rgbWaveLoc = _gl.GetUniformLocation(_program, "uRgbWave");
        _rgbWavePhaseLoc = _gl.GetUniformLocation(_program, "uRgbWavePhase");
        _alphaWaveLoc = _gl.GetUniformLocation(_program, "uAlphaWave");
        _alphaWavePhaseLoc = _gl.GetUniformLocation(_program, "uAlphaWavePhase");
        _constColorLoc = _gl.GetUniformLocation(_program, "uConstColor");
        _constAlphaLoc = _gl.GetUniformLocation(_program, "uConstAlpha");
        _deformTypeLoc = _gl.GetUniformLocation(_program, "uDeformType");
        _deformParams0Loc = _gl.GetUniformLocation(_program, "uDeformParams0");
        _deformParams1Loc = _gl.GetUniformLocation(_program, "uDeformParams1");
        _overbrightScaleLoc = _gl.GetUniformLocation(_program, "uOverbrightScale");
        _useLmUVLoc = _gl.GetUniformLocation(_program, "uUseLmUV");
        _greyscaleLoc = _gl.GetUniformLocation(_program, "uGreyscale");
        _texNormalMapLoc = _gl.GetUniformLocation(_program, "uTexNormalMap");
        _texSpecularMapLoc = _gl.GetUniformLocation(_program, "uTexSpecularMap");
        _useNormalMapLoc = _gl.GetUniformLocation(_program, "uUseNormalMap");
        _useSpecularMapLoc = _gl.GetUniformLocation(_program, "uUseSpecularMap");
        _useParallaxLoc = _gl.GetUniformLocation(_program, "uUseParallax");
        _parallaxScaleLoc = _gl.GetUniformLocation(_program, "uParallaxScale");
        _texDeluxeMapLoc = _gl.GetUniformLocation(_program, "uTexDeluxeMap");
        _useDeluxeMapLoc = _gl.GetUniformLocation(_program, "uUseDeluxeMap");
        _portalMapLoc = _gl.GetUniformLocation(_program, "uPortalMap");
        _screenSizeLoc = _gl.GetUniformLocation(_program, "uScreenSize");
        _usePBRLoc = _gl.GetUniformLocation(_program, "uUsePBR");
        _texCubeMapLoc = _gl.GetUniformLocation(_program, "uTexCubeMap");
        _useCubeMapLoc = _gl.GetUniformLocation(_program, "uUseCubeMap");
        _cubeMapInfoLoc = _gl.GetUniformLocation(_program, "uCubeMapInfo");

        // Set texture unit bindings (static)
        _gl.UseProgram(_program);
        _gl.Uniform1(_texDiffuseLoc, 0);   // GL_TEXTURE0
        _gl.Uniform1(_texLightmapLoc, 1);  // GL_TEXTURE1
        _gl.Uniform1(_texNormalMapLoc, 2); // GL_TEXTURE2
        _gl.Uniform1(_texSpecularMapLoc, 3); // GL_TEXTURE3
        _gl.Uniform1(_texDeluxeMapLoc, 4); // GL_TEXTURE4
        _gl.Uniform1(_texCubeMapLoc, 5);  // GL_TEXTURE5
        _gl.UseProgram(0);

        // Register r_greyscale cvar (0.0 = color, 1.0 = fully grey)
        EngineImports.Cvar_Get("r_greyscale", "0", 1); // 1 = CVAR_ARCHIVE
        EngineImports.Cvar_Get("r_parallaxMapping", "0", 1); // 1 = CVAR_ARCHIVE
        EngineImports.Cvar_Get("r_baseParallax", "0.05", 1);
        EngineImports.Cvar_Get("r_deluxeMapping", "1", 1);
        EngineImports.Cvar_Get("r_pbr", "0", 1); // PBR metallic/roughness workflow
        EngineImports.Cvar_Get("r_mapOverBrightBits", "2", 0x02); // CVAR_LATCH
        EngineImports.Cvar_Get("r_cubeMapping", "1", 1); // 1 = CVAR_ARCHIVE

        // Dlight shader program
        _dlightProgram = CreateDlightProgram();
        _dlMvpLoc = _gl.GetUniformLocation(_dlightProgram, "uMVP");
        _dlLightOriginLoc = _gl.GetUniformLocation(_dlightProgram, "uLightOrigin");
        _dlLightColorLoc = _gl.GetUniformLocation(_dlightProgram, "uLightColor");
        _dlLightRadiusLoc = _gl.GetUniformLocation(_dlightProgram, "uLightRadius");
        int dlTexLoc = _gl.GetUniformLocation(_dlightProgram, "uDlightTex");
        _gl.UseProgram(_dlightProgram);
        _gl.Uniform1(dlTexLoc, 0);
        _gl.UseProgram(0);

        // Generate radial falloff texture for dlight projection
        _dlightTexture = GenerateDlightTexture();

        // Fog shader program
        _fogProgram = CreateFogProgram();
        _fogMvpLoc = _gl.GetUniformLocation(_fogProgram, "uMVP");
        _fogColorLoc = _gl.GetUniformLocation(_fogProgram, "uFogColor");
        _fogDistanceLoc = _gl.GetUniformLocation(_fogProgram, "uFogDistance");
        _fogDepthLoc = _gl.GetUniformLocation(_fogProgram, "uFogDepth");
        _fogEyeTLoc = _gl.GetUniformLocation(_fogProgram, "uFogEyeT");

        // Flare shader program
        _flareProgram = CreateFlareProgram();
        _flarePosLoc = _gl.GetUniformLocation(_flareProgram, "uFlarePos");
        _flareSizeLoc = _gl.GetUniformLocation(_flareProgram, "uFlareSize");
        _flareColorLoc = _gl.GetUniformLocation(_flareProgram, "uFlareColor");
        int flareTexLoc = _gl.GetUniformLocation(_flareProgram, "uFlareTex");
        _gl.UseProgram(_flareProgram);
        _gl.Uniform1(flareTexLoc, 0);
        _gl.UseProgram(0);

        // Flare quad VAO (screen-space unit quad)
        _flareVao = _gl.GenVertexArray();
        _flareVbo = _gl.GenBuffer();
        float[] quadVerts = [-1f, -1f, 1f, -1f, 1f, 1f, -1f, -1f, 1f, 1f, -1f, 1f];
        _gl.BindVertexArray(_flareVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _flareVbo);
        fixed (float* qp = quadVerts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quadVerts.Length * sizeof(float)), qp, BufferUsageARB.StaticDraw);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), null);
        _gl.EnableVertexAttribArray(0);
        _gl.BindVertexArray(0);

        // Flare glow texture (radial falloff with soft edges)
        _flareTexture = GenerateFlareTexture();

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
        _hasDeluxeMapping = world.HasDeluxeMapping;

        UploadGeometry(world);
        UploadLightmaps(world);
        UploadDeluxeMaps(world);
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
            verts[o + 14] = v.TX; verts[o + 15] = v.TY;
            verts[o + 16] = v.TZ; verts[o + 17] = v.TS;
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
        // Tangent (vec4: xyz + handedness sign)
        _gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, stride, (void*)(14 * sizeof(float)));
        _gl.EnableVertexAttribArray(5);

        _gl.BindVertexArray(0);

        // Create dynamic VAO/VBO for autosprite deformed surfaces
        _autospVao = _gl.GenVertexArray();
        _autospVbo = _gl.GenBuffer();
        _autospEbo = _gl.GenBuffer();
        _gl.BindVertexArray(_autospVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _autospVbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _autospEbo);
        // Same vertex layout as the main BSP VAO
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(10 * sizeof(float)));
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, stride, (void*)(14 * sizeof(float)));
        _gl.EnableVertexAttribArray(5);
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

    private void UploadDeluxeMaps(BspWorld world)
    {
        if (!world.HasDeluxeMapping || world.DeluxeMaps.Length == 0)
        {
            _deluxeMapTextures = [];
            return;
        }

        _deluxeMapTextures = new uint[world.DeluxeMaps.Length];
        for (int i = 0; i < world.DeluxeMaps.Length; i++)
        {
            uint tex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, tex);

            fixed (byte* p = world.DeluxeMaps[i].Data)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8,
                    128, 128, 0, PixelFormat.Rgb, PixelType.UnsignedByte, p);
            }

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            _deluxeMapTextures[i] = tex;
        }
    }

    /// <summary>
    /// Load cubemap DDS textures and assign cubemaps to world surfaces.
    /// Must be called after LoadWorld. Requires a Renderer2D for texture upload.
    /// </summary>
    public void LoadCubemaps(Renderer2D renderer2D)
    {
        if (_world == null) return;

        int cubeMappingEnabled = EngineImports.Cvar_VariableIntegerValue("r_cubeMapping");
        if (cubeMappingEnabled == 0 || _world.Cubemaps.Length == 0)
        {
            _cubemapTextures = [];
            return;
        }

        // Extract map base name (e.g., "maps/q3dm1.bsp" -> "q3dm1")
        string baseName = _world.Name;
        int lastSlash = baseName.LastIndexOfAny(['/', '\\']);
        if (lastSlash >= 0) baseName = baseName[(lastSlash + 1)..];
        int dotIdx = baseName.LastIndexOf('.');
        if (dotIdx >= 0) baseName = baseName[..dotIdx];

        // Load cubemap DDS textures
        _cubemapTextures = new uint[_world.Cubemaps.Length];
        int loaded = 0;
        for (int i = 0; i < _world.Cubemaps.Length; i++)
        {
            string path = $"cubemaps/{baseName}/{i:D3}";
            var dds = DdsLoader.LoadFromEngineFS(path);
            if (dds != null && dds.IsCubemap)
            {
                _cubemapTextures[i] = renderer2D.CreateCubemapTexture(dds);
                if (_cubemapTextures[i] != 0) loaded++;
            }
        }

        // Assign cubemaps to surfaces (find nearest cubemap for each surface center)
        AssignCubemapsToSurfaces();

        if (loaded > 0)
        {
            EngineImports.Printf(EngineImports.PRINT_ALL,
                $"[.NET] Loaded {loaded}/{_world.Cubemaps.Length} cubemaps for reflections\n");
        }
        else
        {
            EngineImports.Printf(EngineImports.PRINT_ALL,
                $"[.NET] {_world.Cubemaps.Length} cubemap probes found (no DDS files, will use fallback)\n");
        }
    }

    /// <summary>
    /// For each surface, find the nearest cubemap probe and store the index.
    /// Convention: CubemapIndex = -1 means no cubemap.
    /// </summary>
    private void AssignCubemapsToSurfaces()
    {
        if (_world == null || _world.Cubemaps.Length == 0) return;

        var verts = _world.Vertices;
        var cubemaps = _world.Cubemaps;

        for (int si = 0; si < _world.Surfaces.Length; si++)
        {
            ref var surf = ref _world.Surfaces[si];
            if (surf.NumVertices < 1 || surf.SurfaceType == SurfaceTypes.MST_FLARE)
            {
                surf.CubemapIndex = -1;
                continue;
            }

            // Compute surface center from vertex bounds
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            int end = surf.FirstVertex + surf.NumVertices;
            for (int vi = surf.FirstVertex; vi < end; vi++)
            {
                ref var v = ref verts[vi];
                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
            }
            float cx = (minX + maxX) * 0.5f;
            float cy = (minY + maxY) * 0.5f;
            float cz = (minZ + maxZ) * 0.5f;

            // Find nearest cubemap
            float bestDist = float.MaxValue;
            int bestIdx = -1;
            for (int ci = 0; ci < cubemaps.Length; ci++)
            {
                float dx = cx - cubemaps[ci].OriginX;
                float dy = cy - cubemaps[ci].OriginY;
                float dz = cz - cubemaps[ci].OriginZ;
                float distSq = dx * dx + dy * dy + dz * dz;
                if (distSq < bestDist)
                {
                    bestDist = distSq;
                    bestIdx = ci;
                }
            }
            surf.CubemapIndex = bestIdx;
        }
    }

    /// <summary>
    /// Render the world using BSP tree traversal for visibility.
    /// Renders opaque surfaces first, then transparent surfaces in a second pass.
    /// <summary>Set portal texture mappings for this frame.</summary>
    public void SetPortalTextures(Dictionary<int, uint> textures) 
    {
        _portalTextures.Clear();
        foreach (var kv in textures)
            _portalTextures[kv.Key] = kv.Value;
    }

    /// <summary>Clear all portal textures.</summary>
    public void ClearPortalTextures() => _portalTextures.Clear();

    /// <summary>Get all visible portal surface indices from the last frame.</summary>
    public IReadOnlyList<int> GetPortalSurfaceIndices() => _portalSurfIndices;

    /// <summary>
    /// Get the plane of a specific portal surface.
    /// Returns false if the surface is invalid.
    /// </summary>
    public bool GetPortalSurfacePlane(int surfIdx, out float normalX, out float normalY, out float normalZ,
        out float dist, out float centerX, out float centerY, out float centerZ)
    {
        normalX = normalY = normalZ = dist = centerX = centerY = centerZ = 0;
        if (_world == null || surfIdx < 0 || surfIdx >= _world.Surfaces.Length) return false;

        ref var surf = ref _world.Surfaces[surfIdx];
        if (surf.NumVertices < 3) return false;

        // Use BSP-stored face normal for MST_PLANAR (matches Q3's cullPlane)
        if (surf.SurfaceType == SurfaceTypes.MST_PLANAR &&
            (surf.FaceNormalX != 0 || surf.FaceNormalY != 0 || surf.FaceNormalZ != 0))
        {
            normalX = surf.FaceNormalX;
            normalY = surf.FaceNormalY;
            normalZ = surf.FaceNormalZ;
            float len = MathF.Sqrt(normalX * normalX + normalY * normalY + normalZ * normalZ);
            if (len > 0.001f) { normalX /= len; normalY /= len; normalZ /= len; }
            ref var v0n = ref _world.Vertices[surf.FirstVertex];
            dist = normalX * v0n.X + normalY * v0n.Y + normalZ * v0n.Z;
        }
        else
        {
            // Fallback: compute plane from first 3 vertices
            ref var v0 = ref _world.Vertices[surf.FirstVertex];
            ref var v1 = ref _world.Vertices[surf.FirstVertex + 1];
            ref var v2 = ref _world.Vertices[surf.FirstVertex + 2];

            float e1x = v1.X - v0.X, e1y = v1.Y - v0.Y, e1z = v1.Z - v0.Z;
            float e2x = v2.X - v0.X, e2y = v2.Y - v0.Y, e2z = v2.Z - v0.Z;
            normalX = e1y * e2z - e1z * e2y;
            normalY = e1z * e2x - e1x * e2z;
            normalZ = e1x * e2y - e1y * e2x;
            float len = MathF.Sqrt(normalX * normalX + normalY * normalY + normalZ * normalZ);
            if (len < 0.001f) return false;
            normalX /= len; normalY /= len; normalZ /= len;
            dist = normalX * v0.X + normalY * v0.Y + normalZ * v0.Z;
        }

        // Center = average of all vertices
        float cx = 0, cy = 0, cz = 0;
        for (int i = 0; i < surf.NumVertices; i++)
        {
            ref var v = ref _world.Vertices[surf.FirstVertex + i];
            cx += v.X; cy += v.Y; cz += v.Z;
        }
        centerX = cx / surf.NumVertices;
        centerY = cy / surf.NumVertices;
        centerZ = cz / surf.NumVertices;
        return true;
    }

    /// <summary>
    /// Main BSP rendering entry point. Walks the BSP tree, collects visible surfaces,
    /// and renders them in shader-sorted batches.
    /// </summary>
    public void Render(float* mvp, float viewX, float viewY, float viewZ,
                       ShaderManager shaders, float timeSec,
                       List<DLight>? dlights = null)
    {
        if (_world == null) return;

        _frameCount++;
        _transparentSurfaces.Clear();
        _visibleSurfaceIndices.Clear();
        _visibleFlares.Clear();
        _portalSurfIndices.Clear();
        _viewX = viewX;
        _viewY = viewY;
        _viewZ = viewZ;

        // Extract view right/up axes from the VP matrix for autosprite billboarding
        // In column-major layout: row0 = right, row1 = up
        {
            float rx = mvp[0], ry = mvp[4], rz = mvp[8];
            float rlen = MathF.Sqrt(rx*rx + ry*ry + rz*rz);
            if (rlen > 0.001f) { rx /= rlen; ry /= rlen; rz /= rlen; }
            _viewRight[0] = rx; _viewRight[1] = ry; _viewRight[2] = rz;

            float ux = mvp[1], uy = mvp[5], uz = mvp[9];
            float ulen = MathF.Sqrt(ux*ux + uy*uy + uz*uz);
            if (ulen > 0.001f) { ux /= ulen; uy /= ulen; uz /= ulen; }
            _viewUp[0] = ux; _viewUp[1] = uy; _viewUp[2] = uz;
        }

        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_mvpLoc, 1, false, mvp);
        _gl.Uniform4(_colorLoc, 1f, 1f, 1f, 1f);
        _gl.Uniform3(_lightDirLoc, 0.57735f, 0.57735f, 0.57735f);
        _gl.Uniform1(_texDiffuseLoc, 0);
        _gl.Uniform1(_texLightmapLoc, 1);
        _gl.Uniform1(_envMapLoc, 0);
        _gl.Uniform3(_viewPosLoc, viewX, viewY, viewZ);
        _gl.Uniform1(_timeLoc, timeSec);
        _currentTimeSec = timeSec;
        _gl.Uniform1(_tcModCountLoc, 0);
        _gl.Uniform1(_deformTypeLoc, -1);
        _gl.Uniform1(_overbrightScaleLoc, 1.0f);
        _gl.Uniform1(_useLmUVLoc, 0);
        _gl.Uniform1(_alphaGenModeLoc, 0);
        _gl.Uniform1(_constAlphaLoc, 1f);
        _gl.Uniform3(_constColorLoc, 1f, 1f, 1f);
        _gl.Uniform1(_useNormalMapLoc, 0);
        _gl.Uniform1(_useSpecularMapLoc, 0);
        _gl.Uniform1(_portalMapLoc, 0);

        // Read r_greyscale cvar each frame (0 = color, 1 = fully greyscale)
        int gsInt = EngineImports.Cvar_VariableIntegerValue("r_greyscale");
        _greyscaleValue = gsInt != 0 ? 1.0f : 0.0f;
        _gl.Uniform1(_greyscaleLoc, _greyscaleValue);

        // Read parallax mapping cvar
        int parallaxEnabled = EngineImports.Cvar_VariableIntegerValue("r_parallaxMapping");
        _gl.Uniform1(_useParallaxLoc, parallaxEnabled);
        _gl.Uniform1(_parallaxScaleLoc, 0.05f);

        // Deluxe mapping (set per-frame, actual binding per-surface)
        int deluxeEnabled = _hasDeluxeMapping ? EngineImports.Cvar_VariableIntegerValue("r_deluxeMapping") : 0;
        _gl.Uniform1(_useDeluxeMapLoc, deluxeEnabled != 0 ? 1 : 0);

        // PBR mode (metallic/roughness workflow)
        int pbrEnabled = EngineImports.Cvar_VariableIntegerValue("r_pbr");
        _gl.Uniform1(_usePBRLoc, pbrEnabled);

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

        // Walk BSP tree and collect visible surfaces
        // (opaque surfaces are deferred for shader-sorted batching)
        _deferredOpaque.Clear();
        WalkBspTree(0, cameraCluster, shaders);

        // Record diagnostic counts
        LastDeferredOpaqueCount = _deferredOpaque.Count;
        LastTransparentCount = _transparentSurfaces.Count;

        // Batch-draw opaque surfaces sorted by shader to minimize state changes
        if (_deferredOpaque.Count > 0)
        {
            DrawDeferredOpaque(shaders);
        }

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
                DrawSurfaceGeometry(ref surf, shaders, 0, isTransparentPass: true);
            }

            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        // Dynamic lighting pass: redraw visible surfaces with additive light
        if (dlights != null && dlights.Count > 0 && _visibleSurfaceIndices.Count > 0)
        {
            RenderDlights(mvp, dlights);
        }

        // Fog pass: apply fog to surfaces inside fog volumes
        if (_world.Fogs.Length > 0 && _visibleSurfaceIndices.Count > 0)
        {
            RenderFog(mvp, viewX, viewY, viewZ);
        }

        _gl.BindVertexArray(0);

        // Flare pass: render light flares as screen-space billboards
        if (_visibleFlares.Count > 0)
        {
            RenderFlares(mvp, viewX, viewY, viewZ);
        }
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
        _currentTimeSec = timeSec;
        _gl.Uniform1(_tcModCountLoc, 0);
        _gl.Uniform1(_deformTypeLoc, -1);
        _gl.Uniform1(_overbrightScaleLoc, 1.0f);
        _gl.Uniform1(_useLmUVLoc, 0);
        _gl.Uniform1(_alphaGenModeLoc, 0);
        _gl.Uniform1(_constAlphaLoc, 1f);
        _gl.Uniform3(_constColorLoc, 1f, 1f, 1f);
        _gl.Uniform1(_useNormalMapLoc, 0);
        _gl.Uniform1(_useSpecularMapLoc, 0);

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
                DrawSurfaceGeometry(ref surf, shaders, 0, isTransparentPass: true);
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
        // Collect flare surfaces for later rendering as billboards
        if (surf.SurfaceType == SurfaceTypes.MST_FLARE)
        {
            _visibleFlares.Add((surf.FlareOriginX, surf.FlareOriginY, surf.FlareOriginZ,
                surf.FlareColorR, surf.FlareColorG, surf.FlareColorB,
                surf.FlareNormalX, surf.FlareNormalY, surf.FlareNormalZ));
            return;
        }

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

            // Portal/mirror surfaces (sort key 1): use portal FBO texture if available,
            // fall back to environment mapping if no portal render was performed.
            int sortKey0 = shaders.GetSortKey(surf.ShaderHandle);
            if (sortKey0 == 1)
            {
                // Record all portal surfaces for the next frame's portal render
                if (!_portalSurfIndices.Contains(surfIdx))
                    _portalSurfIndices.Add(surfIdx);

                if (_portalTextures.TryGetValue(surfIdx, out uint portalTex) && portalTex != 0)
                    DrawSurfacePortalFbo(ref surf, shaders, portalTex);
                else
                    DrawSurfacePortal(ref surf, shaders);
                return;
            }

            // Surfaces with alphaFunc (alpha testing) need unique state, draw immediately
            int alphaFunc = shaders.GetAlphaFunc(surf.ShaderHandle);
            if (alphaFunc != 0)
            {
                _deferredOpaque.Add((surfIdx, surf.ShaderHandle, surf.LightmapIndex, alphaFunc));
                _visibleSurfaceIndices.Add(surfIdx);
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
                // If surface is transparent but has no explicit blend mode, default to alpha blending
                if (!blend.NeedsBlending)
                    blend = BlendMode.Alpha;
                int sortKey = shaders.GetSortKey(surf.ShaderHandle);
                if (sortKey == 0) sortKey = isTrans ? 8 : 5; // trans=blend, non-trans=seeThrough
                _transparentSurfaces.Add((surfIdx, blend, sortKey));
                return;
            }
        }

        // Defer regular opaque surface for batched rendering
        _deferredOpaque.Add((surfIdx, surf.ShaderHandle, surf.LightmapIndex, 0));
        _visibleSurfaceIndices.Add(surfIdx);
    }

    private const int CONTENTS_TRANSLUCENT = 0x20000000;

    /// <summary>
    /// Sort deferred opaque surfaces by shader+lightmap and draw in batches.
    /// Surfaces with the same shader and lightmap share state, so we bind once
    /// and issue multiple draws without rebinding. Multi-stage shaders or surfaces
    /// with special state (deforms, polygon offset) draw individually.
    /// </summary>
    private void DrawDeferredOpaque(ShaderManager shaders)
    {
        if (_world == null) return;

        // Sort by shader handle, then by lightmap index for maximum batching
        _deferredOpaque.Sort((a, b) =>
        {
            int cmp = a.ShaderHandle.CompareTo(b.ShaderHandle);
            if (cmp != 0) return cmp;
            cmp = a.LmIdx.CompareTo(b.LmIdx);
            if (cmp != 0) return cmp;
            return a.AlphaFunc.CompareTo(b.AlphaFunc);
        });

        int lastShader = -1;
        int lastLm = -2;
        int lastAlpha = -1;
        bool lastMultiStage = false;

        for (int i = 0; i < _deferredOpaque.Count; i++)
        {
            var (surfIdx, shaderHandle, lmIdx, alphaFunc) = _deferredOpaque[i];
            ref var surf = ref _world.Surfaces[surfIdx];

            // Check if this surface needs multi-stage rendering
            var stages = shaders.GetStages(shaderHandle);
            bool isMultiStage = stages != null && stages.Length > 1;

            // Multi-stage surfaces always draw individually (complex state per stage)
            if (isMultiStage)
            {
                DrawSurfaceMultiStage(ref surf, shaders, stages!, alphaFunc, false);
                lastShader = -1; // force rebind on next surface
                continue;
            }

            // Check if we can skip rebinding state (same shader + lightmap + alphaFunc)
            bool sameState = (shaderHandle == lastShader && lmIdx == lastLm
                              && alphaFunc == lastAlpha && !lastMultiStage);

            if (!sameState)
            {
                // Bind new shader state
                BindSingleStageState(ref surf, shaders, alphaFunc);
                lastShader = shaderHandle;
                lastLm = lmIdx;
                lastAlpha = alphaFunc;
                lastMultiStage = false;
            }

            // Issue draw (geometry-only since state is already bound)
            _gl.DrawElementsBaseVertex(PrimitiveType.Triangles,
                (uint)surf.NumIndices, DrawElementsType.UnsignedInt,
                (void*)(surf.FirstIndex * sizeof(int)),
                surf.FirstVertex);
        }

        // Restore default state after batch
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Front);
        _gl.Uniform1(_deformTypeLoc, -1);
    }

    /// <summary>
    /// Bind all GL state for a single-stage surface (textures, uniforms, cull, etc.)
    /// without issuing a draw call. Used by the batching system.
    /// </summary>
    private void BindSingleStageState(ref BspSurface surf, ShaderManager shaders, int alphaFunc)
    {
        uint texId = shaders.GetTextureId(surf.ShaderHandle);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texId);

        bool hasLightmap = surf.LightmapIndex >= 0 && surf.LightmapIndex < _lightmapTextures.Length;
        _gl.Uniform1(_useLightmapLoc, hasLightmap ? 1 : 0);
        if (hasLightmap)
        {
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _lightmapTextures[surf.LightmapIndex]);

            if (_hasDeluxeMapping && surf.LightmapIndex < _deluxeMapTextures.Length)
            {
                _gl.ActiveTexture(TextureUnit.Texture4);
                _gl.BindTexture(TextureTarget.Texture2D, _deluxeMapTextures[surf.LightmapIndex]);
            }
        }

        uint normalTexId = shaders.GetNormalMapTexId(surf.ShaderHandle);
        _gl.Uniform1(_useNormalMapLoc, normalTexId != 0 ? 1 : 0);
        if (normalTexId != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, normalTexId);
        }

        uint specTexId = shaders.GetSpecularMapTexId(surf.ShaderHandle);
        _gl.Uniform1(_useSpecularMapLoc, specTexId != 0 ? 1 : 0);
        if (specTexId != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture3);
            _gl.BindTexture(TextureTarget.Texture2D, specTexId);
        }

        _gl.Uniform1(_alphaFuncLoc, alphaFunc);

        int rgbGen = shaders.GetRgbGen(surf.ShaderHandle);
        if (rgbGen == 0) rgbGen = 8; // default to NDL fallback
        _gl.Uniform1(_rgbGenLoc, rgbGen);
        _gl.Uniform1(_alphaGenModeLoc, 0);

        bool envMap = shaders.GetHasEnvMap(surf.ShaderHandle);
        _gl.Uniform1(_envMapLoc, envMap ? 1 : 0);

        SetTcModUniforms(shaders.GetTcMods(surf.ShaderHandle));

        int cullMode = shaders.GetCullMode(surf.ShaderHandle);
        ApplyCullMode(cullMode);

        bool polyOffset = shaders.GetPolygonOffset(surf.ShaderHandle);
        if (polyOffset)
        {
            _gl.Enable(EnableCap.PolygonOffsetFill);
            _gl.PolygonOffset(-1f, -1f);
        }
        else
        {
            _gl.Disable(EnableCap.PolygonOffsetFill);
        }

        int depthFuncVal = shaders.GetDepthFunc(surf.ShaderHandle);
        _gl.DepthFunc(depthFuncVal == 1 ? DepthFunction.Equal : DepthFunction.Lequal);

        ApplyDeforms(shaders.GetDeforms(surf.ShaderHandle));
    }

    /// <summary>
    /// Render a portal/mirror surface using the portal FBO texture with screen-space UVs.
    /// </summary>
    private void DrawSurfacePortalFbo(ref BspSurface surf, ShaderManager shaders, uint portalTex)
    {
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, portalTex);

        _gl.Uniform1(_useLightmapLoc, 0);
        _gl.Uniform1(_alphaFuncLoc, 0);
        _gl.Uniform1(_rgbGenLoc, 0); // identity
        _gl.Uniform1(_envMapLoc, 0);
        _gl.Uniform1(_useNormalMapLoc, 0);
        _gl.Uniform1(_useSpecularMapLoc, 0);
        SetTcModUniforms(null);
        _gl.Uniform1(_deformTypeLoc, -1);

        // Enable portal map mode — shader will use gl_FragCoord for UVs
        _gl.Uniform1(_portalMapLoc, 1);
        Span<int> vp = stackalloc int[4];
        _gl.GetInteger(GLEnum.Viewport, vp);
        _gl.Uniform2(_screenSizeLoc, (float)vp[2], (float)vp[3]);

        int cullMode = shaders.GetCullMode(surf.ShaderHandle);
        ApplyCullMode(cullMode);

        _gl.DrawElementsBaseVertex(PrimitiveType.Triangles,
            (uint)surf.NumIndices, DrawElementsType.UnsignedInt,
            (void*)(surf.FirstIndex * sizeof(int)),
            surf.FirstVertex);

        RestoreCullMode(cullMode);

        // Disable portal map mode
        _gl.Uniform1(_portalMapLoc, 0);
    }

    /// <summary>
    /// Render a portal/mirror surface with environment mapping as an approximation.
    /// True portal rendering requires FBO-based scene re-rendering; this provides
    /// a reflective surface instead of a black screen.
    /// </summary>
    private void DrawSurfacePortal(ref BspSurface surf, ShaderManager shaders)
    {
        uint texId = shaders.GetTextureId(surf.ShaderHandle);
        if (texId == 0) texId = shaders.WhiteTexture;
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texId);

        _gl.Uniform1(_useLightmapLoc, 0);
        _gl.Uniform1(_alphaFuncLoc, 0);
        _gl.Uniform1(_rgbGenLoc, 8); // NDL fallback
        _gl.Uniform1(_envMapLoc, 1);
        SetTcModUniforms(null);
        _gl.Uniform1(_deformTypeLoc, -1);

        int cullMode = shaders.GetCullMode(surf.ShaderHandle);
        ApplyCullMode(cullMode);

        _gl.DrawElementsBaseVertex(PrimitiveType.Triangles,
            (uint)surf.NumIndices, DrawElementsType.UnsignedInt,
            (void*)(surf.FirstIndex * sizeof(int)),
            surf.FirstVertex);

        RestoreCullMode(cullMode);
        _gl.Uniform1(_envMapLoc, 0);
    }

    private void DrawSurfaceGeometry(ref BspSurface surf, ShaderManager shaders, int alphaFunc,
        bool isTransparentPass = false)
    {
        // Check for autosprite deform — needs CPU-side billboarding
        var deforms = shaders.GetDeforms(surf.ShaderHandle);
        if (HasAutoSprite(deforms))
        {
            bool isAS2 = false;
            if (deforms != null)
                foreach (var d in deforms)
                    if (d.Type == DeformType.AutoSprite2) { isAS2 = true; break; }
            DrawAutoSpriteSurface(ref surf, shaders, alphaFunc, isTransparentPass, isAS2);
            return;
        }

        // Check for multi-stage rendering
        var stages = shaders.GetStages(surf.ShaderHandle);
        if (stages != null && stages.Length > 1)
        {
            DrawSurfaceMultiStage(ref surf, shaders, stages, alphaFunc, isTransparentPass);
            return;
        }

        // Single-stage path (fallback)
        DrawSurfaceSingleStage(ref surf, shaders, alphaFunc);
    }

    private void DrawSurfaceSingleStage(ref BspSurface surf, ShaderManager shaders, int alphaFunc)
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

            // Bind deluxe map alongside lightmap (same index)
            if (_hasDeluxeMapping && surf.LightmapIndex < _deluxeMapTextures.Length)
            {
                _gl.ActiveTexture(TextureUnit.Texture4);
                _gl.BindTexture(TextureTarget.Texture2D, _deluxeMapTextures[surf.LightmapIndex]);
            }
        }

        // Bind normal map if available
        uint normalTexId = shaders.GetNormalMapTexId(surf.ShaderHandle);
        bool hasNormalMap = normalTexId != 0;
        _gl.Uniform1(_useNormalMapLoc, hasNormalMap ? 1 : 0);
        if (hasNormalMap)
        {
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, normalTexId);
        }

        // Bind specular map if available
        uint specTexId = shaders.GetSpecularMapTexId(surf.ShaderHandle);
        bool hasSpecMap = specTexId != 0;
        _gl.Uniform1(_useSpecularMapLoc, hasSpecMap ? 1 : 0);
        if (hasSpecMap)
        {
            _gl.ActiveTexture(TextureUnit.Texture3);
            _gl.BindTexture(TextureTarget.Texture2D, specTexId);
        }

        // Bind cubemap if available for this surface
        bool hasCubeMap = surf.CubemapIndex >= 0 && surf.CubemapIndex < _cubemapTextures.Length
            && _cubemapTextures[surf.CubemapIndex] != 0;
        _gl.Uniform1(_useCubeMapLoc, hasCubeMap ? 1 : 0);
        if (hasCubeMap)
        {
            _gl.ActiveTexture(TextureUnit.Texture5);
            _gl.BindTexture(TextureTarget.TextureCubeMap, _cubemapTextures[surf.CubemapIndex]);
            ref var cm = ref _world!.Cubemaps[surf.CubemapIndex];
            float invRadius = cm.ParallaxRadius > 0 ? 1f / cm.ParallaxRadius : 0.001f;
            _gl.Uniform4(_cubeMapInfoLoc,
                cm.OriginX - _viewX, cm.OriginY - _viewY, cm.OriginZ - _viewZ, invRadius);
        }

        // Set alpha test mode
        _gl.Uniform1(_alphaFuncLoc, alphaFunc);

        // Per-surface rgbGen — use 8 (NDL fallback) for single-stage when no explicit rgbGen
        int rgbGen = shaders.GetRgbGen(surf.ShaderHandle);
        if (rgbGen == 0) rgbGen = 8; // default to NDL fallback for single-stage
        _gl.Uniform1(_rgbGenLoc, rgbGen);
        _gl.Uniform1(_alphaGenModeLoc, 0);

        // Per-surface environment mapping
        bool envMap = shaders.GetHasEnvMap(surf.ShaderHandle);
        _gl.Uniform1(_envMapLoc, envMap ? 1 : 0);

        // Per-surface tcMod
        SetTcModUniforms(shaders.GetTcMods(surf.ShaderHandle));

        // Per-surface cull mode
        int cullMode = shaders.GetCullMode(surf.ShaderHandle);
        ApplyCullMode(cullMode);

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
        bool hasDeform = ApplyDeforms(shaders.GetDeforms(surf.ShaderHandle));

        _gl.DrawElementsBaseVertex(PrimitiveType.Triangles,
            (uint)surf.NumIndices, DrawElementsType.UnsignedInt,
            (void*)(surf.FirstIndex * sizeof(int)),
            surf.FirstVertex);

        // Restore default state
        RestoreCullMode(cullMode);
        if (polyOffset)
            _gl.Disable(EnableCap.PolygonOffsetFill);
        if (depthFuncVal != 0)
            _gl.DepthFunc(DepthFunction.Lequal);
        if (hasDeform)
            _gl.Uniform1(_deformTypeLoc, -1);
        if (hasNormalMap)
            _gl.Uniform1(_useNormalMapLoc, 0);
        if (hasSpecMap)
            _gl.Uniform1(_useSpecularMapLoc, 0);
    }

    /// <summary>
    /// Multi-stage rendering: draw the same geometry once per stage with
    /// different textures, blend modes, and state. Each stage composites
    /// on top of the previous one (lightmap × texture, glow overlays, etc.)
    /// </summary>
    private void DrawSurfaceMultiStage(ref BspSurface surf, ShaderManager shaders,
        ShaderManager.RuntimeStage[] stages, int alphaFunc, bool isTransparentPass)
    {
        // Per-surface state (shared across stages)
        int cullMode = shaders.GetCullMode(surf.ShaderHandle);
        ApplyCullMode(cullMode);

        bool polyOffset = shaders.GetPolygonOffset(surf.ShaderHandle);
        if (polyOffset)
        {
            _gl.Enable(EnableCap.PolygonOffsetFill);
            _gl.PolygonOffset(-1f, -1f);
        }

        bool hasDeform = ApplyDeforms(shaders.GetDeforms(surf.ShaderHandle));

        // Bind normal/specular maps for multi-stage (shared across all stages)
        uint normalTexId = shaders.GetNormalMapTexId(surf.ShaderHandle);
        bool hasNormalMap = normalTexId != 0;
        _gl.Uniform1(_useNormalMapLoc, hasNormalMap ? 1 : 0);
        if (hasNormalMap)
        {
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, normalTexId);
        }

        uint specTexId = shaders.GetSpecularMapTexId(surf.ShaderHandle);
        bool hasSpecMap = specTexId != 0;
        _gl.Uniform1(_useSpecularMapLoc, hasSpecMap ? 1 : 0);
        if (hasSpecMap)
        {
            _gl.ActiveTexture(TextureUnit.Texture3);
            _gl.BindTexture(TextureTarget.Texture2D, specTexId);
        }

        // Bind deluxe map for multi-stage
        if (_hasDeluxeMapping && surf.LightmapIndex >= 0 && surf.LightmapIndex < _deluxeMapTextures.Length)
        {
            _gl.ActiveTexture(TextureUnit.Texture4);
            _gl.BindTexture(TextureTarget.Texture2D, _deluxeMapTextures[surf.LightmapIndex]);
        }

        // Bind cubemap for multi-stage
        bool hasCubeMap = surf.CubemapIndex >= 0 && surf.CubemapIndex < _cubemapTextures.Length
            && _cubemapTextures[surf.CubemapIndex] != 0;
        _gl.Uniform1(_useCubeMapLoc, hasCubeMap ? 1 : 0);
        if (hasCubeMap)
        {
            _gl.ActiveTexture(TextureUnit.Texture5);
            _gl.BindTexture(TextureTarget.TextureCubeMap, _cubemapTextures[surf.CubemapIndex]);
            ref var cm = ref _world!.Cubemaps[surf.CubemapIndex];
            float invRadius = cm.ParallaxRadius > 0 ? 1f / cm.ParallaxRadius : 0.001f;
            _gl.Uniform4(_cubeMapInfoLoc,
                cm.OriginX - _viewX, cm.OriginY - _viewY, cm.OriginZ - _viewZ, invRadius);
        }

        float timeSec = _currentTimeSec;

        for (int si = 0; si < stages.Length; si++)
        {
            var stage = stages[si];

            // Bind texture for this stage
            if (stage.IsLightmap)
            {
                // Lightmap stage: bind lightmap texture as diffuse, use lightmap UVs
                if (surf.LightmapIndex >= 0 && surf.LightmapIndex < _lightmapTextures.Length)
                {
                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, _lightmapTextures[surf.LightmapIndex]);
                }
                else
                {
                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, shaders.WhiteTexture);
                }
                _gl.Uniform1(_useLightmapLoc, 0);
                _gl.Uniform1(_useLmUVLoc, 1); // sample with lightmap UVs
                // Overbright is baked into lightmap bytes during loading
                _gl.Uniform4(_colorLoc, 1f, 1f, 1f, 1f);
            }
            else
            {
                // Check for cinematic (videoMap) stage
                if (stage.VideoMapHandle >= 0)
                {
                    // Advance cinematic and upload frame to scratch texture
                    EngineImports.CIN_RunCinematic(stage.VideoMapHandle);
                    EngineImports.CIN_UploadCinematic(stage.VideoMapHandle);
                    uint scratchTex = RendererExports.GetScratchTexture(stage.VideoMapHandle);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, scratchTex != 0 ? scratchTex : shaders.WhiteTexture);
                }
                else
                {
                    // Regular texture stage
                    uint stageTexId = shaders.GetStageTextureId(stage, timeSec);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, stageTexId);
                }
                _gl.Uniform1(_useLightmapLoc, 0);
                _gl.Uniform1(_useLmUVLoc, 0); // sample with regular UVs
                _gl.Uniform4(_colorLoc, 1f, 1f, 1f, 1f);
            }

            // Per-stage alpha test (only first stage typically)
            _gl.Uniform1(_alphaFuncLoc, si == 0 ? alphaFunc : stage.AlphaFunc);

            // Per-stage rgbGen
            _gl.Uniform1(_rgbGenLoc, stage.RgbGen);
            _gl.Uniform1(_alphaGenModeLoc, stage.AlphaGen);

            // Per-stage wave/const params
            if (stage.RgbGen == 3) // wave
                _gl.Uniform4(_rgbWaveLoc, (float)stage.WaveFunc, stage.WaveBase, stage.WaveAmp, stage.WaveFreq);
            _gl.Uniform1(_rgbWavePhaseLoc, stage.WavePhase);
            if (stage.RgbGen == 5) // const
                _gl.Uniform3(_constColorLoc, stage.ConstR, stage.ConstG, stage.ConstB);
            if (stage.AlphaGen == 3) // alpha wave
                _gl.Uniform4(_alphaWaveLoc, (float)stage.AlphaWaveFunc, stage.AlphaWaveBase, stage.AlphaWaveAmp, stage.AlphaWaveFreq);
            _gl.Uniform1(_alphaWavePhaseLoc, stage.AlphaWavePhase);
            if (stage.AlphaGen == 4) // alpha const
                _gl.Uniform1(_constAlphaLoc, stage.ConstAlpha);

            // Per-stage env map
            _gl.Uniform1(_envMapLoc, stage.HasEnvMap ? 1 : 0);

            // Per-stage tcMod
            SetTcModUniforms(stage.TcMods);

            // Per-stage blend and depth handling
            if (isTransparentPass)
            {
                // Transparent pass: blend already on, depth mask already off
                _gl.BlendFunc((BlendingFactor)stage.Blend.SrcFactor,
                              (BlendingFactor)stage.Blend.DstFactor);
            }
            else if (si == 0)
            {
                // First stage in opaque pass
                if (stage.Blend.NeedsBlending)
                {
                    _gl.Enable(EnableCap.Blend);
                    _gl.BlendFunc((BlendingFactor)stage.Blend.SrcFactor,
                                  (BlendingFactor)stage.Blend.DstFactor);
                }

                if (stage.DepthFunc == 1)
                    _gl.DepthFunc(DepthFunction.Equal);
            }
            else
            {
                // Subsequent stages in opaque pass: blend on top
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc((BlendingFactor)stage.Blend.SrcFactor,
                              (BlendingFactor)stage.Blend.DstFactor);
                // Respect per-stage depthWrite (default off for later stages)
                _gl.DepthMask(stage.DepthWrite);

                if (stage.DepthFunc == 1)
                    _gl.DepthFunc(DepthFunction.Equal);
                else
                    _gl.DepthFunc(DepthFunction.Lequal);
            }

            _gl.DrawElementsBaseVertex(PrimitiveType.Triangles,
                (uint)surf.NumIndices, DrawElementsType.UnsignedInt,
                (void*)(surf.FirstIndex * sizeof(int)),
                surf.FirstVertex);

            // Restore per-stage state
            if (!isTransparentPass && si > 0)
            {
                _gl.DepthMask(true);
            }
            if (!isTransparentPass && stage.Blend.NeedsBlending && si == 0)
            {
                _gl.Disable(EnableCap.Blend);
            }
            if (stage.DepthFunc != 0)
                _gl.DepthFunc(DepthFunction.Lequal);
        }

        // Restore state
        if (!isTransparentPass)
            _gl.Disable(EnableCap.Blend);

        // Restore uColor, uUseLmUV, and alphaGen/constColor to defaults
        _gl.Uniform4(_colorLoc, 1f, 1f, 1f, 1f);
        _gl.Uniform1(_useLmUVLoc, 0);
        _gl.Uniform1(_alphaGenModeLoc, 0);
        _gl.Uniform1(_constAlphaLoc, 1f);
        _gl.Uniform3(_constColorLoc, 1f, 1f, 1f);

        // Restore shared state
        RestoreCullMode(cullMode);
        if (polyOffset)
            _gl.Disable(EnableCap.PolygonOffsetFill);
        if (hasDeform)
            _gl.Uniform1(_deformTypeLoc, -1);
        if (hasNormalMap)
            _gl.Uniform1(_useNormalMapLoc, 0);
        if (hasSpecMap)
            _gl.Uniform1(_useSpecularMapLoc, 0);
    }

    /// <summary>Set tcMod uniforms from an array of TcMod operations.</summary>
    private void SetTcModUniforms(TcMod[]? tcMods)
    {
        if (tcMods != null && tcMods.Length > 0)
        {
            int count = Math.Min(tcMods.Length, 4);
            _gl.Uniform1(_tcModCountLoc, count);
            for (int i = 0; i < count; i++)
            {
                ref var m = ref tcMods[i];
                switch (m.Type)
                {
                    case TcModType.Scroll:
                        _gl.Uniform4(_tcModLoc + i, 0f, m.Param0, m.Param1, 0f);
                        _gl.Uniform4(_tcModExtraLoc + i, 0f, 0f, 0f, 0f);
                        break;
                    case TcModType.Scale:
                        _gl.Uniform4(_tcModLoc + i, 1f, m.Param0, m.Param1, 0f);
                        _gl.Uniform4(_tcModExtraLoc + i, 0f, 0f, 0f, 0f);
                        break;
                    case TcModType.Rotate:
                        _gl.Uniform4(_tcModLoc + i, 2f, m.Param0, 0f, 0f);
                        _gl.Uniform4(_tcModExtraLoc + i, 0f, 0f, 0f, 0f);
                        break;
                    case TcModType.Turb:
                        _gl.Uniform4(_tcModLoc + i, 3f, m.Param3, m.Param1, m.Param2);
                        _gl.Uniform4(_tcModExtraLoc + i, 0f, 0f, 0f, 0f);
                        break;
                    case TcModType.Stretch:
                        // Param0=waveFunc, Param1=base, Param2=amp, Param3=phase, Param4=freq
                        _gl.Uniform4(_tcModLoc + i, 4f, m.Param0, m.Param1, m.Param2);
                        _gl.Uniform4(_tcModExtraLoc + i, m.Param3, m.Param4, 0f, 0f);
                        break;
                    case TcModType.Transform:
                        // Param0=m00, Param1=m01, Param2=m10, Param3=m11, Param4=t0, Param5=t1
                        _gl.Uniform4(_tcModLoc + i, 5f, m.Param0, m.Param1, m.Param2);
                        _gl.Uniform4(_tcModExtraLoc + i, m.Param3, m.Param4, m.Param5, 0f);
                        break;
                    default:
                        _gl.Uniform4(_tcModLoc + i, -1f, 0f, 0f, 0f);
                        _gl.Uniform4(_tcModExtraLoc + i, 0f, 0f, 0f, 0f);
                        break;
                }
            }
        }
        else
        {
            _gl.Uniform1(_tcModCountLoc, 0);
        }
    }

    private void ApplyCullMode(int cullMode)
    {
        if (cullMode == 2) // none/twosided
            _gl.Disable(EnableCap.CullFace);
        else if (cullMode == 1) // back
            _gl.CullFace(TriangleFace.Back);
    }

    private void RestoreCullMode(int cullMode)
    {
        if (cullMode != 0)
        {
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(TriangleFace.Front);
        }
    }

    private bool ApplyDeforms(DeformVertexes[]? deforms)
    {
        if (deforms == null || deforms.Length == 0)
            return false;

        for (int d = 0; d < deforms.Length; d++)
        {
            var df = deforms[d];
            if (df.Type == DeformType.Wave)
            {
                _gl.Uniform1(_deformTypeLoc, 0);
                _gl.Uniform4(_deformParams0Loc, df.Param0, df.Param1, df.Param2, df.Param3);
                _gl.Uniform4(_deformParams1Loc, df.Param4, df.Param5, 0f, 0f);
                return true;
            }
            else if (df.Type == DeformType.Move)
            {
                _gl.Uniform1(_deformTypeLoc, 1);
                _gl.Uniform4(_deformParams0Loc, df.Param0, df.Param1, df.Param2, df.Param4);
                _gl.Uniform4(_deformParams1Loc, df.Param5, df.Param6, df.Param7, df.Param8);
                return true;
            }
            else if (df.Type == DeformType.Bulge)
            {
                _gl.Uniform1(_deformTypeLoc, 2);
                _gl.Uniform4(_deformParams0Loc, df.Param0, df.Param1, df.Param2, 0f);
                return true;
            }
            else if (df.Type == DeformType.Normal)
            {
                _gl.Uniform1(_deformTypeLoc, 3);
                _gl.Uniform4(_deformParams0Loc, df.Param0, df.Param1, 0f, 0f);
                return true;
            }
        }
        return false;
    }

    /// <summary>Check if deforms include AutoSprite or AutoSprite2.</summary>
    private static bool HasAutoSprite(DeformVertexes[]? deforms)
    {
        if (deforms == null) return false;
        foreach (var d in deforms)
            if (d.Type is DeformType.AutoSprite or DeformType.AutoSprite2)
                return true;
        return false;
    }

    /// <summary>
    /// CPU-side autosprite deform: rebuild quads to face the camera.
    /// Reads vertices from the BSP world data, applies billboard transform,
    /// uploads to dynamic VBO, and draws.
    /// </summary>
    private void DrawAutoSpriteSurface(ref BspSurface surf, ShaderManager shaders,
                                        int alphaFunc, bool isTransparentPass, bool isAutoSprite2)
    {
        if (_world == null) return;

        int numVerts = surf.NumVertices;
        int numIndices = surf.NumIndices;
        if (numVerts < 4 || numIndices < 6) return;

        // View axes for billboarding
        // viewRight = cross(viewForward, worldUp), but we use the stored view position
        // For autosprite, we need the camera right and up vectors.
        // We reconstruct them from the MVP matrix (columns of the view rotation).
        // Actually, we have the view position. The right/up are the standard Q3 axes.
        // For world entity, use the view axes directly.
        float rightX = _viewRight[0], rightY = _viewRight[1], rightZ = _viewRight[2];
        float upX = _viewUp[0], upY = _viewUp[1], upZ = _viewUp[2];

        // Ensure buffer size
        int neededVerts = numVerts * FLOATS_PER_VERT;
        if (_autospVerts.Length < neededVerts)
            _autospVerts = new float[neededVerts];

        // Copy original vertex data
        for (int i = 0; i < numVerts; i++)
        {
            ref var v = ref _world.Vertices[surf.FirstVertex + i];
            int o = i * FLOATS_PER_VERT;
            _autospVerts[o] = v.X; _autospVerts[o+1] = v.Y; _autospVerts[o+2] = v.Z;
            _autospVerts[o+3] = v.NX; _autospVerts[o+4] = v.NY; _autospVerts[o+5] = v.NZ;
            _autospVerts[o+6] = v.U; _autospVerts[o+7] = v.V;
            _autospVerts[o+8] = v.LmU; _autospVerts[o+9] = v.LmV;
            _autospVerts[o+10] = v.R / 255f; _autospVerts[o+11] = v.G / 255f;
            _autospVerts[o+12] = v.B / 255f; _autospVerts[o+13] = v.A / 255f;
            _autospVerts[o+14] = v.TX; _autospVerts[o+15] = v.TY;
            _autospVerts[o+16] = v.TZ; _autospVerts[o+17] = v.TS;
        }

        // Apply autosprite: process quads (4 verts each)
        int numQuads = numVerts / 4;
        for (int q = 0; q < numQuads; q++)
        {
            int qi = q * 4;
            int o0 = qi * FLOATS_PER_VERT;
            int o1 = (qi+1) * FLOATS_PER_VERT;
            int o2 = (qi+2) * FLOATS_PER_VERT;
            int o3 = (qi+3) * FLOATS_PER_VERT;

            // Compute quad center
            float cx = 0.25f * (_autospVerts[o0] + _autospVerts[o1] + _autospVerts[o2] + _autospVerts[o3]);
            float cy = 0.25f * (_autospVerts[o0+1] + _autospVerts[o1+1] + _autospVerts[o2+1] + _autospVerts[o3+1]);
            float cz = 0.25f * (_autospVerts[o0+2] + _autospVerts[o1+2] + _autospVerts[o2+2] + _autospVerts[o3+2]);

            // Compute radius (distance from center to first vert * 0.707)
            float dx = _autospVerts[o0] - cx;
            float dy = _autospVerts[o0+1] - cy;
            float dz = _autospVerts[o0+2] - cz;
            float radius = MathF.Sqrt(dx*dx + dy*dy + dz*dz) * 0.707f;

            // Rebuild quad facing camera: center ± radius * (right ± up)
            float lx = rightX * radius, ly = rightY * radius, lz = rightZ * radius;
            float ux = upX * radius, uy = upY * radius, uz = upZ * radius;

            // v0 = center - left - up (bottom-left)
            _autospVerts[o0] = cx - lx - ux;
            _autospVerts[o0+1] = cy - ly - uy;
            _autospVerts[o0+2] = cz - lz - uz;
            // v1 = center + left - up (bottom-right)
            _autospVerts[o1] = cx + lx - ux;
            _autospVerts[o1+1] = cy + ly - uy;
            _autospVerts[o1+2] = cz + lz - uz;
            // v2 = center + left + up (top-right)
            _autospVerts[o2] = cx + lx + ux;
            _autospVerts[o2+1] = cy + ly + uy;
            _autospVerts[o2+2] = cz + lz + uz;
            // v3 = center - left + up (top-left)
            _autospVerts[o3] = cx - lx + ux;
            _autospVerts[o3+1] = cy - ly + uy;
            _autospVerts[o3+2] = cz - lz + uz;
        }

        // Build indices (relative to 0 since we upload separate VBO)
        int neededIdx = numIndices;
        if (_autospIndices.Length < neededIdx)
            _autospIndices = new uint[neededIdx];
        for (int i = 0; i < numIndices; i++)
            _autospIndices[i] = (uint)(_world.Indices[surf.FirstIndex + i] - surf.FirstVertex);

        // Upload to dynamic VBO
        _gl.BindVertexArray(_autospVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _autospVbo);
        fixed (float* p = _autospVerts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(numVerts * FLOATS_PER_VERT * sizeof(float)), p, BufferUsageARB.StreamDraw);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _autospEbo);
        fixed (uint* p = _autospIndices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(numIndices * sizeof(uint)), p, BufferUsageARB.StreamDraw);

        // Bind texture
        uint texId = shaders.GetTextureId(surf.ShaderHandle);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texId);

        // Set shader state (same as regular surface)
        int rgbGen = shaders.GetRgbGen(surf.ShaderHandle);
        if (rgbGen == 0) rgbGen = 8;
        _gl.Uniform1(_rgbGenLoc, rgbGen);
        _gl.Uniform1(_alphaGenModeLoc, 0);
        _gl.Uniform1(_alphaFuncLoc, alphaFunc);
        _gl.Uniform1(_envMapLoc, 0);
        _gl.Uniform1(_useLightmapLoc, 0);
        _gl.Uniform1(_useLmUVLoc, 0);
        _gl.Uniform1(_deformTypeLoc, -1); // no GPU deform, CPU already applied
        SetTcModUniforms(shaders.GetTcMods(surf.ShaderHandle));

        int cullMode = shaders.GetCullMode(surf.ShaderHandle);
        ApplyCullMode(cullMode);

        // Apply blend mode (autosprite surfaces are often additive/blended)
        BlendMode blend = shaders.GetBlendMode(surf.ShaderHandle);
        if (blend.NeedsBlending || isTransparentPass)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc((BlendingFactor)blend.SrcFactor, (BlendingFactor)blend.DstFactor);
            _gl.DepthMask(false);
        }

        _gl.DrawElements(PrimitiveType.Triangles,
            (uint)numIndices, DrawElementsType.UnsignedInt, null);

        if (blend.NeedsBlending || isTransparentPass)
        {
            _gl.DepthMask(true);
            if (!isTransparentPass)
                _gl.Disable(EnableCap.Blend);
        }

        RestoreCullMode(cullMode);

        // Restore main VAO
        _gl.BindVertexArray(_vao);
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
        if (SkipFrustumCulling) return true;

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

        if (vs == 0 || fs == 0)
        {
            EngineImports.Printf(EngineImports.PRINT_ERROR,
                "[.NET] BSP shader compilation failed, world will not render!\n");
            if (vs != 0) _gl.DeleteShader(vs);
            if (fs != 0) _gl.DeleteShader(fs);
            return 0;
        }

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
            _gl.DeleteProgram(prog);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);
            return 0;
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
            _gl.DeleteShader(s);
            return 0;
        }
        return s;
    }

    /// <summary>
    /// Create the shader program for dynamic light projection.
    /// </summary>
    private uint CreateDlightProgram()
    {
        uint vs = Compile(ShaderType.VertexShader, DlightVertSrc);
        uint fs = Compile(ShaderType.FragmentShader, DlightFragSrc);

        uint prog = _gl.CreateProgram();
        _gl.AttachShader(prog, vs);
        _gl.AttachShader(prog, fs);
        _gl.LinkProgram(prog);

        _gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetProgramInfoLog(prog);
            EngineImports.Printf(EngineImports.PRINT_ERROR,
                $"[.NET] Dlight shader link error: {log}\n");
        }

        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return prog;
    }

    /// <summary>
    /// Generate a 64x64 radial falloff texture for dlight projection.
    /// Intensity = max(0, 1 - dist²) where dist is distance from center (0..1).
    /// </summary>
    private uint GenerateDlightTexture()
    {
        const int size = 64;
        byte[] pixels = new byte[size * size];

        float halfSize = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - halfSize) / halfSize;
                float dy = (y + 0.5f - halfSize) / halfSize;
                float distSq = dx * dx + dy * dy;
                float intensity = MathF.Max(0f, 1f - distSq);
                pixels[y * size + x] = (byte)(intensity * 255f);
            }
        }

        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* ptr = pixels)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, size, size,
                0, PixelFormat.Red, PixelType.UnsignedByte, ptr);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        return tex;
    }

    /// <summary>
    /// Render dynamic lights by redrawing visible opaque surfaces with additive blending.
    /// Each light projects a radial falloff texture onto nearby surfaces.
    /// </summary>
    private void RenderDlights(float* mvp, List<DLight> dlights)
    {
        if (_world == null) return;

        _gl.UseProgram(_dlightProgram);
        _gl.UniformMatrix4(_dlMvpLoc, 1, false, mvp);

        // Additive blending, depth test equal, no depth writes
        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.DepthFunc(DepthFunction.Lequal);

        // Bind dlight falloff texture
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _dlightTexture);

        foreach (var dlight in dlights)
        {
            // Set blend mode: additive for additive lights, modulative otherwise
            if (dlight.Additive)
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
            else
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.One);

            // Set per-light uniforms
            _gl.Uniform3(_dlLightOriginLoc, dlight.OriginX, dlight.OriginY, dlight.OriginZ);
            _gl.Uniform3(_dlLightColorLoc, dlight.R, dlight.G, dlight.B);
            _gl.Uniform1(_dlLightRadiusLoc, dlight.Radius);

            float radiusSq = dlight.Radius * dlight.Radius;

            // Draw each visible surface that is within this light's radius
            foreach (int surfIdx in _visibleSurfaceIndices)
            {
                ref var surf = ref _world.Surfaces[surfIdx];
                if (surf.NumIndices == 0) continue;

                // Quick bounds check: test first vertex of surface against light radius
                if (surf.FirstVertex < _world.Vertices.Length)
                {
                    ref var v = ref _world.Vertices[surf.FirstVertex];
                    float dx = v.X - dlight.OriginX;
                    float dy = v.Y - dlight.OriginY;
                    float dz = v.Z - dlight.OriginZ;
                    if (dx * dx + dy * dy + dz * dz > radiusSq * 4f) // generous radius
                        continue;
                }

                _gl.DrawElementsBaseVertex(PrimitiveType.Triangles,
                    (uint)surf.NumIndices, DrawElementsType.UnsignedInt,
                    (void*)(surf.FirstIndex * sizeof(int)),
                    surf.FirstVertex);
            }
        }

        // Restore state
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_program); // switch back to main program
    }

    /// <summary>
    /// Create the shader program for flare rendering.
    /// </summary>
    private uint CreateFlareProgram()
    {
        uint vs = Compile(ShaderType.VertexShader, FlareVertSrc);
        uint fs = Compile(ShaderType.FragmentShader, FlareFragSrc);

        uint prog = _gl.CreateProgram();
        _gl.AttachShader(prog, vs);
        _gl.AttachShader(prog, fs);
        _gl.LinkProgram(prog);

        _gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetProgramInfoLog(prog);
            EngineImports.Printf(EngineImports.PRINT_ERROR,
                $"[.NET] Flare shader link error: {log}\n");
        }

        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return prog;
    }

    /// <summary>
    /// Generate a 32x32 radial glow texture for flare rendering.
    /// Soft circular falloff for a convincing light glow effect.
    /// </summary>
    private uint GenerateFlareTexture()
    {
        const int size = 32;
        byte[] pixels = new byte[size * size];

        float halfSize = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - halfSize) / halfSize;
                float dy = (y + 0.5f - halfSize) / halfSize;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                // Soft radial falloff with bright center
                float intensity = MathF.Max(0f, 1f - dist);
                intensity *= intensity; // quadratic falloff for softer edges
                pixels[y * size + x] = (byte)(intensity * 255f);
            }
        }

        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* ptr = pixels)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, size, size,
                0, PixelFormat.Red, PixelType.UnsignedByte, ptr);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        return tex;
    }

    /// <summary>
    /// Render visible flare surfaces as screen-space billboards.
    /// Projects each flare origin to screen space, applies back-face culling,
    /// and renders as an additive-blended glow quad.
    /// </summary>
    private void RenderFlares(float* mvp, float viewX, float viewY, float viewZ)
    {
        _gl.UseProgram(_flareProgram);
        _gl.BindVertexArray(_flareVao);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One); // additive
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.DepthTest);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _flareTexture);

        foreach (var flare in _visibleFlares)
        {
            // Back-face culling: check if flare faces the viewer
            float vdx = viewX - flare.X;
            float vdy = viewY - flare.Y;
            float vdz = viewZ - flare.Z;
            float viewDist = MathF.Sqrt(vdx * vdx + vdy * vdy + vdz * vdz);
            if (viewDist < 1f) continue;

            float ndl = (vdx * flare.NX + vdy * flare.NY + vdz * flare.NZ) / viewDist;
            if (ndl < 0f) continue; // flare faces away

            // Project to clip space
            float cx = mvp[0] * flare.X + mvp[4] * flare.Y + mvp[8] * flare.Z + mvp[12];
            float cy = mvp[1] * flare.X + mvp[5] * flare.Y + mvp[9] * flare.Z + mvp[13];
            float cz = mvp[2] * flare.X + mvp[6] * flare.Y + mvp[10] * flare.Z + mvp[14];
            float cw = mvp[3] * flare.X + mvp[7] * flare.Y + mvp[11] * flare.Z + mvp[15];

            if (cw <= 0f) continue; // behind camera

            // NDC
            float ndcX = cx / cw;
            float ndcY = cy / cw;

            // Skip if off screen
            if (ndcX < -1.2f || ndcX > 1.2f || ndcY < -1.2f || ndcY > 1.2f) continue;

            // Size based on distance, matching Q3's formula
            // size = viewportW * (r_flareSize / 640 + 8 / distance)
            float distance = MathF.Max(1f, -cz / cw * viewDist);
            float baseSize = 0.04f + 8f / distance;
            float size = MathF.Min(baseSize, 0.3f); // clamp max size

            // Intensity based on Q3's formula + facing angle
            float flareCoeff = 0.15f;
            float intensity = flareCoeff * size * size /
                ((distance + size * MathF.Sqrt(flareCoeff)) * (distance + size * MathF.Sqrt(flareCoeff)));
            intensity = MathF.Min(intensity, 2f) * ndl;

            if (intensity < 0.01f) continue;

            // Set uniforms and draw
            _gl.Uniform2(_flarePosLoc, ndcX, ndcY);
            _gl.Uniform2(_flareSizeLoc, size, size);
            _gl.Uniform3(_flareColorLoc,
                flare.R * intensity,
                flare.G * intensity,
                flare.B * intensity);

            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        // Restore state
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
        _gl.UseProgram(_program);
    }

    /// <summary>
    /// Create the shader program for fog pass rendering.
    /// </summary>
    private uint CreateFogProgram()
    {
        uint vs = Compile(ShaderType.VertexShader, FogVertSrc);
        uint fs = Compile(ShaderType.FragmentShader, FogFragSrc);

        uint prog = _gl.CreateProgram();
        _gl.AttachShader(prog, vs);
        _gl.AttachShader(prog, fs);
        _gl.LinkProgram(prog);

        _gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetProgramInfoLog(prog);
            EngineImports.Printf(EngineImports.PRINT_ERROR,
                $"[.NET] Fog shader link error: {log}\n");
        }

        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return prog;
    }

    /// <summary>
    /// Render fog volumes by redrawing affected surfaces with alpha-blended fog color.
    /// For each fog volume, surfaces with matching FogIndex are drawn with a fog
    /// density computed from distance and depth relative to the fog surface.
    /// </summary>
    private void RenderFog(float* mvp, float viewX, float viewY, float viewZ)
    {
        if (_world == null) return;

        _gl.UseProgram(_fogProgram);
        _gl.UniformMatrix4(_fogMvpLoc, 1, false, mvp);

        // Alpha blending for fog overlay
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _gl.DepthFunc(DepthFunction.Lequal);

        for (int fogIdx = 0; fogIdx < _world.Fogs.Length; fogIdx++)
        {
            ref var fog = ref _world.Fogs[fogIdx];
            int bspFogIndex = fogIdx + 1; // BSP fog indices are 1-based (0 = no fog)

            // Set fog color
            _gl.Uniform4(_fogColorLoc, fog.ColorR, fog.ColorG, fog.ColorB, 1f);

            // Compute fog distance vector: use negated view up direction (matches C renderer)
            // C renderer: fogDistanceVector = (-modelMatrix[2], -modelMatrix[6], -modelMatrix[10], 0) * tcScale
            // For world entity, modelMatrix row 2 = camera up axis, so this is -viewUp * tcScale
            float fogDistX = -_viewUp[0] * fog.TcScale;
            float fogDistY = -_viewUp[1] * fog.TcScale;
            float fogDistZ = -_viewUp[2] * fog.TcScale;
            _gl.Uniform4(_fogDistanceLoc, fogDistX, fogDistY, fogDistZ, 0f);

            // Compute fog depth vector from fog surface plane
            float eyeT;
            if (fog.HasSurface)
            {
                // C renderer: fogDepthVector.w = -surface[3] + dot(origin, surface)
                // For world entity: origin = camera position, surface = (surfNX, surfNY, surfNZ)
                float fogDepthW = -fog.SurfD + (viewX * fog.SurfNX + viewY * fog.SurfNY + viewZ * fog.SurfNZ);
                _gl.Uniform4(_fogDepthLoc, fog.SurfNX, fog.SurfNY, fog.SurfNZ, fogDepthW);
                // C renderer: eyeT = dot(viewOrigin, fogDepthVector) + fogDepthVector[3]
                // For world entity: viewOrigin = (0,0,0), so eyeT = fogDepthVector.w
                eyeT = fogDepthW;
            }
            else
            {
                // No visible surface — eye is always considered inside fog
                _gl.Uniform4(_fogDepthLoc, 0f, 0f, 0f, 1f);
                eyeT = 1f;
            }
            _gl.Uniform1(_fogEyeTLoc, eyeT);

            // Draw surfaces belonging to this fog volume
            foreach (int surfIdx in _visibleSurfaceIndices)
            {
                ref var surf = ref _world.Surfaces[surfIdx];
                if (surf.FogIndex != bspFogIndex) continue;
                if (surf.NumIndices == 0) continue;

                _gl.DrawElementsBaseVertex(PrimitiveType.Triangles,
                    (uint)surf.NumIndices, DrawElementsType.UnsignedInt,
                    (void*)(surf.FirstIndex * sizeof(int)),
                    surf.FirstVertex);
            }
        }

        // Restore state
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_program);
    }

    public void Dispose()
    {
        foreach (uint tex in _lightmapTextures)
            if (tex != 0) _gl.DeleteTexture(tex);
        _lightmapTextures = [];

        foreach (uint tex in _deluxeMapTextures)
            if (tex != 0) _gl.DeleteTexture(tex);
        _deluxeMapTextures = [];

        foreach (uint tex in _cubemapTextures)
            if (tex != 0) _gl.DeleteTexture(tex);
        _cubemapTextures = [];

        if (_dlightTexture != 0) { _gl.DeleteTexture(_dlightTexture); _dlightTexture = 0; }
        if (_dlightProgram != 0) { _gl.DeleteProgram(_dlightProgram); _dlightProgram = 0; }
        if (_fogProgram != 0) { _gl.DeleteProgram(_fogProgram); _fogProgram = 0; }
        if (_flareTexture != 0) { _gl.DeleteTexture(_flareTexture); _flareTexture = 0; }
        if (_flareProgram != 0) { _gl.DeleteProgram(_flareProgram); _flareProgram = 0; }
        if (_flareVbo != 0) { _gl.DeleteBuffer(_flareVbo); _flareVbo = 0; }
        if (_flareVao != 0) { _gl.DeleteVertexArray(_flareVao); _flareVao = 0; }
        if (_ebo != 0) { _gl.DeleteBuffer(_ebo); _ebo = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_autospEbo != 0) { _gl.DeleteBuffer(_autospEbo); _autospEbo = 0; }
        if (_autospVbo != 0) { _gl.DeleteBuffer(_autospVbo); _autospVbo = 0; }
        if (_autospVao != 0) { _gl.DeleteVertexArray(_autospVao); _autospVao = 0; }
        if (_program != 0) { _gl.DeleteProgram(_program); _program = 0; }
        _world = null;
    }
}
