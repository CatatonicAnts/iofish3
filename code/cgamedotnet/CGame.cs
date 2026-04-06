using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace CGameDotNet;

/// <summary>
/// Core cgame logic — init, shutdown, per-frame rendering, and snapshot processing.
/// Equivalent to cg_main.c + cg_view.c + cg_snapshot.c from the C cgame.
/// </summary>
public static unsafe class CGame
{
    // ── Static game data (cgs_t equivalent) ──
    private static int _clientNum;
    private static int _processedSnapshotNum;
    private static int _serverCommandSequence;
    private static bool _initialized;

    // Screen
    private static int _screenWidth;
    private static int _screenHeight;
    private static float _screenXScale;
    private static float _screenYScale;

    // Server info
    private static int _gametype;
    private static int _fraglimit;
    private static int _timelimit;
    private static int _maxClients;
    private static int _levelStartTime;
    private static string _mapName = "";

    // Media handles
    private static int _charsetShader;
    private static int _whiteShader;

    // Gamestate raw buffer (heap-allocated, persists for config string lookups)
    private static byte* _gameStateRaw;

    // ── Per-frame state (cg_t equivalent) ──
    private static int _time;
    private static int _oldTime;
    private static float _frameTime;
    private static int _weaponSelect = Weapons.WP_MACHINEGUN;
    private static bool _demoPlayback;

    // Snapshot state
    private static int _latestSnapshotNum;
    private static int _latestSnapshotTime;

    // Snapshot buffers (heap-allocated, ~54KB each)
    private static byte* _snapBuffer1;
    private static byte* _snapBuffer2;
    private static Q3Snapshot* _snap;      // current snapshot
    private static Q3Snapshot* _nextSnap;  // next snapshot for interpolation

    // Snapshot size
    private static readonly int SnapshotSize = sizeof(Q3Snapshot) +
        (Q3Snapshot.MAX_ENTITIES_IN_SNAPSHOT - 1) * sizeof(Q3EntityState);

    // ── Init ──
    public static void Init(int serverMessageNum, int serverCommandSequence, int clientNum)
    {
        _clientNum = clientNum;
        _processedSnapshotNum = serverMessageNum;
        _serverCommandSequence = serverCommandSequence;
        _snap = null;
        _nextSnap = null;

        Syscalls.Print("[.NET cgame] CG_Init starting...\n");

        // Allocate snapshot buffers
        _snapBuffer1 = (byte*)NativeMemory.AllocZeroed((nuint)SnapshotSize);
        _snapBuffer2 = (byte*)NativeMemory.AllocZeroed((nuint)SnapshotSize);

        // Get GL config
        byte* glconfig = stackalloc byte[Q3GlConfig.SIZE];
        Syscalls.GetGlconfig(glconfig);
        _screenWidth = *(int*)(glconfig + Q3GlConfig.VID_WIDTH);
        _screenHeight = *(int*)(glconfig + Q3GlConfig.VID_HEIGHT);
        _screenXScale = _screenWidth / 640.0f;
        _screenYScale = _screenHeight / 480.0f;
        Syscalls.Print($"[.NET cgame] Screen: {_screenWidth}x{_screenHeight}\n");

        // Register minimal shaders for loading screen
        _charsetShader = Syscalls.R_RegisterShader("gfx/2d/bigchars");
        _whiteShader = Syscalls.R_RegisterShader("white");

        // Get gamestate
        _gameStateRaw = (byte*)NativeMemory.AllocZeroed((nuint)Q3GameState.RAW_SIZE);
        Syscalls.GetGameState(_gameStateRaw);

        // Parse server info
        ParseServerInfo();

        // Load map
        if (!string.IsNullOrEmpty(_mapName))
        {
            Syscalls.CM_LoadMap(_mapName);
            Syscalls.R_LoadWorldMap(_mapName);
            Syscalls.Print($"[.NET cgame] Loaded map: {_mapName}\n");
        }

        // Read level start time
        string startTimeStr = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_LEVEL_START_TIME);
        _levelStartTime = int.TryParse(startTimeStr, out int st) ? st : 0;

        _weaponSelect = Weapons.WP_MACHINEGUN;
        _initialized = true;
        Syscalls.Print("[.NET cgame] CG_Init complete\n");
    }

    public static void Shutdown()
    {
        Syscalls.Print("[.NET cgame] Shutdown\n");
        _initialized = false;

        if (_snapBuffer1 != null) { NativeMemory.Free(_snapBuffer1); _snapBuffer1 = null; }
        if (_snapBuffer2 != null) { NativeMemory.Free(_snapBuffer2); _snapBuffer2 = null; }
        if (_gameStateRaw != null) { NativeMemory.Free(_gameStateRaw); _gameStateRaw = null; }
        _snap = null;
        _nextSnap = null;
    }

    public static bool ConsoleCommand()
    {
        return false;
    }

    // ── DrawActiveFrame — main per-frame entry point ──
    public static void DrawActiveFrame(int serverTime, int stereoView, bool demoPlayback)
    {
        if (!_initialized) return;

        _oldTime = _time;
        _time = serverTime;
        _demoPlayback = demoPlayback;
        _frameTime = (_time - _oldTime) * 0.001f;
        if (_frameTime < 0) _frameTime = 0;
        if (_frameTime > 0.2f) _frameTime = 0.2f;

        // Clear looping sounds
        Syscalls.S_ClearLoopingSounds(0);

        // Clear render scene
        Syscalls.R_ClearScene();

        // Process snapshots — updates _snap and _nextSnap
        ProcessSnapshots();

        // If no snapshot yet, can't render
        if (_snap == null)
            return;

        // If snapshot is not active (spectating/loading), skip
        if ((_snap->SnapFlags & SnapFlags.SNAPFLAG_NOT_ACTIVE) != 0)
            return;

        // Tell engine our weapon selection
        Syscalls.SetUserCmdValue(_weaponSelect, 1.0f);

        // Track selected weapon from player state
        _weaponSelect = _snap->Ps.Weapon;

        // Build refdef from player state
        Q3RefDef refdef = default;
        CalcViewValues(ref refdef);

        // Add entities from snapshot to the scene
        AddPacketEntities();

        // Finalize refdef
        refdef.Time = _time;
        // Copy areamask from snapshot
        for (int i = 0; i < Q3RefDef.MAX_MAP_AREA_BYTES; i++)
            refdef.Areamask[i] = _snap->Areamask[i];

        // Render 3D scene
        Syscalls.R_RenderScene(&refdef);

        // Draw 2D HUD overlay
        DrawHud();
    }

    public static int CrosshairPlayer() => -1;
    public static int LastAttacker() => -1;
    public static void KeyEvent(int key, bool down) { }
    public static void MouseEvent(int dx, int dy) { }
    public static void EventHandling(int type) { }

    // ── Snapshot Processing (cg_snapshot.c equivalent) ──

    private static void ProcessSnapshots()
    {
        // Get latest snapshot number from engine
        int n = 0, snapTime = 0;
        Syscalls.GetCurrentSnapshotNumber(&n, &snapTime);
        _latestSnapshotTime = snapTime;

        if (n != _latestSnapshotNum)
        {
            if (n < _latestSnapshotNum)
            {
                Syscalls.Print("[.NET cgame] ERROR: snapshot number went backwards\n");
            }
            _latestSnapshotNum = n;
        }

        // If we don't have a current snapshot, try to get one
        while (_snap == null)
        {
            var snap = ReadNextSnapshot();
            if (snap == null)
                return; // no snapshots available yet

            if ((snap->SnapFlags & SnapFlags.SNAPFLAG_NOT_ACTIVE) == 0)
            {
                SetInitialSnapshot(snap);
            }
        }

        // Process server commands that came with recent snapshots
        DrainServerCommands();

        // Try to get nextSnap for interpolation
        while (true)
        {
            if (_nextSnap == null)
            {
                var snap = ReadNextSnapshot();
                if (snap == null)
                    break;
                SetNextSnap(snap);
            }

            // If our time is between snap and nextSnap, we're interpolating
            if (_time >= _snap->ServerTime && _time < _nextSnap->ServerTime)
                break;

            // We've passed the transition point — advance
            TransitionSnapshot();
        }

        // Clamp time
        if (_time < _snap->ServerTime)
            _time = _snap->ServerTime;
    }

    private static Q3Snapshot* ReadNextSnapshot()
    {
        while (_processedSnapshotNum < _latestSnapshotNum)
        {
            _processedSnapshotNum++;

            // Use whichever buffer isn't currently _snap
            byte* buf = (_snap == (Q3Snapshot*)_snapBuffer1) ? _snapBuffer2 : _snapBuffer1;

            if (Syscalls.GetSnapshot(_processedSnapshotNum, buf))
            {
                return (Q3Snapshot*)buf;
            }
        }
        return null;
    }

    private static void SetInitialSnapshot(Q3Snapshot* snap)
    {
        _snap = snap;
        // Process server commands up to this snapshot
        DrainServerCommands();
    }

    private static void SetNextSnap(Q3Snapshot* snap)
    {
        _nextSnap = snap;
    }

    private static void TransitionSnapshot()
    {
        // nextSnap becomes current snap
        _snap = _nextSnap;
        _nextSnap = null;
    }

    private static void DrainServerCommands()
    {
        if (_snap == null) return;

        // Process all pending server commands
        while (_serverCommandSequence < _snap->GetServerCommandSequence())
        {
            _serverCommandSequence++;
            Syscalls.GetServerCommand(_serverCommandSequence);
        }
    }

    // ── View Calculation (cg_view.c equivalent) ──

    private static void CalcViewValues(ref Q3RefDef refdef)
    {
        ref var ps = ref _snap->Ps;

        // Viewport
        refdef.X = 0;
        refdef.Y = 0;
        refdef.Width = _screenWidth;
        refdef.Height = _screenHeight;

        // View origin — player origin + viewheight offset
        refdef.ViewOrgX = ps.OriginX;
        refdef.ViewOrgY = ps.OriginY;
        refdef.ViewOrgZ = ps.OriginZ + ps.ViewHeight;

        // If interpolating between snapshots, lerp the origin
        if (_nextSnap != null && _snap->ServerTime != _nextSnap->ServerTime)
        {
            float f = (float)(_time - _snap->ServerTime) /
                      (float)(_nextSnap->ServerTime - _snap->ServerTime);
            if (f < 0) f = 0;
            if (f > 1) f = 1;

            ref var nextPs = ref _nextSnap->Ps;
            refdef.ViewOrgX = ps.OriginX + f * (nextPs.OriginX - ps.OriginX);
            refdef.ViewOrgY = ps.OriginY + f * (nextPs.OriginY - ps.OriginY);
            refdef.ViewOrgZ = ps.OriginZ + f * (nextPs.OriginZ - ps.OriginZ) + ps.ViewHeight;
        }

        // View angles → axis matrix
        float pitch = ps.ViewAnglesX * MathF.PI / 180.0f;
        float yaw = ps.ViewAnglesY * MathF.PI / 180.0f;
        float roll = ps.ViewAnglesZ * MathF.PI / 180.0f;
        AnglesToAxis(pitch, yaw, roll, ref refdef);

        // FOV (default 90, corrected for aspect ratio)
        float fovX = 90.0f;
        float aspect = (float)_screenWidth / _screenHeight;
        float fovY = 2.0f * MathF.Atan(MathF.Tan(fovX * MathF.PI / 360.0f) / aspect) * 180.0f / MathF.PI;

        refdef.FovX = fovX;
        refdef.FovY = fovY;
    }

    private static void AnglesToAxis(float pitch, float yaw, float roll, ref Q3RefDef refdef)
    {
        float sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
        float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
        float sr = MathF.Sin(roll), cr = MathF.Cos(roll);

        // Forward (axis[0])
        refdef.Axis0X = cp * cy;
        refdef.Axis0Y = cp * sy;
        refdef.Axis0Z = -sp;

        // Right (axis[1]) — Q3 convention: right = left-handed cross
        refdef.Axis1X = (-sr * sp * cy + cr * (-sy));
        refdef.Axis1Y = (-sr * sp * sy + cr * cy);
        refdef.Axis1Z = -sr * cp;

        // Up (axis[2])
        refdef.Axis2X = (cr * sp * cy + sr * (-sy));
        refdef.Axis2Y = (cr * sp * sy + sr * cy);
        refdef.Axis2Z = cr * cp;
    }

    // ── Entity Rendering ──

    private static void AddPacketEntities()
    {
        if (_snap == null) return;

        for (int i = 0; i < _snap->NumEntities; i++)
        {
            ref var es = ref _snap->GetEntity(i);

            // Skip invisible entities
            if (es.EType >= EntityType.ET_EVENTS)
                continue;

            Q3RefEntity rent = default;

            switch (es.EType)
            {
                case EntityType.ET_PLAYER:
                case EntityType.ET_GENERAL:
                case EntityType.ET_MOVER:
                case EntityType.ET_MISSILE:
                    rent.ReType = Q3RefEntity.RT_MODEL;
                    rent.HModel = es.ModelIndex;
                    rent.OriginX = es.OriginX;
                    rent.OriginY = es.OriginY;
                    rent.OriginZ = es.OriginZ;
                    rent.OldOriginX = es.OriginX;
                    rent.OldOriginY = es.OriginY;
                    rent.OldOriginZ = es.OriginZ;
                    rent.FrameNum = es.Frame;
                    rent.OldFrame = es.Frame;
                    rent.Backlerp = 0;
                    rent.SkinNum = 0;
                    rent.ShaderRGBA_R = 255;
                    rent.ShaderRGBA_G = 255;
                    rent.ShaderRGBA_B = 255;
                    rent.ShaderRGBA_A = 255;

                    // Set axis from angles
                    float pitch = es.AnglesX * MathF.PI / 180.0f;
                    float yaw = es.AnglesY * MathF.PI / 180.0f;
                    float roll = es.AnglesZ * MathF.PI / 180.0f;
                    float sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
                    float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
                    float sr = MathF.Sin(roll), cr = MathF.Cos(roll);

                    rent.Axis0X = cp * cy;
                    rent.Axis0Y = cp * sy;
                    rent.Axis0Z = -sp;
                    rent.Axis1X = -sr * sp * cy + cr * -sy;
                    rent.Axis1Y = -sr * sp * sy + cr * cy;
                    rent.Axis1Z = -sr * cp;
                    rent.Axis2X = cr * sp * cy + sr * -sy;
                    rent.Axis2Y = cr * sp * sy + sr * cy;
                    rent.Axis2Z = cr * cp;

                    if (es.ModelIndex > 0)
                        Syscalls.R_AddRefEntityToScene(&rent);
                    break;

                case EntityType.ET_ITEM:
                    rent.ReType = Q3RefEntity.RT_MODEL;
                    rent.HModel = es.ModelIndex;
                    rent.OriginX = es.OriginX;
                    rent.OriginY = es.OriginY;
                    rent.OriginZ = es.OriginZ;
                    rent.OldOriginX = es.OriginX;
                    rent.OldOriginY = es.OriginY;
                    rent.OldOriginZ = es.OriginZ;
                    rent.ShaderRGBA_R = 255;
                    rent.ShaderRGBA_G = 255;
                    rent.ShaderRGBA_B = 255;
                    rent.ShaderRGBA_A = 255;

                    // Items rotate — apply yaw from time
                    float itemYaw = (_time & 2047) * 360.0f / 2048.0f;
                    float iy = itemYaw * MathF.PI / 180.0f;
                    float siy = MathF.Sin(iy), ciy = MathF.Cos(iy);
                    rent.Axis0X = ciy; rent.Axis0Y = siy; rent.Axis0Z = 0;
                    rent.Axis1X = -siy; rent.Axis1Y = ciy; rent.Axis1Z = 0;
                    rent.Axis2X = 0; rent.Axis2Y = 0; rent.Axis2Z = 1;

                    // Items bob up and down
                    float bobPhase = (es.Number & 7) * MathF.PI * 2.0f / 8.0f;
                    rent.OriginZ += 4 + MathF.Cos((_time * 0.005f) + bobPhase) * 4;

                    if (es.ModelIndex > 0)
                        Syscalls.R_AddRefEntityToScene(&rent);
                    break;

                case EntityType.ET_BEAM:
                case EntityType.ET_PORTAL:
                case EntityType.ET_SPEAKER:
                case EntityType.ET_PUSH_TRIGGER:
                case EntityType.ET_TELEPORT_TRIGGER:
                case EntityType.ET_INVISIBLE:
                    break;
            }
        }
    }

    // ── 2D HUD ──

    private static void DrawHud()
    {
        if (_snap == null) return;

        ref var ps = ref _snap->Ps;
        int health = ps.Stats[Stats.STAT_HEALTH];
        int armor = ps.Stats[Stats.STAT_ARMOR];

        // Background bar
        float* bgColor = stackalloc float[4];
        bgColor[0] = 0; bgColor[1] = 0; bgColor[2] = 0; bgColor[3] = 0.5f;
        Syscalls.R_SetColor(bgColor);
        Syscalls.R_DrawStretchPic(0, 440, 640, 40, 0, 0, 1, 1, _whiteShader);

        // Health
        float* hpColor = stackalloc float[4];
        if (health > 50) { hpColor[0] = 0; hpColor[1] = 1; hpColor[2] = 0; }
        else if (health > 25) { hpColor[0] = 1; hpColor[1] = 1; hpColor[2] = 0; }
        else { hpColor[0] = 1; hpColor[1] = 0; hpColor[2] = 0; }
        hpColor[3] = 1;
        Syscalls.R_SetColor(hpColor);
        float hpWidth = MathF.Max(0, MathF.Min(health, 200)) / 200.0f * 200.0f;
        Syscalls.R_DrawStretchPic(20, 450, hpWidth, 20, 0, 0, 1, 1, _whiteShader);

        // Armor
        float* armorColor = stackalloc float[4];
        armorColor[0] = 0.3f; armorColor[1] = 0.5f; armorColor[2] = 1; armorColor[3] = 1;
        Syscalls.R_SetColor(armorColor);
        float armorWidth = MathF.Max(0, MathF.Min(armor, 200)) / 200.0f * 200.0f;
        Syscalls.R_DrawStretchPic(420, 450, armorWidth, 20, 0, 0, 1, 1, _whiteShader);

        // .NET indicator
        float* greenColor = stackalloc float[4];
        greenColor[0] = 0; greenColor[1] = 1; greenColor[2] = 0.5f; greenColor[3] = 0.6f;
        Syscalls.R_SetColor(greenColor);
        Syscalls.R_DrawStretchPic(590, 4, 46, 12, 0, 0, 1, 1, _whiteShader);

        // Reset color
        Syscalls.R_SetColor(null);
    }

    // ── Helpers ──

    private static void ParseServerInfo()
    {
        string info = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_SERVERINFO);
        _gametype = InfoInt(info, "g_gametype");
        _fraglimit = InfoInt(info, "fraglimit");
        _timelimit = InfoInt(info, "timelimit");
        _maxClients = InfoInt(info, "sv_maxclients");

        string mapname = InfoValueForKey(info, "mapname");
        _mapName = $"maps/{mapname}.bsp";

        Syscalls.Print($"[.NET cgame] Server: gametype={_gametype}, map={mapname}, maxclients={_maxClients}\n");
    }

    private static int InfoInt(string info, string key)
    {
        string val = InfoValueForKey(info, key);
        return int.TryParse(val, out int result) ? result : 0;
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

        return info[start..end];
    }
}
