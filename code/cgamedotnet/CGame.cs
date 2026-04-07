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

		// Muzzle flash timing (set on EV_FIRE_WEAPON)
		public int MuzzleFlashTime;
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

	// Item model cache (bg_itemlist equivalent, indexed by item index 0-MAX_ITEMS)
	private const int MAX_ITEMS = 64;
	private static readonly int[] _itemModels = new int[MAX_ITEMS];
	private static readonly int[] _itemIcons = new int[MAX_ITEMS];
	private static readonly bool[] _itemRegistered = new bool[MAX_ITEMS];

	/// <summary>bg_itemlist world_model[0] paths, indexed by item number.</summary>
	private static readonly string?[] _bgItemModels = new string?[]
	{
		null,                                              //  0 — null item
		"models/powerups/armor/shard.md3",                 //  1 — Armor Shard
		"models/powerups/armor/armor_yel.md3",             //  2 — Armor
		"models/powerups/armor/armor_red.md3",             //  3 — Heavy Armor
		"models/powerups/health/small_cross.md3",          //  4 — 5 Health
		"models/powerups/health/medium_cross.md3",         //  5 — 25 Health
		"models/powerups/health/large_cross.md3",          //  6 — 50 Health
		"models/powerups/health/mega_cross.md3",           //  7 — Mega Health
		"models/weapons2/gauntlet/gauntlet.md3",           //  8 — Gauntlet
		"models/weapons2/shotgun/shotgun.md3",             //  9 — Shotgun
		"models/weapons2/machinegun/machinegun.md3",       // 10 — Machinegun
		"models/weapons2/grenadel/grenadel.md3",           // 11 — Grenade Launcher
		"models/weapons2/rocketl/rocketl.md3",             // 12 — Rocket Launcher
		"models/weapons2/lightning/lightning.md3",          // 13 — Lightning Gun
		"models/weapons2/railgun/railgun.md3",             // 14 — Railgun
		"models/weapons2/plasma/plasma.md3",               // 15 — Plasma Gun
		"models/weapons2/bfg/bfg.md3",                    // 16 — BFG10K
		"models/weapons2/grapple/grapple.md3",             // 17 — Grappling Hook
		"models/powerups/ammo/shotgunam.md3",              // 18 — Shells
		"models/powerups/ammo/machinegunam.md3",           // 19 — Bullets
		"models/powerups/ammo/grenadeam.md3",              // 20 — Grenades
		"models/powerups/ammo/plasmaam.md3",               // 21 — Cells
		"models/powerups/ammo/lightningam.md3",            // 22 — Lightning
		"models/powerups/ammo/rocketam.md3",               // 23 — Rockets
		"models/powerups/ammo/railgunam.md3",              // 24 — Slugs
		"models/powerups/ammo/bfgam.md3",                  // 25 — Bfg Ammo
		"models/powerups/holdable/teleporter.md3",         // 26 — Personal Teleporter
		"models/powerups/holdable/medkit.md3",             // 27 — Medkit
		"models/powerups/instant/quad.md3",                // 28 — Quad Damage
		"models/powerups/instant/enviro.md3",              // 29 — Battle Suit
		"models/powerups/instant/haste.md3",               // 30 — Haste
		"models/powerups/instant/invis.md3",               // 31 — Invisibility
		"models/powerups/instant/regen.md3",               // 32 — Regeneration
		"models/powerups/instant/flight.md3",              // 33 — Flight
		"models/flags/r_flag.md3",                         // 34 — Red Flag
		"models/flags/b_flag.md3",                         // 35 — Blue Flag
	};

	/// <summary>bg_itemlist icon paths, indexed by item number.</summary>
	private static readonly string?[] _bgItemIcons = new string?[]
	{
		null,                    //  0
		"icons/iconr_shard",     //  1
		"icons/iconr_yellow",    //  2
		"icons/iconr_red",       //  3
		"icons/iconh_green",     //  4
		"icons/iconh_yellow",    //  5
		"icons/iconh_red",       //  6
		"icons/iconh_mega",      //  7
		"icons/iconw_gauntlet",  //  8
		"icons/iconw_shotgun",   //  9
		"icons/iconw_machinegun",// 10
		"icons/iconw_grenade",   // 11
		"icons/iconw_rocket",    // 12
		"icons/iconw_lightning", // 13
		"icons/iconw_railgun",   // 14
		"icons/iconw_plasma",    // 15
		"icons/iconw_bfg",       // 16
		"icons/iconw_grapple",   // 17
		"icons/icona_shotgun",   // 18
		"icons/icona_machinegun",// 19
		"icons/icona_grenade",   // 20
		"icons/icona_plasma",    // 21
		"icons/icona_lightning", // 22
		"icons/icona_rocket",    // 23
		"icons/icona_railgun",   // 24
		"icons/icona_bfg",       // 25
		"icons/teleporter",      // 26
		"icons/medkit",          // 27
		"icons/quad",            // 28
		"icons/envirosuit",      // 29
		"icons/haste",           // 30
		"icons/invis",           // 31
		"icons/regen",           // 32
		"icons/flight",          // 33
		"icons/iconf_red1",      // 34
		"icons/iconf_blu1",      // 35
	};

	/// <summary>Lazily register item model and icon on first encounter (like CG_RegisterItemVisuals).</summary>
	private static void RegisterItemVisuals(int itemNum)
	{
		if (itemNum <= 0 || itemNum >= MAX_ITEMS) return;
		if (_itemRegistered[itemNum]) return;
		_itemRegistered[itemNum] = true;

		if (itemNum < _bgItemModels.Length && _bgItemModels[itemNum] != null)
			_itemModels[itemNum] = Syscalls.R_RegisterModel(_bgItemModels[itemNum]!);
		if (itemNum < _bgItemIcons.Length && _bgItemIcons[itemNum] != null)
			_itemIcons[itemNum] = Syscalls.R_RegisterShaderNoMip(_bgItemIcons[itemNum]!);
	}

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

	// Crosshair target tracking
	private static int _crosshairClientNum = -1;
	private static int _crosshairClientTime;

	// Attacker display tracking
	private static int _attackerTime;
	private const int ATTACKER_HEAD_TIME = 10000;

	// Warmup countdown
	private static int _warmup;
	private static int _warmupCount;

	// Powerup icon shaders
	private const int MAX_POWERUPS = 16;
	private static readonly int[] _powerupIcons = new int[MAX_POWERUPS];
	private const int POWERUP_BLINKS = 5;
	private const int POWERUP_BLINK_TIME = 1000;

	// Powerup type constants
	private const int PW_NONE = 0;
	private const int PW_QUAD = 1;
	private const int PW_BATTLESUIT = 2;
	private const int PW_HASTE = 3;
	private const int PW_INVIS = 4;
	private const int PW_REGEN = 5;
	private const int PW_FLIGHT = 6;
	private const int PW_REDFLAG = 7;
	private const int PW_BLUEFLAG = 8;

	// Muzzle flash
	private const int MUZZLE_FLASH_TIME = 20;
	private const int EF_FIRING = 0x00000100;

	// Reward medals
	private const int MAX_REWARDSTACK = 10;
	private const int REWARD_TIME = 3000;
	private static int _rewardStack;
	private static int _rewardTime;
	private static readonly int[] _rewardCount = new int[MAX_REWARDSTACK];
	private static readonly int[] _rewardShader = new int[MAX_REWARDSTACK];
	private static readonly int[] _rewardSound = new int[MAX_REWARDSTACK];

	// Reward media
	private static int _medalExcellent, _medalImpressive, _medalGauntlet;
	private static int _medalDefend, _medalAssist, _medalCapture;
	private static int _impressiveSound, _excellentSound, _humiliationSound;
	private static int _defendSound, _assistSound, _captureAwardSound;
	private static int _deniedSound, _hitSound, _hitTeamSound;

	// Lagometer
	private const int LAG_SAMPLES = 128;
	private const int MAX_LAGOMETER_PING = 900;
	private const int MAX_LAGOMETER_RANGE = 300;
	private static readonly int[] _lagFrameSamples = new int[LAG_SAMPLES];
	private static int _lagFrameCount;
	private static readonly int[] _lagSnapshotSamples = new int[LAG_SAMPLES];
	private static readonly int[] _lagSnapshotFlags = new int[LAG_SAMPLES];
	private static int _lagSnapshotCount;
	private static int _lagometerShader;
	private static int _disconnectIcon;

	// Third-person camera
	private static bool _thirdPerson;
	private static float _thirdPersonRange = 80.0f;
	private static float _thirdPersonAngle;

	// Previous player state (for reward detection)
	private static Q3PlayerState _oldPlayerState;

	// Footstep sounds: [footstepType][variant] — 4 variants per type
	private static readonly int[,] _footstepSounds = new int[Player.FOOTSTEP_TOTAL, 4];

	// Announcer sounds
	private static int _countPrepareSound, _countFightSound;
	private static int _count3Sound, _count2Sound, _count1Sound;
	private static int _oneFragSound, _twoFragSound, _threeFragSound;
	private static int _oneMinuteSound, _fiveMinuteSound, _suddenDeathSound;

	// Frag/time limit warning flags (prevent duplicate announcements)
	private static int _fraglimitWarnings;
	private static int _timelimitWarnings;

	// Gurp sounds (underwater pain)
	private static int _sfxGurp1, _sfxGurp2;
	private static int _sfxDrowning;

	// Powerup sounds
	private static int _sfxQuadSound, _sfxProtectSound, _sfxRegenSound;
	private static int _sfxGibSound, _sfxGibBounce1, _sfxGibBounce2, _sfxGibBounce3;

	// Team game sounds
	private static int _sfxCaptureYourTeam, _sfxCaptureOpponent;
	private static int _sfxReturnYourTeam, _sfxReturnOpponent;
	private static int _sfxTakenYourTeam, _sfxTakenOpponent;
	private static int _sfxRedScoredSound, _sfxBlueScoredSound;
	private static int _sfxRedLeadsSound, _sfxBlueLeadsSound, _sfxTiedLeadSound;
	private static int _sfxRedFlagReturned, _sfxBlueFlagReturned;

	// Holdable item sounds
	private static int _sfxUseMedkit, _sfxUseTeleporter;

	// Score plum shader
	private static int _scorePlumShader;

	// Player shadow
	private static int _shadowMarkShader;

	// Vote state
	private static int _voteTime;
	private static string _voteString = "";
	private static int _voteYes, _voteNo;

	// Low ammo warning state
	private static int _lowAmmoWarning; // 0=none, 1=low, 2=empty

	// Last computed view (for crosshair scan)
	private static float _viewOrgX, _viewOrgY, _viewOrgZ;
	private static float _viewFwdX, _viewFwdY, _viewFwdZ;

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
		_rewardStack = 0; _rewardTime = 0;
		_lagFrameCount = 0; _lagSnapshotCount = 0;
		_fraglimitWarnings = 0; _timelimitWarnings = 0;
		// Zero sentinel so CheckLocalSounds skips first snapshot
		_oldPlayerState.CommandTime = 0;
		_thirdPerson = false; _thirdPersonRange = 80.0f; _thirdPersonAngle = 0.0f;
		for (int i = 0; i < MAX_CHAT_LINES; i++) { _chatMessages[i] = ""; _chatTimes[i] = 0; }
		Array.Clear(_itemModels); Array.Clear(_itemIcons); Array.Clear(_itemRegistered);
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

		// Record frame timing for lagometer
		AddLagometerFrameInfo();

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

		// Store view for crosshair scanning
		_viewOrgX = refdef.ViewOrgX; _viewOrgY = refdef.ViewOrgY; _viewOrgZ = refdef.ViewOrgZ;
		_viewFwdX = refdef.Axis0X; _viewFwdY = refdef.Axis0Y; _viewFwdZ = refdef.Axis0Z;

		// Update sound spatialization — tells the sound engine where the listener is
		{
			float* viewOrg = stackalloc float[3];
			viewOrg[0] = refdef.ViewOrgX; viewOrg[1] = refdef.ViewOrgY; viewOrg[2] = refdef.ViewOrgZ;
			// viewaxis is 3x3 row-major: forward, right, up
			float* viewAxis = stackalloc float[9];
			viewAxis[0] = refdef.Axis0X; viewAxis[1] = refdef.Axis0Y; viewAxis[2] = refdef.Axis0Z;
			viewAxis[3] = refdef.Axis1X; viewAxis[4] = refdef.Axis1Y; viewAxis[5] = refdef.Axis1Z;
			viewAxis[6] = refdef.Axis2X; viewAxis[7] = refdef.Axis2Y; viewAxis[8] = refdef.Axis2Z;
			// Check if view is underwater for muffled audio
			const int MASK_WATER = 32 | 8 | 16;
			int contents = Prediction.PointContents(viewOrg, -1);
			int inwater = (contents & MASK_WATER) != 0 ? 1 : 0;
			Syscalls.S_Respatialize(_snap->Ps.ClientNum, viewOrg, viewAxis, inwater);
		}

		CrashLog.Breadcrumb("AddPacketEntities");
		try { AddPacketEntities(); }
		catch (Exception ex) { CrashLog.LogException("AddPacketEntities", ex); Syscalls.Print($"[.NET cgame] ERROR in AddPacketEntities: {ex.Message}\n"); }

		CrashLog.Breadcrumb("LocalEntities");
		try { LocalEntities.AddToScene(_time); }
		catch (Exception ex) { CrashLog.LogException("LocalEntities", ex); Syscalls.Print($"[.NET cgame] ERROR in LocalEntities.AddToScene: {ex.Message}\n"); }

		CrashLog.Breadcrumb("Marks");
		try { Marks.AddToScene(_time); }
		catch (Exception ex) { CrashLog.LogException("Marks", ex); Syscalls.Print($"[.NET cgame] ERROR in Marks.AddToScene: {ex.Message}\n"); }

		// First-person view weapon (skip in third-person mode)
		if (!_thirdPerson)
		{
			CrashLog.Breadcrumb("AddViewWeapon");
			try
			{
				int clientNum2 = _snap->Ps.ClientNum;
				int muzzleFlashTime = (clientNum2 >= 0 && clientNum2 < MAX_GENTITIES)
					? _entities[clientNum2].MuzzleFlashTime : 0;
				int eFlags = Prediction.PredictedPlayerState.EFlags;

				// Drive weapon animation from predicted player state (more responsive than snapshot)
				if (clientNum2 >= 0 && clientNum2 < MAX_CLIENTS)
				{
					// Build powerups bitmask from PowerupTimers array
					int powerups = 0;
					fixed (Q3PlayerState* pps2 = &Prediction.PredictedPlayerState)
					{
						for (int pw = 0; pw < Q3PlayerState.MAX_POWERUPS; pw++)
							if (pps2->PowerupTimers[pw] > 0) powerups |= (1 << pw);
					}
					Player.UpdatePredictedAnimation(clientNum2,
						Prediction.PredictedPlayerState.LegsAnim,
						Prediction.PredictedPlayerState.TorsoAnim,
						powerups, _time);
				}

				fixed (Q3PlayerState* pps = &Prediction.PredictedPlayerState)
				{
					Player.AddViewWeapon(pps, _time,
						refdef.ViewOrgX, refdef.ViewOrgY, refdef.ViewOrgZ,
						refdef.Axis0X, refdef.Axis0Y, refdef.Axis0Z,
						refdef.Axis1X, refdef.Axis1Y, refdef.Axis1Z,
						refdef.Axis2X, refdef.Axis2Y, refdef.Axis2Z,
						(int)refdef.FovX, muzzleFlashTime, eFlags);
				}
			}
			catch (Exception ex) { CrashLog.LogException("AddViewWeapon", ex); Syscalls.Print($"[.NET cgame] ERROR in AddViewWeapon: {ex.Message}\n"); }
		} // end !_thirdPerson

		CrashLog.Breadcrumb("RenderScene");
		DamageBlendBlob(ref refdef);
		refdef.Time = _time;
		for (int i = 0; i < Q3RefDef.MAX_MAP_AREA_BYTES; i++)
			refdef.Areamask[i] = _snap->Areamask[i];

		Syscalls.R_RenderScene(&refdef);
		try { DrawHud(); }
		catch (Exception ex) { Syscalls.Print($"[.NET cgame] ERROR in DrawHud: {ex.Message}\n"); }
	}

	public static int CrosshairPlayer() => _crosshairClientNum;
	public static int LastAttacker()
	{
		if (_snap == null) return -1;
		int attacker = _snap->Ps.Persistant[Persistant.PERS_ATTACKER];
		if (attacker < 0 || attacker >= EntityNum.MAX_CLIENTS) return -1;
		return attacker;
	}
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

		// Powerup icons
		_powerupIcons[PW_QUAD] = Syscalls.R_RegisterShaderNoMip("icons/quad");
		_powerupIcons[PW_BATTLESUIT] = Syscalls.R_RegisterShaderNoMip("icons/envirosuit");
		_powerupIcons[PW_HASTE] = Syscalls.R_RegisterShaderNoMip("icons/haste");
		_powerupIcons[PW_INVIS] = Syscalls.R_RegisterShaderNoMip("icons/invis");
		_powerupIcons[PW_REGEN] = Syscalls.R_RegisterShaderNoMip("icons/regen");
		_powerupIcons[PW_FLIGHT] = Syscalls.R_RegisterShaderNoMip("icons/flight");
		_powerupIcons[PW_REDFLAG] = Syscalls.R_RegisterShaderNoMip("icons/iconf_red1");
		_powerupIcons[PW_BLUEFLAG] = Syscalls.R_RegisterShaderNoMip("icons/iconf_blu1");

		// Reward medal shaders
		_medalExcellent = Syscalls.R_RegisterShaderNoMip("medal_excellent");
		_medalImpressive = Syscalls.R_RegisterShaderNoMip("medal_impressive");
		_medalGauntlet = Syscalls.R_RegisterShaderNoMip("medal_gauntlet");
		_medalDefend = Syscalls.R_RegisterShaderNoMip("medal_defend");
		_medalAssist = Syscalls.R_RegisterShaderNoMip("medal_assist");
		_medalCapture = Syscalls.R_RegisterShaderNoMip("medal_capture");

		// Lagometer
		_lagometerShader = Syscalls.R_RegisterShaderNoMip("lagometer");
		_disconnectIcon = Syscalls.R_RegisterShaderNoMip("gfx/2d/net");

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
		// Save old player state for reward/sound detection before transition
		_oldPlayerState = _snap->Ps;

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

		// Check for reward medals and hit sounds
		CheckLocalSounds(&_snap->Ps, ref _oldPlayerState);

		// Record snapshot info for lagometer
		AddLagometerSnapshotInfo(_snap);
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
		else if (index == Q3GameState.CS_WARMUP)
		{
			string warmupStr = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_WARMUP);
			_warmup = string.IsNullOrEmpty(warmupStr) ? 0 : int.TryParse(warmupStr, out int w) ? w : 0;
		}
		else if (index == Q3GameState.CS_VOTE_TIME)
		{
			string vt = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_VOTE_TIME);
			_voteTime = string.IsNullOrEmpty(vt) ? 0 : int.TryParse(vt, out int v) ? v : 0;
		}
		else if (index == Q3GameState.CS_VOTE_STRING)
		{
			_voteString = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_VOTE_STRING) ?? "";
		}
		else if (index == Q3GameState.CS_VOTE_YES)
		{
			string vy = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_VOTE_YES);
			_voteYes = string.IsNullOrEmpty(vy) ? 0 : int.TryParse(vy, out int v) ? v : 0;
		}
		else if (index == Q3GameState.CS_VOTE_NO)
		{
			string vn = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_VOTE_NO);
			_voteNo = string.IsNullOrEmpty(vn) ? 0 : int.TryParse(vn, out int v) ? v : 0;
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

		// Reward sounds
		_impressiveSound = Syscalls.S_RegisterSound("sound/feedback/impressive.wav", 0);
		_excellentSound = Syscalls.S_RegisterSound("sound/feedback/excellent.wav", 0);
		_humiliationSound = Syscalls.S_RegisterSound("sound/feedback/humiliation.wav", 0);
		_defendSound = Syscalls.S_RegisterSound("sound/feedback/defense.wav", 0);
		_assistSound = Syscalls.S_RegisterSound("sound/feedback/assist.wav", 0);
		_captureAwardSound = Syscalls.S_RegisterSound("sound/teamplay/flagcap_yourteam.wav", 0);
		_deniedSound = Syscalls.S_RegisterSound("sound/feedback/denied.wav", 0);
		_hitSound = Syscalls.S_RegisterSound("sound/feedback/hit.wav", 0);
		_hitTeamSound = Syscalls.S_RegisterSound("sound/feedback/hit_teammate.wav", 0);

		// Footstep sounds (4 variants each)
		for (int i = 0; i < 4; i++)
		{
			_footstepSounds[Player.FOOTSTEP_NORMAL, i] = Syscalls.S_RegisterSound($"sound/player/footsteps/step{i + 1}.wav", 0);
			_footstepSounds[Player.FOOTSTEP_BOOT, i] = Syscalls.S_RegisterSound($"sound/player/footsteps/boot{i + 1}.wav", 0);
			_footstepSounds[Player.FOOTSTEP_FLESH, i] = Syscalls.S_RegisterSound($"sound/player/footsteps/flesh{i + 1}.wav", 0);
			_footstepSounds[Player.FOOTSTEP_MECH, i] = Syscalls.S_RegisterSound($"sound/player/footsteps/mech{i + 1}.wav", 0);
			_footstepSounds[Player.FOOTSTEP_ENERGY, i] = Syscalls.S_RegisterSound($"sound/player/footsteps/energy{i + 1}.wav", 0);
			_footstepSounds[Player.FOOTSTEP_METAL, i] = Syscalls.S_RegisterSound($"sound/player/footsteps/clank{i + 1}.wav", 0);
			_footstepSounds[Player.FOOTSTEP_SPLASH, i] = Syscalls.S_RegisterSound($"sound/player/footsteps/splash{i + 1}.wav", 0);
		}

		// Announcer sounds
		_countPrepareSound = Syscalls.S_RegisterSound("sound/feedback/prepare.wav", 0);
		_countFightSound = Syscalls.S_RegisterSound("sound/feedback/fight.wav", 0);
		_count3Sound = Syscalls.S_RegisterSound("sound/feedback/three.wav", 0);
		_count2Sound = Syscalls.S_RegisterSound("sound/feedback/two.wav", 0);
		_count1Sound = Syscalls.S_RegisterSound("sound/feedback/one.wav", 0);
		_oneFragSound = Syscalls.S_RegisterSound("sound/feedback/1_frag.wav", 0);
		_twoFragSound = Syscalls.S_RegisterSound("sound/feedback/2_frags.wav", 0);
		_threeFragSound = Syscalls.S_RegisterSound("sound/feedback/3_frags.wav", 0);
		_oneMinuteSound = Syscalls.S_RegisterSound("sound/feedback/1_minute.wav", 0);
		_fiveMinuteSound = Syscalls.S_RegisterSound("sound/feedback/5_minute.wav", 0);
		_suddenDeathSound = Syscalls.S_RegisterSound("sound/feedback/sudden_death.wav", 0);

		// Gurp/drown sounds (for underwater pain/death)
		_sfxGurp1 = Syscalls.S_RegisterSound("sound/player/gurp1.wav", 0);
		_sfxGurp2 = Syscalls.S_RegisterSound("sound/player/gurp2.wav", 0);
		_sfxDrowning = Syscalls.S_RegisterSound("sound/player/gurp1.wav", 0);

		// Powerup sounds
		_sfxQuadSound = Syscalls.S_RegisterSound("sound/items/damage3.wav", 0);
		_sfxProtectSound = Syscalls.S_RegisterSound("sound/items/protect3.wav", 0);
		_sfxRegenSound = Syscalls.S_RegisterSound("sound/items/regen.wav", 0);

		// Gib sounds
		_sfxGibSound = Syscalls.S_RegisterSound("sound/player/gibsplt1.wav", 0);
		_sfxGibBounce1 = Syscalls.S_RegisterSound("sound/player/gibimp1.wav", 0);
		_sfxGibBounce2 = Syscalls.S_RegisterSound("sound/player/gibimp2.wav", 0);
		_sfxGibBounce3 = Syscalls.S_RegisterSound("sound/player/gibimp3.wav", 0);

		// CTF/team sounds
		_sfxCaptureYourTeam = Syscalls.S_RegisterSound("sound/teamplay/flagcap_yourteam.wav", 0);
		_sfxCaptureOpponent = Syscalls.S_RegisterSound("sound/teamplay/flagcap_opponent.wav", 0);
		_sfxReturnYourTeam = Syscalls.S_RegisterSound("sound/teamplay/flagreturn_yourteam.wav", 0);
		_sfxReturnOpponent = Syscalls.S_RegisterSound("sound/teamplay/flagreturn_opponent.wav", 0);
		_sfxTakenYourTeam = Syscalls.S_RegisterSound("sound/teamplay/voc_team_flag.wav", 0);
		_sfxTakenOpponent = Syscalls.S_RegisterSound("sound/teamplay/voc_enemy_flag.wav", 0);
		_sfxRedFlagReturned = Syscalls.S_RegisterSound("sound/teamplay/voc_red_returned.wav", 0);
		_sfxBlueFlagReturned = Syscalls.S_RegisterSound("sound/teamplay/voc_blue_returned.wav", 0);
		_sfxRedScoredSound = Syscalls.S_RegisterSound("sound/teamplay/voc_red_scores.wav", 0);
		_sfxBlueScoredSound = Syscalls.S_RegisterSound("sound/teamplay/voc_blue_scores.wav", 0);
		_sfxRedLeadsSound = Syscalls.S_RegisterSound("sound/teamplay/voc_red_lead.wav", 0);
		_sfxBlueLeadsSound = Syscalls.S_RegisterSound("sound/teamplay/voc_blue_lead.wav", 0);
		_sfxTiedLeadSound = Syscalls.S_RegisterSound("sound/teamplay/voc_scores_tied.wav", 0);

		// Holdable item sounds
		_sfxUseMedkit = Syscalls.S_RegisterSound("sound/items/use_medkit.wav", 0);
		_sfxUseTeleporter = Syscalls.S_RegisterSound("sound/world/telein.wav", 0);

		// Score plum shader
		_scorePlumShader = Syscalls.R_RegisterShader("gfx/2d/numbers/eight_32b");

		// Player shadow shader (circular blob)
		_shadowMarkShader = Syscalls.R_RegisterShader("markShadow");

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
			{
				int fsType = Player.GetFootstepType(es.ClientNum);
				int variant = _frameCount & 3; // random-ish variant
				int sfx = _footstepSounds[fsType, variant];
				if (sfx != 0) Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_BODY, sfx);
				break;
			}
			case EntityEvent.EV_FOOTSTEP_METAL:
			{
				int variant = _frameCount & 3;
				int sfx = _footstepSounds[Player.FOOTSTEP_METAL, variant];
				if (sfx != 0) Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_BODY, sfx);
				break;
			}
			case EntityEvent.EV_FOOTSPLASH:
			case EntityEvent.EV_FOOTWADE:
			case EntityEvent.EV_SWIM:
			{
				int variant = _frameCount & 3;
				int sfx = _footstepSounds[Player.FOOTSTEP_SPLASH, variant];
				if (sfx != 0) Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_BODY, sfx);
				break;
			}

			case EntityEvent.EV_FALL_SHORT:
				Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, _sfxLandSound);
				if (es.Number == _snap->Ps.ClientNum)
				{
					_landChange = -8;
					_landTime = _time;
				}
				break;

			case EntityEvent.EV_FALL_MEDIUM:
			{
				// Medium fall — play landing sound + per-model fall sound
				Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, _sfxLandSound);
				int fallSfx = Player.CustomSound(es.Number, "*fall1.wav");
				if (fallSfx != 0)
					Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_VOICE, fallSfx);
				if (es.Number == _snap->Ps.ClientNum)
				{
					_landChange = -16;
					_landTime = _time;
				}
				break;
			}

			case EntityEvent.EV_FALL_FAR:
			{
				// Far fall — play landing sound + per-model falling sound (big damage)
				Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, _sfxLandSound);
				int fallingSfx = Player.CustomSound(es.Number, "*falling1.wav");
				if (fallingSfx != 0)
					Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_VOICE, fallingSfx);
				if (es.Number == _snap->Ps.ClientNum)
				{
					_landChange = -24;
					_landTime = _time;
				}
				break;
			}

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
			{
				int jumpSfx = Player.CustomSound(es.Number, "*jump1.wav");
				if (jumpSfx != 0)
					Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_VOICE, jumpSfx);
				else
					Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_VOICE, _sfxJumpSound);
				break;
			}

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

			case EntityEvent.EV_WATER_TOUCH:
			{
				int variant = _frameCount & 3;
				int sfx = _footstepSounds[Player.FOOTSTEP_SPLASH, variant];
				if (sfx != 0) Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, sfx);
				break;
			}
			case EntityEvent.EV_WATER_LEAVE:
			{
				int variant = _frameCount & 3;
				int sfx = _footstepSounds[Player.FOOTSTEP_SPLASH, variant];
				if (sfx != 0) Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, sfx);
				break;
			}
			case EntityEvent.EV_WATER_UNDER:
				Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_BODY,
					(_frameCount & 1) != 0 ? _sfxGurp1 : _sfxGurp2);
				break;

			case EntityEvent.EV_WATER_CLEAR:
			{
				int gaspSfx = Player.CustomSound(es.Number, "*gasp.wav");
				if (gaspSfx != 0)
					Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_VOICE, gaspSfx);
				break;
			}

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
				PainEvent(ref cent, eventParm);
				break;

			case EntityEvent.EV_DEATH1:
			case EntityEvent.EV_DEATH2:
			case EntityEvent.EV_DEATH3:
			{
				int deathSfx;
				if (WaterLevel(ref cent) == 3)
				{
					// Underwater: play drown sound
					deathSfx = Player.CustomSound(es.Number, "*drown.wav");
				}
				else
				{
					int deathVariant = eventType - EntityEvent.EV_DEATH1 + 1;
					deathSfx = Player.CustomSound(es.Number, $"*death{deathVariant}.wav");
				}
				if (deathSfx != 0)
					Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_VOICE, deathSfx);
				break;
			}

			case EntityEvent.EV_OBITUARY:
				Obituary(ref es);
				break;

			case EntityEvent.EV_POWERUP_QUAD:
				Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_ITEM, _sfxQuadSound);
				break;
			case EntityEvent.EV_POWERUP_BATTLESUIT:
				Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_ITEM, _sfxProtectSound);
				break;
			case EntityEvent.EV_POWERUP_REGEN:
				Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_ITEM, _sfxRegenSound);
				break;

			case EntityEvent.EV_GIB_PLAYER:
				Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_BODY, _sfxGibSound);
				break;

			case EntityEvent.EV_ITEM_POP:
				Syscalls.S_StartSound(origin, EntityNum.ENTITYNUM_NONE, SoundChannel.CHAN_AUTO, _sfxRespawnSound);
				break;

			case EntityEvent.EV_SCOREPLUM:
				ScorePlum(ref es);
				break;

			case EntityEvent.EV_GLOBAL_TEAM_SOUND:
				GlobalTeamSoundEvent(eventParm);
				break;

			case EntityEvent.EV_TAUNT:
			{
				int tauntSfx = Player.CustomSound(es.Number, "*taunt.wav");
				if (tauntSfx != 0)
					Syscalls.S_StartSound(null, es.Number, SoundChannel.CHAN_VOICE, tauntSfx);
				break;
			}

			case EntityEvent.EV_STOPLOOPINGSOUND:
				Syscalls.S_StopLoopingSound(es.Number);
				break;

			// Use item events (EV_USE_ITEM0 through EV_USE_ITEM0+15)
			default:
				if (eventType >= EntityEvent.EV_USE_ITEM0 && eventType < EntityEvent.EV_USE_ITEM0 + 16)
				{
					int itemIndex = eventType - EntityEvent.EV_USE_ITEM0;
					// Item 1 = medkit, Item 5 = teleporter
					if (itemIndex == 1)
						Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, _sfxUseMedkit);
					else if (itemIndex == 5)
						Syscalls.S_StartSound(origin, es.Number, SoundChannel.CHAN_AUTO, _sfxUseTeleporter);
				}
				break;
		}
	}

	/// <summary>Handle EV_PAIN — play health-appropriate pain sound.</summary>
	private static void PainEvent(ref CEntity cent, int health)
	{
		int entNum = cent.CurrentState.Number;

		// Skip pain sounds for local player (handled separately via CG_CheckLocalSounds)
		if (_snap != null && entNum == _snap->Ps.ClientNum) return;

		// Throttle: don't play pain sounds more than once per 500ms
		if (_time - Player.GetPainTime(entNum) < 500) return;
		Player.SetPainTime(entNum, _time);

		int sfx;
		if (WaterLevel(ref cent) == 3)
		{
			// Underwater: play gurp sound instead
			sfx = (_frameCount & 1) != 0 ? _sfxGurp1 : _sfxGurp2;
		}
		else
		{
			// Select sound based on health
			string soundName;
			if (health < 25) soundName = "*pain25_1.wav";
			else if (health < 50) soundName = "*pain50_1.wav";
			else if (health < 75) soundName = "*pain75_1.wav";
			else soundName = "*pain100_1.wav";
			sfx = Player.CustomSound(entNum, soundName);
		}

		if (sfx != 0)
			Syscalls.S_StartSound(null, entNum, SoundChannel.CHAN_VOICE, sfx);
	}

	/// <summary>Returns water level (0-3) for an entity based on BSP content checks.</summary>
	private static int WaterLevel(ref CEntity cent)
	{
		const int MINS_Z = -24;
		const int DEFAULT_VIEWHEIGHT = 26;
		const int CROUCH_VIEWHEIGHT = 12;
		const int MASK_WATER = 32 | 8 | 16; // CONTENTS_WATER | CONTENTS_LAVA | CONTENTS_SLIME

		int anim = cent.CurrentState.LegsAnim & ~128; // ~ANIM_TOGGLEBIT
		int viewheight = (anim == Player.LEGS_WALKCR || anim == Player.LEGS_IDLECR)
			? CROUCH_VIEWHEIGHT : DEFAULT_VIEWHEIGHT;

		float* point = stackalloc float[3];
		point[0] = cent.LerpOriginX;
		point[1] = cent.LerpOriginY;
		point[2] = cent.LerpOriginZ + MINS_Z + 1;

		int contents = Prediction.PointContents(point, -1);
		if ((contents & MASK_WATER) == 0) return 0;

		int sample2 = viewheight - MINS_Z;
		int sample1 = sample2 / 2;

		point[2] = cent.LerpOriginZ + MINS_Z + sample1;
		contents = Prediction.PointContents(point, -1);
		if ((contents & MASK_WATER) == 0) return 1;

		point[2] = cent.LerpOriginZ + MINS_Z + sample2;
		contents = Prediction.PointContents(point, -1);
		return (contents & MASK_WATER) != 0 ? 3 : 2;
	}

	/// <summary>Handle EV_OBITUARY — display kill message in console and center print.</summary>
	private static void Obituary(ref Q3EntityState es)
	{
		int target = es.OtherEntityNum;
		int attacker = es.OtherEntityNum2;
		int mod = es.EventParm;

		if (target < 0 || target >= MAX_CLIENTS) return;

		string targetName = Player.GetClientName(target);
		if (string.IsNullOrEmpty(targetName)) targetName = "noname";

		string? message = null;
		string message2 = "";

		// Self-kills and world kills
		if (attacker == target)
		{
			message = mod switch
			{
				MeansOfDeath.MOD_GRENADE_SPLASH => "tripped on own grenade",
				MeansOfDeath.MOD_ROCKET_SPLASH => "blew up",
				MeansOfDeath.MOD_PLASMA_SPLASH => "melted",
				MeansOfDeath.MOD_BFG_SPLASH => "should have used a smaller gun",
				_ => "killed self",
			};
		}
		else if (attacker < 0 || attacker >= MAX_CLIENTS)
		{
			// World kill
			message = mod switch
			{
				MeansOfDeath.MOD_SUICIDE => "suicides",
				MeansOfDeath.MOD_FALLING => "cratered",
				MeansOfDeath.MOD_CRUSH => "was squished",
				MeansOfDeath.MOD_WATER => "sank like a rock",
				MeansOfDeath.MOD_SLIME => "melted",
				MeansOfDeath.MOD_LAVA => "does a back flip into the lava",
				MeansOfDeath.MOD_TARGET_LASER => "saw the light",
				MeansOfDeath.MOD_TRIGGER_HURT => "was in the wrong place",
				_ => "died",
			};
		}

		if (message != null)
		{
			Syscalls.Print($"{targetName} {message}.\n");
			return;
		}

		// Player-on-player kill
		string attackerName = Player.GetClientName(attacker);
		if (string.IsNullOrEmpty(attackerName)) attackerName = "noname";

		// Update attacker tracking if we were the victim
		if (target == _clientNum)
			_attackerTime = _time;

		switch (mod)
		{
			case MeansOfDeath.MOD_GAUNTLET: message = "was pummeled by"; break;
			case MeansOfDeath.MOD_MACHINEGUN: message = "was machinegunned by"; break;
			case MeansOfDeath.MOD_SHOTGUN: message = "was gunned down by"; break;
			case MeansOfDeath.MOD_GRENADE: message = "ate"; message2 = "'s grenade"; break;
			case MeansOfDeath.MOD_GRENADE_SPLASH: message = "was shredded by"; message2 = "'s shrapnel"; break;
			case MeansOfDeath.MOD_ROCKET: message = "ate"; message2 = "'s rocket"; break;
			case MeansOfDeath.MOD_ROCKET_SPLASH: message = "almost dodged"; message2 = "'s rocket"; break;
			case MeansOfDeath.MOD_PLASMA: message = "was melted by"; message2 = "'s plasmagun"; break;
			case MeansOfDeath.MOD_PLASMA_SPLASH: message = "was melted by"; message2 = "'s plasmagun"; break;
			case MeansOfDeath.MOD_RAILGUN: message = "was railed by"; break;
			case MeansOfDeath.MOD_LIGHTNING: message = "was electrocuted by"; break;
			case MeansOfDeath.MOD_BFG: case MeansOfDeath.MOD_BFG_SPLASH: message = "was blasted by"; message2 = "'s BFG"; break;
			case MeansOfDeath.MOD_TELEFRAG: message = "tried to invade"; message2 = "'s personal space"; break;
			default: message = "was killed by"; break;
		}

		Syscalls.Print($"{targetName} {message} {attackerName}{message2}\n");

		// Center print for our frags
		if (attacker == _clientNum)
		{
			string s = _gametype < GT_TEAM
				? $"You fragged {targetName}"
				: $"You fragged {targetName}";
			_centerPrint = s;
			_centerPrintTime = _time;
		}
	}

	/// <summary>Handle EV_SCOREPLUM — floating damage score numbers.</summary>
	private static void ScorePlum(ref Q3EntityState es)
	{
		// Score plums show "+N" above the victim when you score
		// Currently log-only; visual rendering requires local entity support
		int score = es.OtherEntityNum2;
		if (score < 0) score = 0;
	}

	/// <summary>Handle EV_GLOBAL_TEAM_SOUND — CTF/team announcements.</summary>
	private static void GlobalTeamSoundEvent(int parm)
	{
		if (_snap == null) return;
		int team = _snap->Ps.Persistant[Persistant.PERS_TEAM];

		switch (parm)
		{
			case GlobalTeamSound.GTS_RED_CAPTURE:
				Syscalls.S_StartLocalSound(team == Teams.TEAM_RED ? _sfxCaptureYourTeam : _sfxCaptureOpponent,
					SoundChannel.CHAN_ANNOUNCER);
				break;
			case GlobalTeamSound.GTS_BLUE_CAPTURE:
				Syscalls.S_StartLocalSound(team == Teams.TEAM_BLUE ? _sfxCaptureYourTeam : _sfxCaptureOpponent,
					SoundChannel.CHAN_ANNOUNCER);
				break;
			case GlobalTeamSound.GTS_RED_RETURN:
				Syscalls.S_StartLocalSound(team == Teams.TEAM_RED ? _sfxReturnYourTeam : _sfxReturnOpponent,
					SoundChannel.CHAN_ANNOUNCER);
				Syscalls.S_StartLocalSound(_sfxBlueFlagReturned, SoundChannel.CHAN_ANNOUNCER);
				break;
			case GlobalTeamSound.GTS_BLUE_RETURN:
				Syscalls.S_StartLocalSound(team == Teams.TEAM_BLUE ? _sfxReturnYourTeam : _sfxReturnOpponent,
					SoundChannel.CHAN_ANNOUNCER);
				Syscalls.S_StartLocalSound(_sfxRedFlagReturned, SoundChannel.CHAN_ANNOUNCER);
				break;
			case GlobalTeamSound.GTS_RED_TAKEN:
				if (team == Teams.TEAM_BLUE)
					Syscalls.S_StartLocalSound(_sfxTakenOpponent, SoundChannel.CHAN_ANNOUNCER);
				else if (team == Teams.TEAM_RED)
					Syscalls.S_StartLocalSound(_sfxTakenYourTeam, SoundChannel.CHAN_ANNOUNCER);
				break;
			case GlobalTeamSound.GTS_BLUE_TAKEN:
				if (team == Teams.TEAM_RED)
					Syscalls.S_StartLocalSound(_sfxTakenOpponent, SoundChannel.CHAN_ANNOUNCER);
				else if (team == Teams.TEAM_BLUE)
					Syscalls.S_StartLocalSound(_sfxTakenYourTeam, SoundChannel.CHAN_ANNOUNCER);
				break;
			case GlobalTeamSound.GTS_REDTEAM_SCORED:
				Syscalls.S_StartLocalSound(_sfxRedScoredSound, SoundChannel.CHAN_ANNOUNCER);
				break;
			case GlobalTeamSound.GTS_BLUETEAM_SCORED:
				Syscalls.S_StartLocalSound(_sfxBlueScoredSound, SoundChannel.CHAN_ANNOUNCER);
				break;
			case GlobalTeamSound.GTS_REDTEAM_TOOK_LEAD:
				Syscalls.S_StartLocalSound(_sfxRedLeadsSound, SoundChannel.CHAN_ANNOUNCER);
				break;
			case GlobalTeamSound.GTS_BLUETEAM_TOOK_LEAD:
				Syscalls.S_StartLocalSound(_sfxBlueLeadsSound, SoundChannel.CHAN_ANNOUNCER);
				break;
			case GlobalTeamSound.GTS_TEAMS_ARE_TIED:
				Syscalls.S_StartLocalSound(_sfxTiedLeadSound, SoundChannel.CHAN_ANNOUNCER);
				break;
		}
	}

	private static void FireWeapon(ref CEntity cent)
	{
		ref var es = ref cent.CurrentState;
		int weapon = es.Weapon;
		if (weapon <= 0 || weapon >= Weapons.WP_NUM_WEAPONS) return;

		// Record muzzle flash time for flash model rendering
		cent.MuzzleFlashTime = _time;

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
	private const int PMF_FOLLOW = 4096;
	private const int PMF_SCOREBOARD = 8192;

	// Game type constants
	private const int GT_FFA = 0;
	private const int GT_TOURNAMENT = 1;
	private const int GT_TEAM = 3;

	private static void CalcViewValues(ref Q3RefDef refdef)
	{
		ref var ps = ref Prediction.PredictedPlayerState;

		// Update third-person cvars each frame
		string tp = Syscalls.CvarGetString("cg_thirdPerson");
		_thirdPerson = tp == "1";
		string tpr = Syscalls.CvarGetString("cg_thirdPersonRange");
		_thirdPersonRange = float.TryParse(tpr, out float tpRange) ? tpRange : 80.0f;
		string tpa = Syscalls.CvarGetString("cg_thirdPersonAngle");
		_thirdPersonAngle = float.TryParse(tpa, out float tpAngle) ? tpAngle : 0.0f;

		refdef.X = 0;
		refdef.Y = 0;
		refdef.Width = _screenWidth;
		refdef.Height = _screenHeight;

		// Calculate bob state from predicted player state
		_bobCycle = (ps.BobCycle & 128) >> 7;
		_bobFracSin = MathF.Abs(MathF.Sin((ps.BobCycle & 127) / 127.0f * MathF.PI));
		_xySpeed = MathF.Sqrt(ps.VelocityX * ps.VelocityX + ps.VelocityY * ps.VelocityY);

		// Base view origin from predicted state (viewheight added in offset functions)
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

		if (_thirdPerson)
		{
			// Third-person camera
			OffsetThirdPersonView(ref refdef, ref viewPitch, ref viewYaw, ref viewRoll);
		}
		else
		{
			// Apply first-person view offsets (bobbing, damage kick, duck, landing, step)
			OffsetFirstPersonView(ref refdef, ref viewPitch, ref viewYaw, ref viewRoll);
		}

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

	// ── Third-Person Camera (CG_OffsetThirdPersonView equivalent) ──
	private const float FOCUS_DISTANCE = 512.0f;

	private static void OffsetThirdPersonView(ref Q3RefDef refdef, ref float pitch, ref float yaw, ref float roll)
	{
		ref var ps = ref Prediction.PredictedPlayerState;

		// Add viewheight
		refdef.ViewOrgZ += ps.ViewHeight;

		float focusPitch = pitch;
		float focusYaw = yaw;

		// If dead, look at killer
		if (ps.Stats[Stats.STAT_HEALTH] <= 0)
		{
			focusYaw = ps.Stats[Stats.STAT_DEAD_YAW];
			yaw = ps.Stats[Stats.STAT_DEAD_YAW];
		}

		// Don't go too far overhead
		if (focusPitch > 45) focusPitch = 45;

		// Calculate focus point
		float focusRad = focusPitch * MathF.PI / 180.0f;
		float focusYawRad = focusYaw * MathF.PI / 180.0f;
		float fwdX = MathF.Cos(focusYawRad) * MathF.Cos(focusRad);
		float fwdY = MathF.Sin(focusYawRad) * MathF.Cos(focusRad);
		float fwdZ = -MathF.Sin(focusRad);

		float focusPtX = refdef.ViewOrgX + FOCUS_DISTANCE * fwdX;
		float focusPtY = refdef.ViewOrgY + FOCUS_DISTANCE * fwdY;
		float focusPtZ = refdef.ViewOrgZ + FOCUS_DISTANCE * fwdZ;

		// Start from current view position, raised slightly
		float viewX = refdef.ViewOrgX;
		float viewY = refdef.ViewOrgY;
		float viewZ = refdef.ViewOrgZ + 8;

		// Reduce pitch for less extreme angles
		pitch *= 0.5f;

		// Calculate camera offset using full angle set
		float pitchRad = pitch * MathF.PI / 180.0f;
		float yawRad = yaw * MathF.PI / 180.0f;

		// Forward and right vectors
		float cfX = MathF.Cos(yawRad) * MathF.Cos(pitchRad);
		float cfY = MathF.Sin(yawRad) * MathF.Cos(pitchRad);
		float cfZ = -MathF.Sin(pitchRad);
		float crX = MathF.Sin(yawRad);
		float crY = -MathF.Cos(yawRad);
		float crZ = 0;

		float forwardScale = MathF.Cos(_thirdPersonAngle * MathF.PI / 180.0f);
		float sideScale = MathF.Sin(_thirdPersonAngle * MathF.PI / 180.0f);

		viewX += -_thirdPersonRange * forwardScale * cfX + -_thirdPersonRange * sideScale * crX;
		viewY += -_thirdPersonRange * forwardScale * cfY + -_thirdPersonRange * sideScale * crY;
		viewZ += -_thirdPersonRange * forwardScale * cfZ;

		// Trace to prevent camera in walls
		const int CONTENTS_SOLID = 1;
		float* trStart = stackalloc float[3];
		float* trEnd = stackalloc float[3];
		float* trMins = stackalloc float[3];
		float* trMaxs = stackalloc float[3];
		trStart[0] = refdef.ViewOrgX; trStart[1] = refdef.ViewOrgY; trStart[2] = refdef.ViewOrgZ;
		trEnd[0] = viewX; trEnd[1] = viewY; trEnd[2] = viewZ;
		trMins[0] = -4; trMins[1] = -4; trMins[2] = -4;
		trMaxs[0] = 4; trMaxs[1] = 4; trMaxs[2] = 4;

		Q3Trace trace;
		Prediction.Trace(&trace, trStart, trMins, trMaxs, trEnd, ps.ClientNum, CONTENTS_SOLID);

		if (trace.Fraction != 1.0f)
		{
			viewX = trace.EndPosX;
			viewY = trace.EndPosY;
			viewZ = trace.EndPosZ;
			viewZ += (1.0f - trace.Fraction) * 32;

			// Second trace to handle tunnels/ceilings
			trEnd[0] = viewX; trEnd[1] = viewY; trEnd[2] = viewZ;
			Prediction.Trace(&trace, trStart, trMins, trMaxs, trEnd, ps.ClientNum, CONTENTS_SOLID);
			viewX = trace.EndPosX;
			viewY = trace.EndPosY;
			viewZ = trace.EndPosZ;
		}

		refdef.ViewOrgX = viewX;
		refdef.ViewOrgY = viewY;
		refdef.ViewOrgZ = viewZ;

		// Calculate pitch to look at focus point from camera
		float fpX = focusPtX - refdef.ViewOrgX;
		float fpY = focusPtY - refdef.ViewOrgY;
		float fpZ = focusPtZ - refdef.ViewOrgZ;
		float focusDist = MathF.Sqrt(fpX * fpX + fpY * fpY);
		if (focusDist < 1) focusDist = 1;

		pitch = -180.0f / MathF.PI * MathF.Atan2(fpZ, focusDist);
		yaw -= _thirdPersonAngle;
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
			s1.Number, _clientNum, _time, _shadowMarkShader);
	}

	private static void AddItem(ref CEntity cent)
	{
		ref var s1 = ref cent.CurrentState;
		if (s1.ModelIndex == 0) return;

		// Register item model on first encounter (bg_itemlist lookup)
		RegisterItemVisuals(s1.ModelIndex);

		int model = _itemModels[s1.ModelIndex < MAX_ITEMS ? s1.ModelIndex : 0];
		if (model == 0)
		{
			// Fallback to config string model if item model not found
			if (s1.ModelIndex < MAX_MODELS)
				model = _gameModels[s1.ModelIndex];
			if (model == 0) return;
		}

		Q3RefEntity rent = default;
		rent.ReType = Q3RefEntity.RT_MODEL;
		rent.HModel = model;

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
		if (weapon >= Weapons.WP_NUM_WEAPONS) weapon = 0;

		// Copy angles from entity state (Q3: VectorCopy(s1->angles, cent->lerpAngles))
		cent.LerpAnglesX = s1.AnglesX;
		cent.LerpAnglesY = s1.AnglesY;
		cent.LerpAnglesZ = s1.AnglesZ;

		// Plasma bolts are rendered as sprites, not models
		if (weapon == Weapons.WP_PLASMAGUN)
		{
			WeaponEffects.AddPlasma(cent.LerpOriginX, cent.LerpOriginY, cent.LerpOriginZ, _time);
			WeaponEffects.MissileTrail(s1.Number, weapon,
				s1.Pos.TrBaseX, s1.Pos.TrBaseY, s1.Pos.TrBaseZ,
				cent.LerpOriginX, cent.LerpOriginY, cent.LerpOriginZ, _time);
			return;
		}

		// Projectile trail + dynamic light + looping sound
		WeaponEffects.MissileTrail(s1.Number, weapon,
			s1.Pos.TrBaseX, s1.Pos.TrBaseY, s1.Pos.TrBaseZ,
			cent.LerpOriginX, cent.LerpOriginY, cent.LerpOriginZ, _time);

		// Get missile model — use pre-registered WeaponEffects model (not ModelIndex from entity state,
		// since server-side fire_rocket/fire_grenade don't set s.modelindex)
		int missileModel = WeaponEffects.GetMissileModel(weapon);
		if (missileModel == 0 && s1.ModelIndex != 0)
			missileModel = _gameModels[s1.ModelIndex];
		if (missileModel == 0) return;

		Q3RefEntity rent = default;
		rent.ReType = Q3RefEntity.RT_MODEL;
		rent.HModel = missileModel;

		// Flicker between two skins
		rent.SkinNum = _frameCount & 1;
		rent.RenderFx = Q3RefEntity.RF_NOSHADOW;

		// Position
		rent.OriginX = cent.LerpOriginX;
		rent.OriginY = cent.LerpOriginY;
		rent.OriginZ = cent.LerpOriginZ;
		rent.OldOriginX = cent.LerpOriginX;
		rent.OldOriginY = cent.LerpOriginY;
		rent.OldOriginZ = cent.LerpOriginZ;

		// Axis from direction of travel (VectorNormalize2(s1->pos.trDelta, axis[0]))
		float dx = s1.Pos.TrDeltaX;
		float dy = s1.Pos.TrDeltaY;
		float dz = s1.Pos.TrDeltaZ;
		float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
		if (len > 0)
		{
			float inv = 1.0f / len;
			rent.Axis0X = dx * inv;
			rent.Axis0Y = dy * inv;
			rent.Axis0Z = dz * inv;
		}
		else
		{
			rent.Axis0X = 0; rent.Axis0Y = 0; rent.Axis0Z = 1;
		}

		// Spin as it moves (RotateAroundDirection)
		if (s1.Pos.TrType != TrajectoryType.TR_STATIONARY)
			RotateAroundDirection(ref rent, _time / 4.0f);
		else
			RotateAroundDirection(ref rent, (float)s1.Time);

		SetEntityColors(ref rent, 255, 255, 255, 255);
		Syscalls.R_AddRefEntityToScene(&rent);
	}

	/// <summary>Build axis[1] and axis[2] perpendicular to axis[0], then rotate around axis[0].</summary>
	private static void RotateAroundDirection(ref Q3RefEntity rent, float degrees)
	{
		float a0x = rent.Axis0X, a0y = rent.Axis0Y, a0z = rent.Axis0Z;

		// Build a perpendicular vector
		float px, py, pz;
		if (MathF.Abs(a0x) < 0.99f)
		{
			// Cross with (1,0,0)
			px = 0; py = a0z; pz = -a0y;
		}
		else
		{
			// Cross with (0,1,0)
			px = -a0z; py = 0; pz = a0x;
		}
		float plen = MathF.Sqrt(px * px + py * py + pz * pz);
		if (plen > 0) { float inv = 1.0f / plen; px *= inv; py *= inv; pz *= inv; }

		// Rotate perpendicular vector around axis[0]
		float rad = degrees * (MathF.PI / 180.0f);
		float cr = MathF.Cos(rad), sr = MathF.Sin(rad);

		// axis[1] = perp * cos + (axis[0] x perp) * sin
		// axis[0] x perp
		float cx = a0y * pz - a0z * py;
		float cy = a0z * px - a0x * pz;
		float cz = a0x * py - a0y * px;

		rent.Axis1X = px * cr + cx * sr;
		rent.Axis1Y = py * cr + cy * sr;
		rent.Axis1Z = pz * cr + cz * sr;

		// axis[2] = axis[0] x axis[1]
		rent.Axis2X = a0y * rent.Axis1Z - a0z * rent.Axis1Y;
		rent.Axis2Y = a0z * rent.Axis1X - a0x * rent.Axis1Z;
		rent.Axis2Z = a0x * rent.Axis1Y - a0y * rent.Axis1X;
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
		bool isSpectator = ps.Persistant[Persistant.PERS_TEAM] == Teams.TEAM_SPECTATOR;

		if (isSpectator)
		{
			DrawSpectator();
			DrawCrosshair();
			DrawCrosshairNames();
		}
		else
		{
			// Don't draw status if dead or scoreboard is showing
			if (!Scoreboard.IsShowing && ps.Stats[Stats.STAT_HEALTH] > 0)
			{
				DrawStatusBar();
				DrawAmmoWarning();
				DrawCrosshair();
				DrawCrosshairNames();
				DrawWeaponSelect();
				DrawHoldableItem();
				DrawReward();
			}
		}

		// Vote display (visible in all modes)
		DrawVote();

		// Center print message (objectives, notifications)
		DrawCenterString();

		// Chat messages
		DrawChat();

		// Item pickup notification
		DrawPickupItem();

		// Upper right: FPS + Timer + Powerups + Attacker
		float y = 0;
		y = DrawFPS(y);
		y = DrawTimer(y);
		y = DrawAttacker(y);
		if (!isSpectator) y = DrawPowerups(y);

		// .NET cgame indicator (top right)
		DrawString(SCREEN_WIDTH - 82, (int)y + 2, ".NET CG", 1.0f, 0.0f, 1.0f, 0.6f);

		// Lagometer
		DrawLagometer();

		// Follow mode or warmup
		if (!DrawFollow())
			DrawWarmup();

		// Scoreboard overlay
		if (Scoreboard.IsShowing || ps.PmType >= 4) // PM_DEAD=4, PM_INTERMISSION=5
			Scoreboard.Draw(_time, _clientNum, _gametype);

		Syscalls.R_SetColor(null);
	}

	/// <summary>Show "SPECTATOR" text and instruction for spectators.</summary>
	private static void DrawSpectator()
	{
		DrawBigString(320 - 9 * 8, 440, "SPECTATOR");
		if (_gametype == GT_TOURNAMENT)
			DrawBigString(320 - 15 * 8, 460, "waiting to play");
		else if (_gametype >= GT_TEAM)
			DrawBigString(320 - 25 * 8, 460, "press ESC and use the JOIN menu to play");
	}

	/// <summary>Show "following" + player name when in follow mode.</summary>
	private static bool DrawFollow()
	{
		if (_snap == null) return false;
		if ((_snap->Ps.PmFlags & PMF_FOLLOW) == 0) return false;

		DrawBigString(320 - 9 * 8, 24, "following");

		string name = Player.GetClientName(_snap->Ps.ClientNum);
		if (!string.IsNullOrEmpty(name))
		{
			int nameLen = name.Length;
			float x = 0.5f * (SCREEN_WIDTH - 32 * nameLen); // GIANT_WIDTH=32
			DrawStringScaled((int)x, 40, name, 1.0f, 1.0f, 1.0f, 1.0f, 32, 48); // GIANT size
		}

		return true;
	}

	/// <summary>Draw text using BIGCHAR_WIDTH (16x16) characters.</summary>
	private static void DrawBigString(int x, int y, string text)
	{
		DrawStringScaled(x, y, text, 1.0f, 1.0f, 1.0f, 1.0f, BIGCHAR_WIDTH, BIGCHAR_HEIGHT);
	}

	/// <summary>Draw text at given position with custom character size.</summary>
	private static void DrawStringScaled(int x, int y, string text, float r, float g, float b, float a, int charW, int charH)
	{
		float* color = stackalloc float[4];
		color[0] = r; color[1] = g; color[2] = b; color[3] = a;
		Syscalls.R_SetColor(color);

		float fx = x;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c == ' ') { fx += charW; continue; }

			DrawChar(fx, y, charW, charH, c);
			fx += charW;
		}

		Syscalls.R_SetColor(null);
	}

	// ── Crosshair Target Names (CG_DrawCrosshairNames equivalent) ──

	private static void ScanForCrosshairEntity()
	{
		if (_snap == null) return;

		float* start = stackalloc float[3];
		float* end = stackalloc float[3];
		float* mins = stackalloc float[3];
		start[0] = _viewOrgX; start[1] = _viewOrgY; start[2] = _viewOrgZ;
		end[0] = _viewOrgX + _viewFwdX * 131072;
		end[1] = _viewOrgY + _viewFwdY * 131072;
		end[2] = _viewOrgZ + _viewFwdZ * 131072;
		mins[0] = 0; mins[1] = 0; mins[2] = 0;

		Q3Trace trace;
		const int CONTENTS_SOLID = 1;
		const int CONTENTS_BODY = 0x2000000;
		Prediction.Trace(&trace, start, mins, mins, end, _snap->Ps.ClientNum, CONTENTS_SOLID | CONTENTS_BODY);

		if (trace.EntityNum >= EntityNum.MAX_CLIENTS) return;

		// Skip invisible players
		if (trace.EntityNum < MAX_GENTITIES &&
			(_entities[trace.EntityNum].CurrentState.Powerups & (1 << PW_INVIS)) != 0)
			return;

		_crosshairClientNum = trace.EntityNum;
		_crosshairClientTime = _time;
	}

	private static void DrawCrosshairNames()
	{
		ScanForCrosshairEntity();

		if (_crosshairClientNum < 0 || _crosshairClientNum >= EntityNum.MAX_CLIENTS)
			return;

		// Fade after 1 second
		int elapsed = _time - _crosshairClientTime;
		if (elapsed > 1000)
		{
			_crosshairClientNum = -1;
			return;
		}

		float alpha = 1.0f;
		if (elapsed > 600)
			alpha = 1.0f - (elapsed - 600) / 400.0f;

		string name = Player.GetClientName(_crosshairClientNum);
		if (string.IsNullOrEmpty(name)) return;

		int w = name.Length * BIGCHAR_WIDTH;
		DrawString(320 - w / 2, 170, name, 1.0f, 1.0f, 1.0f, alpha * 0.5f);
	}

	// ── Powerup Timers (CG_DrawPowerups equivalent) ──

	private static float DrawPowerups(float y)
	{
		if (_snap == null) return y;
		ref var ps = ref _snap->Ps;
		if (ps.Stats[0] <= 0) return y; // STAT_HEALTH

		// Sort powerups by time remaining
		Span<int> sorted = stackalloc int[MAX_POWERUPS];
		Span<int> sortedTime = stackalloc int[MAX_POWERUPS];
		int active = 0;

		for (int i = 0; i < MAX_POWERUPS; i++)
		{
			if (ps.PowerupTimers[i] == 0) continue;
			if (ps.PowerupTimers[i] == int.MaxValue) continue; // CTF flags (unlimited)

			int t = ps.PowerupTimers[i] - _time;
			if (t <= 0) continue;

			// Insertion sort by time remaining (ascending)
			int j;
			for (j = 0; j < active; j++)
			{
				if (sortedTime[j] >= t)
				{
					for (int k = active - 1; k >= j; k--)
					{
						sorted[k + 1] = sorted[k];
						sortedTime[k + 1] = sortedTime[k];
					}
					break;
				}
			}
			sorted[j] = i;
			sortedTime[j] = t;
			active++;
		}

		// Draw icons and timers
		int x = SCREEN_WIDTH - ICON_SIZE - CHAR_WIDTH * 2;
		float* color = stackalloc float[4];
		float* mod = stackalloc float[4];
		for (int i = 0; i < active; i++)
		{
			int pwIdx = sorted[i];
			if (pwIdx >= MAX_POWERUPS || _powerupIcons[pwIdx] == 0) continue;

			y -= ICON_SIZE;

			// Draw remaining time
			int secs = sortedTime[i] / 1000;
			color[0] = 1; color[1] = 0.2f; color[2] = 0.2f; color[3] = 1;
			Syscalls.R_SetColor(color);
			DrawField(x, (int)y, 2, secs);

			// Blinking effect when about to expire
			int expireTime = ps.PowerupTimers[pwIdx];
			if (expireTime - _time >= POWERUP_BLINKS * POWERUP_BLINK_TIME)
			{
				Syscalls.R_SetColor(null);
			}
			else
			{
				float f = (float)(expireTime - _time) / POWERUP_BLINK_TIME;
				f -= (int)f;
				mod[0] = f; mod[1] = f; mod[2] = f; mod[3] = f;
				Syscalls.R_SetColor(mod);
			}

			// Draw powerup icon
			DrawPic(SCREEN_WIDTH - ICON_SIZE, y, ICON_SIZE, ICON_SIZE, _powerupIcons[pwIdx]);
			Syscalls.R_SetColor(null);
		}

		return y;
	}

	// ── Attacker Display (CG_DrawAttacker equivalent) ──

	private static float DrawAttacker(float y)
	{
		if (_snap == null) return y;
		ref var ps = ref Prediction.PredictedPlayerState;
		if (ps.Stats[0] <= 0) return y; // STAT_HEALTH

		int clientNum = ps.Persistant[Persistant.PERS_ATTACKER];
		if (clientNum < 0 || clientNum >= EntityNum.MAX_CLIENTS || clientNum == _snap->Ps.ClientNum)
			return y;

		// Track attacker time — update when attacker changes
		if (_attackerTime == 0)
			_attackerTime = _time;

		int elapsed = _time - _attackerTime;
		if (elapsed > ATTACKER_HEAD_TIME)
		{
			_attackerTime = 0;
			return y;
		}

		string name = Player.GetClientName(clientNum);
		if (string.IsNullOrEmpty(name))
		{
			_attackerTime = 0;
			return y;
		}

		float alpha = 1.0f;
		if (elapsed > ATTACKER_HEAD_TIME - 2000)
			alpha = (ATTACKER_HEAD_TIME - elapsed) / 2000.0f;

		// Draw attacker name (upper right area)
		int w = name.Length * BIGCHAR_WIDTH;
		DrawString(SCREEN_WIDTH - w, (int)y, name, 1.0f, 0.3f, 0.3f, alpha * 0.5f);
		y += BIGCHAR_HEIGHT + 2;

		return y;
	}

	// ── Warmup Countdown (CG_DrawWarmup equivalent) ──

	private static void DrawWarmup()
	{
		if (_warmup == 0) return;

		if (_warmup < 0)
		{
			string waiting = "Waiting for players";
			int ww = waiting.Length * BIGCHAR_WIDTH;
			DrawString(320 - ww / 2, 24, waiting, 1.0f, 1.0f, 1.0f, 1.0f);
			_warmupCount = 0;
			return;
		}

		// Game type label
		string gameLabel = _gametype switch
		{
			0 => "Free For All",
			3 => "Team Deathmatch",
			4 => "Capture the Flag",
			1 => "Tournament",
			_ => ""
		};
		if (!string.IsNullOrEmpty(gameLabel))
		{
			int gw = gameLabel.Length * BIGCHAR_WIDTH;
			DrawString(320 - gw / 2, 25, gameLabel, 1.0f, 1.0f, 1.0f, 1.0f);
		}

		// Countdown display
		int sec = (_warmup - _time) / 1000;
		if (sec < 0)
		{
			_warmup = 0;
			sec = 0;
		}
		string countdown = $"Starts in: {sec + 1}";
		int cw = countdown.Length * BIGCHAR_WIDTH;
		DrawString(320 - cw / 2, 70, countdown, 1.0f, 1.0f, 1.0f, 1.0f);
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

	// ── Reward Medals (CG_DrawReward equivalent) ──

	private static void DrawReward()
	{
		if (_rewardTime == 0 && _rewardStack <= 0) return;

		// Calculate fade
		int elapsed = _time - _rewardTime;
		float alpha;
		if (elapsed < 0 || elapsed >= REWARD_TIME)
		{
			// Expired — pop next from stack
			if (_rewardStack > 0)
			{
				for (int i = 0; i < _rewardStack; i++)
				{
					_rewardSound[i] = _rewardSound[i + 1];
					_rewardShader[i] = _rewardShader[i + 1];
					_rewardCount[i] = _rewardCount[i + 1];
				}
				_rewardTime = _time;
				_rewardStack--;
				elapsed = 0;
				alpha = 1.0f;
				Syscalls.S_StartLocalSound(_rewardSound[0], SoundChannel.CHAN_ANNOUNCER);
			}
			else
			{
				_rewardTime = 0;
				return;
			}
		}
		else
		{
			alpha = 1.0f - (float)elapsed / REWARD_TIME;
		}
		if (alpha <= 0) return;

		float* color = stackalloc float[4];
		color[0] = 1; color[1] = 1; color[2] = 1; color[3] = alpha;
		Syscalls.R_SetColor(color);

		int count = _rewardCount[0];
		float y = 56;

		if (count >= 10)
		{
			// Single icon + count text
			float rx = 320 - ICON_SIZE / 2;
			DrawPic(rx, y, ICON_SIZE - 4, ICON_SIZE - 4, _rewardShader[0]);
			string countStr = count.ToString();
			float tx = (SCREEN_WIDTH - 8 * countStr.Length) / 2;
			DrawString((int)tx, (int)(y + ICON_SIZE), countStr, 1.0f, 1.0f, 1.0f, alpha);
		}
		else
		{
			// Repeated icons
			float rx = 320 - count * ICON_SIZE / 2;
			for (int i = 0; i < count; i++)
			{
				DrawPic(rx, y, ICON_SIZE - 4, ICON_SIZE - 4, _rewardShader[0]);
				rx += ICON_SIZE;
			}
		}

		Syscalls.R_SetColor(null);
	}

	// ── Lagometer (CG_DrawLagometer equivalent) ──

	private static void AddLagometerFrameInfo()
	{
		int offset = _time - _latestSnapshotTime;
		_lagFrameSamples[_lagFrameCount & (LAG_SAMPLES - 1)] = offset;
		_lagFrameCount++;
	}

	private static void AddLagometerSnapshotInfo(Q3Snapshot* snap)
	{
		if (snap == null)
		{
			_lagSnapshotSamples[_lagSnapshotCount & (LAG_SAMPLES - 1)] = -1;
			_lagSnapshotCount++;
			return;
		}
		_lagSnapshotSamples[_lagSnapshotCount & (LAG_SAMPLES - 1)] = snap->Ping;
		_lagSnapshotFlags[_lagSnapshotCount & (LAG_SAMPLES - 1)] = snap->SnapFlags;
		_lagSnapshotCount++;
	}

	private static void DrawLagometer()
	{
		// Draw disconnect indicator regardless
		DrawDisconnect();

		// Skip lagometer on local server
		string sv = Syscalls.CvarGetString("sv_running");
		if (sv == "1") return;

		// Check cg_lagometer cvar
		string lagCvar = Syscalls.CvarGetString("cg_lagometer");
		if (lagCvar == "0") return;

		float x = 640 - 48;
		float y = 480 - 48;

		Syscalls.R_SetColor(null);
		DrawPic(x, y, 48, 48, _lagometerShader);

		// Get adjusted coordinates for pixel drawing
		float ax = x, ay = y, aw = 48, ah = 48;
		AdjustFrom640(ref ax, ref ay, ref aw, ref ah);

		int color = -1;
		float range = ah / 3;
		float mid = ay + range;
		float vscale = range / MAX_LAGOMETER_RANGE;

		// Frame interpolate/extrapolate graph (top half)
		for (int a = 0; a < (int)aw; a++)
		{
			int idx = (_lagFrameCount - 1 - a) & (LAG_SAMPLES - 1);
			float v = _lagFrameSamples[idx] * vscale;
			if (v > 0)
			{
				if (color != 1) { color = 1; SetColorYellow(); }
				if (v > range) v = range;
				Syscalls.R_DrawStretchPic(ax + aw - a, mid - v, 1, v, 0, 0, 0, 0, _whiteShader);
			}
			else if (v < 0)
			{
				if (color != 2) { color = 2; SetColorBlue(); }
				v = -v;
				if (v > range) v = range;
				Syscalls.R_DrawStretchPic(ax + aw - a, mid, 1, v, 0, 0, 0, 0, _whiteShader);
			}
		}

		// Snapshot latency/drop graph (bottom half)
		range = ah / 2;
		vscale = range / MAX_LAGOMETER_PING;

		for (int a = 0; a < (int)aw; a++)
		{
			int idx = (_lagSnapshotCount - 1 - a) & (LAG_SAMPLES - 1);
			float v = _lagSnapshotSamples[idx];
			if (v > 0)
			{
				if ((_lagSnapshotFlags[idx] & SnapFlags.SNAPFLAG_RATE_DELAYED) != 0)
				{
					if (color != 5) { color = 5; SetColorYellow(); }
				}
				else
				{
					if (color != 3) { color = 3; SetColorGreen(); }
				}
				v *= vscale;
				if (v > range) v = range;
				Syscalls.R_DrawStretchPic(ax + aw - a, ay + ah - v, 1, v, 0, 0, 0, 0, _whiteShader);
			}
			else if (v < 0)
			{
				if (color != 4) { color = 4; SetColorRed(); }
				Syscalls.R_DrawStretchPic(ax + aw - a, ay + ah - range, 1, range, 0, 0, 0, 0, _whiteShader);
			}
		}

		Syscalls.R_SetColor(null);
	}

	private static void DrawDisconnect()
	{
		// Show disconnected icon if no snapshots received for > 1 second
		if (_snap == null) return;
		int elapsed = _time - _snap->ServerTime;
		if (elapsed < 1000) return;

		// Flash the icon
		float x = 640 - 48;
		float y = 480 - 48;
		DrawPic(x, y, 48, 48, _disconnectIcon);
	}

	private static void SetColorYellow()
	{
		float* c = stackalloc float[4]; c[0] = 1; c[1] = 1; c[2] = 0; c[3] = 1;
		Syscalls.R_SetColor(c);
	}
	private static void SetColorBlue()
	{
		float* c = stackalloc float[4]; c[0] = 0; c[1] = 0; c[2] = 1; c[3] = 1;
		Syscalls.R_SetColor(c);
	}
	private static void SetColorGreen()
	{
		float* c = stackalloc float[4]; c[0] = 0; c[1] = 1; c[2] = 0; c[3] = 1;
		Syscalls.R_SetColor(c);
	}
	private static void SetColorRed()
	{
		float* c = stackalloc float[4]; c[0] = 1; c[1] = 0; c[2] = 0; c[3] = 1;
		Syscalls.R_SetColor(c);
	}

	// ── Reward / Sound Detection (CG_CheckLocalSounds equivalent) ──

	private static void CheckLocalSounds(Q3PlayerState* ps, ref Q3PlayerState ops)
	{
		// Skip on first snapshot (ops hasn't been populated yet)
		if (ops.CommandTime == 0)
			return;

		// Don't play sounds if the player just changed teams
		if (ps->Persistant[Persistant.PERS_TEAM] != ops.Persistant[Persistant.PERS_TEAM])
			return;

		// Hit feedback
		if (ps->Persistant[Persistant.PERS_HITS] > ops.Persistant[Persistant.PERS_HITS])
			Syscalls.S_StartLocalSound(_hitSound, SoundChannel.CHAN_LOCAL_SOUND);
		else if (ps->Persistant[Persistant.PERS_HITS] < ops.Persistant[Persistant.PERS_HITS])
			Syscalls.S_StartLocalSound(_hitTeamSound, SoundChannel.CHAN_LOCAL_SOUND);

		// Reward medals
		if (ps->Persistant[Persistant.PERS_CAPTURES] != ops.Persistant[Persistant.PERS_CAPTURES])
			PushReward(_captureAwardSound, _medalCapture, ps->Persistant[Persistant.PERS_CAPTURES]);

		if (ps->Persistant[Persistant.PERS_IMPRESSIVE_COUNT] != ops.Persistant[Persistant.PERS_IMPRESSIVE_COUNT])
			PushReward(_impressiveSound, _medalImpressive, ps->Persistant[Persistant.PERS_IMPRESSIVE_COUNT]);

		if (ps->Persistant[Persistant.PERS_EXCELLENT_COUNT] != ops.Persistant[Persistant.PERS_EXCELLENT_COUNT])
			PushReward(_excellentSound, _medalExcellent, ps->Persistant[Persistant.PERS_EXCELLENT_COUNT]);

		if (ps->Persistant[Persistant.PERS_GAUNTLET_FRAG_COUNT] != ops.Persistant[Persistant.PERS_GAUNTLET_FRAG_COUNT])
			PushReward(_humiliationSound, _medalGauntlet, ps->Persistant[Persistant.PERS_GAUNTLET_FRAG_COUNT]);

		if (ps->Persistant[Persistant.PERS_DEFEND_COUNT] != ops.Persistant[Persistant.PERS_DEFEND_COUNT])
			PushReward(_defendSound, _medalDefend, ps->Persistant[Persistant.PERS_DEFEND_COUNT]);

		if (ps->Persistant[Persistant.PERS_ASSIST_COUNT] != ops.Persistant[Persistant.PERS_ASSIST_COUNT])
			PushReward(_assistSound, _medalAssist, ps->Persistant[Persistant.PERS_ASSIST_COUNT]);

		// Player events (denied, gauntlet reward)
		if (ps->Persistant[Persistant.PERS_PLAYEREVENTS] != ops.Persistant[Persistant.PERS_PLAYEREVENTS])
		{
			const int PLAYEREVENT_DENIEDREWARD = 1;
			const int PLAYEREVENT_GAUNTLETREWARD = 2;
			int newEvents = ps->Persistant[Persistant.PERS_PLAYEREVENTS];
			int oldEvents = ops.Persistant[Persistant.PERS_PLAYEREVENTS];

			if ((newEvents & PLAYEREVENT_DENIEDREWARD) != (oldEvents & PLAYEREVENT_DENIEDREWARD))
				Syscalls.S_StartLocalSound(_deniedSound, SoundChannel.CHAN_ANNOUNCER);
			else if ((newEvents & PLAYEREVENT_GAUNTLETREWARD) != (oldEvents & PLAYEREVENT_GAUNTLETREWARD))
				Syscalls.S_StartLocalSound(_humiliationSound, SoundChannel.CHAN_ANNOUNCER);
		}

		// Frag limit warnings
		CheckFragWarnings(ps);
	}

	/// <summary>Check score vs fraglimit and play "X frags remaining" announcements.</summary>
	private static void CheckFragWarnings(Q3PlayerState* ps)
	{
		// Read fraglimit from serverinfo
		string serverInfo = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_SERVERINFO);
		int fragLimit = InfoInt(serverInfo, "fraglimit");
		if (fragLimit <= 0) return;

		int score = ps->Persistant[Persistant.PERS_SCORE];

		if (score == fragLimit - 1 && (_fraglimitWarnings & 1) == 0)
		{
			_fraglimitWarnings |= 1;
			Syscalls.S_StartLocalSound(_oneFragSound, SoundChannel.CHAN_ANNOUNCER);
		}
		else if (score == fragLimit - 2 && (_fraglimitWarnings & 2) == 0)
		{
			_fraglimitWarnings |= 2;
			Syscalls.S_StartLocalSound(_twoFragSound, SoundChannel.CHAN_ANNOUNCER);
		}
		else if (score == fragLimit - 3 && (_fraglimitWarnings & 4) == 0)
		{
			_fraglimitWarnings |= 4;
			Syscalls.S_StartLocalSound(_threeFragSound, SoundChannel.CHAN_ANNOUNCER);
		}
	}

	private static void PushReward(int sfx, int shader, int count)
	{
		if (_rewardStack < MAX_REWARDSTACK - 1)
		{
			_rewardStack++;
			_rewardSound[_rewardStack] = sfx;
			_rewardShader[_rewardStack] = shader;
			_rewardCount[_rewardStack] = count;
		}

		// If no reward is currently displaying, shift down and start immediately
		if (_rewardTime == 0 && _rewardStack > 0)
		{
			for (int i = 0; i < _rewardStack; i++)
			{
				_rewardSound[i] = _rewardSound[i + 1];
				_rewardShader[i] = _rewardShader[i + 1];
				_rewardCount[i] = _rewardCount[i + 1];
			}
			_rewardStack--;
			_rewardTime = _time;
			Syscalls.S_StartLocalSound(_rewardSound[0], SoundChannel.CHAN_ANNOUNCER);
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
		else if (health > 0)
		{
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

	/// <summary>Flash "LOW AMMO" warning when ammo is critically low.</summary>
	private static void DrawAmmoWarning()
	{
		if (_snap == null) return;
		ref var ps = ref _snap->Ps;

		// Determine ammo warning level
		_lowAmmoWarning = 0;
		int weapon = ps.Weapon;
		if (weapon > 0 && weapon < Weapons.WP_NUM_WEAPONS && weapon != Weapons.WP_GAUNTLET)
		{
			int ammo = ps.Ammo[weapon];
			if (ammo <= 0)
				_lowAmmoWarning = 2; // empty
			else if (ammo <= 5)
				_lowAmmoWarning = 1; // low
		}

		if (_lowAmmoWarning == 0) return;

		// Flash the warning text
		float* color = stackalloc float[4];
		color[0] = 1; color[1] = 0; color[2] = 0;
		color[3] = ((_time >> 8) & 1) != 0 ? 1.0f : 0.5f; // blink

		string msg = _lowAmmoWarning == 2 ? "OUT OF AMMO" : "LOW AMMO";
		int textWidth = msg.Length * BIGCHAR_WIDTH;
		int x = (SCREEN_WIDTH - textWidth) / 2;

		Syscalls.R_SetColor(color);
		DrawStringScaled(x, 64, msg, color[0], color[1], color[2], color[3], BIGCHAR_WIDTH, BIGCHAR_HEIGHT);
		Syscalls.R_SetColor(null);
	}

	/// <summary>Display current vote in progress (CG_DrawVote).</summary>
	private static void DrawVote()
	{
		if (_voteTime == 0) return;

		// Vote times out after 30 seconds
		int sec = 30 - (_time - _voteTime) / 1000;
		if (sec < 0) { _voteTime = 0; return; }

		string voteText = $"VOTE({sec}): {_voteString}  yes:{_voteYes} no:{_voteNo}";
		DrawString(2, 58, voteText, 1.0f, 1.0f, 0.0f, 1.0f);
	}

	/// <summary>Display holdable item icon on the HUD.</summary>
	private static void DrawHoldableItem()
	{
		if (_snap == null) return;
		ref var ps = ref _snap->Ps;

		int itemIndex = ps.Stats[Stats.STAT_HOLDABLE_ITEM];
		if (itemIndex <= 0) return;

		// Use the item's icon via registered model
		int iconShader = Syscalls.R_RegisterShader(itemIndex == 27 ? "icons/teleporter" : "icons/medkit");
		if (iconShader == 0) return;

		DrawPic(SCREEN_WIDTH / 2 - ICON_SIZE / 2, SCREEN_HEIGHT - ICON_SIZE - 48, ICON_SIZE, ICON_SIZE, iconShader);
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

		// Parse warmup countdown
		string warmupStr = Q3GameState.GetConfigString(_gameStateRaw, Q3GameState.CS_WARMUP);
		_warmup = string.IsNullOrEmpty(warmupStr) ? 0 : int.TryParse(warmupStr, out int w) ? w : 0;
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
