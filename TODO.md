# iofish3 - TODO

A list of planned features, improvements, and tasks for this project.

> **CPX (Complexity Points)** - 1 to 5 scale:
> - **1** - Single file control/component
> - **2** - Single file control/component with single function change dependencies
> - **3** - Multi-file control/component or single file with multiple dependencies, no architecture changes
> - **4** - Multi-file control/component with multiple dependencies and significant logic, possible minor architecture changes
> - **5** - Large feature spanning multiple components and subsystems, major architecture changes

> Instructions for the TODO list:
- Move all completed items into separate Completed section
- Consolidate all completed TODO items by combining similar ones and shortening the descriptions where possible

> How TODO file should be iterated:
- First handle the Uncategorized section, if any similar issues already are on the TODO list, increase their priority instead of adding duplicates (categorize all at once)
- When Uncategorized section is empty, start by fixing Active Bugs (take one at a time)
- After Active Bugs, handle the rest of the TODO file by priority and complexity (High priority takes precedance, then CPX points) (take one at a time).

---

## .NET Renderer — Feature Gap vs GL2

> Comprehensive comparison of `code/rendererdotnet/` against `code/renderergl2/`.
> Items are grouped by visual impact and ordered by priority within each tier.

### Tier 1 — Core Q3 Rendering (Required for visual correctness)

- [ ] **Multi-stage shader rendering** - Q3 shaders have up to 8 stages (e.g. lightmap pass + texture pass + glow pass). We only use the first usable stage's texture/blend. Need to render each stage as a separate draw call with its own blend mode, producing correct multi-pass effects (lightmap×texture, glow overlays, detail textures). `CPX 5`
- [ ] **tcMod support** - Texture coordinate modifiers: `scroll` (conveyor belts, flowing lava), `scale`, `rotate` (fans, radar), `turb` (water distortion), `stretch` (pulsing lights), `transform` (6-param matrix), `entityTranslate`. Many world surfaces and effects depend on these. `CPX 3`
- [ ] **rgbGen / alphaGen** - Color and alpha generation per-vertex: `wave` (pulsing lights/colors), `vertex`/`exactVertex` (vertex colors in BSP — note: `vColor` is declared but **unused** in BSP fragment shader), `entity` (entity RGBA tinting), `identity`, `identityLighting`, `lightingDiffuse`, `oneMinusEntity`, `const`, `portal`. `CPX 3`
- [ ] **tcGen environment** - Environment/reflection mapping using view-space normals to generate UVs. Needed for health pickups (still white), chrome/metallic surfaces, glass reflections. Currently detected but not implemented — envmap stages are skipped. `CPX 2`
- [ ] **Dynamic lighting (AddLightToScene)** - Per-frame point lights from rockets, plasma, railgun, etc. GL2 transforms dlights to surface-local coordinates and accumulates light contribution. Currently stubbed (AddLightToScene/AddAdditiveLightToScene are empty). `CPX 4`
- [ ] **Fog rendering** - Q3 fog volumes defined via `fogParms` (color, depthForOpaque). Surfaces inside fog fade toward the fog color based on depth. GL2 has per-surface fog adjustment, fog culling, and fog-in-water handling. `CPX 4`
- [ ] **Frustum culling** - GL2 tests every node/surface against a 6-plane view frustum before rendering. Our BSP renderer only uses PVS cluster culling — no frustum test. This means we draw many off-screen surfaces. `CPX 2`
- [ ] **cull directive** - Parse and apply `cull none`/`cull twosided`/`cull back`/`cull disable` per shader. Some surfaces (grates, foliage, glass) need two-sided rendering. Currently hardcoded to cull front faces. `CPX 2`
- [ ] **depthFunc / depthWrite directives** - Parse `depthFunc equal` (for multi-pass lightmap rendering) and `depthWrite` (force depth writes on blended surfaces). Missing `depthFunc equal` causes z-fighting on multi-stage surfaces. `CPX 2`
- [ ] **Sort order** - Q3 assigns each shader a sort key (portal=1, sky=2, opaque=3, decal=4, seeThrough=5, banner=6, fog=7, blend0-3=8-11, nearest=14). This controls render order. We only have opaque vs transparent. Need proper sort ordering for correct layering of decals, banners, see-through surfaces. `CPX 3`
- [ ] **deformVertexes** - Vertex deformation effects: `wave` (flag waving), `bulge` (pulsing pipes), `move` (floating items), `normal` (water surface perturbation), `autosprite`/`autosprite2` (always-facing quads). Many world decorations use these. `CPX 3`
- [ ] **Animated textures (animMap)** - We parse `animMap` but only use the first frame. Need to cycle frames based on `frequency` parameter and `shaderTime`. Used for console screens, warning lights, teleporter pads. `CPX 2`
- [ ] **RemapShader** - Runtime shader replacement (e.g. team-colored textures, powerup effects). Currently an empty stub. `CPX 2`

### Tier 2 — Important Visual Quality

- [ ] **Light grid for entity lighting** - BSP stores a 3D grid of ambient+directed light samples. GL2 uses this to light entities (models, weapons) based on world position. We use a hardcoded directional light (0.57, 0.57, 0.57). `CPX 3`
- [ ] **Vertex colors in BSP** - BSP vertices have per-vertex RGBA colors used by `rgbGen vertex` shaders. Our fragment shader declares `vColor` but never uses it. Some surfaces rely on vertex colors for tinting/fading. `CPX 1`
- [ ] **Overbright / gamma correction** - Q3 uses overbright bits (`r_mapOverBrightBits`) and gamma tables to adjust brightness. GL2 builds lookup tables and applies them to textures and lightmaps. We multiply lightmaps by 2.0 which is an approximation. `CPX 2`
- [ ] **Flare rendering** - Light flares (RT_FLARE, MST_FLARE) with depth-based visibility testing and intensity fading. GL2 uses depth reads to check occlusion. We skip MST_FLARE surfaces entirely. `CPX 3`
- [ ] **Portal / mirror rendering** - Recursive scene rendering through portal surfaces. GL2 detects portal sort, renders the scene from the mirrored viewpoint into an FBO, then composites it. Complex but needed for maps with mirrors/portals. `CPX 5`
- [ ] **polygonOffset** - Parse `polygonOffset` directive and apply `glPolygonOffset` for decal surfaces to prevent z-fighting. `CPX 1`
- [ ] **Entity alpha / shader RGBA** - Entity `shaderRGBA[4]` should modulate rendering. Partially working (color extracted but not all paths apply it). Need consistent application in BSP and model rendering. `CPX 2`

### Tier 3 — GL2-Specific Advanced Features

- [ ] **HDR rendering** - GL2 renders to GL_RGBA16F FBOs and does tonemapping with auto-exposure. Controlled by `r_hdr`. `CPX 4`
- [ ] **Bloom / post-processing** - Bokeh blur, Gaussian blur, sun rays via post-process shaders. Requires FBO pipeline. `CPX 3`
- [ ] **Normal mapping** - `normalMap`/`bumpMap` stage type for per-pixel lighting. GL2 generates normals from height maps if missing. `CPX 3`
- [ ] **Specular mapping** - `specularMap` stage type with `specularScale`, `specularReflectance`, `specularExponent`, `gloss` controls. `CPX 3`
- [ ] **PBR (Physically Based Rendering)** - GL2 has `r_pbr` cvar for metallic/roughness workflow. Converts specular values to PBR parameters. `CPX 4`
- [ ] **Parallax mapping** - Height-based parallax offset (`r_parallaxMapping`) with optional parallax shadow mapping. `CPX 3`
- [ ] **Cubemap reflections** - GL2 loads/renders cubemaps for environment reflections with parallax correction. `CPX 4`
- [ ] **Shadow mapping** - Projection shadows (512×512 maps), sun shadow framework. Partially implemented in GL2 itself. `CPX 5`
- [ ] **Deluxe mapping** - World deluxe maps store per-pixel light direction for advanced per-pixel lighting. `CPX 3`
- [ ] **SSAO** - Screen-space ambient occlusion framework (`r_ssao` cvar). `CPX 3`
- [ ] **IQM model format** - Inter-Quake Model format with GPU bone skinning. GL2 has full loader and renderer. `CPX 4`
- [ ] **MDR model format** - Advanced skeletal model format with bone matrices. `CPX 3`
- [ ] **GPU vertex skinning** - Bone matrix uniforms for hardware-accelerated skeletal animation. `CPX 3`
- [ ] **VBO caching / batching** - GL2 has a VAO cache (16MB vertex, 5MB index) with surface batching (1024 surfaces per batch). We upload once and draw per-surface. `CPX 3`
- [ ] **DDS texture format** - DDS loading with RGTC and BPTC compression. `CPX 2`
- [ ] **FBO pipeline** - Framebuffer objects for render-to-texture, multi-pass effects, post-processing. Foundation for HDR/bloom/portals. `CPX 3`

### Tier 4 — Minor / Polish

- [ ] **videoMap** - Cinematic playback on surfaces (video textures on in-game screens). `CPX 2`
- [ ] **nopicmip / nomipmaps** - Parse and honor mipmap control directives per shader. `CPX 1`
- [ ] **Anisotropic filtering** - GL2 supports hardware anisotropic filtering modes. `CPX 1`
- [ ] **Greyscale mode** - `r_greyscale` cvar for desaturated rendering. `CPX 1`
- [ ] **Screenshot support** - Gamma-corrected screenshot capture. `CPX 2`
- [ ] **TakeVideoFrame** - Video frame capture for demo recording. Currently stubbed. `CPX 2`
- [ ] **surfaceparm parsing** - We only parse `trans`. GL2 parses: `water`, `slime`, `lava`, `fog`, `sky`, `alphashadow`, `nomarks`, `noimpact`, `nolightmap`, `nodlight`, `dust`, `metalsteps`, `flesh`, `nosteps`, etc. Most affect gameplay not rendering, but some (nolightmap, nodlight) affect visual output. `CPX 2`
- [ ] **entityMergable** - Allow batching of entities that share shader state. `CPX 2`

---

## Other Features

### Medium Priority

- [ ] **Implement player blacklist** - Add server-side player blacklist for DoS/harassment prevention (`code/server/sv_client.c:483`). `CPX 2`
- [ ] **Complete GL2 shadow mapping** - Multiple unimplemented shadow features in `code/renderergl2/tr_shadows.c`. Sun shadows also render incorrectly in cubemaps (`tr_main.c`, `tr_bsp.c`). `CPX 4`
- [ ] **Enable Freetype font rendering by default** - `USE_FREETYPE` is OFF by default. Evaluate and enable for improved font quality. `CPX 2`

### Lower Priority

- [ ] **DDS cubemap loading** - Complete DDS cubemap support in `code/renderergl2/tr_image_dds.c`. `CPX 2`
- [ ] **Bot AI improvements** - Fix flag carrier defense logic, bridge traversal, and radial damage teammate checking (`code/game/ai_dmq3.c`). `CPX 3`
- [ ] **Local gravity support** - Implement per-entity gravity instead of only global (`code/game/bg_pmove.c`). `CPX 2`

---

## Improvements

### ON HOLD

- [ ] **Add static analysis to CI** - Add `cppcheck` or `clang-tidy` to the GitHub Actions workflow. `CPX 2`
- [ ] **Add sanitizer CI runs** - Run ASAN/MSAN builds to catch memory bugs. `CPX 2`
- [ ] **GL2 VBO upload optimization** - Use faster search and avoid vertex re-upload (`code/renderergl2/tr_vbo.c`). `CPX 2`
- [ ] **Fix floating point precision in shaderTime** - Must be passed as int to avoid fp-precision loss in both renderers (`tr_backend.c`). `CPX 1`
- [ ] **Sky clearing optimization** - Only clear if sky shaders are used instead of unconditionally (both GL1 and GL2 `tr_backend.c`). `CPX 1`
- [ ] **Consolidate renderer code** - GL1 and GL2 have significant duplication that could be shared. `CPX 5`

---

## Documentation **LOW PRIORITY**

- [ ] Architecture overview covering subsystem relationships (renderer, game, botlib)
- [ ] GL1 vs GL2 vs dotnet renderer comparison and migration notes
- [ ] Document AAS magic number constants in `code/botlib/be_aas_reach.c`
- [ ] Build and testing guide for contributors

---

## Code Cleanup & Technical Debt

### Code Refactoring

- [ ] **Refactor global botlib state** - Remove global structure in `code/botlib/be_interface.h`, refactor to instance-based. `CPX 3`
- [ ] **UI subsystem architecture** - Consolidate common data into unified structures, separate text vs window rendering concerns (`code/ui/ui_shared.h`). `CPX 4`
- [ ] **Initialize botgoalstates** - Global array not properly initialized in `code/botlib/be_ai_goal.c:179`. `CPX 1`
- [ ] **Fix shader parser fragility** - Shader parser requires spaces after parens (`code/renderergl1/tr_shader.c`, GL2). `CPX 2`

---

## Known Issues / Bugs

### Active Bugs

*No active bugs*

### ON HOLD

- [ ] **Client disconnect message loss** - Client never parses disconnect message from server (`code/server/sv_client.c`). `CPX 2`
- [ ] **Single lightmap fullbright** - Maps with only one lightmap render as fullbright (`code/renderergl1/tr_bsp.c`). `CPX 2`
- [ ] **Cgame event loop race** - Server restart during cgame event processing causes undefined behavior (`code/client/cl_cgame.c`). `CPX 3`
- [ ] **screenShadowImage null crash** - Potential crash when framebuffers unavailable in GL2 (`code/renderergl2/tr_shade.c`). `CPX 1`
- [ ] **AAS plane orientation** - Axial node planes don't always face positive direction, causing pathfinding errors (`code/botlib/be_aas_sample.c`). `CPX 2`

### Uncategorized (Analyze and create TODO entries in above appropriate sections with priority. Do not fix or implement them just yet. Assign complexity points where applicable. Do not delete this section when you are done, just empty it)

*No uncategorized items*

---

## Notes

- Game installation: `E:\Games\Quake3`
- **Primary build environment: Visual Studio 2022** — open `msvc\ioq3.sln`, all projects output directly to the game directory. CMake is considered deprecated; only use it to regenerate the solution if needed (`cmake -B msvc -G "Visual Studio 17 2022" -DGAME_DIR="E:/Games/Quake3"`).
- .NET renderer: `cd code\rendererdotnet && dotnet publish -c Release` (publishes NativeAOT DLL to game dir)
- Test .NET renderer: launch with `+set cl_renderer dotnet`
- Project uses internal/bundled libraries by default (`USE_INTERNAL_LIBS=ON`)
- Most FIXME comments are concentrated in GL2 renderer (~77) and bot AI (~44)

---

## Completed

### Features

*No completed features yet*

### Improvements

- [x] Set up CMake build with Visual Studio 2022
- [x] Add .NET 9 NativeAOT renderer DLL with full refexport/refimport interop and 30 exports
- [x] 2D rendering pipeline — batched quad renderer with GLSL 450, orthographic projection, texture batching, SetColor/DrawStretchPic/DrawStretchRaw, RegisterShader/RegisterFont
- [x] Q3 shader script parser — parses `scripts/*.shader` files, resolves shader names to image paths, supports `map`/`clampMap`/`animMap`/`blendFunc`/`alphaFunc`/`skyparms`/`surfaceparm trans`/`tcGen environment`
- [x] Texture loading from pk3 — TGA/JPEG/PNG/BMP via StbImageSharp, lazy loading with shader script fallback chain (direct→script image→editorimage→envmap stage→shader name)
- [x] MD3 model loading — binary parser, frame interpolation (backlerp), GLSL 450 shaders, per-entity transforms, directional lighting, skin loading, tag interpolation (LerpTag)
- [x] Scene management — ClearScene/AddRefEntityToScene/AddPolyToScene/RenderScene pipeline with viewport/scissor, Q3→OpenGL coordinate conversion
- [x] BSP world rendering — v46 binary parser, static GPU upload, BSP tree walk with PVS culling, dual-texture lightmap rendering, opaque/transparent two-pass pipeline
- [x] Skybox rendering — 6-face cube from skyparms, rendered before world at far plane
- [x] Shader blending — per-shader blend modes (11 GL factors), alpha testing (GT0/LT128/GE128), BlendMode struct with actual GL factors
- [x] Entity types — RT_MODEL, RT_SPRITE (billboarded), RT_BEAM, RT_LIGHTNING, RT_RAIL_CORE, RT_RAIL_RINGS, RT_POLY
- [x] MarkFragments — BSP traversal with Sutherland-Hodgman polygon clipping across faces/patches/triangle soups
- [x] Depth ordering fix — surfaces only deferred to transparent pass when CONTENTS_TRANSLUCENT or surfaceparm trans
- [x] Blend state leak fix — explicit GL_BLEND disable at BSP opaque pass start
- [x] GetEntityToken, InPVS, ModelBounds — engine interop functions
- [x] Remove intro video and CD key prompt

### Fixed Bugs

*No fixed bugs yet*
