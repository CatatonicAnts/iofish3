using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace CGameDotNet;

/// <summary>
/// Core cgame logic — init, shutdown, per-frame rendering, and snapshot processing.
/// Equivalent to cg_main.c + cg_view.c + cg_snapshot.c + cg_ents.c from the C cgame.
/// </summary>
public static unsafe class CGame
{
    // ── Constants ──
    private const int MAX_GENTITIES = 1024;
    private const int MAX_CLIENTS = 64;
    private const int MAX_MODELS = 256;
    private const int MAX_SOUNDS = 256;
    private const int SOLID_BMODEL = 0xffffff;
    private const int DEFAULT_GRAVITY = 800;
    private const int EF_TELEPORT_BIT = 0x00000004;

    // ── Per-entity persistent state (centity_t equivalent) ──
    private struct CEntity
    {
        public Q3EntityState CurrentState;
        public Q3EntityState NextState;
        public bool Interpolate;  // nextState valid for interpolation
        public bool CurrentValid; // currentState is valid
        public int SnapShotTime; // last time found in snapshot
        public int PreviousEvent; // last processed event (for dedup)

        // Interpolated values (computed each frame)
        public float LerpOriginX, LerpOriginY, LerpOriginZ;
        public float LerpAnglesX, LerpAnglesY, LerpAnglesZ;
    }

    // ── Static game data (cgs_t equivalent) ──
    private static int _clientNum;
    private static int _processedSnapshotNum;
    private static int _serverCommandSequence;
    private static bool _initialized;

    // Screen
    private static int _screenWidth;
    private static int _screenHeight;

    // Server info
    private static int _gametype;
    private static int _maxClients;
    private static int _levelStartTime;
    private static string _mapName = "";

    // HUD constants (640x480 virtual coords)
    private const int SCREEN_WIDTH = 640;
    private const int SCREEN_HEIGHT = 480;
    private const int CHAR_WIDTH = 32;   // status bar number width
    private const int CHAR_HEIGHT = 48;  // status bar number height
    private const int BIGCHAR_WIDTH = 16;
    private const int BIGCHAR_HEIGHT = 16;
    private const int ICON_SIZE = 48;
    private const int TEXT_ICON_SPACE = 4;

    // Screen scale factors (actual resolution / virtual 640x480)
    private static float _screenXScale;
    private static float _screenYScale;
    private const int STAT_MINUS = 10;
    private const int NUM_CROSSHAIRS = 10;
    private const int FPS_FRAMES = 4;

    // Media handles
    private static int _charsetShader;
    private static int _whiteShader;
    private static int _selectShader;
    private static int _noammoShader;
    private static readonly int[] _crosshairShaders = new int[NUM_CROSSHAIRS];
    private static readonly int[] _numberShaders = new int[11]; // 0-9 + minus

    // FPS tracking
    private static readonly int[] _fpsFrameTimes = new int[FPS_FRAMES];
    private static int _fpsIndex;
    private static int _fpsPreviousTime;

    // Weapon icons (per weapon type)
    private static readonly int[] _weaponIcons = new int[Weapons.WP_NUM_WEAPONS];
    private static int _explosionShader;

    // Registered models/sounds from config strings
    private static readonly int[] _gameModels = new int[MAX_MODELS];
    private static readonly int[] _gameSounds = new int[MAX_SOUNDS];
    private static readonly int[] _inlineDrawModel = new int[MAX_MODELS];

    // Gamestate raw buffer
    private static byte* _gameStateRaw;

    // ── Per-frame state (cg_t equivalent) ──
    private static int _time;
    private static int _oldTime;
    private static float _frameTime;
    private static float _frameInterpolation;
    private static int _weaponSelect = Weapons.WP_MACHINEGUN;
    private static bool _demoPlayback;

    // Prediction state
    private static bool _nextFrameTeleport;
    private static bool _thisFrameTeleport;

    // View bob state
    private static int _bobCycle;
    private static float _bobFracSin;
    private static float _xySpeed;

    // Step smoothing (stair climb)
    private static float _stepChange;
    private static int _stepTime;

    // Duck smoothing
    private static float _duckChange;
    private static int _duckTime;

    // Landing effect
    private static float _landChange;
    private static int _landTime;

    // Damage feedback
    private static int _damageTime;
    private static float _damageX, _damageY;
    private static float _damageValue;
    private static float _vDmgPitch, _vDmgRoll;
    private static int _lastDamageEvent;

    // Zoom state
    private static bool _zoomed;
    private static int _zoomTime;

    // View blood shader
    private static int _viewBloodShader;

    // Previous view height for duck tracking
    private static int _lastViewHeight;

    // Center print display
    private static string _centerPrint = "";
    private static int _centerPrintTime;
    private const int CENTER_PRINT_DURATION = 3000; // 3 seconds

    // Chat messages (ring buffer)
    private const int MAX_CHAT_LINES = 8;
    private static readonly string[] _chatMessages = new string[MAX_CHAT_LINES];
    private static readonly int[] _chatTimes = new int[MAX_CHAT_LINES];
    private static int _chatIndex;
    private const int CHAT_DISPLAY_TIME = 6000; // 6 seconds

    // Item pickup notification
    private static string _pickupName = "";
    private static int _pickupTime;
    private const int PICKUP_DISPLAY_TIME = 3000;

    // Snapshot state
    private static int _latestSnapshotNum;
    private static int _latestSnapshotTime;

    // Snapshot buffers (~54KB each)
    private static byte* _snapBuffer1;
    private static byte* _snapBuffer2;
    private static Q3Snapshot* _snap;
    private static Q3Snapshot* _nextSnap;

    private static readonly int SnapshotSize = sizeof(Q3Snapshot) +
        (Q3Snapshot.MAX_ENTITIES_IN_SNAPSHOT - 1) * sizeof(Q3EntityState) +
        8; // +8 for numServerCommands + serverCommandSequence after entities

    // Entity tracking array
    private static CEntity[] _entities = new CEntity[MAX_GENTITIES];

    // ── Init ──
    public static void Init(int serverMessageNum, int serverCommandSequence, int clientNum)
    {
        _clientNum = clientNum;
        _processedSnapshotNum = serverMessageNum;
        _serverCommandSequence = serverCommandSequence;
        _snap = null;
        _nextSnap = null;
        _entities = new CEntity[MAX_GENTITIES];

        Syscalls.Print("[.NET cgame] CG_Init starting...\n");
        CrashLog.Breadcrumb("Init: allocating buffers");

        // Allocate snapshot buffers
        _snapBuffer1 = (byte*)NativeMemory.AllocZeroed((nuint)SnapshotSize);
        _snapBuffer2 = (byte*)NativeMemory.AllocZeroed((nuint)SnapshotSize);

        CrashLog.Breadcrumb("Init: GL config + gamestate");

        // Get GL config
        byte* glconfig = stackalloc byte[Q3GlConfig.SIZE];
        Syscalls.GetGlconfig(glconfig);
        _screenWidth = *(int*)(glconfig + Q3GlConfig.VID_WIDTH);
        _screenHeight = *(int*)(glconfig + Q3GlConfig.VID_HEIGHT);
        _screenXScale = _screenWidth / (float)SCREEN_WIDTH;
        _screenYScale = _screenHeight / (float)SCREEN_HEIGHT;
        Syscalls.Print($"[.NET cgame] Screen: {_screenWidth}x{_screenHeight}\n");

        // Get gamestate
        _gameStateRaw = (byte*)NativeMemory.AllocZeroed((nuint)Q3GameState.RAW_SIZE);
        Syscalls.GetGameState(_gameStateRaw);

        // Parse server info
        ParseServerInfo();

        CrashLog.Breadcrumb("Init: loading map");

        // Load map
        if (!string.IsNullOrEmpty(_mapName))
        {
            Syscalls.CM_LoadMap(_mapName);
            Syscalls.R_LoadWorldMap(_mapName);
            Syscalls.Print($"[.NET cgame] Loaded map: {_mapName}\n");
        }

        CrashLog.Breadcrumb("Init: registering media");

        // Register models from config strings
        RegisterGraphics();

        // Register event sounds
        RegisterEventSounds();

        // Register console commands
        InitConsoleCommands();

        CrashLog.Breadcrumb("Init: subsystems");

        // Initialize local entity and mark systems
        LocalEntities.Init();
        Marks.Init();
        Scoreboard.Init();
        WeaponEffects.Init();
        Player.Init();

        CrashLog.Breadcrumb("Init: client info");

        // Load client info for all players
        for (int i = 0; i < 64; i++)
            Player.NewClientInfo(i, _gameStateRaw);

        // Read level start time
        string startTimeStr = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_LEVEL_START_TIME);
        _levelStartTime = int.TryParse(startTimeStr, out int st) ? st : 0;

        _weaponSelect = Weapons.WP_MACHINEGUN;
        _nextFrameTeleport = false;
        _thisFrameTeleport = false;
        _bobCycle = 0; _bobFracSin = 0; _xySpeed = 0;
        _stepChange = 0; _stepTime = 0;
        _duckChange = 0; _duckTime = 0;
        _landChange = 0; _landTime = 0;
        _damageTime = 0; _damageValue = 0; _damageX = 0; _damageY = 0;
        _vDmgPitch = 0; _vDmgRoll = 0; _lastDamageEvent = 0;
        _zoomed = false; _zoomTime = 0;
        _lastViewHeight = 0;
        _centerPrint = ""; _centerPrintTime = 0;
        _chatIndex = 0; _pickupName = ""; _pickupTime = 0;
        for (int i = 0; i < MAX_CHAT_LINES; i++) { _chatMessages[i] = ""; _chatTimes[i] = 0; }
        Prediction.Reset();
        _initialized = true;
        CrashLog.Breadcrumb("Init: DONE");
        Syscalls.Print("[.NET cgame] CG_Init complete\n");
    }

    public static void Shutdown()
    {
        Syscalls.Print($"[.NET cgame] Shutdown (frames rendered: {_frameCount})\n");
        _initialized = false;
        _frameCount = 0;
        _dumpedEntities = false;

        // Don't free native memory — the DLL stays loaded and these
        // buffers will be re-allocated on next CG_Init. Freeing here
        // can trigger heap corruption asserts if any system wrote
        // out of bounds during the session.
        _snapBuffer1 = null;
        _snapBuffer2 = null;
        _gameStateRaw = null;
        _snap = null;
        _nextSnap = null;
    }

    public static bool ConsoleCommand()
    {
        string cmd = Syscalls.Argv(0);
        Syscalls.Print($"[.NET cgame] ConsoleCommand: {cmd}\n");

        if (cmd == "+scores") { Scoreboard.ScoresDown(); return true; }
        if (cmd == "-scores") { Scoreboard.ScoresUp(); return true; }
        if (cmd == "+zoom") { _zoomed = true; _zoomTime = _time; return true; }
        if (cmd == "-zoom") { _zoomed = false; _zoomTime = _time; return true; }
        if (cmd == "weapnext") { NextWeapon(); return true; }
        if (cmd == "weapprev") { PrevWeapon(); return true; }
        if (cmd == "weapon") { SelectWeapon(); return true; }

        // Server-forwarded commands — return false to let engine handle
        return false;
    }

    // ── Console Commands ──

    private static readonly string[] _serverCommands = new[]
    {
        "kill", "say", "say_team", "tell",
        "give", "god", "notarget", "noclip", "where",
        "team", "follow", "follownext", "followprev",
        "addbot", "setviewpos", "callvote", "vote",
        "callteamvote", "teamvote", "stats", "teamtask",
        "loaddefered"
    };

    private static void InitConsoleCommands()
    {
        foreach (string cmd in _serverCommands)
            Syscalls.AddCommand(cmd);

        Syscalls.AddCommand("+scores");
        Syscalls.AddCommand("-scores");
        Syscalls.AddCommand("+zoom");
        Syscalls.AddCommand("-zoom");
        Syscalls.AddCommand("weapnext");
        Syscalls.AddCommand("weapprev");
        Syscalls.AddCommand("weapon");
    }

    // ── DrawActiveFrame — main per-frame entry point ──
    private static int _frameCount;
    public static void DrawActiveFrame(int serverTime, int stereoView, bool demoPlayback)
    {
        if (!_initialized) return;

        _frameCount++;
        if (_frameCount <= 3)
            Syscalls.Print($"[.NET cgame] DrawActiveFrame #{_frameCount}: serverTime={serverTime}\n");

        CrashLog.Breadcrumb($"DrawActiveFrame #{_frameCount} t={serverTime}");

        _oldTime = _time;
        _time = serverTime;
        _demoPlayback = demoPlayback;
        _frameTime = (_time - _oldTime) * 0.001f;
        if (_frameTime < 0) _frameTime = 0;
        if (_frameTime > 0.2f) _frameTime = 0.2f;

        Syscalls.S_ClearLoopingSounds(0);
        Syscalls.R_ClearScene();

        CrashLog.Breadcrumb("ProcessSnapshots");
        try { ProcessSnapshots(); }
        catch (Exception ex) { CrashLog.LogException("ProcessSnapshots", ex); Syscalls.Print($"[.NET cgame] ERROR in ProcessSnapshots: {ex.Message}\n"); }

        if (_snap == null)
        {
            if (_frameCount <= 5) Syscalls.Print("[.NET cgame] DrawActiveFrame: _snap is null, skipping\n");
            return;
        }
        if ((_snap->SnapFlags & SnapFlags.SNAPFLAG_NOT_ACTIVE) != 0)
        {
            if (_frameCount <= 5) Syscalls.Print("[.NET cgame] DrawActiveFrame: SNAPFLAG_NOT_ACTIVE, skipping\n");
            return;
        }

        // Send desired weapon to server each frame
        Syscalls.SetUserCmdValue(_weaponSelect, 1.0f);

        // Run prediction
        CrashLog.Breadcrumb("PredictPlayerState");
        try
        {
            int team = _snap->Ps.Persistant[Persistant.PERS_TEAM];
            bool hasNext = _nextSnap != null;
            Q3PlayerState* nextPs = hasNext ? &_nextSnap->Ps : null;
            Prediction.PredictPlayerState(
                &_snap->Ps,
                nextPs,
                hasNext,
                _snap->ServerTime,
                hasNext ? _nextSnap->ServerTime : 0,
                _time, _oldTime,
                _demoPlayback,
                _snap->Ps.PmFlags,
                _nextFrameTeleport, _thisFrameTeleport,
                noPredict: false, synchronousClients: false,
                dmflags: 0, pmoveFixed: 0, pmoveMsec: 8,
                team: team);
        }
        catch (Exception ex) { CrashLog.LogException("PredictPlayerState", ex); Syscalls.Print($"[.NET cgame] ERROR in PredictPlayerState: {ex.Message}\n"); }

        // Track duck height changes for smooth transition
        TrackDuckOffset();

        // Calculate frame interpolation factor
        if (_nextSnap != null)
        {
            int delta = _nextSnap->ServerTime - _snap->ServerTime;
            if (delta > 0)
                _frameInterpolation = (float)(_time - _snap->ServerTime) / delta;
            else
                _frameInterpolation = 0;
            if (_frameInterpolation < 0) _frameInterpolation = 0;
            if (_frameInterpolation > 1) _frameInterpolation = 1;
        }
        else
        {
            _frameInterpolation = 0;
        }

        Q3RefDef refdef = default;
        CrashLog.Breadcrumb("CalcViewValues");
        CalcViewValues(ref refdef);

        CrashLog.Breadcrumb("AddPacketEntities");
        try { AddPacketEntities(); }
        catch (Exception ex) { CrashLog.LogException("AddPacketEntities", ex); Syscalls.Print($"[.NET cgame] ERROR in AddPacketEntities: {ex.Message}\n"); }

        CrashLog.Breadcrumb("LocalEntities");
        try { LocalEntities.AddToScene(_time); }
        catch (Exception ex) { CrashLog.LogException("LocalEntities", ex); Syscalls.Print($"[.NET cgame] ERROR in LocalEntities.AddToScene: {ex.Message}\n"); }

        CrashLog.Breadcrumb("Marks");
        try { Marks.AddToScene(_time); }
        catch (Exception ex) { CrashLog.LogException("Marks", ex); Syscalls.Print($"[.NET cgame] ERROR in Marks.AddToScene: {ex.Message}\n"); }

        // First-person view weapon
        CrashLog.Breadcrumb("AddViewWeapon");
        try
        {
            fixed (Q3PlayerState* pps = &Prediction.PredictedPlayerState)
            {
                Player.AddViewWeapon(pps, _time,
                    refdef.ViewOrgX, refdef.ViewOrgY, refdef.ViewOrgZ,
                    refdef.Axis0X, refdef.Axis0Y, refdef.Axis0Z,
                    refdef.Axis1X, refdef.Axis1Y, refdef.Axis1Z,
                    refdef.Axis2X, refdef.Axis2Y, refdef.Axis2Z,
                    (int)refdef.FovX);
            }
        }
        catch (Exception ex) { CrashLog.LogException("AddViewWeapon", ex); Syscalls.Print($"[.NET cgame] ERROR in AddViewWeapon: {ex.Message}\n"); }

        CrashLog.Breadcrumb("RenderScene");
        DamageBlendBlob(ref refdef);
        refdef.Time = _time;
        for (int i = 0; i < Q3RefDef.MAX_MAP_AREA_BYTES; i++)
            refdef.Areamask[i] = _snap->Areamask[i];

        Syscalls.R_RenderScene(&refdef);
        try { DrawHud(); }
        catch (Exception ex) { Syscalls.Print($"[.NET cgame] ERROR in DrawHud: {ex.Message}\n"); }
    }

    public static int CrosshairPlayer() => -1;
    public static int LastAttacker() => -1;
    public static void KeyEvent(int key, bool down) { }
    public static void MouseEvent(int dx, int dy) { }
    public static void EventHandling(int type) { }

    // ── Media Registration ──

    private static void RegisterGraphics()
    {
        _charsetShader = Syscalls.R_RegisterShader("gfx/2d/bigchars");
        _whiteShader = Syscalls.R_RegisterShader("white");
        _selectShader = Syscalls.R_RegisterShader("gfx/2d/select");
        _noammoShader = Syscalls.R_RegisterShader("icons/noammo");
        _explosionShader = Syscalls.R_RegisterShader("rocketExplosion");
        _viewBloodShader = Syscalls.R_RegisterShader("viewBloodBlend");

        // Number shaders (0-9 + minus)
        string[] numNames = {
            "gfx/2d/numbers/zero_32b", "gfx/2d/numbers/one_32b",
            "gfx/2d/numbers/two_32b", "gfx/2d/numbers/three_32b",
            "gfx/2d/numbers/four_32b", "gfx/2d/numbers/five_32b",
            "gfx/2d/numbers/six_32b", "gfx/2d/numbers/seven_32b",
            "gfx/2d/numbers/eight_32b", "gfx/2d/numbers/nine_32b",
            "gfx/2d/numbers/minus_32b"
        };
        for (int i = 0; i < 11; i++)
            _numberShaders[i] = Syscalls.R_RegisterShader(numNames[i]);

        // Crosshair shaders (a-j)
        for (int i = 0; i < NUM_CROSSHAIRS; i++)
            _crosshairShaders[i] = Syscalls.R_RegisterShader($"gfx/2d/crosshair{(char)('a' + i)}");

        // Weapon icons
        string[] weaponIconPaths = {
            "", // WP_NONE
            "icons/iconw_gauntlet", // WP_GAUNTLET
            "icons/iconw_machinegun", // WP_MACHINEGUN
            "icons/iconw_shotgun", // WP_SHOTGUN
            "icons/iconw_grenade", // WP_GRENADE_LAUNCHER
            "icons/iconw_rocket", // WP_ROCKET_LAUNCHER
            "icons/iconw_lightning", // WP_LIGHTNING
            "icons/iconw_railgun", // WP_RAILGUN
            "icons/iconw_plasma", // WP_PLASMAGUN
            "icons/iconw_bfg", // WP_BFG
            "icons/iconw_grapple" // WP_GRAPPLING_HOOK
        };
        for (int i = 1; i < weaponIconPaths.Length && i < Weapons.WP_NUM_WEAPONS; i++)
            _weaponIcons[i] = Syscalls.R_RegisterShaderNoMip(weaponIconPaths[i]);

        // Register inline BSP models
        int numInlineModels = Syscalls.CM_NumInlineModels();
        for (int i = 1; i < numInlineModels; i++)
        {
            string name = $"*{i}";
            _inlineDrawModel[i] = Syscalls.R_RegisterModel(name);
        }
        Syscalls.Print($"[.NET cgame] Registered {numInlineModels - 1} inline models\n");

        // Register server-specified models from config strings
        int modelCount = 0;
        for (int i = 1; i < MAX_MODELS; i++)
        {
            string modelName = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_MODELS + i);
            if (string.IsNullOrEmpty(modelName)) continue;
            _gameModels[i] = Syscalls.R_RegisterModel(modelName);
            modelCount++;
        }
        Syscalls.Print($"[.NET cgame] Registered {modelCount} game models\n");

        // Register server-specified sounds
        int soundCount = 0;
        for (int i = 1; i < MAX_SOUNDS; i++)
        {
            string soundName = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_SOUNDS + i);
            if (string.IsNullOrEmpty(soundName)) continue;
            if (soundName[0] == '*') continue; // custom sound
            _gameSounds[i] = Syscalls.S_RegisterSound(soundName, 0);
            soundCount++;
        }
        Syscalls.Print($"[.NET cgame] Registered {soundCount} game sounds\n");
    }

    // ── Snapshot Processing (cg_snapshot.c equivalent) ──

    private static void ProcessSnapshots()
    {
        int n = 0, snapTime = 0;
        Syscalls.GetCurrentSnapshotNumber(&n, &snapTime);
        _latestSnapshotTime = snapTime;

        if (n != _latestSnapshotNum)
        {
            if (n < _latestSnapshotNum)
                Syscalls.Print("[.NET cgame] WARNING: snapshot number went backwards\n");
            _latestSnapshotNum = n;
        }

        // Get initial snapshot
        while (_snap == null)
        {
            var snap = ReadNextSnapshot();
            if (snap == null) return;
            if ((snap->SnapFlags & SnapFlags.SNAPFLAG_NOT_ACTIVE) == 0)
                SetInitialSnapshot(snap);
        }

        DrainServerCommands();

        // Get next snapshot for interpolation
        while (true)
        {
            if (_nextSnap == null)
            {
                var snap = ReadNextSnapshot();
                if (snap == null) break;
                SetNextSnap(snap);
            }

            if (_time >= _snap->ServerTime && _time < _nextSnap->ServerTime)
                break;

            TransitionSnapshot();
        }

        if (_time < _snap->ServerTime)
            _time = _snap->ServerTime;
    }

    private static Q3Snapshot* ReadNextSnapshot()
    {
        while (_processedSnapshotNum < _latestSnapshotNum)
        {
            _processedSnapshotNum++;
            byte* buf = (_snap == (Q3Snapshot*)_snapBuffer1) ? _snapBuffer2 : _snapBuffer1;
            if (Syscalls.GetSnapshot(_processedSnapshotNum, buf))
                return (Q3Snapshot*)buf;
        }
        return null;
    }

    private static void SetInitialSnapshot(Q3Snapshot* snap)
    {
        _snap = snap;
        _weaponSelect = snap->Ps.Weapon;
        Syscalls.Print($"[.NET cgame] Initial snapshot: serverTime={snap->ServerTime}, entities={snap->NumEntities}, ps.weapon={snap->Ps.Weapon}\n");

        // Initialize all entities from first snapshot
        for (int i = 0; i < snap->NumEntities; i++)
        {
            ref var es = ref snap->GetEntity(i);
            ref var cent = ref _entities[es.Number];
            cent.CurrentState = es;
            cent.CurrentValid = true;
            cent.Interpolate = false;
            cent.SnapShotTime = snap->ServerTime;
            // Set initial position from trajectory base
            EvaluateTrajectory(ref es.Pos, snap->ServerTime,
                out cent.LerpOriginX, out cent.LerpOriginY, out cent.LerpOriginZ);
            EvaluateTrajectory(ref es.APos, snap->ServerTime,
                out cent.LerpAnglesX, out cent.LerpAnglesY, out cent.LerpAnglesZ);
            CheckEvents(ref cent);
        }

        DrainServerCommands();
    }

    private static void SetNextSnap(Q3Snapshot* snap)
    {
        _nextSnap = snap;

        // Detect player teleport between snapshots
        if (_snap != null &&
            ((_snap->Ps.EFlags ^ snap->Ps.EFlags) & EF_TELEPORT_BIT) != 0)
        {
            _nextFrameTeleport = true;
        }
        else
        {
            _nextFrameTeleport = false;
        }

        // Build solid entity list for prediction on each new snapshot
        BuildSolidList();

        // Set up interpolation state for entities in nextSnap
        for (int i = 0; i < snap->NumEntities; i++)
        {
            ref var es = ref snap->GetEntity(i);
            ref var cent = ref _entities[es.Number];
            cent.NextState = es;

            // Can interpolate if currently valid and not teleporting
            if (!cent.CurrentValid ||
                ((cent.CurrentState.EFlags ^ es.EFlags) & EF_TELEPORT_BIT) != 0)
            {
                cent.Interpolate = false;
            }
            else
            {
                cent.Interpolate = true;
            }
        }
    }

    private static void TransitionSnapshot()
    {
        // Shift teleport flags forward
        _thisFrameTeleport = _nextFrameTeleport;
        _nextFrameTeleport = false;

        // Mark all old snap entities as invalid
        for (int i = 0; i < _snap->NumEntities; i++)
        {
            ref var es = ref _snap->GetEntity(i);
            _entities[es.Number].CurrentValid = false;
        }

        // Move nextSnap → snap
        _snap = _nextSnap;
        _nextSnap = null;

        // Transition entities
        for (int i = 0; i < _snap->NumEntities; i++)
        {
            ref var es = ref _snap->GetEntity(i);
            ref var cent = ref _entities[es.Number];

            cent.CurrentState = cent.NextState;
            cent.CurrentValid = true;
            cent.SnapShotTime = _snap->ServerTime;

            if (!cent.Interpolate)
            {
                // Reset — no interpolation (teleport or new entity)
                EvaluateTrajectory(ref cent.CurrentState.Pos, _snap->ServerTime,
                    out cent.LerpOriginX, out cent.LerpOriginY, out cent.LerpOriginZ);
                EvaluateTrajectory(ref cent.CurrentState.APos, _snap->ServerTime,
                    out cent.LerpAnglesX, out cent.LerpAnglesY, out cent.LerpAnglesZ);
            }

            cent.Interpolate = false;

            // Check events on transitioned entities
            CheckEvents(ref cent);
        }

        // Check for damage feedback
        DamageFeedback();
    }

    /// <summary>
    /// Build the solid entity list from the current snapshot for prediction.
    /// Matches CG_BuildSolidList from cg_predict.c.
    /// </summary>
    private static void BuildSolidList()
    {
        if (_snap == null) return;

        // Use the next snap if available, otherwise current
        var snap = _nextSnap != null ? _nextSnap : _snap;

        var solids = new Prediction.SolidEntity[256];
        int solidCount = 0;
        var triggers = new int[256];
        int triggerCount = 0;

        for (int i = 0; i < snap->NumEntities; i++)
        {
            ref var es = ref snap->GetEntity(i);
            ref var cent = ref _entities[es.Number];

            if (es.EType == EntityType.ET_ITEM || es.EType == EntityType.ET_PUSH_TRIGGER ||
                es.EType == EntityType.ET_TELEPORT_TRIGGER)
            {
                if (triggerCount < 256)
                    triggers[triggerCount++] = es.Number;
                continue;
            }

            if (cent.CurrentState.Solid == 0) continue;

            if (solidCount < 256)
            {
                ref var se = ref solids[solidCount];
                se.Number = es.Number;
                se.Solid = cent.CurrentState.Solid;
                se.ModelIndex = cent.CurrentState.ModelIndex;
                se.OriginX = cent.LerpOriginX;
                se.OriginY = cent.LerpOriginY;
                se.OriginZ = cent.LerpOriginZ;
                se.AnglesX = cent.LerpAnglesX;
                se.AnglesY = cent.LerpAnglesY;
                se.AnglesZ = cent.LerpAnglesZ;
                solidCount++;
            }
        }

        Prediction.SetSolidEntities(solids, solidCount);
        Prediction.SetTriggerEntities(triggers, triggerCount);
    }

    private static void DrainServerCommands()
    {
        if (_snap == null) return;
        while (_serverCommandSequence < _snap->GetServerCommandSequence())
        {
            _serverCommandSequence++;
            if (!Syscalls.GetServerCommand(_serverCommandSequence)) continue;

            string cmd = Syscalls.Argv(0);
            Syscalls.Print($"[.NET cgame] ServerCmd: {cmd}\n");
            if (cmd == "scores")
                Scoreboard.ParseScores();
            else if (cmd == "cp")
            {
                _centerPrint = Syscalls.Argv(1);
                _centerPrintTime = _time;
            }
            else if (cmd == "print")
            {
                string msg = Syscalls.Argv(1);
                Syscalls.Print(msg);
            }
            else if (cmd == "chat" || cmd == "tchat")
            {
                string msg = Syscalls.Argv(1);
                _chatMessages[_chatIndex % MAX_CHAT_LINES] = msg;
                _chatTimes[_chatIndex % MAX_CHAT_LINES] = _time;
                _chatIndex++;
                Syscalls.Print($"{msg}\n");
            }
            else if (cmd == "cs")
            {
                string numStr = Syscalls.Argv(1);
                if (int.TryParse(numStr, out int csNum))
                    ConfigStringModified(csNum);
            }
            else if (cmd == "map_restart")
            {
                _nextFrameTeleport = true;
                _centerPrint = ""; _centerPrintTime = 0;
            }
        }
    }

    private static void ConfigStringModified(int index)
    {
        // Refresh the local copy so GetConfigString reads the updated value
        Syscalls.GetGameState(_gameStateRaw);

        // Re-register models from updated config strings
        const int CS_MODELS = 32;
        const int CS_SOUNDS = CS_MODELS + MAX_MODELS;
        if (index >= CS_MODELS && index < CS_MODELS + MAX_MODELS)
        {
            string modelName = Q3GameState.GetConfigString(_gameStateRaw, index);
            if (!string.IsNullOrEmpty(modelName))
            {
                _gameModels[index - CS_MODELS] = Syscalls.R_RegisterModel(modelName);
                if (modelName[0] == '*')
                    _inlineDrawModel[index - CS_MODELS] = Syscalls.CM_InlineModel(int.Parse(modelName.AsSpan(1)));
            }
        }
        else if (index >= CS_SOUNDS && index < CS_SOUNDS + MAX_SOUNDS)
        {
            string soundName = Q3GameState.GetConfigString(_gameStateRaw, index);
            if (!string.IsNullOrEmpty(soundName) && soundName[0] != '*')
                _gameSounds[index - CS_SOUNDS] = Syscalls.S_RegisterSound(soundName, 0);
        }
        else if (index >= Q3GameState.CS_PLAYERS && index < Q3GameState.CS_PLAYERS + 64)
        {
            Player.NewClientInfo(index - Q3GameState.CS_PLAYERS, _gameStateRaw);
        }
    }

    // ── Event System (cg_event.c equivalent) ──

    // Registered event sounds
    private static int _sfxLandSound;
    private static int _sfxJumpSound;
    private static int _sfxNoAmmoSound;
    private static int _sfxTeleportIn;
    private static int _sfxTeleportOut;
    private static int _sfxRespawnSound;
    private static int _sfxGrenBounce1;
    private static int _sfxGrenBounce2;
    private static int _sfxRocketExplosion;
    private static int _sfxPlasmaExplosion;

    // Weapon fire sounds (up to 4 per weapon for variety)
    private const int MAX_WEAPON_SOUNDS = 4;
    private static readonly int[,] _weaponFireSounds = new int[Weapons.WP_NUM_WEAPONS, MAX_WEAPON_SOUNDS];

    private static void RegisterEventSounds()
    {
        _sfxLandSound = Syscalls.S_RegisterSound("sound/player/land1.wav", 0);
        _sfxJumpSound = Syscalls.S_RegisterSound("sound/player/jump1.wav", 0);
        _sfxNoAmmoSound = Syscalls.S_RegisterSound("sound/weapons/noammo.wav", 0);
        _sfxTeleportIn = Syscalls.S_RegisterSound("sound/world/telein.wav", 0);
        _sfxTeleportOut = Syscalls.S_RegisterSound("sound/world/teleout.wav", 0);
        _sfxRespawnSound = Syscalls.S_RegisterSound("sound/items/respawn1.wav", 0);
        _sfxGrenBounce1 = Syscalls.S_RegisterSound("sound/weapons/grenade/hgrenb1a.wav", 0);
        _sfxGrenBounce2 = Syscalls.S_RegisterSound("sound/weapons/grenade/hgrenb2a.wav", 0);
        _sfxRocketExplosion = Syscalls.S_RegisterSound("sound/weapons/rocket/rocklx1a.wav", 0);
        _sfxPlasmaExplosion = Syscalls.S_RegisterSound("sound/weapons/plasma/plasmx1a.wav", 0);

        // Weapon fire sounds
        _weaponFireSounds[Weapons.WP_GAUNTLET, 0] = Syscalls.S_RegisterSound("sound/weapons/melee/fstatck.wav", 0);
        _weaponFireSounds[Weapons.WP_MACHINEGUN, 0] = Syscalls.S_RegisterSound("sound/weapons/machinegun/machgf1b.wav", 0);
        _weaponFireSounds[Weapons.WP_MACHINEGUN, 1] = Syscalls.S_RegisterSound("sound/weapons/machinegun/machgf2b.wav", 0);
        _weaponFireSounds[Weapons.WP_MACHINEGUN, 2] = Syscalls.S_RegisterSound("sound/weapons/machinegun/machgf3b.wav", 0);
        _weaponFireSounds[Weapons.WP_MACHINEGUN, 3] = Syscalls.S_RegisterSound("sound/weapons/machinegun/machgf4b.wav", 0);
        _weaponFireSounds[Weapons.WP_SHOTGUN, 0] = Syscalls.S_RegisterSound("sound/weapons/shotgun/sshotf1b.wav", 0);
        _weaponFireSounds[Weapons.WP_GRENADE_LAUNCHER, 0] = Syscalls.S_RegisterSound("sound/weapons/grenade/grenlf1a.wav", 0);
        _weaponFireSounds[Weapons.WP_ROCKET_LAUNCHER, 0] = Syscalls.S_RegisterSound("sound/weapons/rocket/rocklf1a.wav", 0);
        _weaponFireSounds[Weapons.WP_LIGHTNING, 0] = Syscalls.S_RegisterSound("sound/weapons/lightning/lg_fire.wav", 0);
        _weaponFireSounds[Weapons.WP_RAILGUN, 0] = Syscalls.S_RegisterSound("sound/weapons/railgun/railgf1a.wav", 0);
        _weaponFireSounds[Weapons.WP_PLASMAGUN, 0] = Syscalls.S_RegisterSound("sound/weapons/plasma/hyprbf1a.wav", 0);
        _weaponFireSounds[Weapons.WP_BFG, 0] = Syscalls.S_RegisterSound("sound/weapons/bfg/bfg_fire.wav", 0);

        Syscalls.Print("[.NET cgame] Registered event sounds\n");
    }

    private static void CheckEvents(ref CEntity cent)
    {
        ref var es = ref cent.CurrentState;

        // Event-only entities (eType >= ET_EVENTS)
        if (es.EType > EntityType.ET_EVENTS)
        {
            if (cent.PreviousEvent != 0) return; // already fired
            cent.PreviousEvent = 1;
            int eventType = es.EType - EntityType.ET_EVENTS;
            HandleEntityEvent(ref cent, eventType, es.EventParm);
            return;
        }

        // Regular entity events
        if (es.Event == cent.PreviousEvent) return;
        cent.PreviousEvent = es.Event;
        int ev = es.Event & ~EntityEvent.EV_EVENT_BITS;
        if (ev == 0) return;
        HandleEntityEvent(ref cent, ev, es.EventParm);
    }

    private static void HandleEntityEvent(ref CEntity cent, int eventType, int eventParm)
    {
        ref var es = ref cent.CurrentState;
        Syscalls.Print($"[.NET cgame] Event: type={eventType} parm={eventParm} ent={es.Number} eType={es.EType}\n");
        float* origin = stackalloc float[3];
        origin[0] = cent.LerpOriginX;
        origin[1] = cent.LerpOriginY;
        origin[2] = cent.LerpOriginZ;

        switch (eventType)
        {
            case EntityEvent.EV_FOOTSTEP:
            case EntityEvent.EV_FOOTSTEP_METAL:
            case EntityEvent.EV_FOOTSPLASH:
            case EntityEvent.EV_FOOTWADE:
            case EntityEvent.EV_SWIM:
                // Footstep sounds — use land sound as fallback
                Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_BODY, _sfxLandSound);
                break;

            case EntityEvent.EV_FALL_SHORT:
                Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, _sfxLandSound);
                if (es.Number == _snap->Ps.ClientNum)
                {
                    _landChange = -8;
                    _landTime = _time;
                }
                break;

            case EntityEvent.EV_FALL_MEDIUM:
                Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, _sfxLandSound);
                if (es.Number == _snap->Ps.ClientNum)
                {
                    _landChange = -16;
                    _landTime = _time;
                }
                break;

            case EntityEvent.EV_FALL_FAR:
                Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, _sfxLandSound);
                if (es.Number == _snap->Ps.ClientNum)
                {
                    _landChange = -24;
                    _landTime = _time;
                }
                break;

            case EntityEvent.EV_STEP_4:
            case EntityEvent.EV_STEP_8:
            case EntityEvent.EV_STEP_12:
            case EntityEvent.EV_STEP_16:
                if (es.Number == _snap->Ps.ClientNum)
                {
                    float stepSize = eventType switch
                    {
                        EntityEvent.EV_STEP_4 => 4,
                        EntityEvent.EV_STEP_8 => 8,
                        EntityEvent.EV_STEP_12 => 12,
                        _ => 16
                    };
                    _stepChange = stepSize;
                    _stepTime = _time;
                }
                break;

            case EntityEvent.EV_JUMP:
                Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_VOICE, _sfxJumpSound);
                break;

            case EntityEvent.EV_JUMP_PAD:
                Syscalls.S_StartSound(origin, EntityNum.ENTITYNUM_NONE, SoundChannel.CHAN_VOICE, _sfxJumpSound);
                break;

            case EntityEvent.EV_ITEM_PICKUP:
            case EntityEvent.EV_GLOBAL_ITEM_PICKUP:
                // Play item pickup sound (eventParm = item index)
                if (eventParm > 0 && eventParm < MAX_SOUNDS && _gameSounds[eventParm] != 0)
                    Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_AUTO, _gameSounds[eventParm]);
                else
                    Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_AUTO, _sfxRespawnSound);
                // Show pickup notification for local player
                if (es.Number == _snap->Ps.ClientNum)
                {
                    _pickupName = GetItemName(eventParm);
                    _pickupTime = _time;
                }
                break;

            case EntityEvent.EV_NOAMMO:
                Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_AUTO, _sfxNoAmmoSound);
                break;

            case EntityEvent.EV_CHANGE_WEAPON:
                // Weapon change — no sound by default
                break;

            case EntityEvent.EV_FIRE_WEAPON:
                FireWeapon(ref cent);
                break;

            case EntityEvent.EV_ITEM_RESPAWN:
                Syscalls.S_StartSound(origin, EntityNum.ENTITYNUM_NONE, SoundChannel.CHAN_AUTO, _sfxRespawnSound);
                break;

            case EntityEvent.EV_PLAYER_TELEPORT_IN:
                Syscalls.S_StartSound(origin, EntityNum.ENTITYNUM_NONE, SoundChannel.CHAN_AUTO, _sfxTeleportIn);
                break;

            case EntityEvent.EV_PLAYER_TELEPORT_OUT:
                Syscalls.S_StartSound(origin, EntityNum.ENTITYNUM_NONE, SoundChannel.CHAN_AUTO, _sfxTeleportOut);
                break;

            case EntityEvent.EV_GRENADE_BOUNCE:
                if ((Random.Shared.Next() & 1) != 0)
                    Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, _sfxGrenBounce1);
                else
                    Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, _sfxGrenBounce2);
                break;

            case EntityEvent.EV_GENERAL_SOUND:
                // eventParm = sound index from config strings
                if (eventParm > 0 && eventParm < MAX_SOUNDS && _gameSounds[eventParm] != 0)
                    Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_VOICE, _gameSounds[eventParm]);
                break;

            case EntityEvent.EV_GLOBAL_SOUND:
                if (eventParm > 0 && eventParm < MAX_SOUNDS && _gameSounds[eventParm] != 0)
                    Syscalls.S_StartSound(null, EntityNum.ENTITYNUM_NONE, SoundChannel.CHAN_AUTO, _gameSounds[eventParm]);
                break;

            case EntityEvent.EV_MISSILE_HIT:
            {
                Syscalls.S_StartSound(origin, EntityNum.ENTITYNUM_NONE, SoundChannel.CHAN_AUTO, _sfxRocketExplosion);
                float ox = es.OriginX, oy = es.OriginY, oz = es.OriginZ;
                LocalEntities.MakeExplosion(ox, oy, oz, _explosionShader, 600,
                    300, 1.0f, 0.75f, 0.0f, _time);
                float dx = es.Angles2X, dy = es.Angles2Y, dz = es.Angles2Z;
                float dirLen = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dirLen > 0.001f) { dx /= dirLen; dy /= dirLen; dz /= dirLen; }
                else { dx = 0; dy = 0; dz = 1; }
                Marks.ImpactMark(Marks.BurnMarkShader, ox, oy, oz, dx, dy, dz,
                    32, 1, 1, 1, 1, false, false);
                break;
            }

            case EntityEvent.EV_MISSILE_MISS:
            case EntityEvent.EV_MISSILE_MISS_METAL:
            {
                Syscalls.S_StartSound(origin, EntityNum.ENTITYNUM_NONE, SoundChannel.CHAN_AUTO, _sfxRocketExplosion);
                float mx = es.OriginX, my = es.OriginY, mz = es.OriginZ;
                LocalEntities.MakeExplosion(mx, my, mz, _explosionShader, 600,
                    300, 1.0f, 0.75f, 0.0f, _time);
                float ndx = es.Angles2X, ndy = es.Angles2Y, ndz = es.Angles2Z;
                float nLen = MathF.Sqrt(ndx * ndx + ndy * ndy + ndz * ndz);
                if (nLen > 0.001f) { ndx /= nLen; ndy /= nLen; ndz /= nLen; }
                else { ndx = 0; ndy = 0; ndz = 1; }
                Marks.ImpactMark(Marks.BurnMarkShader, mx, my, mz, ndx, ndy, ndz,
                    32, 1, 1, 1, 1, false, false);
                break;
            }

            case EntityEvent.EV_BULLET_HIT_WALL:
            {
                float bx = es.OriginX, by = es.OriginY, bz = es.OriginZ;
                float bdx = es.Angles2X, bdy = es.Angles2Y, bdz = es.Angles2Z;
                float bLen = MathF.Sqrt(bdx * bdx + bdy * bdy + bdz * bdz);
                if (bLen > 0.001f) { bdx /= bLen; bdy /= bLen; bdz /= bLen; }
                else { bdx = 0; bdy = 0; bdz = 1; }
                Marks.ImpactMark(Marks.BulletMarkShader, bx, by, bz, bdx, bdy, bdz,
                    8, 1, 1, 1, 1, true, false);
                break;
            }

            case EntityEvent.EV_BULLET_HIT_FLESH:
                // Blood effect — just sound for now
                break;

            case EntityEvent.EV_SHOTGUN:
                // Shotgun pellet pattern — each pellet leaves a bullet mark
                break;

            case EntityEvent.EV_RAILTRAIL:
            {
                // Rail trail beam — render as temporary refEntity
                // Sound is handled by the weapon fire event
                break;
            }

            case EntityEvent.EV_PAIN:
            case EntityEvent.EV_DEATH1:
            case EntityEvent.EV_DEATH2:
            case EntityEvent.EV_DEATH3:
                // Pain/death sounds — would need player-specific sounds
                break;
        }
    }

    private static void FireWeapon(ref CEntity cent)
    {
        ref var es = ref cent.CurrentState;
        int weapon = es.Weapon;
        if (weapon <= 0 || weapon >= Weapons.WP_NUM_WEAPONS) return;

        // Count available fire sound variants
        int count = 0;
        for (int i = 0; i < MAX_WEAPON_SOUNDS; i++)
        {
            if (_weaponFireSounds[weapon, i] == 0) break;
            count++;
        }
        if (count > 0)
        {
            int idx = Random.Shared.Next(count);
            Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_WEAPON, _weaponFireSounds[weapon, idx]);
        }

        // Muzzle flash light at the entity's position
        float* origin = stackalloc float[3];
        origin[0] = cent.LerpOriginX;
        origin[1] = cent.LerpOriginY;
        origin[2] = cent.LerpOriginZ + 16; // approximate muzzle height
        switch (weapon)
        {
            case Weapons.WP_GAUNTLET:
                Syscalls.R_AddLightToScene(origin, 200 + Random.Shared.Next(32), 1.0f, 0.6f, 0.1f);
                break;
            case Weapons.WP_MACHINEGUN:
                Syscalls.R_AddLightToScene(origin, 150 + Random.Shared.Next(64), 1.0f, 1.0f, 0.6f);
                break;
            case Weapons.WP_SHOTGUN:
                Syscalls.R_AddLightToScene(origin, 250 + Random.Shared.Next(32), 1.0f, 1.0f, 0.6f);
                break;
            case Weapons.WP_GRENADE_LAUNCHER:
                Syscalls.R_AddLightToScene(origin, 150 + Random.Shared.Next(32), 1.0f, 0.7f, 0.5f);
                break;
            case Weapons.WP_ROCKET_LAUNCHER:
                Syscalls.R_AddLightToScene(origin, 300 + Random.Shared.Next(32), 1.0f, 0.75f, 0.0f);
                break;
            case Weapons.WP_LIGHTNING:
                Syscalls.R_AddLightToScene(origin, 200 + Random.Shared.Next(32), 0.6f, 0.6f, 1.0f);
                break;
            case Weapons.WP_RAILGUN:
                Syscalls.R_AddLightToScene(origin, 300 + Random.Shared.Next(32), 0.3f, 1.0f, 0.3f);
                break;
            case Weapons.WP_PLASMAGUN:
                Syscalls.R_AddLightToScene(origin, 200 + Random.Shared.Next(32), 0.6f, 0.6f, 1.0f);
                break;
            case Weapons.WP_BFG:
                Syscalls.R_AddLightToScene(origin, 400 + Random.Shared.Next(32), 0.2f, 1.0f, 0.2f);
                break;
        }
    }

    // ── Trajectory Evaluation (bg_misc.c BG_EvaluateTrajectory equivalent) ──

    private static void EvaluateTrajectory(ref Q3Trajectory tr, int atTime,
        out float rx, out float ry, out float rz)
    {
        float deltaTime;

        switch (tr.TrType)
        {
            case TrajectoryType.TR_STATIONARY:
            case TrajectoryType.TR_INTERPOLATE:
                rx = tr.TrBaseX; ry = tr.TrBaseY; rz = tr.TrBaseZ;
                break;

            case TrajectoryType.TR_LINEAR:
                deltaTime = (atTime - tr.TrTime) * 0.001f;
                rx = tr.TrBaseX + tr.TrDeltaX * deltaTime;
                ry = tr.TrBaseY + tr.TrDeltaY * deltaTime;
                rz = tr.TrBaseZ + tr.TrDeltaZ * deltaTime;
                break;

            case TrajectoryType.TR_LINEAR_STOP:
                if (atTime > tr.TrTime + tr.TrDuration)
                    atTime = tr.TrTime + tr.TrDuration;
                deltaTime = (atTime - tr.TrTime) * 0.001f;
                if (deltaTime < 0) deltaTime = 0;
                rx = tr.TrBaseX + tr.TrDeltaX * deltaTime;
                ry = tr.TrBaseY + tr.TrDeltaY * deltaTime;
                rz = tr.TrBaseZ + tr.TrDeltaZ * deltaTime;
                break;

            case TrajectoryType.TR_SINE:
            {
                deltaTime = (atTime - tr.TrTime) / (float)tr.TrDuration;
                float phase = MathF.Sin(deltaTime * MathF.PI * 2);
                rx = tr.TrBaseX + tr.TrDeltaX * phase;
                ry = tr.TrBaseY + tr.TrDeltaY * phase;
                rz = tr.TrBaseZ + tr.TrDeltaZ * phase;
                break;
            }

            case TrajectoryType.TR_GRAVITY:
                deltaTime = (atTime - tr.TrTime) * 0.001f;
                rx = tr.TrBaseX + tr.TrDeltaX * deltaTime;
                ry = tr.TrBaseY + tr.TrDeltaY * deltaTime;
                rz = tr.TrBaseZ + tr.TrDeltaZ * deltaTime - 0.5f * DEFAULT_GRAVITY * deltaTime * deltaTime;
                break;

            default:
                rx = tr.TrBaseX; ry = tr.TrBaseY; rz = tr.TrBaseZ;
                break;
        }
    }

    // ── Per-Entity Interpolation (cg_ents.c CG_CalcEntityLerpPositions) ──

    private static void CalcEntityLerpPositions(ref CEntity cent)
    {
        // For players, force TR_INTERPOLATE for smooth movement
        if (cent.CurrentState.Number < MAX_CLIENTS)
            cent.CurrentState.Pos.TrType = TrajectoryType.TR_INTERPOLATE;

        // Interpolate between current and next snapshot
        if (cent.Interpolate &&
            (cent.CurrentState.Pos.TrType == TrajectoryType.TR_INTERPOLATE ||
             (cent.CurrentState.Pos.TrType == TrajectoryType.TR_LINEAR_STOP &&
              cent.CurrentState.Number < MAX_CLIENTS)))
        {
            InterpolateEntityPosition(ref cent);
            return;
        }

        // Otherwise evaluate trajectory at current time
        EvaluateTrajectory(ref cent.CurrentState.Pos, _time,
            out cent.LerpOriginX, out cent.LerpOriginY, out cent.LerpOriginZ);
        EvaluateTrajectory(ref cent.CurrentState.APos, _time,
            out cent.LerpAnglesX, out cent.LerpAnglesY, out cent.LerpAnglesZ);
    }

    private static void InterpolateEntityPosition(ref CEntity cent)
    {
        // Evaluate current and next trajectory endpoints
        EvaluateTrajectory(ref cent.CurrentState.Pos, _snap->ServerTime,
            out float curX, out float curY, out float curZ);
        EvaluateTrajectory(ref cent.NextState.Pos, _nextSnap->ServerTime,
            out float nextX, out float nextY, out float nextZ);

        float f = _frameInterpolation;
        cent.LerpOriginX = curX + f * (nextX - curX);
        cent.LerpOriginY = curY + f * (nextY - curY);
        cent.LerpOriginZ = curZ + f * (nextZ - curZ);

        // Angles
        EvaluateTrajectory(ref cent.CurrentState.APos, _snap->ServerTime,
            out float curAX, out float curAY, out float curAZ);
        EvaluateTrajectory(ref cent.NextState.APos, _nextSnap->ServerTime,
            out float nextAX, out float nextAY, out float nextAZ);

        cent.LerpAnglesX = LerpAngle(curAX, nextAX, f);
        cent.LerpAnglesY = LerpAngle(curAY, nextAY, f);
        cent.LerpAnglesZ = LerpAngle(curAZ, nextAZ, f);
    }

    // ── View Calculation (cg_view.c equivalent) ──

    // View effect constants
    private const int STEP_TIME = 200;
    private const int DUCK_TIME = 100;
    private const int DAMAGE_DEFLECT_TIME = 150;
    private const int DAMAGE_RETURN_TIME = 400;
    private const int DAMAGE_TIME = 500;
    private const int LAND_DEFLECT_TIME = 150;
    private const int LAND_RETURN_TIME = 300;
    private const int ZOOM_TIME = 150;
    private const int PMF_DUCKED = 1;

    private static void CalcViewValues(ref Q3RefDef refdef)
    {
        ref var ps = ref Prediction.PredictedPlayerState;

        refdef.X = 0;
        refdef.Y = 0;
        refdef.Width = _screenWidth;
        refdef.Height = _screenHeight;

        // Calculate bob state from predicted player state
        _bobCycle = (ps.BobCycle & 128) >> 7;
        _bobFracSin = MathF.Abs(MathF.Sin((ps.BobCycle & 127) / 127.0f * MathF.PI));
        _xySpeed = MathF.Sqrt(ps.VelocityX * ps.VelocityX + ps.VelocityY * ps.VelocityY);

        // Base view origin from predicted state (viewheight added in OffsetFirstPersonView)
        refdef.ViewOrgX = ps.OriginX;
        refdef.ViewOrgY = ps.OriginY;
        refdef.ViewOrgZ = ps.OriginZ;

        // Apply prediction error smoothing
        Prediction.GetPredictionError(out float errX, out float errY, out float errZ, _time);
        refdef.ViewOrgX += errX;
        refdef.ViewOrgY += errY;
        refdef.ViewOrgZ += errZ;

        // Base view angles from predicted state
        float viewPitch = ps.ViewAnglesX;
        float viewYaw = ps.ViewAnglesY;
        float viewRoll = ps.ViewAnglesZ;

        // Apply first-person view offsets (bobbing, damage kick, duck, landing, step)
        OffsetFirstPersonView(ref refdef, ref viewPitch, ref viewYaw, ref viewRoll);

        // Convert angles to axis AFTER applying offsets
        float pitch = viewPitch * MathF.PI / 180.0f;
        float yaw = viewYaw * MathF.PI / 180.0f;
        float roll = viewRoll * MathF.PI / 180.0f;
        AnglesToAxis(pitch, yaw, roll, ref refdef);

        // Calculate FOV (with zoom support)
        CalcFov(ref refdef);
    }

    private static void OffsetFirstPersonView(ref Q3RefDef refdef, ref float pitch, ref float yaw, ref float roll)
    {
        ref var ps = ref Prediction.PredictedPlayerState;

        // If dead, fix the angle and don't add any kick
        if (ps.Stats[Stats.STAT_HEALTH] <= 0)
        {
            roll = 40;
            pitch = -15;
            yaw = ps.Stats[Stats.STAT_DEAD_YAW];
            refdef.ViewOrgZ += ps.ViewHeight;
            return;
        }

        // Add angles based on damage kick
        if (_damageTime > 0)
        {
            float ratio = _time - _damageTime;
            if (ratio < DAMAGE_DEFLECT_TIME)
            {
                ratio /= DAMAGE_DEFLECT_TIME;
                pitch += ratio * _vDmgPitch;
                roll += ratio * _vDmgRoll;
            }
            else
            {
                ratio = 1.0f - (ratio - DAMAGE_DEFLECT_TIME) / DAMAGE_RETURN_TIME;
                if (ratio > 0)
                {
                    pitch += ratio * _vDmgPitch;
                    roll += ratio * _vDmgRoll;
                }
            }
        }

        // Add angles based on velocity (run pitch/roll)
        const float cg_runpitch = 0.002f;
        const float cg_runroll = 0.005f;

        float sp = MathF.Sin(pitch * MathF.PI / 180f), cp = MathF.Cos(pitch * MathF.PI / 180f);
        float sy = MathF.Sin(yaw * MathF.PI / 180f), cy = MathF.Cos(yaw * MathF.PI / 180f);
        float fwdX = cp * cy, fwdY = cp * sy;
        float rightX = -sy, rightY = cy;

        float vFwd = ps.VelocityX * fwdX + ps.VelocityY * fwdY;
        pitch += vFwd * cg_runpitch;

        float vRight = ps.VelocityX * rightX + ps.VelocityY * rightY;
        roll -= vRight * cg_runroll;

        // Add angles based on bob
        const float cg_bobpitch = 0.002f;
        const float cg_bobroll = 0.002f;
        const float cg_bobup = 0.005f;

        float speed = _xySpeed > 200 ? _xySpeed : 200;
        float delta = _bobFracSin * cg_bobpitch * speed;
        if ((ps.PmFlags & PMF_DUCKED) != 0)
            delta *= 3;
        pitch += delta;

        delta = _bobFracSin * cg_bobroll * speed;
        if ((ps.PmFlags & PMF_DUCKED) != 0)
            delta *= 3;
        if ((_bobCycle & 1) != 0)
            delta = -delta;
        roll += delta;

        // Add view height
        refdef.ViewOrgZ += ps.ViewHeight;

        // Smooth out duck height changes
        if (_duckTime > 0)
        {
            int timeDelta = _time - _duckTime;
            if (timeDelta < DUCK_TIME)
            {
                refdef.ViewOrgZ -= _duckChange * (DUCK_TIME - timeDelta) / (float)DUCK_TIME;
            }
        }

        // Add bob height
        float bob = _bobFracSin * _xySpeed * cg_bobup;
        if (bob > 6) bob = 6;
        refdef.ViewOrgZ += bob;

        // Add fall/landing height
        if (_landTime > 0)
        {
            delta = _time - _landTime;
            if (delta < LAND_DEFLECT_TIME)
            {
                float f = delta / LAND_DEFLECT_TIME;
                refdef.ViewOrgZ += _landChange * f;
            }
            else if (delta < LAND_DEFLECT_TIME + LAND_RETURN_TIME)
            {
                delta -= LAND_DEFLECT_TIME;
                float f = 1.0f - delta / LAND_RETURN_TIME;
                refdef.ViewOrgZ += _landChange * f;
            }
        }

        // Add step offset (stair smoothing)
        if (_stepTime > 0)
        {
            int stepDelta = _time - _stepTime;
            if (stepDelta < STEP_TIME)
            {
                refdef.ViewOrgZ -= _stepChange * (STEP_TIME - stepDelta) / (float)STEP_TIME;
            }
        }
    }

    private static void CalcFov(ref Q3RefDef refdef)
    {
        float fovX;
        if (Prediction.PredictedPlayerState.PmType == PmType.PM_INTERMISSION)
        {
            fovX = 90;
        }
        else
        {
            string fovStr = Syscalls.CvarGetString("cg_fov");
            fovX = float.TryParse(fovStr, System.Globalization.CultureInfo.InvariantCulture, out float fovParsed)
                ? fovParsed : 90;
            if (fovX < 1) fovX = 90;
            else if (fovX > 160) fovX = 160;

            // Zoom FOV
            string zoomStr = Syscalls.CvarGetString("cg_zoomFov");
            float zoomFov = float.TryParse(zoomStr, System.Globalization.CultureInfo.InvariantCulture, out float zfParsed)
                ? zfParsed : 22.5f;
            if (zoomFov < 1) zoomFov = 22.5f;
            else if (zoomFov > 160) zoomFov = 160;

            if (_zoomed)
            {
                float f = (_time - _zoomTime) / (float)ZOOM_TIME;
                if (f > 1.0f)
                    fovX = zoomFov;
                else
                    fovX = fovX + f * (zoomFov - fovX);
            }
            else if (_zoomTime > 0)
            {
                float f = (_time - _zoomTime) / (float)ZOOM_TIME;
                if (f <= 1.0f)
                    fovX = zoomFov + f * (fovX - zoomFov);
            }
        }

        float aspect = (float)_screenWidth / _screenHeight;
        refdef.FovX = fovX;
        refdef.FovY = 2.0f * MathF.Atan(MathF.Tan(fovX * MathF.PI / 360.0f) / aspect) * 180.0f / MathF.PI;
    }

    private static void DamageFeedback()
    {
        if (_snap == null) return;
        ref var ps = ref _snap->Ps;

        if (ps.DamageEvent == _lastDamageEvent || ps.DamageCount == 0)
            return;
        _lastDamageEvent = ps.DamageEvent;

        int health = ps.Stats[Stats.STAT_HEALTH];
        float scale = health < 40 ? 1.0f : 40.0f / health;
        float kick = ps.DamageCount * scale;
        kick = Math.Clamp(kick, 5f, 10f);

        int yawByte = ps.DamageYaw;
        int pitchByte = ps.DamagePitch;

        if (yawByte == 255 && pitchByte == 255)
        {
            // Centered damage (falling, etc.)
            _damageX = 0;
            _damageY = 0;
            _vDmgRoll = 0;
            _vDmgPitch = -kick;
        }
        else
        {
            // Directional damage
            float dmgPitch = pitchByte / 255.0f * 360.0f;
            float dmgYaw = yawByte / 255.0f * 360.0f;

            float pr = dmgPitch * MathF.PI / 180f;
            float yr = dmgYaw * MathF.PI / 180f;

            float dirX = -(MathF.Cos(pr) * MathF.Cos(yr));
            float dirY = -(MathF.Cos(pr) * MathF.Sin(yr));
            float dirZ = -MathF.Sin(pr);

            // Get view axes from predicted player state angles
            ref var pps = ref Prediction.PredictedPlayerState;
            float vPitch = pps.ViewAnglesX * MathF.PI / 180f;
            float vYaw = pps.ViewAnglesY * MathF.PI / 180f;

            float vsp = MathF.Sin(vPitch), vcp = MathF.Cos(vPitch);
            float vsy = MathF.Sin(vYaw), vcy = MathF.Cos(vYaw);

            float fwdX = vcp * vcy, fwdY = vcp * vsy, fwdZ = -vsp;
            float leftX = -vsy, leftY = vcy, leftZ = 0;
            float upX = vsp * vcy, upY = vsp * vsy, upZ = vcp;

            float front = dirX * fwdX + dirY * fwdY + dirZ * fwdZ;
            float left = dirX * leftX + dirY * leftY + dirZ * leftZ;
            float up = dirX * upX + dirY * upY + dirZ * upZ;

            float dist = MathF.Sqrt(front * front + left * left);
            if (dist < 0.1f) dist = 0.1f;

            _vDmgRoll = kick * left;
            _vDmgPitch = -kick * front;

            if (front <= 0.1f) front = 0.1f;
            _damageX = Math.Clamp(-left / front, -1f, 1f);
            _damageY = Math.Clamp(up / dist, -1f, 1f);
        }

        _damageValue = kick;
        _damageTime = _time;
    }

    private static void DamageBlendBlob(ref Q3RefDef refdef)
    {
        if (_damageValue <= 0 || _damageTime <= 0) return;

        int t = _time - _damageTime;
        if (t <= 0 || t >= DAMAGE_TIME) return;

        var ent = new Q3RefEntity();
        ent.ReType = Q3RefEntity.RT_SPRITE;
        ent.RenderFx = Q3RefEntity.RF_FIRST_PERSON;

        ent.OriginX = refdef.ViewOrgX + 8 * refdef.Axis0X + _damageX * -8 * refdef.Axis1X + _damageY * 8 * refdef.Axis2X;
        ent.OriginY = refdef.ViewOrgY + 8 * refdef.Axis0Y + _damageX * -8 * refdef.Axis1Y + _damageY * 8 * refdef.Axis2Y;
        ent.OriginZ = refdef.ViewOrgZ + 8 * refdef.Axis0Z + _damageX * -8 * refdef.Axis1Z + _damageY * 8 * refdef.Axis2Z;

        ent.Radius = _damageValue * 3;
        ent.CustomShader = _viewBloodShader;
        ent.ShaderRGBA_R = 255;
        ent.ShaderRGBA_G = 255;
        ent.ShaderRGBA_B = 255;
        ent.ShaderRGBA_A = (byte)(200 * (1.0f - (float)t / DAMAGE_TIME));

        Syscalls.R_AddRefEntityToScene(&ent);
    }

    private static void TrackDuckOffset()
    {
        ref var ps = ref Prediction.PredictedPlayerState;
        int viewHeight = ps.ViewHeight;

        if (viewHeight != _lastViewHeight)
        {
            if (_lastViewHeight != 0)
            {
                _duckChange = _lastViewHeight - viewHeight;
                _duckTime = _time;
            }
            _lastViewHeight = viewHeight;
        }
    }

    private static void AnglesToAxis(float pitch, float yaw, float roll, ref Q3RefDef refdef)
    {
        float sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
        float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
        float sr = MathF.Sin(roll), cr = MathF.Cos(roll);

        refdef.Axis0X = cp * cy;
        refdef.Axis0Y = cp * sy;
        refdef.Axis0Z = -sp;
        refdef.Axis1X = -sr * sp * cy + cr * -sy;
        refdef.Axis1Y = -sr * sp * sy + cr * cy;
        refdef.Axis1Z = -sr * cp;
        refdef.Axis2X = cr * sp * cy + sr * -sy;
        refdef.Axis2Y = cr * sp * sy + sr * cy;
        refdef.Axis2Z = cr * cp;
    }

    // ── Entity Rendering (cg_ents.c equivalent) ──

    private static bool _dumpedEntities;
    private static void AddPacketEntities()
    {
        if (_snap == null) return;

        // One-time dump of all entity types in the snapshot
        if (!_dumpedEntities && _snap->NumEntities > 0)
        {
            _dumpedEntities = true;
            Syscalls.Print($"[.NET cgame] Entity dump: {_snap->NumEntities} entities in snapshot\n");
            int[] typeCounts = new int[16];
            for (int i = 0; i < _snap->NumEntities; i++)
            {
                ref var e = ref _snap->GetEntity(i);
                int t = e.EType < 16 ? e.EType : 15;
                typeCounts[t]++;
            }
            for (int t = 0; t < 16; t++)
            {
                if (typeCounts[t] > 0)
                    Syscalls.Print($"[.NET cgame]   eType={t}: {typeCounts[t]} entities\n");
            }
        }

        for (int i = 0; i < _snap->NumEntities; i++)
        {
            ref var es = ref _snap->GetEntity(i);
            ref var cent = ref _entities[es.Number];

            if (es.EType >= EntityType.ET_EVENTS) continue;

            // Calculate interpolated position for this entity
            CalcEntityLerpPositions(ref cent);

            // Dispatch based on entity type
            switch (es.EType)
            {
                case EntityType.ET_GENERAL:
                    AddGeneral(ref cent);
                    break;
                case EntityType.ET_PLAYER:
                    AddPlayer(ref cent);
                    break;
                case EntityType.ET_ITEM:
                    AddItem(ref cent);
                    break;
                case EntityType.ET_MISSILE:
                    AddMissile(ref cent);
                    break;
                case EntityType.ET_MOVER:
                    AddMover(ref cent);
                    break;
                case EntityType.ET_BEAM:
                    AddBeam(ref cent);
                    break;
                case EntityType.ET_SPEAKER:
                    // Speakers handled via looping sounds
                    break;
            }
        }
    }

    private static void AddGeneral(ref CEntity cent)
    {
        ref var s1 = ref cent.CurrentState;
        if (s1.ModelIndex == 0) return;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_MODEL;
        rent.HModel = _gameModels[s1.ModelIndex];
        SetEntityOriginAndAxis(ref rent, ref cent);
        rent.FrameNum = s1.Frame;
        rent.OldFrame = s1.Frame;
        SetEntityColors(ref rent, 255, 255, 255, 255);
        Syscalls.R_AddRefEntityToScene(&rent);

        if (s1.ModelIndex2 != 0)
        {
            rent.HModel = _gameModels[s1.ModelIndex2];
            Syscalls.R_AddRefEntityToScene(&rent);
        }
    }

    private static void AddPlayer(ref CEntity cent)
    {
        ref var s1 = ref cent.CurrentState;

        Player.Render(ref s1,
            cent.LerpOriginX, cent.LerpOriginY, cent.LerpOriginZ,
            cent.LerpAnglesX, cent.LerpAnglesY, cent.LerpAnglesZ,
            s1.Number, _clientNum, _time);
    }

    private static void AddItem(ref CEntity cent)
    {
        ref var s1 = ref cent.CurrentState;
        if (s1.ModelIndex == 0) return;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_MODEL;
        rent.HModel = _gameModels[s1.ModelIndex];

        // Items use lerp origin for position
        rent.OriginX = cent.LerpOriginX;
        rent.OriginY = cent.LerpOriginY;
        rent.OriginZ = cent.LerpOriginZ;
        rent.OldOriginX = cent.LerpOriginX;
        rent.OldOriginY = cent.LerpOriginY;
        rent.OldOriginZ = cent.LerpOriginZ;

        // Items rotate
        float itemYaw = (_time & 2047) * 360.0f / 2048.0f * MathF.PI / 180.0f;
        float siy = MathF.Sin(itemYaw), ciy = MathF.Cos(itemYaw);
        rent.Axis0X = ciy; rent.Axis0Y = siy; rent.Axis0Z = 0;
        rent.Axis1X = -siy; rent.Axis1Y = ciy; rent.Axis1Z = 0;
        rent.Axis2X = 0; rent.Axis2Y = 0; rent.Axis2Z = 1;

        // Items bob
        float bobPhase = (s1.Number & 7) * MathF.PI * 2.0f / 8.0f;
        rent.OriginZ += 4 + MathF.Cos((_time * 0.005f) + bobPhase) * 4;

        SetEntityColors(ref rent, 255, 255, 255, 255);
        Syscalls.R_AddRefEntityToScene(&rent);
    }

    private static void AddMissile(ref CEntity cent)
    {
        ref var s1 = ref cent.CurrentState;
        int weapon = s1.Weapon;

        // Plasma bolts are rendered as sprites, not models
        if (weapon == Weapons.WP_PLASMAGUN)
        {
            WeaponEffects.AddPlasma(cent.LerpOriginX, cent.LerpOriginY, cent.LerpOriginZ, _time);
            WeaponEffects.MissileTrail(s1.Number, weapon,
                s1.Pos.TrBaseX, s1.Pos.TrBaseY, s1.Pos.TrBaseZ,
                cent.LerpOriginX, cent.LerpOriginY, cent.LerpOriginZ, _time);
            return;
        }

        if (s1.ModelIndex == 0) return;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_MODEL;

        // Prefer the missile model registered by WeaponEffects (rocket.md3, grenade1.md3, etc.)
        // since config-string model registration may have gaps.
        int missileModel = WeaponEffects.GetMissileModel(weapon);
        rent.HModel = missileModel != 0 ? missileModel : _gameModels[s1.ModelIndex];

        SetEntityOriginAndAxis(ref rent, ref cent);
        SetEntityColors(ref rent, 255, 255, 255, 255);
        Syscalls.R_AddRefEntityToScene(&rent);

        // Projectile trail (smoke puffs for rockets/grenades)
        WeaponEffects.MissileTrail(s1.Number, weapon,
            s1.Pos.TrBaseX, s1.Pos.TrBaseY, s1.Pos.TrBaseZ,
            cent.LerpOriginX, cent.LerpOriginY, cent.LerpOriginZ, _time);
    }

    private static void AddMover(ref CEntity cent)
    {
        ref var s1 = ref cent.CurrentState;
        if (s1.ModelIndex == 0) return;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_MODEL;

        // Movers with SOLID_BMODEL use inline BSP models
        if (s1.Solid == SOLID_BMODEL)
            rent.HModel = _inlineDrawModel[s1.ModelIndex];
        else
            rent.HModel = _gameModels[s1.ModelIndex];

        SetEntityOriginAndAxis(ref rent, ref cent);
        rent.RenderFx = Q3RefEntity.RF_NOSHADOW;
        SetEntityColors(ref rent, 255, 255, 255, 255);
        Syscalls.R_AddRefEntityToScene(&rent);

        // Secondary model
        if (s1.ModelIndex2 != 0)
        {
            rent.HModel = _gameModels[s1.ModelIndex2];
            Syscalls.R_AddRefEntityToScene(&rent);
        }
    }

    private static void AddBeam(ref CEntity cent)
    {
        ref var s1 = ref cent.CurrentState;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_BEAM;
        // Beam: origin = start, oldorigin = end
        rent.OriginX = s1.Pos.TrBaseX;
        rent.OriginY = s1.Pos.TrBaseY;
        rent.OriginZ = s1.Pos.TrBaseZ;
        rent.OldOriginX = s1.Origin2X;
        rent.OldOriginY = s1.Origin2Y;
        rent.OldOriginZ = s1.Origin2Z;
        // Identity axis
        rent.Axis0X = 1; rent.Axis0Y = 0; rent.Axis0Z = 0;
        rent.Axis1X = 0; rent.Axis1Y = 1; rent.Axis1Z = 0;
        rent.Axis2X = 0; rent.Axis2Y = 0; rent.Axis2Z = 1;
        rent.RenderFx = Q3RefEntity.RF_NOSHADOW;
        Syscalls.R_AddRefEntityToScene(&rent);
    }

    // ── Entity Helpers ──

    private static void SetEntityOriginAndAxis(ref Q3RefEntity rent, ref CEntity cent)
    {
        rent.OriginX = cent.LerpOriginX;
        rent.OriginY = cent.LerpOriginY;
        rent.OriginZ = cent.LerpOriginZ;
        rent.OldOriginX = cent.LerpOriginX;
        rent.OldOriginY = cent.LerpOriginY;
        rent.OldOriginZ = cent.LerpOriginZ;

        float pitch = cent.LerpAnglesX * MathF.PI / 180.0f;
        float yaw = cent.LerpAnglesY * MathF.PI / 180.0f;
        float roll = cent.LerpAnglesZ * MathF.PI / 180.0f;
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetEntityColors(ref Q3RefEntity rent, byte r, byte g, byte b, byte a)
    {
        rent.ShaderRGBA_R = r; rent.ShaderRGBA_G = g;
        rent.ShaderRGBA_B = b; rent.ShaderRGBA_A = a;
    }

    // ── 2D HUD (cg_draw.c CG_Draw2D equivalent) ──

    private static void DrawHud()
    {
        if (_snap == null) return;

        ref var ps = ref _snap->Ps;

        // Status bar (bottom)
        DrawStatusBar();

        // Crosshair (center)
        DrawCrosshair();

        // Weapon select overlay
        DrawWeaponSelect();

        // Center print message (objectives, notifications)
        DrawCenterString();

        // Chat messages
        DrawChat();

        // Item pickup notification
        DrawPickupItem();

        // Upper right: FPS + Timer
        float y = 0;
        y = DrawFPS(y);
        y = DrawTimer(y);

        // .NET cgame indicator (top right)
        DrawString(SCREEN_WIDTH - 82, (int)y + 2, ".NET CG", 1.0f, 0.0f, 1.0f, 0.6f);

        // Scoreboard overlay
        if (Scoreboard.IsShowing || _snap->Ps.PmType >= 4) // PM_DEAD=4, PM_INTERMISSION=5
            Scoreboard.Draw(_time, _clientNum, _gametype);

        Syscalls.R_SetColor(null);
    }

    private static void DrawCenterString()
    {
        if (_centerPrintTime == 0 || string.IsNullOrEmpty(_centerPrint)) return;

        int elapsed = _time - _centerPrintTime;
        if (elapsed > CENTER_PRINT_DURATION) { _centerPrintTime = 0; return; }

        // Fade out in last 500ms
        float alpha = elapsed > CENTER_PRINT_DURATION - 500
            ? (CENTER_PRINT_DURATION - elapsed) / 500.0f
            : 1.0f;

        var lines = _centerPrint.Split('\n');
        int lineHeight = 16;
        int startY = (int)(SCREEN_HEIGHT * 0.30f) - lines.Length * lineHeight / 2;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int w = line.Length * 8;
            int x = (SCREEN_WIDTH - w) / 2;
            DrawString(x, startY + i * lineHeight, line, 1.0f, 1.0f, 1.0f, alpha);
        }
    }

    private static void DrawChat()
    {
        int y = SCREEN_HEIGHT - 200;

        for (int i = 0; i < MAX_CHAT_LINES; i++)
        {
            int idx = (_chatIndex - MAX_CHAT_LINES + i + MAX_CHAT_LINES * 2) % MAX_CHAT_LINES;
            if (_chatTimes[idx] == 0) continue;
            int elapsed = _time - _chatTimes[idx];
            if (elapsed > CHAT_DISPLAY_TIME) continue;

            float alpha = elapsed > CHAT_DISPLAY_TIME - 1000
                ? (CHAT_DISPLAY_TIME - elapsed) / 1000.0f
                : 1.0f;

            string msg = _chatMessages[idx] ?? "";
            DrawString(8, y, msg, 0.0f, 1.0f, 0.0f, alpha);
            y += 12;
        }
    }

    private static void DrawPickupItem()
    {
        if (_pickupTime == 0 || string.IsNullOrEmpty(_pickupName)) return;

        int elapsed = _time - _pickupTime;
        if (elapsed > PICKUP_DISPLAY_TIME) { _pickupTime = 0; return; }

        float alpha = elapsed > PICKUP_DISPLAY_TIME - 500
            ? (PICKUP_DISPLAY_TIME - elapsed) / 500.0f
            : 1.0f;

        int w = _pickupName.Length * 8;
        int x = (SCREEN_WIDTH - w) / 2;
        DrawString(x, (int)(SCREEN_HEIGHT * 0.65f), _pickupName, 1.0f, 1.0f, 0.5f, alpha);
    }

    private static string GetItemName(int itemIndex)
    {
        // Item names from bg_itemlist — map common Q3 item indices
        return itemIndex switch
        {
            1 => "Armor Shard",
            2 => "Armor",
            3 => "Heavy Armor",
            4 => "5 Health",
            5 => "25 Health",
            6 => "50 Health",
            7 => "Mega Health",
            8 => "Shotgun",
            9 => "Grenade Launcher",
            10 => "Rocket Launcher",
            11 => "Lightning Gun",
            12 => "Railgun",
            13 => "Plasma Gun",
            14 => "BFG10K",
            15 => "Shells",
            16 => "Grenades",
            17 => "Rockets",
            18 => "Lightning",
            19 => "Slugs",
            20 => "Cells",
            21 => "BFG Ammo",
            22 => "Quad Damage",
            23 => "Battle Suit",
            24 => "Haste",
            25 => "Invisibility",
            26 => "Regeneration",
            27 => "Flight",
            28 => "Personal Teleporter",
            29 => "Medkit",
            _ => "Item"
        };
    }

    private static void DrawStatusBar()
    {
        ref var ps = ref _snap->Ps;
        int health = ps.Stats[Stats.STAT_HEALTH];
        int armor = ps.Stats[Stats.STAT_ARMOR];

        // Color scheme matching Q3
        // colors[0] = gold/normal, [1] = red/low, [2] = grey/firing, [3] = white/over100
        float* color = stackalloc float[4];

        // ── Ammo ──
        int weapon = ps.Weapon;
        if (weapon > 0 && weapon < Weapons.WP_NUM_WEAPONS)
        {
            int ammo = ps.Ammo[weapon];
            if (ammo > -1)
            {
                // Gold for normal, red for low, grey when firing
                if (ammo <= 0) { color[0] = 1; color[1] = 0.2f; color[2] = 0.2f; }
                else { color[0] = 1; color[1] = 0.69f; color[2] = 0; }
                color[3] = 1;
                Syscalls.R_SetColor(color);
                DrawField(0, 432, 3, ammo);
                Syscalls.R_SetColor(null);

                // Weapon icon next to ammo
                if (_weaponIcons[weapon] != 0)
                    DrawPic(CHAR_WIDTH * 3 + TEXT_ICON_SPACE, 432, ICON_SIZE, ICON_SIZE, _weaponIcons[weapon]);
            }
        }

        // ── Health ──
        if (health > 100) { color[0] = 1; color[1] = 1; color[2] = 1; }
        else if (health > 25) { color[0] = 1; color[1] = 0.69f; color[2] = 0; }
        else if (health > 0) {
            // Flash between gold and red
            if ((_time >> 8 & 1) != 0) { color[0] = 1; color[1] = 0.2f; color[2] = 0.2f; }
            else { color[0] = 1; color[1] = 0.69f; color[2] = 0; }
        }
        else { color[0] = 1; color[1] = 0.2f; color[2] = 0.2f; }
        color[3] = 1;
        Syscalls.R_SetColor(color);
        DrawField(185, 432, 3, health);

        // ── Armor ──
        if (armor > 0)
        {
            color[0] = 1; color[1] = 0.69f; color[2] = 0; color[3] = 1;
            Syscalls.R_SetColor(color);
            DrawField(370, 432, 3, armor);
        }

        Syscalls.R_SetColor(null);
    }

    private static void DrawField(int x, int y, int width, int value)
    {
        if (width < 1) return;
        if (width > 5) width = 5;

        // Clamp value to fit width
        int maxVal = 1, minVal = 0;
        for (int i = 0; i < width; i++) maxVal *= 10;
        maxVal--;
        for (int i = 0; i < width - 1; i++) minVal = minVal * 10 + 9;
        minVal = -minVal;
        if (value > maxVal) value = maxVal;
        if (value < minVal) value = minVal;

        // Convert to string and draw right-aligned
        string num = value.ToString();
        int len = num.Length;
        if (len > width) len = width;

        x += 2 + CHAR_WIDTH * (width - len);

        for (int i = 0; i < len; i++)
        {
            char c = num[i];
            int frame = (c == '-') ? STAT_MINUS : (c - '0');
            if (frame >= 0 && frame < 11)
                DrawPic(x, y, CHAR_WIDTH, CHAR_HEIGHT, _numberShaders[frame]);
            x += CHAR_WIDTH;
        }
    }

    private static void DrawCrosshair()
    {
        if (_snap == null) return;
        ref var ps = ref _snap->Ps;
        if (ps.Stats[Stats.STAT_HEALTH] <= 0) return;

        // Use crosshair 'e' (index 4) as default — the classic Q3 crosshair
        int crosshairIdx = 4;
        int shader = _crosshairShaders[crosshairIdx % NUM_CROSSHAIRS];
        if (shader == 0) return;

        float size = 24;
        float x = 320 - size / 2;
        float y = 240 - size / 2;

        float* color = stackalloc float[4];
        color[0] = 1; color[1] = 1; color[2] = 1; color[3] = 0.8f;
        Syscalls.R_SetColor(color);
        float w = size; float h = size;
        AdjustFrom640(ref x, ref y, ref w, ref h);
        Syscalls.R_DrawStretchPic(x, y, w, h, 0, 0, 1, 1, shader);
        Syscalls.R_SetColor(null);
    }

    // ── Weapon Switching ──

    private static bool WeaponSelectable(int weapon)
    {
        if (_snap == null) return false;
        ref var ps = ref _snap->Ps;
        if ((ps.Stats[Stats.STAT_WEAPONS] & (1 << weapon)) == 0) return false;
        // Gauntlet always selectable (no ammo needed)
        if (weapon == Weapons.WP_GAUNTLET) return true;
        return ps.Ammo[weapon] > 0;
    }

    private static void NextWeapon()
    {
        if (_snap == null) return;
        if (_snap->Ps.Stats[Stats.STAT_HEALTH] <= 0) return;

        int current = _weaponSelect;
        for (int i = 0; i < Weapons.WP_NUM_WEAPONS; i++)
        {
            current++;
            if (current >= Weapons.WP_NUM_WEAPONS) current = 1;
            if (current == Weapons.WP_GAUNTLET) continue; // skip gauntlet on scroll
            if (WeaponSelectable(current))
            {
                _weaponSelect = current;
                _weaponSelectTime = _time;
                break;
            }
        }
    }

    private static void PrevWeapon()
    {
        if (_snap == null) return;
        if (_snap->Ps.Stats[Stats.STAT_HEALTH] <= 0) return;

        int current = _weaponSelect;
        for (int i = 0; i < Weapons.WP_NUM_WEAPONS; i++)
        {
            current--;
            if (current < 1) current = Weapons.WP_NUM_WEAPONS - 1;
            if (current == Weapons.WP_GAUNTLET) continue;
            if (WeaponSelectable(current))
            {
                _weaponSelect = current;
                _weaponSelectTime = _time;
                break;
            }
        }
    }

    private static void SelectWeapon()
    {
        if (_snap == null) return;
        string arg = Syscalls.Argv(1);
        if (!int.TryParse(arg, out int num)) return;
        if (num < 1 || num >= Weapons.WP_NUM_WEAPONS) return;
        if (WeaponSelectable(num))
        {
            _weaponSelect = num;
            _weaponSelectTime = _time;
        }
    }

    // Weapon select display — shows weapon bar when weapon is changed
    private static int _weaponSelectTime;

    private static void DrawWeaponSelect()
    {
        ref var ps = ref _snap->Ps;

        // Show for 1400ms after weapon change
        int elapsed = _time - _weaponSelectTime;
        if (elapsed > 1400) return;

        // Fade out
        float alpha = 1.0f;
        if (elapsed > 1000)
            alpha = 1.0f - (elapsed - 1000) / 400.0f;
        if (alpha <= 0) return;

        // Count weapons the player has
        int weaponBits = ps.Stats[Stats.STAT_WEAPONS];
        int count = 0;
        for (int i = 1; i < Weapons.WP_NUM_WEAPONS; i++)
            if ((weaponBits & (1 << i)) != 0) count++;

        if (count == 0) return;

        // Draw weapon bar centered
        int startX = 320 - count * 20;
        int y = 380;
        int drawX = startX;

        float* color = stackalloc float[4];

        for (int i = 1; i < Weapons.WP_NUM_WEAPONS; i++)
        {
            if ((weaponBits & (1 << i)) == 0) continue;

            // Selection highlight
            if (i == _weaponSelect)
            {
                color[0] = 1; color[1] = 1; color[2] = 1; color[3] = alpha;
                Syscalls.R_SetColor(color);
                DrawPic(drawX - 4, y - 4, 40, 40, _selectShader);
            }

            // Weapon icon
            if (_weaponIcons[i] != 0)
            {
                if (ps.Ammo[i] <= 0 && i != Weapons.WP_GAUNTLET)
                {
                    // Dim if no ammo
                    color[0] = 1; color[1] = 1; color[2] = 1; color[3] = alpha * 0.3f;
                }
                else
                {
                    color[0] = 1; color[1] = 1; color[2] = 1; color[3] = alpha;
                }
                Syscalls.R_SetColor(color);
                DrawPic(drawX, y, 32, 32, _weaponIcons[i]);
            }

            drawX += 40;
        }

        // Weapon name below
        string[] weaponNames = {
            "", "Gauntlet", "Machinegun", "Shotgun", "Grenade Launcher",
            "Rocket Launcher", "Lightning Gun", "Railgun", "Plasma Gun",
            "BFG10K", "Grappling Hook"
        };
        if (_weaponSelect > 0 && _weaponSelect < weaponNames.Length)
        {
            string name = weaponNames[_weaponSelect];
            int textW = name.Length * BIGCHAR_WIDTH;
            DrawString(320 - textW / 2, 358, name, 1, 1, 1, alpha);
        }

        Syscalls.R_SetColor(null);
    }

    private static float DrawFPS(float y)
    {
        int t = Syscalls.Milliseconds();
        int frameTime = t - _fpsPreviousTime;
        _fpsPreviousTime = t;

        _fpsFrameTimes[_fpsIndex % FPS_FRAMES] = frameTime;
        _fpsIndex++;

        if (_fpsIndex > FPS_FRAMES)
        {
            int total = 0;
            for (int i = 0; i < FPS_FRAMES; i++) total += _fpsFrameTimes[i];
            if (total == 0) total = 1;
            int fps = 1000 * FPS_FRAMES / total;
            string s = $"{fps}fps";
            int w = s.Length * BIGCHAR_WIDTH;
            DrawString(635 - w, (int)y + 2, s, 1, 1, 1, 1);
        }
        return y + BIGCHAR_HEIGHT + 4;
    }

    private static float DrawTimer(float y)
    {
        int msec = _time - _levelStartTime;
        int seconds = msec / 1000;
        int mins = seconds / 60;
        seconds -= mins * 60;
        int tens = seconds / 10;
        seconds -= tens * 10;

        string s = $"{mins}:{tens}{seconds}";
        int w = s.Length * BIGCHAR_WIDTH;
        DrawString(635 - w, (int)y + 2, s, 1, 1, 1, 1);

        return y + BIGCHAR_HEIGHT + 4;
    }

    // ── Character Drawing (bigchars charset — 16x16 grid of glyphs) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AdjustFrom640(ref float x, ref float y, ref float w, ref float h)
    {
        x *= _screenXScale;
        y *= _screenYScale;
        w *= _screenXScale;
        h *= _screenYScale;
    }

    public static void DrawChar(float x, float y, int charWidth, int charHeight, int ch)
    {
        if (ch == ' ') return;
        ch &= 255;
        if (ch == 0) return;

        int row = ch >> 4;
        int col = ch & 15;
        float frow = row * 0.0625f;
        float fcol = col * 0.0625f;
        float size = 0.0625f;

        float w = charWidth;
        float h = charHeight;
        AdjustFrom640(ref x, ref y, ref w, ref h);
        Syscalls.R_DrawStretchPic(x, y, w, h,
            fcol, frow, fcol + size, frow + size, _charsetShader);
    }

    public static void DrawString(int x, int y, string text, float r, float g, float b, float a)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Shadow pass
        float* shadowColor = stackalloc float[4];
        shadowColor[0] = 0; shadowColor[1] = 0; shadowColor[2] = 0; shadowColor[3] = a;
        Syscalls.R_SetColor(shadowColor);
        int xx = x;
        for (int i = 0; i < text.Length; i++)
        {
            DrawChar(xx + 2, y + 2, BIGCHAR_WIDTH, BIGCHAR_HEIGHT, text[i]);
            xx += BIGCHAR_WIDTH;
        }

        // Text pass
        float* textColor = stackalloc float[4];
        textColor[0] = r; textColor[1] = g; textColor[2] = b; textColor[3] = a;
        Syscalls.R_SetColor(textColor);
        xx = x;
        for (int i = 0; i < text.Length; i++)
        {
            DrawChar(xx, y, BIGCHAR_WIDTH, BIGCHAR_HEIGHT, text[i]);
            xx += BIGCHAR_WIDTH;
        }
    }

    private static void DrawPic(float x, float y, float w, float h, int shader)
    {
        AdjustFrom640(ref x, ref y, ref w, ref h);
        Syscalls.R_DrawStretchPic(x, y, w, h, 0, 0, 1, 1, shader);
    }

    public static void FillRect(float x, float y, float w, float h, float* color)
    {
        Syscalls.R_SetColor(color);
        int white = Syscalls.R_RegisterShader("white");
        AdjustFrom640(ref x, ref y, ref w, ref h);
        Syscalls.R_DrawStretchPic(x, y, w, h, 0, 0, 0, 0, white);
        Syscalls.R_SetColor(null);
    }

    public static byte* GetGameStateRaw() => _gameStateRaw;

    public static int ClientNum => _clientNum;
    public static int GameType => _gametype;

    // ── Helpers ──

    private static void ParseServerInfo()
    {
        string info = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_SERVERINFO);
        _gametype = InfoInt(info, "g_gametype");
        _maxClients = InfoInt(info, "sv_maxclients");

        string mapname = InfoValueForKey(info, "mapname");
        _mapName = $"maps/{mapname}.bsp";

        Syscalls.Print($"[.NET cgame] Server: gametype={_gametype}, map={mapname}, maxclients={_maxClients}\n");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LerpAngle(float from, float to, float frac)
    {
        float diff = to - from;
        if (diff > 180) diff -= 360;
        if (diff < -180) diff += 360;
        return from + frac * diff;
    }

    private static int InfoInt(string info, string key)
    {
        string val = InfoValueForKey(info, key);
        return int.TryParse(val, out int result) ? result : 0;
    }

    private static string InfoValueForKey(string info, string key)
    {
        string search = $"\\{key}\\";
        int idx = info.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        int start = idx + search.Length;
        int end = info.IndexOf('\\', start);
        if (end < 0) end = info.Length;
        return info[start..end];
    }
}
