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

// API struct passed to the .NET mod host at init
typedef struct {
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
	// Returns model name string for entity (from configstring). Writes to buf, returns length.
	int		(*GetEntityModelName)( int entityNum, char *buf, int bufSize );
	// Returns packed entity info: weapon, eFlags, frame, event
	void	(*GetEntityInfo)( int entityNum, int *weapon, int *eFlags, int *frame, int *event );
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

// Route a server command to the mod host (returns qtrue if handled)
qboolean CG_Mod_ServerCommand( const char *cmd );

#endif // CG_MOD_H
