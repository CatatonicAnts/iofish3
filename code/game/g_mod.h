/*
===========================================================================
g_mod.h -- .NET mod host integration for server game module

Loads qagamemod.dll (NativeAOT) and calls exported hook functions
at key points in the game module lifecycle.
===========================================================================
*/

#ifndef G_MOD_H
#define G_MOD_H

#include "../qcommon/q_shared.h"

// Initialize the mod host (loads DLL, calls QgMod_Init)
void G_Mod_Init( intptr_t (QDECL *syscall)( intptr_t, ... ) );

// Shutdown the mod host (calls QgMod_Shutdown, unloads DLL)
void G_Mod_Shutdown( void );

// Per-frame update
void G_Mod_Frame( int levelTime );

// Console command dispatch (returns qtrue if a mod handled it)
qboolean G_Mod_ConsoleCommand( void );

#endif // G_MOD_H
