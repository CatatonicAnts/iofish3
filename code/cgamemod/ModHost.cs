using System.Runtime.InteropServices;

namespace CGameMod;

/// <summary>
/// NativeAOT mod host. Exports C-callable functions that the C cgame calls at hook points.
/// Manages mod lifecycle and dispatches events to registered mod handlers.
/// </summary>
public static unsafe class ModHost
{
    private static readonly List<ICGameMod> _mods = new();
    private static bool _initialized;
    private static int _screenWidth;
    private static int _screenHeight;

    /// <summary>Called by the C cgame at CG_Init. Receives the engine syscall pointer and mod API.</summary>
    [UnmanagedCallersOnly(EntryPoint = "CgMod_Init")]
    public static void Init(nint syscallPtr, int screenWidth, int screenHeight, nint cgameModApiPtr)
    {
        try
        {
            Syscalls.Init(syscallPtr);
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;

            // Parse the cgame mod API (trace, view, highlight, entity queries)
            if (cgameModApiPtr != 0)
                CGameApi.Init(cgameModApiPtr);

            Syscalls.Print("[MOD] .NET mod host initializing...\n");

            // Discover and load mods
            LoadMods();

            // Initialize all loaded mods
            foreach (var mod in _mods)
            {
                try
                {
                    mod.Init();
                    Syscalls.Print($"[MOD] Initialized: {mod.Name}\n");
                }
                catch (Exception ex)
                {
                    Syscalls.Print($"[MOD] ^1Failed to init '{mod.Name}': {ex.Message}\n");
                }
            }

            _initialized = true;
            Syscalls.Print($"[MOD] Host ready ({_mods.Count} mod(s) loaded)\n");
        }
        catch (Exception ex)
        {
            // If syscalls aren't working, we can't even print. Just swallow.
            try { Syscalls.Print($"[MOD] ^1Init failed: {ex.Message}\n"); } catch { }
        }
    }

    /// <summary>Called by the C cgame at CG_Shutdown.</summary>
    [UnmanagedCallersOnly(EntryPoint = "CgMod_Shutdown")]
    public static void Shutdown()
    {
        if (!_initialized) return;
        try
        {
            foreach (var mod in _mods)
            {
                try { mod.Shutdown(); }
                catch { }
            }
            _mods.Clear();
            _initialized = false;
            Syscalls.Print("[MOD] Host shutdown\n");
        }
        catch { }
    }

    /// <summary>Called every frame from CG_DrawActiveFrame.</summary>
    [UnmanagedCallersOnly(EntryPoint = "CgMod_Frame")]
    public static void Frame(int serverTime)
    {
        if (!_initialized) return;
        foreach (var mod in _mods)
        {
            try { mod.Frame(serverTime); }
            catch { }
        }
    }

    /// <summary>Called from CG_Draw2D after all standard HUD drawing.</summary>
    [UnmanagedCallersOnly(EntryPoint = "CgMod_Draw2D")]
    public static void Draw2D()
    {
        if (!_initialized) return;
        foreach (var mod in _mods)
        {
            try { mod.Draw2D(_screenWidth, _screenHeight); }
            catch { }
        }
    }

    /// <summary>Called from CG_ConsoleCommand. Returns 1 if a mod handled the command.</summary>
    [UnmanagedCallersOnly(EntryPoint = "CgMod_ConsoleCommand")]
    public static int ConsoleCommand()
    {
        if (!_initialized) return 0;

        string cmd = Syscalls.Argv(0);
        foreach (var mod in _mods)
        {
            try
            {
                if (mod.ConsoleCommand(cmd))
                    return 1;
            }
            catch { }
        }
        return 0;
    }

    /// <summary>Called when a game entity event fires.</summary>
    [UnmanagedCallersOnly(EntryPoint = "CgMod_EntityEvent")]
    public static void EntityEvent(int entityNum, int eventType, int eventParm)
    {
        if (!_initialized) return;
        foreach (var mod in _mods)
        {
            try { mod.EntityEvent(entityNum, eventType, eventParm); }
            catch { }
        }
    }

    /// <summary>Called when a server command is routed to the mod host.</summary>
    [UnmanagedCallersOnly(EntryPoint = "CgMod_ServerCommand")]
    public static void ServerCommand(nint cmdPtr)
    {
        if (!_initialized) return;
        string args = Marshal.PtrToStringUTF8(cmdPtr) ?? "";
        foreach (var mod in _mods)
        {
            try { mod.ServerCommand(args); }
            catch { }
        }
    }

    private static void LoadMods()
    {
        // Built-in mods
        _mods.Add(new ExampleMod());
        _mods.Add(new EntityPickerMod());
        _mods.Add(new HudMod());

        // Future: scan baseq3/mods/ for additional NativeAOT mod DLLs
    }
}
