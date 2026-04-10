# iofish3 Architecture Overview

This document describes the high-level architecture of iofish3 (fork of ioquake3), covering subsystem boundaries, data flow, and entry points.

## Subsystem Diagram

```
┌─────────────────────────────────────────────┐
│  QCOMMON (Core Engine)                      │
│  vm.c, common.c, files.c, cmd.c, cvar.c    │
│  Memory, VM, Filesystem, Commands, Network  │
└──────┬──────────┬───────────┬───────────────┘
       │          │           │
       ▼          ▼           ▼
  ┌─────────┐ ┌────────┐ ┌────────┐ ┌────────┐
  │RENDERER │ │ CLIENT │ │ SERVER │ │ BOTLIB │
  │  (DLL)  │ │ CGAME  │ │  GAME  │ │(linked)│
  │         │ │  (VM)  │ │  (VM)  │ │        │
  └─────────┘ └────────┘ └────────┘ └────────┘
       ▲           │           │
       │      syscalls    syscalls
       └───────────┴───────────┘
```

## Subsystems

### Core Engine (qcommon/)

The foundation all other subsystems depend on.

| File | Responsibility |
|------|----------------|
| `common.c` | Main loop, initialization, error handling |
| `vm.c` | Virtual machine: bytecode interpreter, JIT, syscall dispatch |
| `files.c` | Virtual filesystem: pk3/zip archive loading, path resolution |
| `cmd.c` | Console command buffer and execution |
| `cvar.c` | Configuration variable system |
| `cm_*.c` | Collision model: BSP loading, traces, area portals |
| `msg.c` | Network message serialization |
| `net_*.c` | UDP networking, packet handling |
| `q_shared.c/h` | Math utilities, shared types, platform abstractions |

**Entry point:** `Com_Init()` in `common.c`

### Renderer (renderergl1/, renderergl2/, rendererdotnet/)

Dynamically loaded DLL implementing the rendering interface.

**Interface:** `refexport_t` / `refimport_t` in `renderercommon/tr_public.h`

**Loading:** `CL_InitRef()` in `client/cl_main.c` calls `GetRefAPI(REF_API_VERSION, &ri)` from the renderer DLL. The `cl_renderer` cvar selects which DLL to load (`opengl1`, `opengl2`, or `dotnet`).

**Key exports:** `BeginFrame`, `EndFrame`, `RenderScene`, `RegisterModel`, `RegisterShader`, `LoadWorld`, `DrawStretchPic`, `MarkFragments`

**Key imports (from engine):** Memory allocation, filesystem, cvars, console commands, collision queries

**Data flow:** Client builds scene → renderer draws it. The renderer never initiates communication; it only responds to calls from the client.

### Client Game (cgame/)

Game logic running on the client: player rendering, HUD, prediction, effects.

**Interface:** `cgameExport_t` / `cgameImport_t` enums in `cgame/cg_public.h`

**Loading:** `CL_InitCGame()` in `client/cl_cgame.c` calls `VM_Create("cgame", CL_CgameSystemCalls, interpret)`. The `vm_cgame` cvar selects native DLL (0), JIT-compiled QVM (1), or interpreted QVM (2).

**Key exports (engine → cgame):** `CG_INIT`, `CG_SHUTDOWN`, `CG_DRAW_ACTIVE_FRAME`, `CG_CONSOLE_COMMAND`, `CG_KEY_EVENT`, `CG_MOUSE_EVENT`

**Key imports (cgame → engine via syscalls):** Renderer commands (`CG_R_*`), sound (`CG_S_*`), snapshots (`CG_GETSNAPSHOT`), collision (`CG_CM_*`), filesystem, cvars

**Data flow:** Network snapshots → cgame prediction → renderer scene building → renderer draws

### Server Game (game/)

Server-side game logic: physics, entities, gameplay rules.

**Interface:** `gameExport_t` / `gameImport_t` in `game/g_public.h`

**Loading:** `SV_InitGameProgs()` in `server/sv_game.c` calls `VM_Create("qagame", SV_GameSystemCalls, ...)`.

**Key exports:** `G_INIT`, `G_SHUTDOWN`, `G_CLIENT_CONNECT`, `G_CLIENT_THINK`, `G_RUN_FRAME`

**Key imports:** Entity state management, collision traces, networking, botlib access

### Bot Library (botlib/)

AI system for bot navigation and behavior, linked directly into the server.

**Interface:** `botlib_export_t` / `botlib_import_t` in `botlib/botlib.h`

**Loading:** `GetBotLibAPI()` in `botlib/be_interface.c`

**Sub-systems:**
- **AAS (Area Awareness System):** Spatial queries, reachability, pathfinding
- **EA (Elementary Actions):** Movement commands, attack, use
- **AI:** Goal selection, weapon choice, chat, character profiles

**Data flow:** Server game ↔ botlib (entity positions in, bot commands out)

### Shared Renderer Code (renderercommon/)

Code shared by all three renderers:
- `tr_public.h` — renderer interface definitions
- `tr_types.h` — refEntity_t, refdef_t, glconfig_t structures
- `tr_font.c` — font/glyph rendering
- `tr_image_*.c` — image format loaders (BMP, JPG, PNG, TGA, PCX)
- `tr_noise.c` — Perlin noise for shader effects
- `qgl.h` — OpenGL function pointer declarations

## Runtime Frame Flow

### Server Frame (`SV_Frame`)
1. Process client commands
2. `G_RUN_FRAME` → server game logic (physics, AI, entity updates)
3. Send entity state snapshots to clients

### Client Frame (`CL_Frame`)
1. Read network snapshots from server
2. `CG_DRAW_ACTIVE_FRAME` → client game builds the scene:
   - Predict player movement
   - Interpolate entities
   - Add render entities, effects, sounds
3. `re.RenderScene()` → renderer draws the 3D scene
4. `re.EndFrame()` → swap buffers

### Initialization Sequence
1. `Com_Init()` — core engine
2. `CL_InitRef()` — load renderer DLL, call `GetRefAPI()`
3. `SV_InitGameProgs()` — load server game VM
4. `CL_InitCGame()` — load client game VM

## API Versions

| Subsystem | Entry Point | API Version |
|-----------|-------------|-------------|
| Renderer | `GetRefAPI(version, imports)` | `REF_API_VERSION = 8` |
| CGame | `VM_Create("cgame", ...)` | `CGAME_IMPORT_API_VERSION = 4` |
| Game | `VM_Create("qagame", ...)` | `GAME_API_VERSION = 8` |
| BotLib | `GetBotLibAPI(version, imports)` | `BOTLIB_API_VERSION` |
