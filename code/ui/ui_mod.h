/*
===========================================================================
ui_mod.h -- .NET mod host integration for the UI module

Loads uimod.dll (NativeAOT) and calls exported hook functions.
Each function returns qtrue if the mod handled the event (skip C code),
or qfalse to let the C UI handle it (pass-through).
===========================================================================
*/

#ifndef UI_MOD_H
#define UI_MOD_H

#include "../qcommon/q_shared.h"

// Initialize the mod host (loads uimod.dll). Call from UI_Init.
// syscall is the engine syscall function pointer.
void UI_Mod_Init( intptr_t (QDECL *syscall)( intptr_t, ... ) );

// Shutdown the mod host. Call from UI_Shutdown.
void UI_Mod_Shutdown( void );

// Notify mod of active menu change.
// Returns qtrue if mod handles the menu (skip C menu activation).
qboolean UI_Mod_SetActiveMenu( int menu );

// Main draw call. Returns qtrue if mod handled all drawing (skip C).
qboolean UI_Mod_Refresh( int realtime );

// Key event. Returns qtrue if mod consumed the key.
qboolean UI_Mod_KeyEvent( int key, int down );

// Mouse event. Returns qtrue if mod consumed the mouse delta.
qboolean UI_Mod_MouseEvent( int dx, int dy );

// Fullscreen query. Returns -1 if mod doesn't handle it,
// 0 if not fullscreen, 1 if fullscreen.
int UI_Mod_IsFullscreen( void );

// Console command. Returns qtrue if mod handled it.
qboolean UI_Mod_ConsoleCommand( int realtime );

// Draw connect screen. Returns qtrue if mod handled it.
qboolean UI_Mod_DrawConnectScreen( int overlay );

#endif // UI_MOD_H
