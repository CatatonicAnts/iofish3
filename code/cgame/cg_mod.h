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

#endif // CG_MOD_H
