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
typedef void	(*CgMod_InitFunc)( intptr_t syscallPtr, int screenWidth, int screenHeight );
typedef void	(*CgMod_ShutdownFunc)( void );
typedef void	(*CgMod_FrameFunc)( int serverTime );
typedef void	(*CgMod_Draw2DFunc)( void );
typedef int		(*CgMod_ConsoleCommandFunc)( void );
typedef void	(*CgMod_EntityEventFunc)( int entityNum, int eventType, int eventParm );

static void		*modLib = NULL;

static CgMod_InitFunc			fn_Init;
static CgMod_ShutdownFunc		fn_Shutdown;
static CgMod_FrameFunc			fn_Frame;
static CgMod_Draw2DFunc		fn_Draw2D;
static CgMod_ConsoleCommandFunc	fn_ConsoleCommand;
static CgMod_EntityEventFunc	fn_EntityEvent;


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
	char	basePath[MAX_OSPATH];
	char	gameDir[MAX_OSPATH];
	char	searchDir[MAX_OSPATH];

	// Get fs_basepath and fs_game via trap calls
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

	if ( !fn_Init ) {
		CG_Printf( "^1Mod host missing CgMod_Init export\n" );
		MOD_FREELIB( modLib );
		modLib = NULL;
		return;
	}

	CG_Printf( "Mod host loaded, initializing...\n" );
	fn_Init( (intptr_t)syscall, screenWidth, screenHeight );
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
