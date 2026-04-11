using System.Runtime.InteropServices;

namespace UiMod;

/// <summary>
/// NativeAOT mod host for the UI module. Exports C-callable functions
/// that the C UI DLL calls at hook points. Each function returns 1 to
/// override C behavior, 0 for pass-through.
/// </summary>
public static unsafe class ModHost
{
    private static readonly List<IUiMod> _mods = new();
    private static bool _initialized;

    [UnmanagedCallersOnly(EntryPoint = "UiMod_Init")]
    public static void Init(nint syscallPtr)
    {
        try
        {
            Syscalls.Init(syscallPtr);
            Drawing.Init();

            Syscalls.Print("[UIMOD] .NET UI mod host initializing...\n");

            LoadMods();

            foreach (var mod in _mods)
            {
                try
                {
                    mod.Init();
                    Syscalls.Print($"[UIMOD] Initialized: {mod.Name}\n");
                }
                catch (Exception ex)
                {
                    Syscalls.Print($"[UIMOD] ^1Failed to init '{mod.Name}': {ex.Message}\n");
                }
            }

            _initialized = true;
            Syscalls.Print($"[UIMOD] Host ready ({_mods.Count} mod(s) loaded)\n");
        }
        catch (Exception ex)
        {
            try { Syscalls.Print($"[UIMOD] ^1Init failed: {ex.Message}\n"); } catch { }
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "UiMod_Shutdown")]
    public static void Shutdown()
    {
        if (!_initialized) return;
        try
        {
            foreach (var mod in _mods)
            {
                try { mod.Shutdown(); } catch { }
            }
            _mods.Clear();
            _initialized = false;
            Syscalls.Print("[UIMOD] Host shutdown\n");
        }
        catch { }
    }

    [UnmanagedCallersOnly(EntryPoint = "UiMod_SetActiveMenu")]
    public static int SetActiveMenu(int menu)
    {
        if (!_initialized) return 0;
        foreach (var mod in _mods)
        {
            try { if (mod.SetActiveMenu(menu)) return 1; }
            catch { }
        }
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "UiMod_Refresh")]
    public static int Refresh(int realtime)
    {
        if (!_initialized) return 0;
        foreach (var mod in _mods)
        {
            try { if (mod.Refresh(realtime)) return 1; }
            catch { }
        }
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "UiMod_KeyEvent")]
    public static int KeyEvent(int key, int down)
    {
        if (!_initialized) return 0;
        foreach (var mod in _mods)
        {
            try { if (mod.KeyEvent(key, down)) return 1; }
            catch { }
        }
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "UiMod_MouseEvent")]
    public static int MouseEvent(int dx, int dy)
    {
        if (!_initialized) return 0;
        foreach (var mod in _mods)
        {
            try { if (mod.MouseEvent(dx, dy)) return 1; }
            catch { }
        }
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "UiMod_IsFullscreen")]
    public static int IsFullscreen()
    {
        if (!_initialized) return -1;
        foreach (var mod in _mods)
        {
            try
            {
                int result = mod.IsFullscreen();
                if (result >= 0) return result;
            }
            catch { }
        }
        return -1;
    }

    [UnmanagedCallersOnly(EntryPoint = "UiMod_ConsoleCommand")]
    public static int ConsoleCommand(int realtime)
    {
        if (!_initialized) return 0;
        foreach (var mod in _mods)
        {
            try { if (mod.ConsoleCommand(realtime)) return 1; }
            catch { }
        }
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "UiMod_DrawConnectScreen")]
    public static int DrawConnectScreen(int overlay)
    {
        if (!_initialized) return 0;
        foreach (var mod in _mods)
        {
            try { if (mod.DrawConnectScreen(overlay)) return 1; }
            catch { }
        }
        return 0;
    }

    private static void LoadMods()
    {
        _mods.Add(new MainMenuMod());
        _mods.Add(new InGameMenuMod());
    }
}
