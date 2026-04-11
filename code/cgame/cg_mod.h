/*
===========================================================================
cg_mod.h -- .NET mod host integration for cgame

Loads cgamemod.dll (NativeAOT) and calls exported hook functions
at key points in the cgame lifecycle.
===========================================================================
*/

#ifndef CG_MOD_H
#define CG_MOD_H

#include "../qcommon/q_shared.h"

/*
==================
HUD data structures passed to mod
==================
*/

#define MOD_MAX_WEAPONS		16
#define MOD_MAX_STATS		16
#define MOD_MAX_PERSISTANT	16
#define MOD_MAX_POWERUPS	16

// HUD control flags (bitmask)
#define HUD_FLAG_DISABLED	0x0001	// Suppress all C HUD drawing

// Player state data for HUD mod
typedef struct {
	int		health;
	int		armor;
	int		weapon;
	int		weaponstate;
	int		ammo[MOD_MAX_WEAPONS];
	int		stats[MOD_MAX_STATS];
	int		persistant[MOD_MAX_PERSISTANT];
	int		powerups[MOD_MAX_POWERUPS];
	int		pm_type;
	int		pm_flags;
	int		clientNum;
	int		eFlags;
} modPlayerState_t;

// Game and HUD state data for HUD mod
typedef struct {
	int		gametype;
	int		fraglimit;
	int		timelimit;
	int		capturelimit;
	int		scores1;
	int		scores2;
	int		redflag;
	int		blueflag;
	int		levelStartTime;
	int		warmup;
	int		time;				// cg.time (client predicted time)
	int		realTime;			// trap_Milliseconds()
	int		lowAmmoWarning;		// 0=none, 1=low, 2=empty
	int		weaponSelect;
	int		weaponSelectTime;
	int		showScores;
	int		itemPickup;			// bg_itemlist index
	int		itemPickupTime;
	int		itemPickupBlendTime;
	int		crosshairClientNum;
	int		crosshairClientTime;
	int		centerPrintTime;
	int		centerPrintCharWidth;
	int		voteTime;
	int		voteYes;
	int		voteNo;
	int		attackerTime;
	int		attackerClientNum;	// from PERS_ATTACKER
	int		numClients;			// number of connected clients
	int		localServer;		// 1 if local server (for lagometer)
	char	centerPrint[1024];
	char	voteString[256];
	char	crosshairClientName[64];
	char	itemPickupName[64];		// bg_itemlist[itemPickup].pickup_name
} modHudState_t;


/*
==================
API struct passed to the .NET mod host at init
==================
*/

// API struct passed to the .NET mod host at init
typedef struct {
	// --- Existing API (15 pointers) ---
	void	(*DoTrace)( float *results, float *start, float *end, int skipNum, int mask );
	void	(*GetViewOrigin)( float *out );
	void	(*GetViewAngles)( float *out );
	void	(*SetHighlightEntity)( int entityNum );
	int		(*GetPlayerWeapon)( void );
	void	(*GetEntityOrigin)( int entityNum, float *out );
	int		(*GetEntityModelHandle)( int entityNum );
	int		(*GetEntityType)( int entityNum );
	int		(*GetSnapshotEntityCount)( void );
	int		(*GetSnapshotEntityNum)( int index );
	int		(*GetEntityModelName)( int entityNum, char *buf, int bufSize );
	void	(*GetEntityInfo)( int entityNum, int *weapon, int *eFlags, int *frame, int *event );
	void	(*SetHighlightAABB)( float *mins, float *maxs );
	void	(*SetHighlightTrajectory)( float *points, int numPoints );
	int		(*GetClientNum)( void );

	// --- HUD data API (new) ---
	void	(*GetPlayerState)( modPlayerState_t *out );
	void	(*GetHudState)( modHudState_t *out );
	void	(*SetHudFlags)( int flags );
	int		(*GetConfigString)( int index, char *buf, int bufSize );
} cgameModApi_t;

// Initialize the mod host (loads DLL, calls CgMod_Init)
void CG_Mod_Init( intptr_t (QDECL *syscall)( intptr_t, ... ), int screenWidth, int screenHeight );

// Shutdown the mod host (calls CgMod_Shutdown, unloads DLL)
void CG_Mod_Shutdown( void );

// Per-frame update
void CG_Mod_Frame( int serverTime );

// 2D HUD overlay drawing (call after standard HUD)
void CG_Mod_Draw2D( void );

// Console command dispatch (returns qtrue if a mod handled it)
qboolean CG_Mod_ConsoleCommand( void );

// Entity event notification
void CG_Mod_EntityEvent( int entityNum, int eventType, int eventParm );

// Get the currently highlighted entity (-1 if none)
int CG_Mod_GetHighlightEntity( void );

// Get the world-space AABB set by the mod. Returns qtrue if set.
qboolean CG_Mod_GetHighlightAABB( float *mins, float *maxs );

// Get the trajectory polyline. Returns number of points (0 if none).
int CG_Mod_GetHighlightTrajectory( float **points );

// Route a server command to the mod host (returns qtrue if handled)
qboolean CG_Mod_ServerCommand( const char *cmd );

// Get the HUD flags set by the mod (0 if no mod loaded)
int CG_Mod_GetHudFlags( void );

#endif // CG_MOD_H
