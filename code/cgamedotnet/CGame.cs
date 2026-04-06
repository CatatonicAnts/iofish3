using System.Runtime.InteropServices;

namespace CGameDotNet;

/// <summary>
/// Core cgame logic — init, shutdown, and per-frame rendering.
/// This is the minimal scaffolding that proves the DLL loads and runs.
/// </summary>
public static unsafe class CGame
{
    // Mirrored from Q3 defines
    private const int MAX_CONFIGSTRINGS = 1024;
    private const int MAX_GAMESTATE_CHARS = 16000;
    private const int CS_SERVERINFO = 0;
    private const int CS_SYSTEMINFO = 1;

    // Game state
    private static int _clientNum;
    private static int _serverMessageNum;
    private static int _serverCommandSequence;
    private static int _lastServerTime;
    private static bool _initialized;

    // Screen dimensions (from glconfig)
    private static int _screenWidth;
    private static int _screenHeight;

    // Registered media handles
    private static int _charsetShader;
    private static int _whiteShader;

    public static void Init(int serverMessageNum, int serverCommandSequence, int clientNum)
    {
        _clientNum = clientNum;
        _serverMessageNum = serverMessageNum;
        _serverCommandSequence = serverCommandSequence;

        Syscalls.Print("[.NET cgame] CG_Init starting...\n");

        // Get GL config for screen dimensions
        // glconfig_t layout: 3×char[1024] + char[8192] + ints...
        // vidWidth at offset 11304, vidHeight at offset 11308
        byte* glconfig = stackalloc byte[11328];
        Syscalls.GetGlconfig(glconfig);
        _screenWidth = *(int*)(glconfig + 11304);
        _screenHeight = *(int*)(glconfig + 11308);
        Syscalls.Print($"[.NET cgame] Screen: {_screenWidth}x{_screenHeight}\n");

        // Register basic shaders
        _charsetShader = Syscalls.R_RegisterShader("gfx/2d/bigchars");
        _whiteShader = Syscalls.R_RegisterShader("white");
        Syscalls.Print($"[.NET cgame] charset={_charsetShader}, white={_whiteShader}\n");

        // Get game state
        // gameState_t: int stringOffsets[MAX_CONFIGSTRINGS] + char stringData[MAX_GAMESTATE_CHARS] + int dataCount + int stringCount
        int gameStateSize = MAX_CONFIGSTRINGS * 4 + MAX_GAMESTATE_CHARS + 4 + 4;
        byte* gameState = stackalloc byte[gameStateSize];
        Syscalls.GetGameState(gameState);

        int dataCount = *(int*)(gameState + MAX_CONFIGSTRINGS * 4 + MAX_GAMESTATE_CHARS);
        int stringCount = *(int*)(gameState + MAX_CONFIGSTRINGS * 4 + MAX_GAMESTATE_CHARS + 4);
        Syscalls.Print($"[.NET cgame] GameState: {stringCount} strings, {dataCount} data bytes\n");

        // Read server info
        string serverInfo = GetConfigString(gameState, CS_SERVERINFO);
        string mapname = InfoValueForKey(serverInfo, "mapname");
        Syscalls.Print($"[.NET cgame] Map: {mapname}\n");

        // Load collision map
        if (!string.IsNullOrEmpty(mapname))
        {
            string bspPath = $"maps/{mapname}.bsp";
            Syscalls.CM_LoadMap(bspPath);
            Syscalls.R_LoadWorldMap(bspPath);
            Syscalls.Print($"[.NET cgame] Loaded map: {bspPath}\n");
        }

        _initialized = true;
        Syscalls.Print("[.NET cgame] CG_Init complete\n");
    }

    public static void Shutdown()
    {
        Syscalls.Print("[.NET cgame] Shutdown\n");
        _initialized = false;
    }

    public static bool ConsoleCommand()
    {
        string cmd = Syscalls.Argv(0);
        Syscalls.Print($"[.NET cgame] Console command: {cmd}\n");
        return false;
    }

    public static void DrawActiveFrame(int serverTime, int stereoView, bool demoPlayback)
    {
        if (!_initialized) return;

        _lastServerTime = serverTime;

        // Clear the scene
        Syscalls.R_ClearScene();

        // Process snapshots to get current game state
        int snapshotNumber = 0;
        int snapServerTime = 0;
        Syscalls.GetCurrentSnapshotNumber(&snapshotNumber, &snapServerTime);

        // Build a minimal refdef and render
        // refdef_t layout: int x, y, width, height; float fov_x, fov_y; vec3_t vieworg; vec3_t viewaxis[3]; int time; int rdflags;
        // Total size needs to accommodate the full struct including areamask
        // We'll use a fixed buffer
        byte* refdef = stackalloc byte[400]; // refdef_t is ~368 bytes
        for (int i = 0; i < 400; i++) refdef[i] = 0;

        // Set viewport
        *(int*)(refdef + 0) = 0;              // x
        *(int*)(refdef + 4) = 0;              // y
        *(int*)(refdef + 8) = _screenWidth;   // width
        *(int*)(refdef + 12) = _screenHeight; // height

        // FOV
        *(float*)(refdef + 16) = 90.0f;  // fov_x
        *(float*)(refdef + 20) = 73.74f; // fov_y (approximate for 16:9)

        // Time
        *(int*)(refdef + 72) = serverTime;

        // Render the scene (world + entities)
        Syscalls.R_RenderScene(refdef);

        // Draw a simple HUD overlay
        DrawHud(serverTime);
    }

    public static int CrosshairPlayer() => -1;
    public static int LastAttacker() => -1;

    public static void KeyEvent(int key, bool down)
    {
    }

    public static void MouseEvent(int dx, int dy)
    {
    }

    public static void EventHandling(int type)
    {
    }

    // ── Private helpers ──

    private static void DrawHud(int serverTime)
    {
        // Draw a small ".NET" indicator in top-right corner
        float* color = stackalloc float[4];
        color[0] = 0.0f; color[1] = 1.0f; color[2] = 0.5f; color[3] = 0.8f; // green-ish
        Syscalls.R_SetColor(color);
        Syscalls.R_DrawStretchPic(580, 4, 56, 16, 0, 0, 1, 1, _whiteShader);

        // Reset color
        float* white = stackalloc float[4];
        white[0] = 1; white[1] = 1; white[2] = 1; white[3] = 1;
        Syscalls.R_SetColor(white);
    }

    private static string GetConfigString(byte* gameState, int index)
    {
        int* offsets = (int*)gameState;
        int offset = offsets[index];
        if (offset == 0 && index != 0) return "";

        byte* stringData = gameState + MAX_CONFIGSTRINGS * 4;
        return Marshal.PtrToStringUTF8((nint)(stringData + offset)) ?? "";
    }

    private static string InfoValueForKey(string info, string key)
    {
        // Q3 info string format: \key\value\key2\value2
        string search = $"\\{key}\\";
        int idx = info.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";

        int start = idx + search.Length;
        int end = info.IndexOf('\\', start);
        if (end < 0) end = info.Length;

        return info.Substring(start, end - start);
    }
}
