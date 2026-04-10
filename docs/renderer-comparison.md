# Renderer Comparison: GL1 vs GL2 vs .NET

Three rendering backends are available in iofish3. All implement the same `refexport_t` interface defined in `renderercommon/tr_public.h` and are loaded as DLLs selected by the `cl_renderer` cvar.

## Quick Comparison

| Aspect | GL1 | GL2 | .NET |
|--------|-----|-----|------|
| **GL Version** | 1.1 + extensions | 2.0+ (prefer 3.0+) | 4.5 Core |
| **Language** | C | C | C# (NativeAOT) |
| **Pipeline** | Fixed-function | Programmable (GLSL) | Programmable (GLSL 4.50) |
| **VBOs/VAOs** | ✗ | ✓ | ✓ |
| **FBOs** | ✗ | ✓ | ✓ |
| **HDR** | ✗ | ✓ (float textures) | ✓ (RGB16F) |
| **Bloom** | ✗ | ✗ (infrastructure only) | ✓ (multi-pass Gaussian) |
| **SSAO** | ✗ | ✓ | ✓ |
| **Normal Mapping** | ✗ | ✓ | ✓ |
| **Specular/Parallax** | ✗ | ✓ | ✓ |
| **Cubemap Reflections** | ✗ | ✓ | ✓ |
| **Projected Shadows** | ✗ | ✓ (16 maps) | ✓ (16 maps) |
| **Tone Mapping** | ✗ | ✓ (auto-exposure) | ✓ (auto-exposure) |
| **Build System** | MSBuild / CMake | MSBuild / CMake | `dotnet publish` |

## GL1 Renderer (`code/renderergl1/`)

Classic fixed-function OpenGL pipeline targeting maximum compatibility.

**Rendering approach:** Multi-pass rendering using `glTexEnv()` for texture combining. Each Q3 shader stage is drawn as a separate pass with explicit blend state changes. Uses immediate mode (`glBegin`/`glEnd`) for all geometry.

**Key files:**
- `tr_shade.c` — fixed-function shader stage application
- `tr_shade_calc.c` — per-vertex CPU lighting calculations
- `tr_backend.c` — low-level GL command execution

**Strengths:** Runs on virtually any GPU from the last 25 years. No shader compilation. Simple, well-understood code path.

**Limitations:** No post-processing, no advanced materials, no batch optimization. Relies entirely on CPU for vertex processing.

## GL2 Renderer (`code/renderergl2/`)

Modern programmable pipeline with advanced rendering features.

**Rendering approach:** GLSL shaders for all geometry. Vertex data stored in VBOs with VAO state tracking. Scene rendered to FBOs for HDR and post-processing. 32 GLSL shader pairs in the `glsl/` subdirectory cover different material types.

**Key files (beyond GL1 equivalents):**
- `tr_glsl.c` — GLSL shader compilation and uniform management
- `tr_vbo.c` — vertex buffer objects and vertex packing
- `tr_fbo.c` — framebuffer objects (64 max)
- `tr_postprocess.c` — tone mapping, SSAO, auto-exposure
- `tr_extensions.c` — GL 2.0/3.0/3.2 capability detection
- `tr_dsa.c` — Direct State Access wrappers
- `glsl/lightall_vp.glsl` / `lightall_fp.glsl` — PBR-ready lighting

**Notable GLSL shaders:**
| Shader | Purpose |
|--------|---------|
| `generic` | Basic textured surfaces |
| `lightall` | Full PBR lighting (normal + specular + deluxe) |
| `shadowfill` / `pshadow` | Shadow map rendering and application |
| `ssao` | Screen-space ambient occlusion |
| `tonemap` | HDR tone mapping with auto-exposure |
| `fogpass` | Volumetric fog |

**Extension fallback chain:** Detects GL capabilities at startup and adjusts feature levels. GLSL version ranges from 1.20 to 1.50 depending on hardware.

## .NET Renderer (`code/rendererdotnet/`)

Modern C# renderer using Silk.NET for OpenGL 4.5 Core bindings and SDL2 for windowing.

**Rendering approach:** All GLSL shaders are 4.50 Core and embedded as C# string literals. Scene rendered to FBO with full HDR pipeline including bloom (which GL2 lacks). Modular architecture with separate manager classes.

**Key files:**
| File | Purpose |
|------|---------|
| `Renderer3D.cs` | 3D model rendering (MD3, IQM) |
| `Renderer2D.cs` | HUD/2D overlay rendering |
| `World/BspRenderer.cs` | BSP world with multi-stage shading |
| `World/SkyboxRenderer.cs` | Cubemap skybox |
| `PostProcess.cs` | Bloom, SSAO, tone mapping, HDR |
| `ShadowMapper.cs` | Projected shadow maps |
| `ShaderScriptParser.cs` | Q3 shader script parsing |

**Post-processing pipeline:**
1. Bright pass extraction → 2. Multi-scale Gaussian blur → 3. Luminance pyramid → 4. Temporal exposure blending → 5. SSAO (optional) → 6. Composite (tone map + bloom + SSAO)

**Build:** `cd code\rendererdotnet && dotnet publish -c Release` (NativeAOT → native DLL)

## Feature Details

### HDR & Tone Mapping
Both GL2 and .NET render to floating-point FBOs and apply tone mapping with auto-exposure. The .NET renderer adds bloom on top of this pipeline (GL2 has the infrastructure but doesn't implement the bloom blur passes).

### Shadows
Both GL2 and .NET support 16 projected shadow maps at 512×512 resolution. GL2 additionally supports experimental sunlight cascaded shadow maps via `r_sunlightMode`.

### Materials
Both GL2 and .NET support normal mapping, specular mapping, and parallax mapping via external texture files (`_n`, `_s`, `_p` suffixes). GL2 uses deluxe maps for per-pixel light direction; the .NET renderer supports the same via its BSP shader.

### Model Formats
| Format | GL1 | GL2 | .NET |
|--------|-----|-----|------|
| MD3 (Quake 3) | ✓ | ✓ | ✓ |
| IQM (skeletal) | ✓ | ✓ | ✓ |
| MDR (advanced skeletal) | ✗ | ✓ | ✗ (planned) |

### Texture Compression
| Format | GL1 | GL2 | .NET |
|--------|-----|-----|------|
| S3TC (DXT) | ✓ | ✓ | ✓ |
| RGTC | ✗ | ✓ | ✓ |
| BPTC | ✗ | ✓ | ✓ |

## Selecting a Renderer

Set the `cl_renderer` cvar before starting the game:

```
+set cl_renderer opengl1    # GL1: maximum compatibility
+set cl_renderer opengl2    # GL2: advanced features, broad support
+set cl_renderer dotnet     # .NET: modern pipeline, bloom, C# codebase
```

**Use GL1** for legacy hardware or when debugging shader issues.
**Use GL2** for production play with advanced features on a wide range of GPUs.
**Use .NET** for development of new rendering features or when targeting modern systems exclusively.
