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

## Features

### High Priority

- [ ] **C# OpenGL 4.5 rendering backend** - Implement a new renderer backend in C# using OpenGL 4.5. 2D pipeline, 3D model rendering (MD3), BSP world loading with lightmaps and PVS, skybox rendering, shader blending, beam/lightning entities, mark fragments, entity token parsing, and alpha testing all working. Next: environment mapping, dynamic lighting, fog, world portals, improved patch tessellation. `CPX 5`

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
- [x] Add .NET 9 support — NativeAOT C# renderer DLL (`code/rendererdotnet/`) with full `refexport_t`/`refimport_t` interop, stub implementations, and `GetRefAPI` entry point. Loads via `+set cl_renderer dotnet`.
- [x] Implement 2D rendering pipeline — batched quad renderer (Renderer2D) with GLSL 450 shaders, orthographic projection, texture batching, SetColor/DrawStretchPic, RegisterShader, RegisterFont with binary font data parsing.
- [x] Add Q3 shader script parser — parses all `scripts/*.shader` files at startup via `FS_ListFiles`, resolves shader names to image paths (1434 definitions), supports `map`/`clampMap`/`animMap` directives. Fixed menu background rendering.
- [x] Texture loading from pk3 — loads TGA/JPEG/PNG via StbImageSharp through engine's virtual filesystem (`FS_ReadFile`). Lazy loading with shader script fallback.
- [x] Fix FS_ReadFile interop type — MSVC x64 C `long` is 4 bytes, changed delegate from C# `long` to `int`.
- [x] Add FS_ListFiles/FS_FreeFileList engine import wrappers.
- [x] Implement MD3 model loading and 3D rendering — binary MD3 parser, frame interpolation (backlerp), Renderer3D with GLSL 450 vertex/fragment shaders, per-entity model-view-projection transforms, directional lighting.
- [x] Add scene management — ClearScene/AddRefEntityToScene/RenderScene pipeline with proper viewport/scissor from refdef_t, Q3→OpenGL coordinate conversion.
- [x] Add skin loading (SkinManager) — parses `.skin` files mapping surface names to shader handles, customSkin/customShader priority in rendering.
- [x] Fix window duplication — reuse existing SDL window/GL context on renderer re-init instead of spawning new windows.
- [x] Implement BSP map loading — binary BSP v46 parser (vertices, indices, surfaces, nodes, leafs, planes, lightmaps, PVS), static GPU upload, BSP tree walk with PVS culling, dual-texture lightmap rendering.
- [x] Implement skybox rendering — parses Q3 `skyparms` directives, loads 6-face cube textures, renders before world geometry with depth at far plane.
- [x] Implement shader blending — parses `blendFunc` directives (add/filter/blend + GL_* long form), applies per-shader blend modes in 2D, 3D, sprites, polys, and BSP transparent surface pass.
- [x] Implement beam/lightning/rail entities — billboarded quad strips for RT_BEAM, RT_LIGHTNING, RT_RAIL_CORE, RT_RAIL_RINGS entity types.
- [x] Implement MarkFragments — proper polygon clipping via BSP traversal with Sutherland-Hodgman clipping against bounding planes (matches Q3's R_MarkFragments algorithm). Supports multi-fragment output across faces, patches, and triangle soups.
- [x] Fix depth ordering — BSP surfaces only deferred to transparent pass when CONTENTS_TRANSLUCENT or surfaceparm trans is set. Prevents multi-pass shaders from being incorrectly rendered without depth writes.
- [x] BlendMode refactor — replaced enum with struct storing actual GL blend factors for correct rendering of all Q3 blend modes (marks, filter, additive, etc).
- [x] Fix sprite/poly blend — sprites and polys always blend (never fully opaque), preventing screen-blocking effects like lightning gun overlay.
- [x] Improve shader fallback — env-mapped stages capture blend mode for fallback, try shader name itself when all stages use tcGen environment.
- [x] Implement GetEntityToken — tokenizer for BSP entity string, used by cgame for entity spawning.
- [x] Implement InPVS — PVS visibility check between two world points.
- [x] Implement DrawStretchRaw — raw pixel data rendering for cinematic frames.
- [x] Fix pickup textures — skip `tcGen environment` shader stages, fall back to `qer_editorimage`.
- [x] Remove intro video and CD key prompt — commented out cinematics defines, CL_CDKeyValidate always returns qtrue.
- [x] Implement alpha testing — parses `alphaFunc` directive (GT0/LT128/GE128) from shader scripts, adds per-fragment discard in BSP fragment shader. Alpha-tested surfaces render in opaque pass with depth writes. Fixes translucent world geometry on q3dm0.

### Fixed Bugs

*No fixed bugs yet*
