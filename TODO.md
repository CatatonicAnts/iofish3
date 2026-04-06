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

- [ ] **C# OpenGL 4.5 rendering backend** - Implement a new renderer backend in C# using OpenGL 4.5. The .NET interop foundation is in place. `CPX 5`

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
- [ ] GL1 vs GL2 renderer comparison and migration notes
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
- Build: `cmake -B build -G "Visual Studio 17 2022"` then `cmake --build build --config Release`
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

### Fixed Bugs

*No fixed bugs yet*
