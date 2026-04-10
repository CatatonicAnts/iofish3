# Building iofish3

This guide covers building iofish3 on Windows with Visual Studio 2022.

## Prerequisites

- **Visual Studio 2022** (Community or higher) with "Desktop development with C++" workload
- **.NET 9 SDK** (for the .NET renderer and cgame)
- **CMake 3.25+** (only needed if regenerating the solution)
- **Quake 3 Arena** game data (`pak0.pk3`) installed to `E:\Games\Quake3\baseq3\`

## Quick Start

1. Open `msvc\ioq3.sln` in Visual Studio 2022
2. Set configuration to **Release / x64**
3. Build the solution (Ctrl+Shift+B)
4. Build the .NET renderer:
   ```
   cd code\rendererdotnet
   dotnet publish -c Release
   ```
5. Run: launch `ioquake3.exe` from the game directory, or press F5 in Visual Studio

## Project Overview

The solution contains these projects:

| Project | Output | Description |
|---------|--------|-------------|
| `ioquake3` | `ioquake3.exe` | Main client executable |
| `ioq3ded` | `ioq3ded.exe` | Dedicated server |
| `renderer_opengl1` | `renderer_opengl1.dll` | OpenGL 1.x renderer |
| `renderer_opengl2` | `renderer_opengl2.dll` | OpenGL 2.0+ renderer (HDR, shadows) |
| `cgame_baseq3` | `cgame.dll` | Client game module (baseq3) |
| `qagame_baseq3` | `qagame.dll` | Server game module (baseq3) |
| `ui_baseq3` | `ui.dll` | UI module (baseq3) |
| `cgame_missionpack` | `cgame.dll` | Client game (Team Arena) |
| `qagame_missionpack` | `qagame.dll` | Server game (Team Arena) |
| `ui_missionpack` | `ui.dll` | UI module (Team Arena) |

All outputs go directly to the game directory (`E:\Games\Quake3\` for executables/renderers, `baseq3\` subdirectory for game DLLs).

## .NET Components

The .NET renderer and cgame use NativeAOT to compile to native DLLs. They are **not** part of the Visual Studio solution and must be built separately with `dotnet publish`.

### .NET Renderer

```
cd code\rendererdotnet
dotnet publish -c Release
```

Outputs `renderer_dotnet.dll` to `E:\Games\Quake3\`.

### .NET Client Game

```
cd code\cgamedotnet
dotnet publish -c Release
```

Outputs `cgamex86_64.dll` to `E:\Games\Quake3\baseq3\`.

> **Note:** `dotnet build` only produces a managed DLL. You must use `dotnet publish` for NativeAOT to generate the native DLL that the engine can load.

## Regenerating the Solution

The `msvc\` directory contains a CMake-generated Visual Studio solution. To regenerate it (e.g., after adding source files):

```
cmake -B msvc -G "Visual Studio 17 2022" -DGAME_DIR="E:/Games/Quake3"
```

The `-DGAME_DIR` option directs all build outputs to the game installation directory. Without it, outputs go to `msvc\Release\`.

## Runtime Configuration

### Selecting the Renderer

The engine dynamically loads renderer DLLs. Use the `cl_renderer` cvar:

| Value | Renderer |
|-------|----------|
| `opengl1` | GL1 (default) |
| `opengl2` | GL2 with advanced features |
| `dotnet` | .NET renderer |

Example: `+set cl_renderer dotnet`

### Selecting the Client Game

The `vm_cgame` cvar controls how the client game module loads:

| Value | Behavior |
|-------|----------|
| `2` | QVM interpreted (loads `vm/cgame.qvm`) |
| `1` | QVM compiled (loads `vm/cgame.qvm`, JIT compiled) |
| `0` | Native DLL (loads `cgame.dll` or `cgamex86_64.dll` from `baseq3\`) |

The C cgame builds as `cgame.dll`, the .NET cgame as `cgamex86_64.dll`. With `vm_cgame 0`, the engine tries `cgamex86_64.dll` first, then `cgame.dll`.

### Launch from Visual Studio

The solution is configured to launch with `+set cl_renderer dotnet`. To change launch arguments, edit the ioquake3 project's debugging properties.

## Troubleshooting

- **"Failed to load renderer_dotnet.dll"**: Run `dotnet publish -c Release` in `code\rendererdotnet\`
- **Game loads wrong cgame**: Check `vm_cgame` value and which DLLs exist in `baseq3\`
- **Linker errors after adding files**: Regenerate the solution with CMake
- **NativeAOT publish fails**: Ensure .NET 9 SDK is installed and `PublishAot=true` is in the `.csproj`
