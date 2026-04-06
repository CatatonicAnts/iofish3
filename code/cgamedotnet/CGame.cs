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

    // Media handles
    private static int _charsetShader;
    private static int _whiteShader;
    private static int _crosshairShader;

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

    // Snapshot state
    private static int _latestSnapshotNum;
    private static int _latestSnapshotTime;

    // Snapshot buffers (~54KB each)
    private static byte* _snapBuffer1;
    private static byte* _snapBuffer2;
    private static Q3Snapshot* _snap;
    private static Q3Snapshot* _nextSnap;

    private static readonly int SnapshotSize = sizeof(Q3Snapshot) +
        (Q3Snapshot.MAX_ENTITIES_IN_SNAPSHOT - 1) * sizeof(Q3EntityState);

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

        // Allocate snapshot buffers
        _snapBuffer1 = (byte*)NativeMemory.AllocZeroed((nuint)SnapshotSize);
        _snapBuffer2 = (byte*)NativeMemory.AllocZeroed((nuint)SnapshotSize);

        // Get GL config
        byte* glconfig = stackalloc byte[Q3GlConfig.SIZE];
        Syscalls.GetGlconfig(glconfig);
        _screenWidth = *(int*)(glconfig + Q3GlConfig.VID_WIDTH);
        _screenHeight = *(int*)(glconfig + Q3GlConfig.VID_HEIGHT);
        Syscalls.Print($"[.NET cgame] Screen: {_screenWidth}x{_screenHeight}\n");

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

        // Register models from config strings
        RegisterGraphics();

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

    public static bool ConsoleCommand() => false;

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

        Syscalls.S_ClearLoopingSounds(0);
        Syscalls.R_ClearScene();

        ProcessSnapshots();

        if (_snap == null) return;
        if ((_snap->SnapFlags & SnapFlags.SNAPFLAG_NOT_ACTIVE) != 0) return;

        Syscalls.SetUserCmdValue(_weaponSelect, 1.0f);
        _weaponSelect = _snap->Ps.Weapon;

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
        CalcViewValues(ref refdef);

        AddPacketEntities();

        refdef.Time = _time;
        for (int i = 0; i < Q3RefDef.MAX_MAP_AREA_BYTES; i++)
            refdef.Areamask[i] = _snap->Areamask[i];

        Syscalls.R_RenderScene(&refdef);
        DrawHud();
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
        _crosshairShader = Syscalls.R_RegisterShader("gfx/2d/crosshaire");

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
            if (string.IsNullOrEmpty(modelName)) break;
            _gameModels[i] = Syscalls.R_RegisterModel(modelName);
            modelCount++;
        }
        Syscalls.Print($"[.NET cgame] Registered {modelCount} game models\n");

        // Register server-specified sounds
        int soundCount = 0;
        for (int i = 1; i < MAX_SOUNDS; i++)
        {
            string soundName = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_SOUNDS + i);
            if (string.IsNullOrEmpty(soundName)) break;
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
        }

        DrainServerCommands();
    }

    private static void SetNextSnap(Q3Snapshot* snap)
    {
        _nextSnap = snap;

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
        }
    }

    private static void DrainServerCommands()
    {
        if (_snap == null) return;
        while (_serverCommandSequence < _snap->GetServerCommandSequence())
        {
            _serverCommandSequence++;
            Syscalls.GetServerCommand(_serverCommandSequence);
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

    private static void CalcViewValues(ref Q3RefDef refdef)
    {
        ref var ps = ref _snap->Ps;

        refdef.X = 0;
        refdef.Y = 0;
        refdef.Width = _screenWidth;
        refdef.Height = _screenHeight;

        // View origin — player origin + viewheight
        refdef.ViewOrgX = ps.OriginX;
        refdef.ViewOrgY = ps.OriginY;
        refdef.ViewOrgZ = ps.OriginZ + ps.ViewHeight;

        // Interpolate between snapshots
        if (_nextSnap != null && _snap->ServerTime != _nextSnap->ServerTime)
        {
            ref var nextPs = ref _nextSnap->Ps;
            float f = _frameInterpolation;
            refdef.ViewOrgX = ps.OriginX + f * (nextPs.OriginX - ps.OriginX);
            refdef.ViewOrgY = ps.OriginY + f * (nextPs.OriginY - ps.OriginY);
            refdef.ViewOrgZ = ps.OriginZ + f * (nextPs.OriginZ - ps.OriginZ) + ps.ViewHeight;
        }

        // View angles → axis
        float pitch = ps.ViewAnglesX * MathF.PI / 180.0f;
        float yaw = ps.ViewAnglesY * MathF.PI / 180.0f;
        float roll = ps.ViewAnglesZ * MathF.PI / 180.0f;
        AnglesToAxis(pitch, yaw, roll, ref refdef);

        // FOV
        float fovX = 90.0f;
        float aspect = (float)_screenWidth / _screenHeight;
        refdef.FovX = fovX;
        refdef.FovY = 2.0f * MathF.Atan(MathF.Tan(fovX * MathF.PI / 360.0f) / aspect) * 180.0f / MathF.PI;
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

    private static void AddPacketEntities()
    {
        if (_snap == null) return;

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
        if (s1.ModelIndex == 0) return;

        // Skip our own player model (first person)
        if (s1.ClientNum == _clientNum) return;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_MODEL;
        rent.HModel = _gameModels[s1.ModelIndex];
        SetEntityOriginAndAxis(ref rent, ref cent);
        rent.FrameNum = s1.Frame;
        rent.OldFrame = s1.Frame;
        SetEntityColors(ref rent, 255, 255, 255, 255);
        Syscalls.R_AddRefEntityToScene(&rent);
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
        if (s1.ModelIndex == 0) return;

        Q3RefEntity rent = default;
        rent.ReType = Q3RefEntity.RT_MODEL;
        rent.HModel = _gameModels[s1.ModelIndex];
        SetEntityOriginAndAxis(ref rent, ref cent);
        SetEntityColors(ref rent, 255, 255, 255, 255);
        Syscalls.R_AddRefEntityToScene(&rent);
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

    // ── 2D HUD ──

    private static void DrawHud()
    {
        if (_snap == null) return;

        ref var ps = ref _snap->Ps;
        int health = ps.Stats[Stats.STAT_HEALTH];
        int armor = ps.Stats[Stats.STAT_ARMOR];

        // Crosshair
        if (_crosshairShader != 0)
        {
            float* xhairColor = stackalloc float[4];
            xhairColor[0] = 1; xhairColor[1] = 1; xhairColor[2] = 1; xhairColor[3] = 0.8f;
            Syscalls.R_SetColor(xhairColor);
            float size = 24;
            Syscalls.R_DrawStretchPic(320 - size / 2, 240 - size / 2, size, size,
                0, 0, 1, 1, _crosshairShader);
        }

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

        Syscalls.R_SetColor(null);
    }

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
