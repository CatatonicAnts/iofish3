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

- [x] **Multi-stage shader rendering** - Q3 shaders have up to 8 stages (e.g. lightmap pass + texture pass + glow pass). Each stage rendered as a separate draw call with its own blend mode, producing correct multi-pass effects (lightmap×texture, glow overlays, detail textures). `CPX 5`
- [x] **Dynamic lighting (AddLightToScene)** - Per-frame point lights from rockets, plasma, railgun, etc. GL2 transforms dlights to surface-local coordinates and accumulates light contribution. Currently stubbed (AddLightToScene/AddAdditiveLightToScene are empty). `CPX 4`
- [x] **Fog rendering** - Q3 fog volumes defined via `fogParms` (color, depthForOpaque). Surfaces inside fog fade toward the fog color based on depth. GL2 has per-surface fog adjustment, fog culling, and fog-in-water handling. `CPX 4`

### Tier 2 — Important Visual Quality

- [x] **Light grid for entity lighting** - BSP stores a 3D grid of ambient+directed light samples. Trilinear interpolation samples the grid at entity position. Ambient + directed lighting with per-entity light direction replaces hardcoded uniform light. Supports RF_LIGHTING_ORIGIN for multi-part models. `CPX 3`
- [x] **Flare rendering** - Light flares (RT_FLARE, MST_FLARE) with depth-based visibility testing and intensity fading. GL2 uses depth reads to check occlusion. We skip MST_FLARE surfaces entirely. `CPX 3`
- [ ] **Portal / mirror rendering** - Recursive scene rendering through portal surfaces. GL2 detects portal sort, renders the scene from the mirrored viewpoint into an FBO, then composites it. Complex but needed for maps with mirrors/portals. `CPX 5`

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
- [x] **nopicmip / nomipmaps** - Parse and honor mipmap control directives per shader. Mipmaps generated by default with trilinear filtering; `nomipmaps` disables mipmap generation. `CPX 1`
- [x] **Anisotropic filtering** - Hardware anisotropic filtering (up to 16x) auto-detected and applied to mipmapped textures. `CPX 1`
- [ ] **Greyscale mode** - `r_greyscale` cvar for desaturated rendering. `CPX 1`
- [ ] **Screenshot support** - Gamma-corrected screenshot capture. `CPX 2`
- [ ] **TakeVideoFrame** - Video frame capture for demo recording. Currently stubbed. `CPX 2`
- [x] **surfaceparm parsing** - Parse `trans`, `nolightmap`, `nodlight`, `nomarks`/`noimpact` surfaceparms. Flags propagated to shader entries for use by dlight pass and mark system. `CPX 2`
- [ ] **entityMergable** - Allow batching of entities that share shader state. `CPX 2`

---

## .NET Client Game (cgame_dotnet)

> Reimplement `code/cgame/` (baseq3 cgame) in C# as a NativeAOT DLL, following the same architecture as `code/rendererdotnet/`.
> The engine loads cgame via `VM_Create("cgame", ...)` → `Sys_LoadGameDll()` which looks for `dllEntry` and `vmMain` exports.
> Set `vm_cgame 0` to force native DLL loading. To load `cgame_dotnet` by default for debugging, set `vm_cgame 0` and place the compiled DLL as the first cgame DLL found by `FS_FindVM`.

### Architecture

The cgame DLL interface is:
- **`dllEntry(syscall)`** — Called once at load. Receives a pointer to the engine syscall dispatcher (`intptr_t (*)(intptr_t, ...)`) for ~90 import functions (CG_PRINT, CG_R_REGISTERMODEL, CG_GETSNAPSHOT, etc. — see `cg_public.h`)
- **`vmMain(command, arg0..arg11)`** — Called by the engine for ~8 export functions: CG_INIT, CG_SHUTDOWN, CG_DRAW_ACTIVE_FRAME, CG_CONSOLE_COMMAND, CG_CROSSHAIR_PLAYER, CG_LAST_ATTACKER, CG_KEY_EVENT, CG_MOUSE_EVENT, CG_EVENT_HANDLING
- **Shared code** - Serverside part is also planned to be rewritten in C#, but it shares code with the cgame (e.g. pmove physics, bg_*), so that shared code should be implemented in a way that can be used by both the cgame and server DLLs (e.g. separate `code/shared/` library).

### Implementation Plan

- [x] **Project scaffolding** — Create `code/cgamedotnet/` with .csproj targeting net9.0 NativeAOT (same pattern as rendererdotnet). Export `dllEntry` and `vmMain` via `[UnmanagedCallersOnly]`. Publish to game directory as `cgamex86_64.dll`. `CPX 3`
- [x] **Engine syscall interop** — Marshal all ~90 `cgameImport_t` syscalls (CG_PRINT through CG_R_INPVS). Wrap engine calls (cvars, filesystem, renderer, sound, collision, input, snapshots) in type-safe C# classes. `CPX 4`
- [x] **Core game state** — Port `cg_t`, `cgs_t`, `centity_t`, `cg_weapons_t` structs and CG_Init/CG_Shutdown lifecycle. Handle gamestate parsing, server info, map loading, media registration. `CPX 4`
- [x] **Snapshot processing** — Port CG_ProcessSnapshots: snapshot interpolation, entity state transitions (enter/leave PVS), player state prediction, event processing. `CPX 4`
- [x] **Entity rendering** — Port CG_AddCEntity: per-entity-type rendering (players, items, missiles, movers, portals), model attachment via tags, animation state machines, shell/powerup effects. `CPX 5`
- [x] **Player rendering** — Port CG_Player: multi-part player model (head/torso/legs), team skins, animation blending, weapon attachment, first-person weapon rendering. `CPX 5`
- [x] **Weapon effects** — Port CG_AddPlayerWeapon, CG_RegisterWeapon, weapon fire effects (muzzle flash, trails, projectiles, impacts, explosions). All weapon-specific rendering (railgun beam, lightning, BFG, etc.). `CPX 4`
- [x] **HUD / 2D drawing** — Port CG_Draw2D: health/armor/ammo bars, crosshair, pickup notifications, timer, scores, obituaries, chat overlay, lagometer, speed display. `CPX 4`
- [x] **Scoreboard** — Port CG_DrawScoreboard: player list with scores, ping, time, spectator info, team scores. `CPX 3`
- [x] **Local movement prediction** — Port CG_PredictPlayerState: client-side physics prediction using pmove, command replay for lag compensation. Critical for responsive movement. `CPX 5`
- [x] **Event system** — Port CG_EntityEvent: sound triggers, visual effects, item pickups, deaths, jumppads, teleporters, footsteps, pain sounds, weapon switching. `CPX 4`
- [x] **Console commands** — Port cgame console commands: say, tell, +scores, weapon selection, zoom, team overlay toggles, etc. `CPX 2`
- [x] **Marks / decals** — Port CG_ImpactMark: bullet holes, explosion scorch marks, blood splatters via MarkFragments API with fade-out timing. `CPX 3`
- [x] **Particle / local entity system** — Port CG_AddLocalEntities: brass casings, debris, blood trails, smoke puffs, sparks with physics simulation. `CPX 3`
- [x] **Sound integration** — Port CG_AddLoopingSound, entity sound triggers, positional audio, ambient sounds, announcer voice. `CPX 3`
- [x] **Default loading** — Modify engine to prefer `cgame_dotnet` DLL over QVM when `vm_cgame 0` is set. Add a cvar (e.g. `cl_cgame`) to select cgame implementation by name, similar to `cl_renderer` for renderers. `CPX 3`

### Polish Features

- [x] **View effects** — Port CG_OffsetFirstPersonView: view bobbing (head bob from bobCycle/xySpeed), step offset smoothing (stair climbing), duck height smoothing (crouch transition), landing deflection (fall impact), damage kick (pitch/roll punch when hit), velocity-based run pitch/roll, dead view angles. `CPX 3`
- [x] **FOV and zoom** — Port CG_CalcFov: read cg_fov cvar (1-160), zoom support (+zoom/-zoom commands with cg_zoomFov interpolation over 150ms). `CPX 2`
- [x] **Damage blend blob** — Port CG_DamageBlendBlob: directional blood vignette sprite rendered in front of view on damage with fade-out. `CPX 2`
- [x] **Center print** — Port CG_CenterPrint: handle "cp" server command, display centered text messages (objectives, notifications) with 3-second fade-out. `CPX 2`
- [x] **Chat display** — Handle "chat"/"tchat" server commands, display recent chat messages with ring buffer and 6-second fade-out. `CPX 2`
- [x] **Item pickup notifications** — Show item name on pickup (e.g. "Rocket Launcher", "Mega Health") with 3-second centered display and fade-out. `CPX 2`
- [x] **Crosshair target names** — Port CG_DrawCrosshairNames: trace from view to find player under crosshair, display name with 1-second fade-out. `CPX 2`
- [x] **Powerup timers** — Port CG_DrawPowerups: display active powerup icons with remaining time countdown, blinking effect when expiring. `CPX 3`
- [x] **Warmup countdown** — Port CG_DrawWarmup: pre-match countdown display showing game type and "Starts in: N" timer. `CPX 2`
- [x] **Attacker display** — Port CG_DrawAttacker: show last attacker's name in upper-right when damaged by a player, with 10-second fade. `CPX 2`
- [x] **Muzzle flash model** — Render flash model at tag_flash on first-person weapon during fire events, with dynamic light. `CPX 2`
- [x] **Reward medals** — Port CG_DrawReward: display excellence/impressive/gauntlet medals with sound on multi-kills and skill achievements. `CPX 3`
- [x] **Lagometer** — Port CG_DrawLagometer: network latency graph showing snapshot timing and command round-trip, with disconnect indicator. `CPX 3`
- [x] **Third-person camera** — Port CG_OffsetThirdPersonView: third-person camera with collision tracing, controlled by cg_thirdPerson cvar. `CPX 3`
- [x] **Hit feedback sounds** — Play hit/hit_teammate sounds when PERS_HITS changes. `CPX 1`
- [x] **Denied/gauntlet reward events** — Play denied/humiliation announcer sounds on player events. `CPX 1`
- [x] **Spectator/follow mode display** — Show "SPECTATOR" text with gametype instructions, "following <name>" display in follow mode, skip full HUD for spectators. `CPX 2`
- [x] **Surface-aware footstep sounds** — 7 footstep types (normal/boot/flesh/mech/energy/metal/splash) × 4 variants, parsed from animation.cfg footsteps directive. `CPX 3`
- [x] **Pain/death sounds** — Per-model pain sounds with health-based selection (pain25/50/75/100) and 500ms throttle, per-model death sounds (death1-3). `CPX 3`
- [x] **Announcer voice** — Frag limit warnings (1/2/3 frags remaining), prepare/fight/countdown sounds, sudden death, time warnings. `CPX 3`
- [x] **Water/fall sounds** — Gurp sounds on underwater, gasp on surfacing, per-model fall/falling sounds on medium/far falls. `CPX 2`

### Notes

- The cgame communicates with the engine entirely through numbered syscalls (not function pointers like the renderer). The NativeAOT DLL must store the syscall pointer from `dllEntry` and dispatch through it.
- Unlike the renderer (which has its own OpenGL context), the cgame only calls renderer APIs indirectly via engine syscalls (CG_R_REGISTERMODEL, CG_R_ADDREFENTITYTOSCENE, etc.). It never touches GPU directly.
- The original cgame source is ~15,000 lines across ~30 files. Full port is a large effort but each subsystem can be implemented incrementally (start with init + basic HUD, add entity rendering, then prediction).
- `code/cgame/cg_public.h` defines the complete import/export interface — this is the contract between engine and cgame.
- Player prediction (`pmove`) shares code with server-side physics (`code/game/bg_pmove.c`, `bg_slidemove.c`). This shared code must also be ported to C#.
- We are using VisualStudio for development, make sure the .sln is kept up to date

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
- .NET cgame: `cd code\cgamedotnet && dotnet publish -c Release` (publishes NativeAOT DLL to game dir)
- Test .NET renderer: launch with `+set cl_renderer dotnet`
- Test .NET cgame: launch with `+set vm_cgame 0` (loads cgamex86_64.dll from baseq3)
- Project uses internal/bundled libraries by default (`USE_INTERNAL_LIBS=ON`)
- Most FIXME comments are concentrated in GL2 renderer (~77) and bot AI (~44)

---

## Completed

### Features

- [x] .NET cgame (cgame_dotnet) — full C# client game module with 15 core subsystems: project scaffolding, syscalls, core game state, snapshot processing, entity/player rendering, weapon effects, HUD, scoreboard, movement prediction, events, console commands, marks/decals, particles, sound integration, engine default loading
- [x] HUD scaling — AdjustFrom640 coordinate scaling from virtual 640×480 to actual screen resolution
- [x] Weapon switching — NextWeapon/PrevWeapon/SelectWeapon with STAT_WEAPONS bitmask and ammo checks
- [x] Missile rendering — projectile models loaded via WeaponEffects with configstring refresh on dynamic model registration
- [x] Weapon animation — proper MapTorsoToWeaponFrame mapping torso attack/drop/idle to weapon hand frames
- [x] Mirror rendering — portal surfaces rendered with environment mapping approximation
- [x] Muzzle flash model — flash model rendered at tag_flash on first-person weapon during fire events

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
- [x] BSP vertex colors — wired `vColor` into fragment shader for `rgbGen vertex` and vertex-colored surfaces
- [x] Cull directive — parse `cull none/twosided/back/disable` from shader scripts, apply per-surface GL cull mode
- [x] tcGen environment — reflection UV generation from view-space normals in both BSP and MD3 vertex shaders
- [x] Frustum culling — 6-plane frustum extraction from MVP matrix, AABB test on BSP nodes and leaves
- [x] Animated textures (animMap) — parse all frames and frequency, lazy-load each frame, time-based cycling
- [x] tcMod support — scroll, scale, rotate, turb parsed and applied via vertex shader uniforms (up to 4 ops per surface)
- [x] rgbGen / alphaGen — parse identity/vertex/entity/wave/identityLighting, apply vertex color path in BSP fragment shader
- [x] polygonOffset — parse directive, apply `glPolygonOffset(-1,-1)` per-surface for decals
- [x] depthFunc / depthWrite — parse `depthFunc equal` and `depthWrite`, apply per-surface depth state
- [x] RemapShader — runtime shader handle remapping via dictionary, resolve in GetTextureId
- [x] Sort order — parse `sort` directive (portal/sky/opaque/decal/seeThrough/banner/additive/nearest + numeric), sort transparent surfaces by key before drawing
- [x] Entity alpha / shaderRGBA — all-zero detection (uninitialized→white), alpha<1 enables blending on models
- [x] deformVertexes — parse wave/move/bulge/normal/autosprite/autosprite2 from shader scripts, GPU-based wave and move displacement in BSP vertex shader
- [x] Overbright scale — configurable lightmap overbright multiplier uniform (replaces hardcoded 2.0)
- [x] Inline BSP models — doors, platforms, and other brush models (*N) registered by ModelManager, rendered via BspRenderer.RenderSubmodel with entity transforms
- [x] Model shader blend modes — Renderer3D uses shader-defined blend factors (additive, alpha, filter) instead of only entity alpha; fullbright for non-opaque effects
- [x] BSP transparent surface detection — surfaces with non-opaque blend modes deferred to transparent pass regardless of surfaceparm trans (fixes water, force fields)
- [x] animMap tokenizer fix — ShaderTokenizer pushback prevents directive loss after frame list parsing
- [x] rgbGen/alphaGen wave/const — parser now properly consumes all wave/const parameters

### Fixed Bugs

*No fixed bugs yet*
