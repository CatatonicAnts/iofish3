/*
===========================================================================
g_mod.c -- .NET mod host integration for server game module

Loads qagamemod.dll (NativeAOT shared library) and dispatches
game events to the .NET mod host via exported C functions.

Also provides a "Game API" — a set of callbacks the .NET mod can
use to query and manipulate entities within the running game.
===========================================================================
*/

#include "g_local.h"
#include "g_mod.h"

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

// =====================================================================
// Game API callbacks — entity manipulation functions for .NET mods
// =====================================================================

/*
Get the current number of active entities.
*/
static int QDECL GMod_GetEntityCount( void ) {
	return level.num_entities;
}

/*
Fill buffer with a description of entity at index.
Format: "classname\ttargetname\ttype\thealth\toriginX\toriginY\toriginZ\tinuse"
Returns 1 on success, 0 if index is out of range.
*/
static int QDECL GMod_GetEntityInfo( int index, char *buf, int bufSize ) {
	gentity_t	*ent;

	if ( index < 0 || index >= level.num_entities || bufSize <= 0 ) {
		if ( bufSize > 0 ) buf[0] = '\0';
		return 0;
	}

	ent = &g_entities[index];
	if ( !ent->inuse ) {
		Com_sprintf( buf, bufSize, "\t\t0\t0\t0\t0\t0\t0" );
		return 1;
	}

	Com_sprintf( buf, bufSize, "%s\t%s\t%i\t%i\t%f\t%f\t%f\t1",
		ent->classname ? ent->classname : "",
		ent->targetname ? ent->targetname : "",
		(int)ent->s.eType,
		ent->health,
		ent->s.pos.trBase[0],
		ent->s.pos.trBase[1],
		ent->s.pos.trBase[2] );

	return 1;
}

/*
Spawn a new entity with the given classname at the specified origin.
Returns the entity number on success, -1 on failure.
*/
static int QDECL GMod_SpawnEntity( const char *classname, float x, float y, float z ) {
	gentity_t	*ent;

	if ( !classname || !classname[0] ) {
		return -1;
	}

	ent = G_Spawn();
	if ( !ent ) {
		return -1;
	}

	ent->classname = G_NewString( classname );
	ent->s.pos.trBase[0] = x;
	ent->s.pos.trBase[1] = y;
	ent->s.pos.trBase[2] = z;
	VectorCopy( ent->s.pos.trBase, ent->r.currentOrigin );
	ent->s.pos.trType = TR_STATIONARY;

	if ( !G_CallSpawn( ent ) ) {
		G_Printf( "GMod_SpawnEntity: no spawn function for '%s'\n", classname );
		G_FreeEntity( ent );
		return -1;
	}

	return ent->s.number;
}

/*
Fire (call use function on) all entities with the given targetname.
activatorNum is the entity index of the activator (typically player 0).
Returns the count of entities fired.
*/
static int QDECL GMod_FireEntity( const char *targetname, int activatorNum ) {
	gentity_t	*t;
	gentity_t	*activator;
	int			count = 0;

	if ( !targetname || !targetname[0] ) {
		return 0;
	}

	if ( activatorNum >= 0 && activatorNum < level.num_entities ) {
		activator = &g_entities[activatorNum];
	} else {
		activator = &g_entities[0]; // default to world
	}

	t = NULL;
	while ( (t = G_Find( t, FOFS(targetname), targetname )) != NULL ) {
		if ( t->use ) {
			t->use( t, t, activator );
			count++;
		}
	}

	return count;
}

/*
Remove (free) entity at the given index.
Returns 1 on success, 0 on failure.
*/
static int QDECL GMod_RemoveEntity( int index ) {
	gentity_t	*ent;

	if ( index < MAX_CLIENTS || index >= level.num_entities ) {
		return 0;
	}

	ent = &g_entities[index];
	if ( !ent->inuse ) {
		return 0;
	}

	G_FreeEntity( ent );
	return 1;
}


// =====================================================================
// Game API struct — passed to the .NET mod during Init
// =====================================================================

typedef struct {
	int		(QDECL *GetEntityCount)( void );
	int		(QDECL *GetEntityInfo)( int index, char *buf, int bufSize );
	int		(QDECL *SpawnEntity)( const char *classname, float x, float y, float z );
	int		(QDECL *FireEntity)( const char *targetname, int activatorNum );
	int		(QDECL *RemoveEntity)( int index );
} gameModApi_t;

static gameModApi_t gameApi = {
	GMod_GetEntityCount,
	GMod_GetEntityInfo,
	GMod_SpawnEntity,
	GMod_FireEntity,
	GMod_RemoveEntity
};


// =====================================================================
// Mod host DLL management
// =====================================================================

// Function pointer types for mod host exports
typedef void	(*QgMod_InitFunc)( intptr_t syscallPtr, void *gameApiPtr );
typedef void	(*QgMod_ShutdownFunc)( void );
typedef void	(*QgMod_FrameFunc)( int levelTime );
typedef int		(*QgMod_ConsoleCommandFunc)( void );

static void		*modLib = NULL;

static QgMod_InitFunc			fn_Init;
static QgMod_ShutdownFunc		fn_Shutdown;
static QgMod_FrameFunc			fn_Frame;
static QgMod_ConsoleCommandFunc	fn_ConsoleCommand;


/*
==================
G_Mod_TryLoad

Attempt to load qagamemod.dll from the given directory.
Returns qtrue on success.
==================
*/
static qboolean G_Mod_TryLoad( const char *dir ) {
	char libPath[MAX_OSPATH];

	Com_sprintf( libPath, sizeof(libPath), "%s%cqagamemod.dll", dir, PATH_SEP );
	G_Printf( "Trying game mod host: %s\n", libPath );
	modLib = MOD_LOADLIB( libPath );

	return modLib != NULL ? qtrue : qfalse;
}


/*
==================
G_Mod_Init

Load qagamemod.dll and initialize the .NET mod host.
Called from G_InitGame after basic setup is complete.
==================
*/
void G_Mod_Init( intptr_t (QDECL *syscall)( intptr_t, ... ) ) {
	char	basePath[MAX_OSPATH];
	char	gameDir[MAX_OSPATH];
	char	searchDir[MAX_OSPATH];

	// Get fs_basepath and fs_game
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
	if ( !G_Mod_TryLoad( searchDir ) ) {
		// Try fs_basepath/ (next to engine exe)
		if ( !G_Mod_TryLoad( basePath ) ) {
			G_Printf( "^3Game mod host not found (qagamemod.dll) - server mods disabled\n" );
			return;
		}
	}

	// Resolve exports
	fn_Init				= (QgMod_InitFunc)MOD_LOADFUNC( modLib, "QgMod_Init" );
	fn_Shutdown			= (QgMod_ShutdownFunc)MOD_LOADFUNC( modLib, "QgMod_Shutdown" );
	fn_Frame			= (QgMod_FrameFunc)MOD_LOADFUNC( modLib, "QgMod_Frame" );
	fn_ConsoleCommand	= (QgMod_ConsoleCommandFunc)MOD_LOADFUNC( modLib, "QgMod_ConsoleCommand" );

	if ( !fn_Init ) {
		G_Printf( "^1Game mod host missing QgMod_Init export\n" );
		MOD_FREELIB( modLib );
		modLib = NULL;
		return;
	}

	G_Printf( "Game mod host loaded, initializing...\n" );
	fn_Init( (intptr_t)syscall, &gameApi );
}


/*
==================
G_Mod_Shutdown
==================
*/
void G_Mod_Shutdown( void ) {
	if ( !modLib ) return;

	if ( fn_Shutdown ) {
		fn_Shutdown();
	}

	MOD_FREELIB( modLib );
	modLib = NULL;
	fn_Init = NULL;
	fn_Shutdown = NULL;
	fn_Frame = NULL;
	fn_ConsoleCommand = NULL;
}


/*
==================
G_Mod_Frame
==================
*/
void G_Mod_Frame( int levelTime ) {
	if ( fn_Frame ) {
		fn_Frame( levelTime );
	}
}


/*
==================
G_Mod_ConsoleCommand
==================
*/
qboolean G_Mod_ConsoleCommand( void ) {
	if ( fn_ConsoleCommand ) {
		return fn_ConsoleCommand() ? qtrue : qfalse;
	}
	return qfalse;
}
