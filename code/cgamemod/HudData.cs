using System.Runtime.InteropServices;

namespace CGameMod;

// Must match C modPlayerState_t in cg_mod.h exactly
[StructLayout(LayoutKind.Sequential)]
public struct ModPlayerState
{
    public int Health;
    public int Armor;
    public int Weapon;
    public int WeaponState;

    // MOD_MAX_WEAPONS = 16
    public int Ammo0, Ammo1, Ammo2, Ammo3, Ammo4, Ammo5, Ammo6, Ammo7;
    public int Ammo8, Ammo9, Ammo10, Ammo11, Ammo12, Ammo13, Ammo14, Ammo15;

    // MOD_MAX_STATS = 16
    public int Stat0, Stat1, Stat2, Stat3, Stat4, Stat5, Stat6, Stat7;
    public int Stat8, Stat9, Stat10, Stat11, Stat12, Stat13, Stat14, Stat15;

    // MOD_MAX_PERSISTANT = 16
    public int Pers0, Pers1, Pers2, Pers3, Pers4, Pers5, Pers6, Pers7;
    public int Pers8, Pers9, Pers10, Pers11, Pers12, Pers13, Pers14, Pers15;

    // MOD_MAX_POWERUPS = 16
    public int PW0, PW1, PW2, PW3, PW4, PW5, PW6, PW7;
    public int PW8, PW9, PW10, PW11, PW12, PW13, PW14, PW15;

    public int PmType;
    public int PmFlags;
    public int ClientNum;
    public int EFlags;

    public int GetAmmo(int index) => index switch
    {
        0 => Ammo0, 1 => Ammo1, 2 => Ammo2, 3 => Ammo3,
        4 => Ammo4, 5 => Ammo5, 6 => Ammo6, 7 => Ammo7,
        8 => Ammo8, 9 => Ammo9, 10 => Ammo10, 11 => Ammo11,
        12 => Ammo12, 13 => Ammo13, 14 => Ammo14, 15 => Ammo15,
        _ => 0
    };

    public int GetStat(int index) => index switch
    {
        0 => Stat0, 1 => Stat1, 2 => Stat2, 3 => Stat3,
        4 => Stat4, 5 => Stat5, 6 => Stat6, 7 => Stat7,
        8 => Stat8, 9 => Stat9, 10 => Stat10, 11 => Stat11,
        12 => Stat12, 13 => Stat13, 14 => Stat14, 15 => Stat15,
        _ => 0
    };

    public int GetPersistant(int index) => index switch
    {
        0 => Pers0, 1 => Pers1, 2 => Pers2, 3 => Pers3,
        4 => Pers4, 5 => Pers5, 6 => Pers6, 7 => Pers7,
        8 => Pers8, 9 => Pers9, 10 => Pers10, 11 => Pers11,
        12 => Pers12, 13 => Pers13, 14 => Pers14, 15 => Pers15,
        _ => 0
    };

    public int GetPowerup(int index) => index switch
    {
        0 => PW0, 1 => PW1, 2 => PW2, 3 => PW3,
        4 => PW4, 5 => PW5, 6 => PW6, 7 => PW7,
        8 => PW8, 9 => PW9, 10 => PW10, 11 => PW11,
        12 => PW12, 13 => PW13, 14 => PW14, 15 => PW15,
        _ => 0
    };

    // Weapon enum
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

    // Stat indices
    public const int STAT_HEALTH = 0;
    public const int STAT_HOLDABLE_ITEM = 1;
    public const int STAT_WEAPONS = 2;
    public const int STAT_ARMOR = 3;
    public const int STAT_MAX_HEALTH = 6;

    // Persistant indices
    public const int PERS_SCORE = 0;
    public const int PERS_HITS = 1;
    public const int PERS_RANK = 2;
    public const int PERS_TEAM = 3;
    public const int PERS_SPAWN_COUNT = 4;
    public const int PERS_ATTACKER = 6;
    public const int PERS_KILLED = 7;

    // Powerup indices
    public const int PW_QUAD = 1;
    public const int PW_BATTLESUIT = 2;
    public const int PW_HASTE = 3;
    public const int PW_INVIS = 4;
    public const int PW_REGEN = 5;
    public const int PW_FLIGHT = 6;
    public const int PW_REDFLAG = 7;
    public const int PW_BLUEFLAG = 8;

    // PM types
    public const int PM_NORMAL = 0;
    public const int PM_DEAD = 4;
    public const int PM_INTERMISSION = 5;

    public bool HasWeapon(int wp) => (GetStat(STAT_WEAPONS) & (1 << wp)) != 0;
}

// Must match C modHudState_t in cg_mod.h exactly
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ModHudState
{
    public int Gametype;
    public int Fraglimit;
    public int Timelimit;
    public int Capturelimit;
    public int Scores1;
    public int Scores2;
    public int RedFlag;
    public int BlueFlag;
    public int LevelStartTime;
    public int Warmup;
    public int Time;
    public int RealTime;
    public int LowAmmoWarning;
    public int WeaponSelect;
    public int WeaponSelectTime;
    public int ShowScores;
    public int ItemPickup;
    public int ItemPickupTime;
    public int ItemPickupBlendTime;
    public int CrosshairClientNum;
    public int CrosshairClientTime;
    public int CenterPrintTime;
    public int CenterPrintCharWidth;
    public int VoteTime;
    public int VoteYes;
    public int VoteNo;
    public int AttackerTime;
    public int AttackerClientNum;
    public int NumClients;
    public int LocalServer;
    public int TeamVoteTime;
    public int TeamVoteYes;
    public int TeamVoteNo;
    public int ArmorIconShader;
    public fixed byte CenterPrint[1024];
    public fixed byte VoteString[256];
    public fixed byte CrosshairClientName[64];
    public fixed byte ItemPickupName[64];
    public fixed byte TeamVoteString[256];
    public fixed byte AttackerName[64];

    // Gametype constants
    public const int GT_FFA = 0;
    public const int GT_TOURNAMENT = 1;
    public const int GT_TEAM = 3;
    public const int GT_CTF = 4;

    // Weapon select display time
    public const int WEAPON_SELECT_TIME = 1400;
}
