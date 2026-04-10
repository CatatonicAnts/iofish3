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

*Tiers 1–3 complete (core rendering, visual quality, GL2 advanced features). See Completed section.*

### ON HOLD

- [ ] **MDR model format** - Advanced skeletal model format with bone matrices. `CPX 3`
- [ ] **GPU vertex skinning** - Bone matrix uniforms for hardware-accelerated skeletal animation. `CPX 3`

*Tier 4 complete (videoMap, nopicmip, anisotropic, greyscale, screenshots, surfaceparm, entityMergable). See Completed section.*

---

## .NET Client Game (cgame_dotnet)

*Fully implemented — 15 core subsystems + 35 polish features. See Completed section for details.*

### Notes

- The cgame communicates with the engine entirely through numbered syscalls (not function pointers like the renderer). The NativeAOT DLL must store the syscall pointer from `dllEntry` and dispatch through it.
- Unlike the renderer (which has its own OpenGL context), the cgame only calls renderer APIs indirectly via engine syscalls (CG_R_REGISTERMODEL, CG_R_ADDREFENTITYTOSCENE, etc.). It never touches GPU directly.
- `code/cgame/cg_public.h` defines the complete import/export interface — this is the contract between engine and cgame.
- Player prediction (`pmove`) shares code with server-side physics (`code/game/bg_pmove.c`, `bg_slidemove.c`). This shared code must also be ported to C#.
- We are using VisualStudio for development, make sure the .sln is kept up to date

---

## Other Features

*All completed (HUD cvars, FreeType, shadow mapping, local gravity, bot AI, DDS cubemaps). See Completed section.*

---

## Improvements

### ON HOLD

- [ ] **Add static analysis to CI** - Add `cppcheck` or `clang-tidy` to the GitHub Actions workflow. `CPX 2`
- [ ] **Add sanitizer CI runs** - Run ASAN/MSAN builds to catch memory bugs. `CPX 2`
- [ ] **Consolidate renderer code** - GL1 and GL2 have significant duplication that could be shared. `CPX 5`

---

## Documentation

*All completed. See `docs/` directory.*

---

## Code Cleanup & Technical Debt

### Code Refactoring

- [ ] **Refactor global botlib state** - `botlibglobals` encapsulated (static + accessor functions). Remaining: `botimport` (500+ refs, 26 files), `aasworld` (1247 refs, 13 files), per-subsystem statics (goal/weapon/chat/move states). `CPX 5`

---

## Known Issues / Bugs

### Active Bugs

(none)

### ON HOLD

*(none — all resolved)*

### Uncategorized (Analyze and create TODO entries in above appropriate sections with priority. Do not fix or implement them just yet. Assign complexity points where applicable. Do not delete this section when you are done, just empty it)


---

## Notes

- Game installation: `E:\Games\Quake3`
- **Primary build environment: Visual Studio 2022** — open `msvc\ioq3.sln`, all projects output directly to the game directory. CMake is considered deprecated; only use it to regenerate the solution if needed (`cmake -B msvc -G "Visual Studio 17 2022" -DGAME_DIR="E:/Games/Quake3"`).
- .NET renderer: `cd code\rendererdotnet && dotnet publish -c Release` (publishes NativeAOT DLL to game dir)
- .NET cgame: `cd code\cgamedotnet && dotnet publish -c Release` (publishes NativeAOT DLL to game dir)
- Test .NET renderer: launch with `+set cl_renderer dotnet`
- Test .NET cgame: launch with `+set vm_cgame 0` (loads cgamex86_64.dll from baseq3)
- Project uses internal/bundled libraries by default (`USE_INTERNAL_LIBS=ON`)
- Most FIXME comments are concentrated in GL2 renderer (~77) and bot AI (~44)

---

## Completed

### .NET Renderer (all tiers)

- [x] Multi-stage shaders, dynamic lighting, fog, light grid, flares, portal/mirror rendering
- [x] HDR/bloom/tonemapping, normal/specular/PBR/parallax/deluxe mapping, SSAO, cubemap reflections
- [x] Shadow mapping (pshadows with PCF), IQM model format, VBO caching/batching, DDS textures, FBO pipeline
- [x] videoMap, nopicmip/nomipmaps, anisotropic filtering, greyscale mode, screenshots, TakeVideoFrame, surfaceparm/entityMergable parsing

### .NET Client Game (cgame_dotnet)

- [x] Full C# cgame — 15 core subsystems (scaffolding, syscalls, game state, snapshots, entity/player rendering, weapons, HUD, scoreboard, prediction, events, commands, marks, particles, sound, default loading)
- [x] 35 polish features: view effects, FOV/zoom, damage blob, center print, chat, pickups, crosshair names, powerup timers, warmup, attacker display, muzzle flash, rewards, lagometer, third-person, spectator mode, footsteps, pain/death/announcer/water sounds, kill messages, powerup sounds/effects, gibs, CTF, use items, ammo warning, vote display, holdable items, player shadow, taunt, stop looping sound, corrected event constants

### Other Features

- [x] HUD customization cvars (cg_hudScale, per-element toggles), FreeType font rendering, shadow mapping, local gravity, bot AI improvements, DDS cubemaps

### Improvements

- [x] CMake build with Visual Studio 2022, .NET 9 NativeAOT renderer DLL with full refexport/refimport interop
- [x] 2D rendering pipeline, Q3 shader script parser, texture loading from pk3 (TGA/JPEG/PNG/BMP/DDS)
- [x] MD3 model loading with frame interpolation, scene management, BSP world rendering with PVS culling
- [x] Skybox, shader blending (11 GL factors), entity types (MODEL/SPRITE/BEAM/LIGHTNING/RAIL/POLY)
- [x] MarkFragments, depth ordering, blend state leak fix, BSP vertex colors, cull directive
- [x] tcGen environment, frustum culling, animMap, tcMod (scroll/scale/rotate/turb), rgbGen/alphaGen
- [x] polygonOffset, depthFunc/depthWrite, HDR rendering, FBO pipeline, bloom
- [x] RemapShader, sort order, entity alpha/shaderRGBA, deformVertexes, overbright scale
- [x] R_ColorShiftLightingBytes, inline BSP models, model shader blend modes, BSP transparent surface detection
- [x] GL2 VBO hash optimization, shaderTime float→int fix, sky clearing optimization

### Code Cleanup

- [x] UI subsystem: organized WINDOW_* flags by concern, renamed animation fields, extracted windowTransition_t sub-struct
- [x] Botlib: encapsulated `botlibglobals` and `botDeveloper` as file-scope statics with accessor functions
- [x] AAS reachability: replaced magic numbers with named constants (surface normals, fall damage, prediction, travel times)

### Documentation

- [x] Architecture overview (`docs/architecture.md`), renderer comparison (`docs/renderer-comparison.md`), build guide (`docs/building.md`), AAS magic number constants documented in source

- [x] Missing S_Respatialize in cgame_dotnet, continuous rocket smoke trail, single lightmap fullbright (dotnet + GL1)
- [x] Transparent surfaces with surfaceparm trans rendered opaque, items not visible in cgame_dotnet
- [x] AAS axial plane orientation, client disconnect message loss, screenShadowImage null crash
- [x] Cgame event loop race (VM_IsRunning guard), shader parser fragility (COM_ParseExt `{}`/`}` delimiters)
- [x] Initialize botgoalstates, animMap tokenizer fix, rgbGen/alphaGen wave/const parsing
- [x] Bot debug polygon rendering (bot_debug AAS visualization, r_debugSurface collision debug)
