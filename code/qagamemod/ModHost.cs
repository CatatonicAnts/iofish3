using System.Runtime.InteropServices;

namespace QGameMod;

/// <summary>
/// NativeAOT mod host for the server game module.
/// Exports C-callable functions that the C game module calls at hook points.
/// Manages mod lifecycle and dispatches events to registered mod handlers.
/// </summary>
public static unsafe class ModHost
{
    private static readonly List<IQGameMod> _mods = new();
    private static bool _initialized;

    /// <summary>
    /// Called by the C game module at G_InitGame.
    /// Receives the engine syscall pointer and a pointer to the gameModApi_t struct.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "QgMod_Init")]
    public static void Init(nint syscallPtr, nint gameApiPtr)
    {
        try
        {
            Syscalls.Init(syscallPtr);
            GameApi.Init(gameApiPtr);

            Syscalls.Print("[GMOD] .NET server mod host initializing...\n");

            LoadMods();

            foreach (var mod in _mods)
            {
                try
                {
                    mod.Init();
                    Syscalls.Print($"[GMOD] Initialized: {mod.Name}\n");
                }
                catch (Exception ex)
                {
                    Syscalls.Print($"[GMOD] ^1Failed to init '{mod.Name}': {ex.Message}\n");
                }
            }

            _initialized = true;
            Syscalls.Print($"[GMOD] Host ready ({_mods.Count} mod(s) loaded)\n");
        }
        catch (Exception ex)
        {
            try { Syscalls.Print($"[GMOD] ^1Init failed: {ex.Message}\n"); } catch { }
        }
    }

    /// <summary>Called by the C game module at G_ShutdownGame.</summary>
    [UnmanagedCallersOnly(EntryPoint = "QgMod_Shutdown")]
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
            Syscalls.Print("[GMOD] Host shutdown\n");
        }
        catch { }
    }

    /// <summary>Called every server frame from G_RunFrame.</summary>
    [UnmanagedCallersOnly(EntryPoint = "QgMod_Frame")]
    public static void Frame(int levelTime)
    {
        if (!_initialized) return;
        foreach (var mod in _mods)
        {
            try { mod.Frame(levelTime); }
            catch { }
        }
    }

    /// <summary>Called from ConsoleCommand. Returns 1 if a mod handled the command.</summary>
    [UnmanagedCallersOnly(EntryPoint = "QgMod_ConsoleCommand")]
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

    private static void LoadMods()
    {
        // Built-in entity commands mod
        _mods.Add(new EntityCommandsMod());
    }
}
