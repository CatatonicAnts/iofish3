/*
===========================================================================
ui_mod.c -- .NET mod host integration for the UI module

Loads uimod.dll (NativeAOT shared library) and dispatches
UI events to the .NET mod host via exported C functions.
Each mod function returns 1 to override C behavior, 0 for pass-through.
===========================================================================
*/

#include "../qcommon/q_shared.h"
#include "../renderercommon/tr_types.h"
#include "ui_public.h"
#include "ui_mod.h"

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

// ---- Trap wrappers we need (from ui_syscalls.c, but we can't reference them directly) ----
// The syscall pointer is passed to us from the UI module.
static intptr_t (QDECL *ui_syscall)( intptr_t arg, ... ) = NULL;

static void UiMod_Print( const char *msg ) {
	if ( ui_syscall ) ui_syscall( UI_PRINT, msg );
}

static void UiMod_CvarGet( const char *name, char *buf, int bufSize ) {
	if ( ui_syscall ) ui_syscall( UI_CVAR_VARIABLESTRINGBUFFER, name, buf, bufSize );
}

// ---- Function pointer types for mod host exports ----
typedef void	(*UiMod_InitFunc)( intptr_t syscallPtr );
typedef void	(*UiMod_ShutdownFunc)( void );
typedef int		(*UiMod_SetActiveMenuFunc)( int menu );
typedef int		(*UiMod_RefreshFunc)( int realtime );
typedef int		(*UiMod_KeyEventFunc)( int key, int down );
typedef int		(*UiMod_MouseEventFunc)( int dx, int dy );
typedef int		(*UiMod_IsFullscreenFunc)( void );
typedef int		(*UiMod_ConsoleCommandFunc)( int realtime );
typedef int		(*UiMod_DrawConnectScreenFunc)( int overlay );

// ---- Module state ----
static void		*modLib = NULL;

static UiMod_InitFunc				fn_Init;
static UiMod_ShutdownFunc			fn_Shutdown;
static UiMod_SetActiveMenuFunc		fn_SetActiveMenu;
static UiMod_RefreshFunc			fn_Refresh;
static UiMod_KeyEventFunc			fn_KeyEvent;
static UiMod_MouseEventFunc		fn_MouseEvent;
static UiMod_IsFullscreenFunc		fn_IsFullscreen;
static UiMod_ConsoleCommandFunc	fn_ConsoleCommand;
static UiMod_DrawConnectScreenFunc	fn_DrawConnectScreen;


/*
==================
UI_Mod_TryLoad

Attempt to load uimod.dll from the given directory.
Returns qtrue on success.
==================
*/
static qboolean UI_Mod_TryLoad( const char *dir ) {
	char libPath[MAX_OSPATH];

	Com_sprintf( libPath, sizeof(libPath), "%s%cuimod.dll", dir, PATH_SEP );
	UiMod_Print( va( "Trying UI mod host: %s\n", libPath ) );
	modLib = MOD_LOADLIB( libPath );

	return modLib != NULL ? qtrue : qfalse;
}


/*
==================
UI_Mod_Init

Load uimod.dll and initialize the .NET mod host.
Called from UI_Init.
==================
*/
void UI_Mod_Init( intptr_t (QDECL *syscall)( intptr_t, ... ) ) {
	char basePath[MAX_OSPATH];
	char gameDir[MAX_OSPATH];
	char searchDir[MAX_OSPATH];

	ui_syscall = syscall;

	// Get fs_basepath and fs_game
	UiMod_CvarGet( "fs_basepath", basePath, sizeof(basePath) );
	UiMod_CvarGet( "fs_game", gameDir, sizeof(gameDir) );

	if ( !basePath[0] ) {
		Q_strncpyz( basePath, ".", sizeof(basePath) );
	}
	if ( !gameDir[0] ) {
		Q_strncpyz( gameDir, BASEGAME, sizeof(gameDir) );
	}

	// Try fs_basepath/gameDir/
	Com_sprintf( searchDir, sizeof(searchDir), "%s%c%s", basePath, PATH_SEP, gameDir );
	if ( !UI_Mod_TryLoad( searchDir ) ) {
		// Try fs_basepath/ (next to engine exe)
		if ( !UI_Mod_TryLoad( basePath ) ) {
			UiMod_Print( "^3UI mod host not found (uimod.dll) - UI mods disabled\n" );
			return;
		}
	}

	// Resolve exports
	fn_Init				= (UiMod_InitFunc)MOD_LOADFUNC( modLib, "UiMod_Init" );
	fn_Shutdown			= (UiMod_ShutdownFunc)MOD_LOADFUNC( modLib, "UiMod_Shutdown" );
	fn_SetActiveMenu	= (UiMod_SetActiveMenuFunc)MOD_LOADFUNC( modLib, "UiMod_SetActiveMenu" );
	fn_Refresh			= (UiMod_RefreshFunc)MOD_LOADFUNC( modLib, "UiMod_Refresh" );
	fn_KeyEvent			= (UiMod_KeyEventFunc)MOD_LOADFUNC( modLib, "UiMod_KeyEvent" );
	fn_MouseEvent		= (UiMod_MouseEventFunc)MOD_LOADFUNC( modLib, "UiMod_MouseEvent" );
	fn_IsFullscreen		= (UiMod_IsFullscreenFunc)MOD_LOADFUNC( modLib, "UiMod_IsFullscreen" );
	fn_ConsoleCommand	= (UiMod_ConsoleCommandFunc)MOD_LOADFUNC( modLib, "UiMod_ConsoleCommand" );
	fn_DrawConnectScreen = (UiMod_DrawConnectScreenFunc)MOD_LOADFUNC( modLib, "UiMod_DrawConnectScreen" );

	if ( !fn_Init ) {
		UiMod_Print( "^1UI mod host missing UiMod_Init export\n" );
		MOD_FREELIB( modLib );
		modLib = NULL;
		return;
	}

	UiMod_Print( "UI mod host loaded, initializing...\n" );
	fn_Init( (intptr_t)syscall );
	UiMod_Print( "UI mod host ready\n" );
}


/*
==================
UI_Mod_Shutdown
==================
*/
void UI_Mod_Shutdown( void ) {
	if ( !modLib ) return;

	if ( fn_Shutdown ) {
		fn_Shutdown();
	}
	MOD_FREELIB( modLib );
	modLib = NULL;

	fn_Init = NULL;
	fn_Shutdown = NULL;
	fn_SetActiveMenu = NULL;
	fn_Refresh = NULL;
	fn_KeyEvent = NULL;
	fn_MouseEvent = NULL;
	fn_IsFullscreen = NULL;
	fn_ConsoleCommand = NULL;
	fn_DrawConnectScreen = NULL;
}


/*
==================
UI_Mod_SetActiveMenu
Returns qtrue if the mod handled the menu change.
==================
*/
qboolean UI_Mod_SetActiveMenu( int menu ) {
	if ( fn_SetActiveMenu ) {
		return fn_SetActiveMenu( menu ) ? qtrue : qfalse;
	}
	return qfalse;
}


/*
==================
UI_Mod_Refresh
Returns qtrue if the mod handled all drawing.
==================
*/
qboolean UI_Mod_Refresh( int realtime ) {
	if ( fn_Refresh ) {
		return fn_Refresh( realtime ) ? qtrue : qfalse;
	}
	return qfalse;
}


/*
==================
UI_Mod_KeyEvent
Returns qtrue if the mod consumed the key.
==================
*/
qboolean UI_Mod_KeyEvent( int key, int down ) {
	if ( fn_KeyEvent ) {
		return fn_KeyEvent( key, down ) ? qtrue : qfalse;
	}
	return qfalse;
}


/*
==================
UI_Mod_MouseEvent
Returns qtrue if the mod consumed the mouse delta.
==================
*/
qboolean UI_Mod_MouseEvent( int dx, int dy ) {
	if ( fn_MouseEvent ) {
		return fn_MouseEvent( dx, dy ) ? qtrue : qfalse;
	}
	return qfalse;
}


/*
==================
UI_Mod_IsFullscreen
Returns -1 if mod doesn't handle it, 0/1 otherwise.
==================
*/
int UI_Mod_IsFullscreen( void ) {
	if ( fn_IsFullscreen ) {
		return fn_IsFullscreen();
	}
	return -1;
}


/*
==================
UI_Mod_ConsoleCommand
Returns qtrue if the mod handled it.
==================
*/
qboolean UI_Mod_ConsoleCommand( int realtime ) {
	if ( fn_ConsoleCommand ) {
		return fn_ConsoleCommand( realtime ) ? qtrue : qfalse;
	}
	return qfalse;
}


/*
==================
UI_Mod_DrawConnectScreen
Returns qtrue if the mod handled it.
==================
*/
qboolean UI_Mod_DrawConnectScreen( int overlay ) {
	if ( fn_DrawConnectScreen ) {
		return fn_DrawConnectScreen( overlay ) ? qtrue : qfalse;
	}
	return qfalse;
}
