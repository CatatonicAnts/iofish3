using System.Runtime.InteropServices;

namespace CGameDotNet;

/// <summary>
/// Binary-compatible C structs for engine interop.
/// All sizes and layouts must match the C definitions exactly.
/// </summary>

// trType_t trajectory types
public static class TrajectoryType
{
    public const int TR_STATIONARY = 0;
    public const int TR_INTERPOLATE = 1;
    public const int TR_LINEAR = 2;
    public const int TR_LINEAR_STOP = 3;
    public const int TR_SINE = 4;
    public const int TR_GRAVITY = 5;
}

// trajectory_t — 36 bytes
[StructLayout(LayoutKind.Sequential)]
public struct Q3Trajectory
{
    public int TrType;
    public int TrTime;
    public int TrDuration;
    public float TrBaseX, TrBaseY, TrBaseZ;
    public float TrDeltaX, TrDeltaY, TrDeltaZ;
}

// entityState_t — 208 bytes
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Q3EntityState
{
    public int Number;
    public int EType;
    public int EFlags;
    public Q3Trajectory Pos;
    public Q3Trajectory APos;
    public int Time;
    public int Time2;
    public float OriginX, OriginY, OriginZ;
    public float Origin2X, Origin2Y, Origin2Z;
    public float AnglesX, AnglesY, AnglesZ;
    public float Angles2X, Angles2Y, Angles2Z;
    public int OtherEntityNum;
    public int OtherEntityNum2;
    public int GroundEntityNum;
    public int ConstantLight;
    public int LoopSound;
    public int ModelIndex;
    public int ModelIndex2;
    public int ClientNum;
    public int Frame;
    public int Solid;
    public int Event;
    public int EventParm;
    public int Powerups;
    public int Weapon;
    public int LegsAnim;
    public int TorsoAnim;
    public int Generic1;
}

// playerState_t — 468 bytes
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Q3PlayerState
{
    public int CommandTime;
    public int PmType;
    public int BobCycle;
    public int PmFlags;
    public int PmTime;

    public float OriginX, OriginY, OriginZ;
    public float VelocityX, VelocityY, VelocityZ;
    public int WeaponTime;
    public int Gravity;
    public int Speed;
    public fixed int DeltaAngles[3];

    public int GroundEntityNum;

    public int LegsTimer;
    public int LegsAnim;
    public int TorsoTimer;
    public int TorsoAnim;

    public int MovementDir;

    public float GrapplePointX, GrapplePointY, GrapplePointZ;

    public int EFlags;
    public int EventSequence;
    public fixed int Events[2];
    public fixed int EventParms[2];

    public int ExternalEvent;
    public int ExternalEventParm;
    public int ExternalEventTime;

    public int ClientNum;
    public int Weapon;
    public int WeaponState;

    public float ViewAnglesX, ViewAnglesY, ViewAnglesZ;
    public int ViewHeight;

    public int DamageEvent;
    public int DamageYaw;
    public int DamagePitch;
    public int DamageCount;

    public fixed int Stats[MAX_STATS];
    public fixed int Persistant[MAX_PERSISTANT];
    public fixed int PowerupTimers[MAX_POWERUPS];
    public fixed int Ammo[MAX_WEAPONS];

    public int Generic1;
    public int LoopSound;
    public int JumppadEnt;

    public int Ping;
    public int PmoveFramecount;
    public int JumppadFrame;
    public int EntityEventSequence;

    // Constants
    public const int MAX_STATS = 16;
    public const int MAX_PERSISTANT = 16;
    public const int MAX_POWERUPS = 16;
    public const int MAX_WEAPONS = 16;
}

// snapshot_t — ~53772 bytes
// Note: The entities[256] array can't be represented as a fixed array of structs in C#.
// We use a single _entity0 placeholder and read entities via pointer arithmetic.
// NumServerCommands and ServerCommandSequence come AFTER the full 256-element array
// in the C layout, so we access them via helper methods, not struct fields.
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Q3Snapshot
{
    public int SnapFlags;
    public int Ping;
    public int ServerTime;

    public fixed byte Areamask[MAX_MAP_AREA_BYTES];

    public Q3PlayerState Ps;

    public int NumEntities;
    // First entity — remaining 255 follow in memory (buffer must be full size)
    private Q3EntityState _entity0;

    public const int MAX_MAP_AREA_BYTES = 32;
    public const int MAX_ENTITIES_IN_SNAPSHOT = 256;

    // Offset to NumServerCommands (after all 256 entities)
    private static readonly int ServerCmdOffset =
        4 + 4 + 4 + MAX_MAP_AREA_BYTES + sizeof(Q3PlayerState) + 4 + MAX_ENTITIES_IN_SNAPSHOT * sizeof(Q3EntityState);

    public readonly unsafe ref Q3EntityState GetEntity(int index)
    {
        fixed (Q3EntityState* p = &_entity0)
            return ref p[index];
    }

    /// <summary>Read ServerCommandSequence from the correct C layout offset.</summary>
    public readonly int GetServerCommandSequence()
    {
        fixed (int* p = &SnapFlags)
            return *(int*)((byte*)p + ServerCmdOffset + 4);
    }
}

// refdef_t — ~368 bytes
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Q3RefDef
{
    public int X, Y, Width, Height;
    public float FovX, FovY;

    // vieworg: vec3_t
    public float ViewOrgX, ViewOrgY, ViewOrgZ;

    // viewaxis: vec3_t[3] — 3 rows of 3 floats = 9 floats
    public float Axis0X, Axis0Y, Axis0Z; // forward
    public float Axis1X, Axis1Y, Axis1Z; // right  (actually left in Q3)
    public float Axis2X, Axis2Y, Axis2Z; // up

    public int Time;
    public int RdFlags;

    public fixed byte Areamask[MAX_MAP_AREA_BYTES];

    // text[MAX_RENDER_STRINGS][MAX_RENDER_STRING_LENGTH]
    public fixed byte Text[MAX_RENDER_STRINGS * MAX_RENDER_STRING_LENGTH];

    public const int MAX_MAP_AREA_BYTES = 32;
    public const int MAX_RENDER_STRINGS = 8;
    public const int MAX_RENDER_STRING_LENGTH = 32;
}

// refEntity_t — 152 bytes
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Q3RefEntity
{
    public int ReType;           // refEntityType_t
    public int RenderFx;

    public int HModel;

    public float LightingOriginX, LightingOriginY, LightingOriginZ;
    public float ShadowPlane;

    // axis[3][3]
    public float Axis0X, Axis0Y, Axis0Z;
    public float Axis1X, Axis1Y, Axis1Z;
    public float Axis2X, Axis2Y, Axis2Z;

    public int NonNormalizedAxes;

    public float OriginX, OriginY, OriginZ;
    public int FrameNum;

    public float OldOriginX, OldOriginY, OldOriginZ;
    public int OldFrame;
    public float Backlerp;

    public int SkinNum;
    public int CustomSkin;
    public int CustomShader;

    public byte ShaderRGBA_R, ShaderRGBA_G, ShaderRGBA_B, ShaderRGBA_A;

    public float ShaderTexCoordX, ShaderTexCoordY;
    public float ShaderTime;

    public float Radius;
    public float Rotation;

    // RT_* entity types
    public const int RT_MODEL = 0;
    public const int RT_POLY = 1;
    public const int RT_SPRITE = 2;
    public const int RT_BEAM = 3;
    public const int RT_RAIL_CORE = 4;
    public const int RT_RAIL_RINGS = 5;
    public const int RT_LIGHTNING = 6;
    public const int RT_PORTALSURFACE = 7;
    public const int RT_MAX_REF_ENTITY_TYPE = 8;

    // RF_* render flags
    public const int RF_MINLIGHT = 1;
    public const int RF_THIRD_PERSON = 2;
    public const int RF_FIRST_PERSON = 4;
    public const int RF_DEPTHHACK = 8;
    public const int RF_NOSHADOW = 64;
    public const int RF_LIGHTING_ORIGIN = 128;
    public const int RF_SHADOW_PLANE = 256;
    public const int RF_WRAP_FRAMES = 512;
}

// gameState_t for raw reading
public static class Q3GameState
{
    public const int MAX_CONFIGSTRINGS = 1024;
    public const int MAX_GAMESTATE_CHARS = 16000;

    // Total raw size: 1024*4 + 16000 + 4 + 4 = 20104 bytes
    public const int RAW_SIZE = MAX_CONFIGSTRINGS * 4 + MAX_GAMESTATE_CHARS + 4 + 4;

    public static unsafe string GetConfigString(byte* gameState, int index)
    {
        int* offsets = (int*)gameState;
        int offset = offsets[index];
        if (offset == 0 && index != 0) return "";

        byte* stringData = gameState + MAX_CONFIGSTRINGS * 4;
        return Marshal.PtrToStringUTF8((nint)(stringData + offset)) ?? "";
    }

    // Config string indices
    public const int CS_SERVERINFO = 0;
    public const int CS_SYSTEMINFO = 1;
    public const int CS_MUSIC = 2;
    public const int CS_MESSAGE = 3;
    public const int CS_MOTD = 4;
    public const int CS_WARMUP = 5;
    public const int CS_SCORES1 = 6;
    public const int CS_SCORES2 = 7;
    public const int CS_VOTE_TIME = 8;
    public const int CS_VOTE_STRING = 9;
    public const int CS_VOTE_YES = 10;
    public const int CS_VOTE_NO = 11;
    public const int CS_TEAMVOTE_TIME = 12;
    public const int CS_TEAMVOTE_STRING = 14;
    public const int CS_TEAMVOTE_YES = 16;
    public const int CS_TEAMVOTE_NO = 18;
    public const int CS_GAME_VERSION = 20;
    public const int CS_LEVEL_START_TIME = 21;
    public const int CS_INTERMISSION = 22;
    public const int CS_FLAGSTATUS = 23;
    public const int CS_SHADERSTATE = 24;
    public const int CS_BOTINFO = 25;
    public const int CS_ITEMS = 27;
    public const int CS_MODELS = 32;
    public const int CS_SOUNDS = 288;   // CS_MODELS + MAX_MODELS (256)
    public const int CS_PLAYERS = 544;  // CS_SOUNDS + MAX_SOUNDS (256)
    public const int CS_LOCATIONS = 608; // CS_PLAYERS + MAX_CLIENTS (64)
    public const int CS_PARTICLES = 672;
    public const int CS_MAX = 1024;
}

// glconfig_t field offsets (matching RendererExports.cs)
public static class Q3GlConfig
{
    public const int SIZE = 11328;
    public const int VID_WIDTH = 11304;
    public const int VID_HEIGHT = 11308;
    public const int WINDOW_ASPECT = 11312;
}

// Stat indices
public static class Stats
{
    public const int STAT_HEALTH = 0;
    public const int STAT_HOLDABLE_ITEM = 1;
    public const int STAT_WEAPONS = 2;
    public const int STAT_ARMOR = 3;
    public const int STAT_DEAD_YAW = 4;
    public const int STAT_CLIENTS_READY = 5;
    public const int STAT_MAX_HEALTH = 6;
}

// Persistant indices
public static class Persistant
{
    public const int PERS_SCORE = 0;
    public const int PERS_HITS = 1;
    public const int PERS_RANK = 2;
    public const int PERS_TEAM = 3;
    public const int PERS_SPAWN_COUNT = 4;
    public const int PERS_PLAYEREVENTS = 5;
    public const int PERS_ATTACKER = 6;
    public const int PERS_ATTACKEE_ARMOR = 7;
    public const int PERS_KILLED = 8;
    public const int PERS_IMPRESSIVE_COUNT = 9;
    public const int PERS_EXCELLENT_COUNT = 10;
    public const int PERS_DEFEND_COUNT = 11;
    public const int PERS_ASSIST_COUNT = 12;
    public const int PERS_GAUNTLET_FRAG_COUNT = 13;
    public const int PERS_CAPTURES = 14;
}

// Entity types
public static class EntityType
{
    public const int ET_GENERAL = 0;
    public const int ET_PLAYER = 1;
    public const int ET_ITEM = 2;
    public const int ET_MISSILE = 3;
    public const int ET_MOVER = 4;
    public const int ET_BEAM = 5;
    public const int ET_PORTAL = 6;
    public const int ET_SPEAKER = 7;
    public const int ET_PUSH_TRIGGER = 8;
    public const int ET_TELEPORT_TRIGGER = 9;
    public const int ET_INVISIBLE = 10;
    public const int ET_GRAPPLE = 11;
    public const int ET_TEAM = 12;
    public const int ET_EVENTS = 13;
}

// Weapon indices
public static class Weapons
{
    public const int WP_NONE = 0;
    public const int WP_GAUNTLET = 1;
    public const int WP_MACHINEGUN = 2;
    public const int WP_SHOTGUN = 3;
    public const int WP_GRENADE_LAUNCHER = 4;
    public const int WP_ROCKET_LAUNCHER = 5;
    public const int WP_LIGHTNING = 6;
    public const int WP_RAILGUN = 7;
    public const int WP_PLASMAGUN = 8;
    public const int WP_BFG = 9;
    public const int WP_GRAPPLING_HOOK = 10;
    public const int WP_NUM_WEAPONS = 11;
}

// Team indices
public static class Teams
{
    public const int TEAM_FREE = 0;
    public const int TEAM_RED = 1;
    public const int TEAM_BLUE = 2;
    public const int TEAM_SPECTATOR = 3;
}

// Snapshot flags
public static class SnapFlags
{
    public const int SNAPFLAG_RATE_DELAYED = 1;
    public const int SNAPFLAG_NOT_ACTIVE = 2;
    public const int SNAPFLAG_SERVERCOUNT = 4;
}

// PM types
public static class PmType
{
    public const int PM_NORMAL = 0;
    public const int PM_NOCLIP = 1;
    public const int PM_SPECTATOR = 2;
    public const int PM_DEAD = 3;
    public const int PM_FREEZE = 4;
    public const int PM_INTERMISSION = 5;
    public const int PM_SPINTERMISSION = 6;
}

// Entity number constants
public static class EntityNum
{
    public const int ENTITYNUM_NONE = 1023;
    public const int ENTITYNUM_WORLD = 1022;
    public const int ENTITYNUM_MAX_NORMAL = 1022;
    public const int MAX_GENTITIES = 1024;
    public const int MAX_CLIENTS = 64;
}
