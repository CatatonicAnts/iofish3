/*
===========================================================================
cg_mod.c -- .NET mod host integration for cgame

Loads cgamemod.dll (NativeAOT shared library) and dispatches
cgame events to the .NET mod host via exported C functions.
===========================================================================
*/

#include "cg_local.h"
#include "cg_mod.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#define MOD_LOADLIB(path)		((void *)LoadLibraryA(path))
#define MOD_LOADFUNC(lib, fn)	((void *)GetProcAddress((HMODULE)(lib), (fn)))
#define MOD_FREELIB(lib)		FreeLibrary((HMODULE)(lib))
#else
#include <dlfcn.h>
#define MOD_LOADLIB(path)		dlopen(path, RTLD_NOW)
#define MOD_LOADFUNC(lib, fn)	dlsym(lib, fn)
#define MOD_FREELIB(lib)		dlclose(lib)
#endif

// Function pointer types for mod host exports
typedef void	(*CgMod_InitFunc)( intptr_t syscallPtr, int screenWidth, int screenHeight, cgameModApi_t *api );
typedef void	(*CgMod_ShutdownFunc)( void );
typedef void	(*CgMod_FrameFunc)( int serverTime );
typedef void	(*CgMod_Draw2DFunc)( void );
typedef int		(*CgMod_ConsoleCommandFunc)( void );
typedef void	(*CgMod_EntityEventFunc)( int entityNum, int eventType, int eventParm );
typedef void	(*CgMod_ServerCommandFunc)( const char *cmd );

static void		*modLib = NULL;

static CgMod_InitFunc			fn_Init;
static CgMod_ShutdownFunc		fn_Shutdown;
static CgMod_FrameFunc			fn_Frame;
static CgMod_Draw2DFunc		fn_Draw2D;
static CgMod_ConsoleCommandFunc	fn_ConsoleCommand;
static CgMod_EntityEventFunc	fn_EntityEvent;
static CgMod_ServerCommandFunc	fn_ServerCommand;

// Highlight entity set by the mod
static int		highlightEntity = -1;

// World-space AABB for highlight wireframe (set by mod)
static qboolean	highlightAABBSet = qfalse;
static vec3_t	highlightMins, highlightMaxs;

// Trajectory polyline for highlight drawing (set by mod)
#define MAX_TRAJECTORY_POINTS 128
static float	trajectoryPoints[MAX_TRAJECTORY_POINTS * 3];
static int		trajectoryNumPoints = 0;


/*
==================
Mod API callback implementations
==================
*/
static void ModApi_DoTrace( float *results, float *start, float *end, int skipNum, int mask ) {
	trace_t tr;
	vec3_t	mins = {0,0,0}, maxs = {0,0,0};

	CG_Trace( &tr, start, mins, maxs, end, skipNum, mask );

	// Pack trace results: fraction, endpos[3], entityNum
	results[0] = tr.fraction;
	results[1] = tr.endpos[0];
	results[2] = tr.endpos[1];
	results[3] = tr.endpos[2];
	results[4] = *(float *)&tr.entityNum;
}

static void ModApi_GetViewOrigin( float *out ) {
	VectorCopy( cg.refdef.vieworg, out );
}

static void ModApi_GetViewAngles( float *out ) {
	VectorCopy( cg.refdefViewAngles, out );
}

static void ModApi_SetHighlightEntity( int entityNum ) {
	highlightEntity = entityNum;
}

static int ModApi_GetPlayerWeapon( void ) {
	return cg.predictedPlayerState.weapon;
}

static void ModApi_GetEntityOrigin( int entityNum, float *out ) {
	if ( entityNum >= 0 && entityNum < MAX_GENTITIES ) {
		VectorCopy( cg_entities[entityNum].lerpOrigin, out );
	} else {
		VectorClear( out );
	}
}

static int ModApi_GetEntityModelHandle( int entityNum ) {
	if ( entityNum >= 0 && entityNum < MAX_GENTITIES ) {
		return cgs.gameModels[ cg_entities[entityNum].currentState.modelindex ];
	}
	return 0;
}

static int ModApi_GetEntityType( int entityNum ) {
	if ( entityNum >= 0 && entityNum < MAX_GENTITIES ) {
		return cg_entities[entityNum].currentState.eType;
	}
	return -1;
}

static int ModApi_GetSnapshotEntityCount( void ) {
	if ( cg.snap ) {
		return cg.snap->numEntities;
	}
	return 0;
}

static int ModApi_GetSnapshotEntityNum( int index ) {
	if ( cg.snap && index >= 0 && index < cg.snap->numEntities ) {
		return cg.snap->entities[index].number;
	}
	return -1;
}

static int ModApi_GetEntityModelName( int entityNum, char *buf, int bufSize ) {
	if ( entityNum >= 0 && entityNum < MAX_GENTITIES && bufSize > 0 ) {
		int modelindex = cg_entities[entityNum].currentState.modelindex;
		if ( modelindex > 0 ) {
			const char *name = CG_ConfigString( CS_MODELS + modelindex );
			if ( name && name[0] ) {
				Q_strncpyz( buf, name, bufSize );
				return (int)strlen( buf );
			}
		}
	}
	if ( bufSize > 0 ) buf[0] = '\0';
	return 0;
}

static void ModApi_GetEntityInfo( int entityNum, int *weapon, int *eFlags, int *frame, int *event ) {
	if ( entityNum >= 0 && entityNum < MAX_GENTITIES ) {
		entityState_t *s = &cg_entities[entityNum].currentState;
		*weapon = s->weapon;
		*eFlags = s->eFlags;
		*frame  = s->frame;
		*event  = s->event;
	} else {
		*weapon = 0;
		*eFlags = 0;
		*frame  = 0;
		*event  = 0;
	}
}

static void ModApi_SetHighlightAABB( float *mins, float *maxs ) {
	if ( mins && maxs ) {
		VectorCopy( mins, highlightMins );
		VectorCopy( maxs, highlightMaxs );
		highlightAABBSet = qtrue;
	} else {
		highlightAABBSet = qfalse;
	}
}

static void ModApi_SetHighlightTrajectory( float *points, int numPoints ) {
	if ( points && numPoints > 1 && numPoints <= MAX_TRAJECTORY_POINTS ) {
		memcpy( trajectoryPoints, points, numPoints * 3 * sizeof(float) );
		trajectoryNumPoints = numPoints;
	} else {
		trajectoryNumPoints = 0;
	}
}


/*
==================
CG_Mod_TryLoad

Attempt to load cgamemod.dll from the given directory.
Returns qtrue on success.
==================
*/
static qboolean CG_Mod_TryLoad( const char *dir ) {
	char libPath[MAX_OSPATH];

	Com_sprintf( libPath, sizeof(libPath), "%s%ccgamemod.dll", dir, PATH_SEP );
	CG_Printf( "Trying mod host: %s\n", libPath );
	modLib = MOD_LOADLIB( libPath );

	return modLib != NULL ? qtrue : qfalse;
}


/*
==================
CG_Mod_Init

Load cgamemod.dll and initialize the .NET mod host.
Called from CG_Init after basic setup is complete.
==================
*/
void CG_Mod_Init( intptr_t (QDECL *syscall)( intptr_t, ... ), int screenWidth, int screenHeight ) {
	char			basePath[MAX_OSPATH];
	char			gameDir[MAX_OSPATH];
	char			searchDir[MAX_OSPATH];
	cgameModApi_t	api;

	highlightEntity = -1;
	highlightAABBSet = qfalse;
	trajectoryNumPoints = 0;

	// Get fs_basepathand fs_game via trap calls
	trap_Cvar_VariableStringBuffer( "fs_basepath", basePath, sizeof(basePath) );
	trap_Cvar_VariableStringBuffer( "fs_game", gameDir, sizeof(gameDir) );

	if ( !basePath[0] ) {
		Q_strncpyz( basePath, ".", sizeof(basePath) );
	}
	if ( !gameDir[0] ) {
		Q_strncpyz( gameDir, BASEGAME, sizeof(gameDir) );
	}

	// Try fs_basepath/gameDir/
	Com_sprintf( searchDir, sizeof(searchDir), "%s%c%s", basePath, PATH_SEP, gameDir );
	if ( !CG_Mod_TryLoad( searchDir ) ) {
		// Try fs_basepath/ (next to engine exe)
		if ( !CG_Mod_TryLoad( basePath ) ) {
			CG_Printf( "^3Mod host not found (cgamemod.dll) - mods disabled\n" );
			return;
		}
	}

	// Resolve exports
	fn_Init				= (CgMod_InitFunc)MOD_LOADFUNC( modLib, "CgMod_Init" );
	fn_Shutdown			= (CgMod_ShutdownFunc)MOD_LOADFUNC( modLib, "CgMod_Shutdown" );
	fn_Frame			= (CgMod_FrameFunc)MOD_LOADFUNC( modLib, "CgMod_Frame" );
	fn_Draw2D			= (CgMod_Draw2DFunc)MOD_LOADFUNC( modLib, "CgMod_Draw2D" );
	fn_ConsoleCommand	= (CgMod_ConsoleCommandFunc)MOD_LOADFUNC( modLib, "CgMod_ConsoleCommand" );
	fn_EntityEvent		= (CgMod_EntityEventFunc)MOD_LOADFUNC( modLib, "CgMod_EntityEvent" );
	fn_ServerCommand	= (CgMod_ServerCommandFunc)MOD_LOADFUNC( modLib, "CgMod_ServerCommand" );

	if ( !fn_Init ) {
		CG_Printf( "^1Mod host missing CgMod_Init export\n" );
		MOD_FREELIB( modLib );
		modLib = NULL;
		return;
	}

	// Populate the mod API struct
	api.DoTrace					= ModApi_DoTrace;
	api.GetViewOrigin			= ModApi_GetViewOrigin;
	api.GetViewAngles			= ModApi_GetViewAngles;
	api.SetHighlightEntity		= ModApi_SetHighlightEntity;
	api.GetPlayerWeapon			= ModApi_GetPlayerWeapon;
	api.GetEntityOrigin			= ModApi_GetEntityOrigin;
	api.GetEntityModelHandle	= ModApi_GetEntityModelHandle;
	api.GetEntityType			= ModApi_GetEntityType;
	api.GetSnapshotEntityCount	= ModApi_GetSnapshotEntityCount;
	api.GetSnapshotEntityNum	= ModApi_GetSnapshotEntityNum;
	api.GetEntityModelName		= ModApi_GetEntityModelName;
	api.GetEntityInfo			= ModApi_GetEntityInfo;
	api.SetHighlightAABB		= ModApi_SetHighlightAABB;
	api.SetHighlightTrajectory	= ModApi_SetHighlightTrajectory;

	CG_Printf( "Mod host loaded, initializing...\n" );
	fn_Init( (intptr_t)syscall, screenWidth, screenHeight, &api );
}


/*
==================
CG_Mod_Shutdown
==================
*/
void CG_Mod_Shutdown( void ) {
	if ( !modLib ) return;

	if ( fn_Shutdown ) {
		fn_Shutdown();
	}

	MOD_FREELIB( modLib );
	modLib = NULL;
	fn_Init = NULL;
	fn_Shutdown = NULL;
	fn_Frame = NULL;
	fn_Draw2D = NULL;
	fn_ConsoleCommand = NULL;
	fn_EntityEvent = NULL;
	fn_ServerCommand = NULL;
	highlightEntity = -1;
	highlightAABBSet = qfalse;
}


/*
==================
CG_Mod_Frame
==================
*/
void CG_Mod_Frame( int serverTime ) {
	if ( fn_Frame ) {
		fn_Frame( serverTime );
	}
}


/*
==================
CG_Mod_Draw2D
==================
*/
void CG_Mod_Draw2D( void ) {
	if ( fn_Draw2D ) {
		fn_Draw2D();
	}
}


/*
==================
CG_Mod_ConsoleCommand
==================
*/
qboolean CG_Mod_ConsoleCommand( void ) {
	if ( fn_ConsoleCommand ) {
		return fn_ConsoleCommand() ? qtrue : qfalse;
	}
	return qfalse;
}


/*
==================
CG_Mod_EntityEvent
==================
*/
void CG_Mod_EntityEvent( int entityNum, int eventType, int eventParm ) {
	if ( fn_EntityEvent ) {
		fn_EntityEvent( entityNum, eventType, eventParm );
	}
}


/*
==================
CG_Mod_GetHighlightEntity

Returns the entity number the mod wants highlighted, or -1 for none.
==================
*/
int CG_Mod_GetHighlightEntity( void ) {
	return highlightEntity;
}


/*
==================
CG_Mod_GetHighlightAABB

Returns qtrue if the mod has set a world-space AABB for highlight wireframe.
==================
*/
qboolean CG_Mod_GetHighlightAABB( float *mins, float *maxs ) {
	if ( !highlightAABBSet ) return qfalse;
	VectorCopy( highlightMins, mins );
	VectorCopy( highlightMaxs, maxs );
	return qtrue;
}

/*
==================
CG_Mod_GetHighlightTrajectory

Returns number of trajectory points (0 if none). Sets *points to the array.
==================
*/
int CG_Mod_GetHighlightTrajectory( float **points ) {
	if ( trajectoryNumPoints > 0 ) {
		*points = trajectoryPoints;
		return trajectoryNumPoints;
	}
	*points = NULL;
	return 0;
}


/*
==================
CG_Mod_ServerCommand

Route a server command to the .NET mod host.
Returns qtrue if a mod handled it.
==================
*/
qboolean CG_Mod_ServerCommand( const char *cmd ) {
	if ( fn_ServerCommand ) {
		fn_ServerCommand( cmd );
		return qtrue;
	}
	return qfalse;
}
